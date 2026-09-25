using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CaddyManager.Config.Admin;
using CaddyManager.Config.Generation;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Config.Services;

/// <summary>
/// Builds, validates and applies the Caddy configuration. Platform/Ops services are resolved lazily from
/// the service provider (they depend on this module too, and may be absent in tests).
/// </summary>
public sealed partial class CaddyConfigService(
    IStore store,
    AppPaths paths,
    ISecretProtector secrets,
    CaddyAdminClient admin,
    IServiceProvider services,
    ILogger<CaddyConfigService> logger) : ICaddyConfigService
{
    public const int RevisionsToKeep = 100;
    public const string EventKey = "config-apply";
    public const string AlertRule = "configFailure";

    /// <summary>
    /// User name for revisions when the apply is triggered from an API request of this module
    /// (ICaddyConfigService.ApplyAsync has no user parameter).
    /// </summary>
    internal static readonly AsyncLocal<string?> AmbientUser = new();

    private readonly SemaphoreSlim _applyLock = new(1, 1);
    private volatile IReadOnlyCollection<string>? _modules;
    private volatile bool _modulesLoaded;
    private bool? _lastFailed;

    // ------------------------------------------------------------------ generation

    /// <summary>Loads the model from the store and generates the managed config.</summary>
    public ConfigGeneratorResult Generate()
    {
        if (!_modulesLoaded)
        {
            try
            {
                RefreshModulesAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(15));
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not read installed Caddy modules");
            }
        }
        return Generate(store.GetSettings<CaddySettings>());
    }

    private ConfigGeneratorResult Generate(CaddySettings settings)
    {
        var hosts = store.Col<SiteHost>().FindAll().ToList();
        var certs = store.Col<Certificate>().FindAll().ToList();
        var extraWarnings = new List<string>();
        string? mac = null;
        if (!string.IsNullOrEmpty(settings.EabMacKeyProtected))
        {
            try
            {
                mac = secrets.Unprotect(settings.EabMacKeyProtected);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "EAB MAC key could not be decrypted");
                extraWarnings.Add("The stored EAB MAC key could not be decrypted (was the database restored from another server?). Re-enter it under Settings > Caddy.");
            }
        }

        var input = new ConfigGeneratorInput
        {
            Settings = settings,
            Paths = paths,
            Hosts = hosts,
            Streams = store.Col<StreamHost>().FindAll().ToList(),
            AccessLists = store.Col<AccessList>().FindAll().ToList(),
            Certificates = certs,
            InstalledModules = _modules,
            EabMacKey = mac,
            UnavailableCertificateIds = UnavailableCertificates(hosts, certs),
        };
        var result = CaddyConfigGenerator.Generate(input);
        result.Warnings.InsertRange(0, extraWarnings);
        return result;
    }

    /// <summary>Custom certificates used by enabled hosts whose files cannot be read.</summary>
    private HashSet<string> UnavailableCertificates(List<SiteHost> hosts, List<Certificate> certs)
    {
        var used = hosts.Where(h => h.Enabled && h.Tls == TlsMode.Custom && h.CertificateId is not null)
            .Select(h => h.CertificateId!).ToHashSet();
        var result = new HashSet<string>();
        foreach (var c in certs.Where(c => used.Contains(c.Id)))
        {
            if (!IsReadable(c.CertPath) || !IsReadable(c.KeyPath))
            {
                logger.LogWarning("Certificate {Name} ({Id}) files are missing or unreadable: {Cert} / {Key}", c.Name, c.Id, c.CertPath, c.KeyPath);
                result.Add(c.Id);
            }
        }
        return result;
    }

    private static bool IsReadable(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public string BuildConfigJson() => Generate().ToJson();

    /// <summary>Installed modules (layer4 detection). Null when the binary is missing or unknown.</summary>
    public async Task<IReadOnlyCollection<string>?> RefreshModulesAsync(CancellationToken ct)
    {
        var bm = services.GetService<ICaddyBinaryManager>();
        if (bm is null)
        {
            _modulesLoaded = true;
            return _modules;
        }
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            var installed = await bm.GetInstalledAsync(cts.Token);
            _modules = installed?.Modules.ToList();
            _modulesLoaded = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not determine the modules of the installed Caddy binary");
        }
        return _modules;
    }

    // ------------------------------------------------------------------ apply

    public async Task<ApplyResult> ApplyAsync(string reason, CancellationToken ct = default)
    {
        await _applyLock.WaitAsync(ct);
        try
        {
            return await ApplyCoreAsync(string.IsNullOrWhiteSpace(reason) ? "apply" : reason, ct);
        }
        finally
        {
            _applyLock.Release();
        }
    }

    private async Task<ApplyResult> ApplyCoreAsync(string reason, CancellationToken ct)
    {
        var settings = store.GetSettings<CaddySettings>();
        var warnings = new List<string>();
        var target = await FindReachableAdminAsync(settings, ct);
        string json;

        if (settings.Mode == ConfigMode.Caddyfile)
        {
            if (string.IsNullOrWhiteSpace(settings.RawCaddyfile))
                return await FailAsync(reason, null, "Caddyfile mode is enabled but the Caddyfile is empty.", warnings);
            try
            {
                var adapted = await AdaptCaddyfileAsync(settings.RawCaddyfile, target, ct);
                if (adapted is null)
                {
                    warnings.Add("Caddy is not running and its binary is not installed, so the Caddyfile could not be converted. It will be applied once Caddy is installed.");
                    logger.LogWarning("Apply ({Reason}): Caddyfile not adapted — Caddy not running and binary missing", reason);
                    return new ApplyResult { Success = true, WrittenOnly = true, Warnings = warnings };
                }
                json = adapted.Value.Json;
                warnings.AddRange(adapted.Value.Warnings);
            }
            catch (CaddyAdminException ex)
            {
                return await FailAsync(reason, null, "The Caddyfile is invalid: " + ex.Message, warnings);
            }
        }
        else
        {
            await RefreshModulesAsync(ct);
            ConfigGeneratorResult generated;
            try
            {
                generated = Generate(settings);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Config generation failed");
                return await FailAsync(reason, null, "Could not generate the Caddy configuration: " + ex.Message, warnings);
            }
            json = generated.ToJson();
            warnings.AddRange(generated.Warnings);
        }

        var writtenOnly = false;
        if (target is not null)
        {
            try
            {
                await target.LoadAsync(json, ct);
                logger.LogInformation("Configuration loaded into Caddy ({Reason})", reason);
            }
            catch (CaddyAdminException ex)
            {
                return await FailAsync(reason, json, ex.Message, warnings);
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "Caddy admin API became unreachable while loading the configuration; falling back to validation");
                target = null;
            }
        }

        if (target is null)
        {
            writtenOnly = true;
            if (File.Exists(paths.CaddyExe))
            {
                var v = await ValidateAsync(json, ct);
                if (!v.Valid) return await FailAsync(reason, json, v.Error ?? "Caddy reported the configuration as invalid.", warnings);
                warnings.Add("Caddy is not running. The configuration was validated and saved; it takes effect when Caddy starts.");
            }
            else
            {
                warnings.Add("The Caddy binary is not installed. The configuration was saved without validation; it takes effect once Caddy is installed and started.");
            }
        }

        try
        {
            WriteAtomic(paths.CaddyConfigFile, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Could not write {File}", paths.CaddyConfigFile);
            var msg = $"The configuration could not be written to {paths.CaddyConfigFile}: {ex.Message}";
            if (writtenOnly) return await FailAsync(reason, json, msg, warnings);
            // Caddy already runs the new config; the boot file is stale — report as warning.
            warnings.Add(msg);
        }

        var rev = SaveRevision(json, reason, success: true, error: null);
        if (LastFailed())
        {
            RaiseEvent(EventSeverity.Recovered, "Caddy configuration applied successfully again.", $"Reason: {reason}");
        }
        _lastFailed = false;
        return new ApplyResult { Success = true, RevisionId = rev.Id, WrittenOnly = writtenOnly, Warnings = warnings };
    }

    private Task<ApplyResult> FailAsync(string reason, string? json, string error, List<string> warnings)
    {
        logger.LogWarning("Applying the Caddy configuration failed ({Reason}): {Error}", reason, error);
        ConfigRevision? rev = null;
        try
        {
            rev = SaveRevision(json ?? "", reason, success: false, error: error);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not store the failed config revision");
        }
        RaiseEvent(EventSeverity.Error, "Caddy rejected the configuration.", $"Reason: {reason}\n\n{error}");
        _lastFailed = true;
        return Task.FromResult(new ApplyResult { Success = false, Error = error, RevisionId = rev?.Id, Warnings = warnings });
    }

    private bool LastFailed()
    {
        if (_lastFailed is { } v) return v;
        try
        {
            var last = store.Col<ConfigRevision>().Query().OrderByDescending(r => r.CreatedAt).Limit(1).FirstOrDefault();
            _lastFailed = last is { Success: false };
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read the last config revision");
            _lastFailed = false;
        }
        return _lastFailed.Value;
    }

    private void RaiseEvent(EventSeverity severity, string message, string details)
    {
        try
        {
            services.GetService<IEventSink>()?.Raise(severity, "config", message, details, EventKey, AlertRule);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not raise config event");
        }
    }

    private string CurrentUserName()
    {
        if (!string.IsNullOrWhiteSpace(AmbientUser.Value)) return AmbientUser.Value!;
        try
        {
            using var scope = services.CreateScope();
            var user = scope.ServiceProvider.GetService<ICurrentUser>();
            if (user is not null && !string.IsNullOrWhiteSpace(user.UserName)) return user.UserName;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Current user not available");
        }
        return "system";
    }

    private ConfigRevision SaveRevision(string json, string reason, bool success, string? error)
    {
        var rev = new ConfigRevision
        {
            Json = json,
            Hash = string.IsNullOrEmpty(json) ? "" : CaddyJson.Hash(json),
            Reason = reason.Length > 500 ? reason[..500] : reason,
            AppliedBy = CurrentUserName(),
            Success = success,
            Error = error,
        };
        var col = store.Col<ConfigRevision>();
        col.Insert(rev);
        var excess = col.Count() - RevisionsToKeep;
        if (excess > 0)
        {
            var old = col.Query().OrderBy(r => r.CreatedAt).Limit(excess).ToList().Select(r => r.Id).ToList();
            foreach (var id in old) col.Delete(id);
        }
        return rev;
    }

    /// <summary>
    /// The admin API to talk to: the configured address, or — after the admin address was changed in settings —
    /// the address of the config Caddy is currently running.
    /// </summary>
    private async Task<CaddyAdminClient?> FindReachableAdminAsync(CaddySettings settings, CancellationToken ct)
    {
        var candidates = new List<string> { settings.AdminListen };
        var running = ReadAdminListen(paths.CaddyConfigFile);
        if (running is not null && !string.Equals(running, settings.AdminListen, StringComparison.OrdinalIgnoreCase))
            candidates.Add(running);
        foreach (var c in candidates)
        {
            var client = admin.ForAddress(c);
            if (await client.IsReachableAsync(ct)) return client;
        }
        return null;
    }

    private static string? ReadAdminListen(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            return (JsonNode.Parse(File.ReadAllText(file)) as JsonObject)?["admin"]?["listen"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ Caddyfile

    /// <summary>
    /// Adapts a Caddyfile to JSON via the admin API (when reachable) or the binary. Throws CaddyAdminException
    /// when Caddy reports an error; returns null when neither Caddy nor its binary is available.
    /// </summary>
    public async Task<(string Json, List<string> Warnings)?> AdaptCaddyfileAsync(string caddyfile, CancellationToken ct = default)
    {
        var target = await FindReachableAdminAsync(store.GetSettings<CaddySettings>(), ct);
        return await AdaptCaddyfileAsync(caddyfile, target, ct);
    }

    private async Task<(string Json, List<string> Warnings)?> AdaptCaddyfileAsync(string caddyfile, CaddyAdminClient? target, CancellationToken ct)
    {
        if (target is not null)
        {
            try
            {
                return await target.AdaptCaddyfileAsync(caddyfile, ct);
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "Caddy admin API unreachable while adapting; falling back to the binary");
            }
        }
        if (!File.Exists(paths.CaddyExe)) return null;

        var tmp = TempFile("Caddyfile");
        try
        {
            await File.WriteAllTextAsync(tmp, caddyfile, ct);
            var r = await CaddyProcessRunner.RunAsync(paths.CaddyExe,
                ["adapt", "--config", tmp, "--adapter", "caddyfile"], environment: CaddyEnvironment(), ct: ct);
            var warnings = new List<string>();
            foreach (var line in r.StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var (level, msg) = ParseLogLine(line);
                if (level is "warn" or "warning") warnings.Add(msg);
            }
            if (r.ExitCode != 0) throw new CaddyAdminException(ExtractCliError(r.StdErr + "\n" + r.StdOut));
            return (CaddyJson.Reformat(r.StdOut), warnings);
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    // ------------------------------------------------------------------ validation

    public async Task<ValidationResult> ValidateAsync(string json, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(json)) return new ValidationResult { Valid = false, Error = "The configuration is empty." };
        try
        {
            JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            return new ValidationResult { Valid = false, Error = "The configuration is not valid JSON: " + ex.Message };
        }

        var bm = services.GetService<ICaddyBinaryManager>();
        if (bm is null && !File.Exists(paths.CaddyExe))
            return new ValidationResult { Valid = false, Error = "The Caddy binary is not installed, so the configuration cannot be validated." };

        var tmp = TempFile("json");
        try
        {
            await File.WriteAllTextAsync(tmp, json, ct);
            string[] args = ["validate", "--config", tmp];
            int exit;
            string output;
            if (bm is not null)
            {
                (exit, output) = await bm.RunCaddyAsync(args, null, ct);
            }
            else
            {
                var r = await CaddyProcessRunner.RunAsync(paths.CaddyExe, args, environment: CaddyEnvironment(), timeout: TimeSpan.FromSeconds(120), ct: ct);
                (exit, output) = (r.ExitCode, r.Combined);
            }
            return exit == 0
                ? new ValidationResult { Valid = true, Output = output }
                : new ValidationResult { Valid = false, Error = ExtractCliError(output), Output = output };
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or FileNotFoundException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning(ex, "caddy validate could not be run");
            return new ValidationResult { Valid = false, Error = "caddy validate could not be run: " + ex.Message };
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    // ------------------------------------------------------------------ boot config

    public void EnsureBootConfig()
    {
        if (File.Exists(paths.CaddyConfigFile)) return;
        var settings = store.GetSettings<CaddySettings>();
        var json = CaddyJson.Serialize(CaddyConfigGenerator.BuildBootConfig(settings, paths));
        WriteAtomic(paths.CaddyConfigFile, json);
        logger.LogInformation("Wrote minimal boot configuration to {File}", paths.CaddyConfigFile);
    }

    // ------------------------------------------------------------------ helpers

    internal Dictionary<string, string> CaddyEnvironment() => new()
    {
        ["XDG_DATA_HOME"] = paths.CaddyStorageDir,
        ["XDG_CONFIG_HOME"] = Path.Combine(paths.DataDir, "caddy", "config"),
    };

    private string TempFile(string ext)
    {
        var dir = Path.Combine(paths.DataDir, "caddy", "tmp");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"validate-{Guid.NewGuid():N}.{ext}");
    }

    private void TryDelete(string file)
    {
        try
        {
            if (File.Exists(file)) File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not delete temp file {File}", file);
        }
    }

    public static void WriteAtomic(string file, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = file + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(tmp, content);
        File.Move(tmp, file, overwrite: true);
    }

    private static (string Level, string Message) ParseLogLine(string line)
    {
        if (line.StartsWith('{'))
        {
            try
            {
                if (JsonNode.Parse(line) is JsonObject o)
                {
                    var level = o["level"]?.GetValue<string>() ?? "";
                    var msg = o["msg"]?.GetValue<string>() ?? line;
                    var file = o["file"]?.ToString();
                    var ln = o["line"]?.ToString();
                    var err = o["error"]?.ToString();
                    if (file is not null) msg = $"{file}{(ln is null ? "" : ":" + ln)}: {msg}";
                    if (err is not null) msg += ": " + err;
                    return (level.ToLowerInvariant(), msg);
                }
            }
            catch (JsonException)
            {
            }
        }
        return ("", line);
    }

    [GeneratedRegex(@"^\s*Error:\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex CliErrorRegex();

    /// <summary>Picks the human readable "Error: ..." line from caddy CLI output.</summary>
    internal static string ExtractCliError(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return "Caddy reported an error without details.";
        var m = CliErrorRegex().Matches(output);
        if (m.Count > 0) return m[^1].Groups[1].Value.Trim();
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var (level, msg) = ParseLogLine(lines[i]);
            if (level is "error" or "fatal" or "") return msg;
        }
        return lines[^1];
    }
}
