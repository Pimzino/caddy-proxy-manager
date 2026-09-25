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

        string? acmeIssuerJson = null;
        if (!string.IsNullOrEmpty(settings.AcmeIssuerJsonProtected))
        {
            try
            {
                acmeIssuerJson = secrets.Unprotect(settings.AcmeIssuerJsonProtected);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "ACME issuer JSON could not be decrypted");
                extraWarnings.Add("The stored ACME issuer JSON (DNS challenge settings) could not be decrypted (was the database restored from another server?). Re-enter it under Settings > Caddy.");
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
            AcmeIssuerJson = acmeIssuerJson,
            UnavailableCertificateIds = UnavailableCertificates(hosts, certs),
            EndpointGuard = Validation.LocalEndpointGuard.Create(settings, store.GetSettings<UiSettings>(), includeUi: false),
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
                warnings.AddRange(adapted.Value.Warnings);
                json = CaddyConfigGenerator.CompleteAdaptedConfig(adapted.Value.Json, settings, paths, warnings);
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
                await ReleaseZeroRttQuicListenersAsync(target, json, ct);
                await target.LoadAsync(json, ct);
                logger.LogInformation("Configuration loaded into Caddy ({Reason})", reason);
            }
            catch (CaddyAdminException ex)
            {
                await RestoreAdminEndpointAsync(target, json, ct);
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
        if (running is not null && !candidates.Contains(running, StringComparer.OrdinalIgnoreCase))
            candidates.Add(running);
        // A rejected load still moves Caddy's admin endpoint to the rejected config's address (see
        // RestoreAdminEndpointAsync); if that restore never happened (manager restarted, Caddy busy), Caddy is there.
        if (LastRevisionAdminListenIfFailed() is { } stranded && !candidates.Contains(stranded, StringComparer.OrdinalIgnoreCase))
            candidates.Add(stranded);
        foreach (var c in candidates)
        {
            var client = admin.ForAddress(c);
            if (await client.IsReachableAsync(ct)) return client;
        }
        return null;
    }

    /// <summary>
    /// After Caddy rejected a config whose admin address differs from the one used: Caddy swaps its admin endpoint
    /// BEFORE provisioning the apps and does not restore it when provisioning fails (the old apps keep running), so
    /// the admin API now listens on the rejected config's address while caddy.json and the settings keep the old one.
    /// Re-load the last good config through the new address to move the admin endpoint back.
    /// https://github.com/caddyserver/caddy/blob/v2.11.4/caddy.go (provisionContext: replaceLocalAdminServer)
    /// </summary>
    private async Task RestoreAdminEndpointAsync(CaddyAdminClient used, string rejectedJson, CancellationToken ct)
    {
        try
        {
            var rejectedListen = (JsonNode.Parse(rejectedJson) as JsonObject)?["admin"]?["listen"]?.GetValue<string>();
            if (rejectedListen is null) return;
            string rejectedBase;
            try { rejectedBase = CaddyAdminClient.BaseUrlFor(rejectedListen); }
            catch (CaddyAdminException) { return; }
            if (string.Equals(rejectedBase, used.BaseUrl, StringComparison.OrdinalIgnoreCase)) return;
            if (await used.IsReachableAsync(ct)) return;
            var moved = admin.ForAddress(rejectedListen);
            if (!await moved.IsReachableAsync(ct)) return;
            if (!File.Exists(paths.CaddyConfigFile))
            {
                logger.LogWarning("Caddy moved its admin API to {Address} after rejecting the configuration and no previous configuration exists to restore it", rejectedListen);
                return;
            }
            await moved.LoadAsync(await File.ReadAllTextAsync(paths.CaddyConfigFile, ct), ct);
            logger.LogWarning("Caddy moved its admin API to {Address} although it rejected the configuration; the last good configuration was re-loaded to restore it", rejectedListen);
        }
        catch (Exception ex) when (ex is CaddyAdminException or HttpRequestException or IOException or JsonException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not restore Caddy's admin endpoint after a rejected configuration");
        }
    }

    /// <summary>
    /// Caddy keeps ONE QUIC listener per address across config reloads and keeps the Allow0RTT it was created with
    /// (listeners.go ListenQUIC: listenerPool.LoadOrNew), so a new "allow_0rtt": false only takes effect on a fresh
    /// listener. When the running config serves HTTP/3 with 0-RTT on an address the new config serves with 0-RTT off
    /// (e.g. after upgrading from a version that did not disable it), the new config is first loaded without HTTP/3
    /// on those servers, which releases the listener; the real load then creates a new one. Clients use HTTP/2
    /// meanwhile. https://github.com/caddyserver/caddy/blob/v2.11.4/listeners.go
    /// </summary>
    private async Task ReleaseZeroRttQuicListenersAsync(CaddyAdminClient target, string json, CancellationToken ct)
    {
        if (!json.Contains("\"allow_0rtt\"", StringComparison.Ordinal)) return;
        JsonObject? next, running;
        try
        {
            next = JsonNode.Parse(json) as JsonObject;
            var runningText = await target.GetConfigAsync(ct);
            running = string.IsNullOrWhiteSpace(runningText) ? null : JsonNode.Parse(runningText) as JsonObject;
        }
        catch (JsonException)
        {
            return;
        }
        if (next?["apps"]?["http"]?["servers"] is not JsonObject nextServers || running?["apps"]?["http"]?["servers"] is not JsonObject runningServers) return;

        static bool ServesH3(JsonObject srv) =>
            srv["protocols"] is not JsonArray p || p.Any(x => x?.GetValue<string>() == "h3"); // Caddy's default includes h3
        static bool ZeroRttOff(JsonObject srv) => srv["allow_0rtt"] is JsonValue v && v.GetValueKind() == JsonValueKind.False;
        static HashSet<string> Listen(JsonObject srv) =>
            (srv["listen"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

        var affected = new List<string>();
        foreach (var (name, node) in nextServers)
        {
            if (node is not JsonObject srv || !ServesH3(srv) || !ZeroRttOff(srv)) continue;
            var listen = Listen(srv);
            if (runningServers.Any(r => r.Value is JsonObject old && ServesH3(old) && !ZeroRttOff(old) && Listen(old).Overlaps(listen)))
                affected.Add(name);
        }
        if (affected.Count == 0) return;

        var interim = (JsonObject)next.DeepClone();
        // The interim load must keep Caddy's admin API where it is: Caddy moves its admin endpoint to the loaded
        // config's admin.listen, so an interim copy with a NEW admin address sent the following real load (through
        // `target`, the old address) into the void, and the interim config (no HTTP/3) stayed live.
        // https://github.com/caddyserver/caddy/blob/v2.11.4/caddy.go (replaceLocalAdminServer)
        if (running!["admin"] is JsonNode runningAdmin) interim["admin"] = runningAdmin.DeepClone();
        else interim.Remove("admin"); // Caddy's default admin address, where it runs now
        foreach (var name in affected)
        {
            var srv = interim["apps"]!["http"]!["servers"]![name]!.AsObject();
            srv["protocols"] = new JsonArray((srv["protocols"] as JsonArray)?.Select(x => x?.GetValue<string>()).Where(p => p is not null && p != "h3").Select(p => (JsonNode)p!).ToArray() ?? [JsonValue.Create("h1")!, JsonValue.Create("h2")!]);
        }
        logger.LogInformation("Re-creating Caddy's HTTP/3 listener so that 0-RTT is disabled ({Servers})", string.Join(", ", affected));
        await target.LoadAsync(interim.ToJsonString(), ct);
    }

    /// <summary>The admin address of the most recent config revision when that revision was rejected, else null.</summary>
    private string? LastRevisionAdminListenIfFailed()
    {
        try
        {
            var last = store.Col<ConfigRevision>().Query().OrderByDescending(r => r.CreatedAt).Limit(1).ToList().FirstOrDefault();
            if (last is null || last.Success || string.IsNullOrWhiteSpace(last.Json)) return null;
            return (JsonNode.Parse(last.Json) as JsonObject)?["admin"]?["listen"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
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
            // Show "Caddyfile" instead of the temporary file's path.
            if (r.ExitCode != 0) throw new CaddyAdminException(ExtractCliError(r.StdErr + "\n" + r.StdOut).Replace(tmp, "Caddyfile", StringComparison.Ordinal));
            return (CaddyJson.Reformat(r.StdOut), warnings.Select(w => w.Replace(tmp, "Caddyfile", StringComparison.Ordinal)).ToList());
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
