using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using CaddyManager.Platform.Hosting;
using CaddyManager.Platform.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Platform.Binary;

/// <summary>Written next to the binary after every install so the UI can show where it came from.</summary>
public sealed record InstallMetadata
{
    public string Version { get; init; } = "";
    public DateTime InstalledAt { get; init; }
    /// <summary>"github-release" | "caddyserver-build" | "dev-copy"</summary>
    public string Source { get; init; } = "";
    public string? Url { get; init; }
    public string? Sha512 { get; init; }
    public List<string> Plugins { get; init; } = new();
    public string Platform { get; init; } = "";
    public long FileSize { get; init; }
}

/// <summary>
/// Manages the Caddy binary: inspection (version / modules), GitHub release checks, the caddyserver.com
/// plugin registry, and the verified install/update job with rollback.
/// </summary>
public sealed class CaddyBinaryManager(
    AppPaths paths,
    IStore store,
    IJobRunner jobs,
    OutboundHttp http,
    IServiceProvider services,
    ILogger<CaddyBinaryManager> logger) : ICaddyBinaryManager
{
    public const string JobKind = "caddy-install";
    public const string MetadataFileName = "caddy-install.json";
    public const string GitHubLatestUrl = "https://api.github.com/repos/caddyserver/caddy/releases/latest";
    public const string PackagesUrl = "https://caddyserver.com/api/packages";

    private static readonly TimeSpan LatestCacheTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan LatestFailureBackoff = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CatalogCacheTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan AdminWait = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions MetadataJson = new(JsonDefaults.Storage) { WriteIndented = true };

    private readonly SemaphoreSlim _inspectGate = new(1, 1);
    private (DateTime WriteTime, long Size)? _installedKey;
    private InstalledBinary? _installed;

    private readonly SemaphoreSlim _latestGate = new(1, 1);
    private ReleaseInfo? _latest;
    private DateTime _latestFetchedAt;
    private DateTime _latestFailedAt;
    private DateTime? _rateLimitResetAt;

    private readonly SemaphoreSlim _catalogGate = new(1, 1);
    private List<PluginPackage>? _catalog;
    private DateTime _catalogFetchedAt;

    private readonly Lock _jobLock = new();

    private string MetadataFile => Path.Combine(paths.CaddyBinDir, MetadataFileName);

    // ------------------------------------------------------------------ installed binary

    public async Task<InstalledBinary?> GetInstalledAsync(CancellationToken ct = default)
    {
        var fi = new FileInfo(paths.CaddyExe);
        if (!fi.Exists) return null;
        var key = (fi.LastWriteTimeUtc, fi.Length);
        if (_installedKey == key && _installed is not null) return _installed;

        await _inspectGate.WaitAsync(ct);
        try
        {
            fi.Refresh();
            if (!fi.Exists) return null;
            key = (fi.LastWriteTimeUtc, fi.Length);
            if (_installedKey == key && _installed is not null) return _installed;

            var (version, modules) = await InspectAsync(paths.CaddyExe, ct);
            var meta = ReadMetadata();
            var installed = new InstalledBinary
            {
                Version = version,
                Path = paths.CaddyExe,
                InstalledAt = meta is not null && meta.FileSize == fi.Length ? meta.InstalledAt : fi.LastWriteTimeUtc,
                Plugins = CaddyOutputParser.PluginPackages(modules),
                Modules = modules.Select(m => m.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            };
            _installed = installed;
            _installedKey = key;
            logger.LogDebug("Inspected Caddy binary: {Version}, {Modules} modules, plugins: {Plugins}",
                version, installed.Modules.Count, string.Join(", ", installed.Plugins));
            return installed;
        }
        finally
        {
            _inspectGate.Release();
        }
    }

    /// <summary>Runs `version` and `list-modules` against a binary.</summary>
    internal async Task<(string Version, List<CaddyModuleInfo> Modules)> InspectAsync(string exe, CancellationToken ct)
    {
        var v = await RunBinaryAsync(exe, ["version"], null, TimeSpan.FromSeconds(30), ct);
        if (v.ExitCode != 0)
            throw new InvalidOperationException($"'{exe} version' failed with exit code {v.ExitCode}: {Trim(v.Combined)}");
        var version = CaddyOutputParser.ParseVersion(v.StdOut) ?? CaddyOutputParser.ParseVersion(v.Combined)
            ?? throw new InvalidOperationException($"Could not read the version from '{exe} version' output: {Trim(v.Combined)}");

        List<CaddyModuleInfo>? modules = null;
        var json = await RunBinaryAsync(exe, ["list-modules", "--json"], null, TimeSpan.FromSeconds(30), ct);
        if (json.ExitCode == 0)
        {
            try
            {
                modules = CaddyOutputParser.ParseModulesJson(json.StdOut);
            }
            catch (Exception ex) when (ex is JsonException or FormatException)
            {
                logger.LogDebug(ex, "list-modules --json output could not be parsed; falling back to text output");
            }
        }
        if (modules is null)
        {
            var text = await RunBinaryAsync(exe, ["list-modules", "--packages"], null, TimeSpan.FromSeconds(30), ct);
            if (text.ExitCode != 0)
                throw new InvalidOperationException($"'{exe} list-modules' failed with exit code {text.ExitCode}: {Trim(text.Combined)}");
            modules = CaddyOutputParser.ParseModulesText(text.StdOut);
        }
        return (version, modules);
    }

    private void InvalidateInstalled()
    {
        _installed = null;
        _installedKey = null;
    }

    public InstallMetadata? ReadMetadata()
    {
        try
        {
            return File.Exists(MetadataFile)
                ? JsonSerializer.Deserialize<InstallMetadata>(File.ReadAllText(MetadataFile), MetadataJson)
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read {File}", MetadataFile);
            return null;
        }
    }

    private void WriteMetadata(InstallMetadata meta)
    {
        var tmp = MetadataFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(meta, MetadataJson));
        File.Move(tmp, MetadataFile, overwrite: true);
    }

    // ------------------------------------------------------------------ latest release

    public async Task<ReleaseInfo?> GetLatestAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && _latest is not null && DateTime.UtcNow - _latestFetchedAt < LatestCacheTtl) return _latest;
        await _latestGate.WaitAsync(ct);
        try
        {
            if (!force)
            {
                if (_latest is not null && DateTime.UtcNow - _latestFetchedAt < LatestCacheTtl) return _latest;
                // Do not hammer GitHub after a failure / while rate limited.
                if (DateTime.UtcNow - _latestFailedAt < LatestFailureBackoff || _rateLimitResetAt > DateTime.UtcNow)
                    return _latest ?? FromSettings();
            }
            try
            {
                var latest = await FetchLatestAsync(ct);
                _latest = latest;
                _latestFetchedAt = DateTime.UtcNow;
                _rateLimitResetAt = null;
                var s = store.GetSettings<BinarySettings>();
                s.LastCheckedAt = DateTime.UtcNow;
                s.LatestKnownVersion = latest.Version;
                store.SaveSettings(s);
                return latest;
            }
            catch (Exception ex) when (!force && !ct.IsCancellationRequested)
            {
                _latestFailedAt = DateTime.UtcNow;
                logger.LogWarning("Checking GitHub for the latest Caddy release failed: {Error}", ex.Message);
                return _latest ?? FromSettings();
            }
            catch (Exception) when (force && !ct.IsCancellationRequested)
            {
                _latestFailedAt = DateTime.UtcNow;
                throw;
            }
        }
        finally
        {
            _latestGate.Release();
        }
    }

    private ReleaseInfo? FromSettings()
    {
        var known = store.GetSettings<BinarySettings>().LatestKnownVersion;
        return string.IsNullOrEmpty(known)
            ? null
            : new ReleaseInfo { Version = known, Url = $"https://github.com/caddyserver/caddy/releases/tag/{known}" };
    }

    private async Task<ReleaseInfo> FetchLatestAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, GitHubLatestUrl);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        var token = Environment.GetEnvironmentVariable("CM_GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        HttpResponseMessage resp;
        try
        {
            resp = await http.Client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("GitHub (api.github.com) did not answer within 30s. Check outbound HTTPS access or configure an outbound proxy.");
        }
        catch (HttpRequestException ex)
        {
            throw new HttpRequestException($"Could not reach api.github.com: {ex.Message}. Check DNS/firewall or configure an outbound proxy.", ex);
        }
        using (resp)
        {
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                var remaining = Header(resp, "X-RateLimit-Remaining");
                if (resp.StatusCode == HttpStatusCode.TooManyRequests || remaining == "0" || body.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
                {
                    DateTime? reset = long.TryParse(Header(resp, "X-RateLimit-Reset"), out var epoch)
                        ? DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime
                        : resp.Headers.RetryAfter?.Delta is { } d ? DateTime.UtcNow + d : null;
                    _rateLimitResetAt = reset ?? DateTime.UtcNow.AddMinutes(30);
                    throw new InvalidOperationException(
                        "GitHub API rate limit exceeded for this server's public IP (60 requests/hour without a token)" +
                        (reset is { } r ? $"; it resets at {r:yyyy-MM-dd HH:mm} UTC" : "") +
                        ". Try again later, or set the CM_GITHUB_TOKEN environment variable for the manager service.");
                }
            }
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"GitHub returned HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} for the latest Caddy release: {Trim(body, 300)}");
            try
            {
                return CaddyOutputParser.ParseGitHubRelease(body);
            }
            catch (Exception ex) when (ex is JsonException or FormatException)
            {
                throw new InvalidOperationException($"Unexpected response from GitHub: {ex.Message}", ex);
            }
        }
    }

    private static string? Header(HttpResponseMessage resp, string name) =>
        resp.Headers.TryGetValues(name, out var v) ? v.FirstOrDefault() : null;

    // ------------------------------------------------------------------ overview

    public async Task<BinaryOverview> GetOverviewAsync(CancellationToken ct = default)
    {
        var settings = store.GetSettings<BinarySettings>();
        InstalledBinary? installed = null;
        try
        {
            installed = await GetInstalledAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Could not inspect the installed Caddy binary: {Error}", ex.Message);
        }
        var latest = await GetLatestAsync(false, ct);
        settings = store.GetSettings<BinarySettings>(); // LastCheckedAt may have been updated
        var desired = settings.Plugins.Select(CaddyOutputParser.PackageWithoutVersion)
            .Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var outOfSync = installed is not null &&
                        !new HashSet<string>(installed.Plugins, StringComparer.OrdinalIgnoreCase).SetEquals(desired);
        return new BinaryOverview
        {
            Installed = installed,
            Latest = latest,
            UpdateAvailable = installed is not null && latest is not null && CaddyVersion.IsNewer(latest.Version, installed.Version),
            LastCheckedAt = settings.LastCheckedAt,
            DesiredPlugins = settings.Plugins.ToList(),
            PluginsOutOfSync = outOfSync,
            Platform = CaddyPlatform.Current.ToString(),
        };
    }

    // ------------------------------------------------------------------ plugin catalog

    public async Task<List<PluginPackage>> GetPluginCatalogAsync(string? query = null, CancellationToken ct = default)
    {
        var all = await GetCatalogAsync(ct);
        IEnumerable<PluginPackage> items = all;
        var terms = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length > 0)
            items = items.Where(p => terms.All(t =>
                p.Path.Contains(t, StringComparison.OrdinalIgnoreCase)
                || (p.Repo?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false)
                || p.Modules.Any(m => m.Contains(t, StringComparison.OrdinalIgnoreCase))));
        return items.OrderByDescending(p => p.Downloads).ThenBy(p => p.Path, StringComparer.OrdinalIgnoreCase).Take(200).ToList();
    }

    internal async Task<List<PluginPackage>> GetCatalogAsync(CancellationToken ct)
    {
        if (_catalog is not null && DateTime.UtcNow - _catalogFetchedAt < CatalogCacheTtl) return _catalog;
        await _catalogGate.WaitAsync(ct);
        try
        {
            if (_catalog is not null && DateTime.UtcNow - _catalogFetchedAt < CatalogCacheTtl) return _catalog;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(45));
                using var resp = await http.Client.GetAsync(PackagesUrl, cts.Token);
                var body = await resp.Content.ReadAsStringAsync(cts.Token);
                if (!resp.IsSuccessStatusCode)
                    throw new HttpRequestException($"caddyserver.com returned HTTP {(int)resp.StatusCode} for the package list: {Trim(body, 300)}");
                _catalog = CaddyOutputParser.ParsePackageCatalog(body);
                _catalogFetchedAt = DateTime.UtcNow;
                logger.LogInformation("Loaded {Count} Caddy plugin packages from caddyserver.com", _catalog.Count);
                return _catalog;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                if (_catalog is not null)
                {
                    logger.LogWarning("Refreshing the plugin catalog failed ({Error}); using the cached copy", ex.Message);
                    _catalogFetchedAt = DateTime.UtcNow - CatalogCacheTtl + TimeSpan.FromMinutes(10); // retry in 10 minutes
                    return _catalog;
                }
                throw new InvalidOperationException($"Could not load the plugin catalog from caddyserver.com: {ex.Message}", ex);
            }
        }
        finally
        {
            _catalogGate.Release();
        }
    }

    // ------------------------------------------------------------------ run caddy

    public async Task<(int ExitCode, string Output)> RunCaddyAsync(IEnumerable<string> args, string? stdin = null, CancellationToken ct = default)
    {
        if (!File.Exists(paths.CaddyExe))
            throw new FileNotFoundException($"The Caddy binary is not installed ({paths.CaddyExe}).", paths.CaddyExe);
        var r = await RunBinaryAsync(paths.CaddyExe, args, stdin, TimeSpan.FromMinutes(2), ct);
        return (r.ExitCode, r.Combined);
    }

    private Task<ProcessResult> RunBinaryAsync(string exe, IEnumerable<string> args, string? stdin, TimeSpan timeout, CancellationToken ct) =>
        ProcessRunner.RunAsync(exe, args, new ProcessOptions
        {
            StdIn = stdin,
            Timeout = timeout,
            Environment = CaddyHostSupport.CaddyEnvironment(paths),
            WorkingDirectory = paths.CaddyBinDir,
        }, ct);

    // ------------------------------------------------------------------ install / update

    public JobInfo StartInstallOrUpdate(string? version = null) =>
        StartInstall(version, pluginsOverride: null, "requested");

    /// <summary>
    /// Starts the install job. <paramref name="pluginsOverride"/> = [] forces a vanilla build (used by the bootstrapper).
    /// Throws InvalidOperationException when a job is already running and ArgumentException for a malformed version.
    /// </summary>
    public JobInfo StartInstall(string? version, IReadOnlyList<string>? pluginsOverride, string reason)
    {
        CaddyVersion? requested = null;
        if (!string.IsNullOrWhiteSpace(version))
        {
            if (!CaddyVersion.TryParse(version, out requested))
                throw new ArgumentException($"'{version}' is not a valid Caddy version (expected e.g. v2.11.4).", nameof(version));
        }
        var plugins = (pluginsOverride ?? store.GetSettings<BinarySettings>().Plugins)
            .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var title = plugins.Count > 0
            ? $"Build Caddy with {plugins.Count} plugin(s)"
            : requested is not null ? $"Install Caddy {requested}" : "Install latest Caddy";

        lock (_jobLock)
        {
            if (jobs.IsRunning(JobKind))
                throw new InvalidOperationException("A Caddy install/update job is already running. Wait for it to finish.");
            logger.LogInformation("Starting Caddy install job ({Reason}): {Title}", reason, title);
            return jobs.Start(JobKind, title, (log, _) => InstallAsync(requested, plugins, log));
        }
    }

    internal async Task InstallAsync(CaddyVersion? requested, IReadOnlyList<string> plugins, Action<string> log)
    {
        using var overall = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var ct = overall.Token;
        var platform = CaddyPlatform.Current;
        Directory.CreateDirectory(paths.CaddyStagingDir);
        Directory.CreateDirectory(paths.CaddyBinDir);
        var stagingDir = Path.Combine(paths.CaddyStagingDir, Entity.NewId());
        Directory.CreateDirectory(stagingDir);
        var staged = Path.Combine(stagingDir, platform.BinaryName);
        var sink = services.GetService<IEventSink>();
        var swapped = false;
        try
        {
            string source, url;
            string? sha = null;
            string? expectedVersion = null;
            if (plugins.Count == 0)
            {
                var version = requested;
                if (version is null)
                {
                    log("Looking up the latest Caddy release on GitHub");
                    var latest = await GetLatestAsync(force: true, ct)
                                 ?? throw new InvalidOperationException("GitHub did not return a latest Caddy release.");
                    version = CaddyVersion.Parse(latest.Version);
                }
                expectedVersion = version.ToString();
                var asset = platform.ReleaseAssetName(version);
                log($"Installing Caddy {version} for {platform} from the official GitHub release ({asset})");

                var checksumsUrl = CaddyPlatform.ReleaseDownloadUrl(version, CaddyPlatform.ChecksumsAssetName(version));
                log("Downloading " + checksumsUrl);
                var checksumsText = await GetStringAsync(checksumsUrl, $"Caddy release {version} (checksums file)", ct);
                var sums = CaddyOutputParser.ParseChecksums(checksumsText);
                if (!sums.TryGetValue(asset, out var expectedSha))
                    throw new InvalidOperationException($"Caddy release {version} has no build for {platform} ({asset} is not listed in the checksums file).");

                url = CaddyPlatform.ReleaseDownloadUrl(version, asset);
                var archive = Path.Combine(stagingDir, asset);
                await DownloadAsync(url, archive, log, TimeSpan.FromMinutes(15), ct);
                sha = await Sha512Async(archive, ct);
                if (!string.Equals(sha, expectedSha, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"SHA-512 checksum mismatch for {asset} (expected {expectedSha[..16]}…, got {sha[..16]}…). The download was corrupted or tampered with; nothing was changed.");
                log("SHA-512 checksum verified");
                ExtractBinary(archive, platform, staged);
                source = "github-release";
            }
            else
            {
                if (requested is not null)
                    log($"Note: builds with plugins always use the latest Caddy release; the requested version {requested} is ignored.");
                url = platform.BuildServerUrl(plugins);
                log($"Requesting a custom build for {platform} with: {string.Join(", ", plugins)}");
                log("The caddyserver.com build server compiles the binary on demand; this can take a few minutes.");
                await DownloadAsync(url, staged, log, TimeSpan.FromMinutes(20), ct);
                source = "caddyserver-build";
                sha = await Sha512Async(staged, ct);
            }

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(staged, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                             UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                             UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            log("Checking the downloaded binary");
            var (stagedVersion, modules) = await InspectAsync(staged, ct);
            log($"Downloaded binary reports {stagedVersion} with {modules.Count} modules");
            if (expectedVersion is not null && CaddyVersion.Compare(stagedVersion, expectedVersion) != 0)
                throw new InvalidOperationException($"The downloaded binary reports version {stagedVersion}, expected {expectedVersion}.");
            var stagedPlugins = new HashSet<string>(CaddyOutputParser.PluginPackages(modules), StringComparer.OrdinalIgnoreCase);
            var missing = plugins.Select(CaddyOutputParser.PackageWithoutVersion).Where(p => !stagedPlugins.Contains(p)).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException($"The custom build does not contain the requested plugin(s): {string.Join(", ", missing)}.");

            if (File.Exists(paths.CaddyConfigFile))
            {
                log("Validating the current configuration with the new binary");
                var val = await RunBinaryAsync(staged, ["validate", "--config", paths.CaddyConfigFile], null, TimeSpan.FromSeconds(90), ct);
                if (val.ExitCode != 0)
                    throw new InvalidOperationException(
                        $"The new Caddy binary rejected the current configuration, so nothing was changed:\n{Trim(val.Combined, 2000)}");
                log("Configuration is valid with the new binary");
            }

            var host = services.GetService<ICaddyHost>();
            // From here on only this job may start Caddy (see CaddyHostSupport.BeginBinarySwap).
            using var swapGuard = services.GetService<CaddyHostSupport>()?.BeginBinarySwap();
            var hadBinary = File.Exists(paths.CaddyExe);
            string? oldVersion = null;
            var wasRunning = false;
            if (hadBinary)
            {
                try { oldVersion = (await GetInstalledAsync(ct))?.Version; }
                catch (Exception ex) when (ex is not OperationCanceledException) { log($"Warning: could not read the current version: {ex.Message}"); }
                if (host is not null)
                {
                    var st = await host.GetStatusAsync(ct);
                    wasRunning = st.State is CaddyRunState.Running or CaddyRunState.Starting;
                    if (wasRunning)
                    {
                        log("Stopping Caddy");
                        await host.StopAsync(ct);
                    }
                }
            }

            var previousMeta = ReadMetadata();
            if (hadBinary)
            {
                log($"Keeping the current binary ({oldVersion ?? "unknown version"}) as {Path.GetFileName(paths.CaddyExeBackup)} for rollback");
                await MoveWithRetryAsync(paths.CaddyExe, paths.CaddyExeBackup, ct);
            }
            await MoveWithRetryAsync(staged, paths.CaddyExe, ct);
            swapped = true;
            InvalidateInstalled();
            WriteMetadata(new InstallMetadata
            {
                Version = stagedVersion,
                InstalledAt = DateTime.UtcNow,
                Source = source,
                Url = url,
                Sha512 = sha,
                Plugins = CaddyOutputParser.PluginPackages(modules),
                Platform = platform.ToString(),
                FileSize = new FileInfo(paths.CaddyExe).Length,
            });
            log($"Installed {stagedVersion} to {paths.CaddyExe}");

            if (host is not null && (wasRunning || !hadBinary))
            {
                try
                {
                    log("Starting Caddy");
                    await host.StartAsync(ct);
                    if (!await WaitForAdminAsync(ct))
                        throw new InvalidOperationException($"Caddy started but its admin API did not respond within {AdminWait.TotalSeconds:0}s.");
                    log("Caddy is running and its admin API responds");
                }
                catch (Exception ex) when (hadBinary && ex is not OperationCanceledException)
                {
                    log("ERROR: " + ex.Message);
                    log("Rolling back to the previous binary");
                    await RollbackAsync(host, previousMeta, log, ct);
                    sink?.Raise(EventSeverity.Error, "update",
                        $"Caddy update to {stagedVersion} failed and was rolled back to {oldVersion ?? "the previous version"}",
                        ex.Message, key: "caddy-update-failed", alertRule: "updateAvailable");
                    throw new InvalidOperationException($"Caddy {stagedVersion} failed to start; the previous version was restored. {ex.Message}", ex);
                }

                if (!hadBinary && services.GetService<ICaddyConfigService>() is { } config)
                {
                    log("Applying the managed configuration");
                    var r = await config.ApplyAsync("caddy installed", ct);
                    log(r.Success ? "Configuration applied" : $"Warning: applying the configuration failed: {r.Error}");
                }
            }
            else if (hadBinary && !wasRunning)
            {
                log("Caddy was not running before the update, so it was left stopped.");
            }

            sink?.Raise(EventSeverity.Info, "update",
                hadBinary ? $"Caddy updated from {oldVersion ?? "unknown"} to {stagedVersion}" : $"Caddy {stagedVersion} installed",
                plugins.Count > 0 ? "Plugins: " + string.Join(", ", plugins) : null);
            log("Done");
        }
        catch (Exception ex) when (!swapped)
        {
            sink?.Raise(EventSeverity.Error, "update", "Installing Caddy failed; the current installation was not changed", ex.Message,
                key: "caddy-update-failed", alertRule: "updateAvailable");
            throw;
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Could not clean up staging directory {Dir}: {Error}", stagingDir, ex.Message);
            }
        }
    }

    private async Task RollbackAsync(ICaddyHost host, InstallMetadata? previousMeta, Action<string> log, CancellationToken ct)
    {
        try
        {
            await host.StopAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log("Warning: stopping the failed Caddy returned: " + ex.Message);
        }
        if (!File.Exists(paths.CaddyExeBackup))
        {
            log("ERROR: no previous binary to restore");
            return;
        }
        await MoveWithRetryAsync(paths.CaddyExeBackup, paths.CaddyExe, ct);
        InvalidateInstalled();
        if (previousMeta is not null) WriteMetadata(previousMeta);
        else if (File.Exists(MetadataFile)) File.Delete(MetadataFile);
        log("Previous binary restored; starting Caddy");
        try
        {
            await host.StartAsync(ct);
            log(await WaitForAdminAsync(ct) ? "Caddy is running again with the previous binary" : "Warning: the admin API of the restored Caddy does not respond");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log("ERROR: the previous binary did not start either: " + ex.Message);
            services.GetService<IEventSink>()?.Raise(EventSeverity.Error, "caddy",
                "Caddy is down after a failed update and rollback", ex.Message, key: "caddy-down", alertRule: "caddyDown");
        }
    }

    private async Task<bool> WaitForAdminAsync(CancellationToken ct)
    {
        var admin = services.GetService<ICaddyAdminClient>();
        if (admin is null) return true;
        var deadline = DateTime.UtcNow + AdminWait;
        while (DateTime.UtcNow < deadline)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                if (await admin.IsReachableAsync(cts.Token)) return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // not up yet
            }
            await Task.Delay(500, ct);
        }
        return false;
    }

    /// <summary>
    /// If no binary is installed and a development binary exists (.dev/bin/caddy relative to the working
    /// directory or its parents, or CM_DEV_CADDY), copies it into AppPaths.CaddyExe. Returns true when copied.
    /// </summary>
    public bool TryCopyDevBinary()
    {
        if (File.Exists(paths.CaddyExe)) return false;
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("CM_DEV_CADDY") is { Length: > 0 } env) candidates.Add(env);
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            for (var i = 0; dir is not null && i < 8; i++, dir = dir.Parent)
                candidates.Add(Path.Combine(dir.FullName, ".dev", "bin", CaddyPlatform.Current.BinaryName));
        }
        var found = candidates.FirstOrDefault(File.Exists);
        if (found is null) return false;
        Directory.CreateDirectory(paths.CaddyBinDir);
        File.Copy(found, paths.CaddyExe, overwrite: false);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(paths.CaddyExe, File.GetUnixFileMode(paths.CaddyExe) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        WriteMetadata(new InstallMetadata
        {
            Version = "", InstalledAt = DateTime.UtcNow, Source = "dev-copy", Url = found,
            Platform = CaddyPlatform.Current.ToString(), FileSize = new FileInfo(paths.CaddyExe).Length,
        });
        InvalidateInstalled();
        logger.LogInformation("Copied development Caddy binary {Source} to {Target}", found, paths.CaddyExe);
        return true;
    }

    // ------------------------------------------------------------------ download helpers

    private async Task<string> GetStringAsync(string url, string what, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        using var resp = await http.Client.GetAsync(url, cts.Token);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException($"{what} was not found at {url}. Check that the version exists on https://github.com/caddyserver/caddy/releases.");
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Downloading {what} failed: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
        return await resp.Content.ReadAsStringAsync(cts.Token);
    }

    private async Task DownloadAsync(string url, string destination, Action<string> log, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        log("Downloading " + url);
        HttpResponseMessage resp;
        try
        {
            resp = await http.Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Download from {new Uri(url).Host} timed out after {timeout.TotalMinutes:0} minutes.");
        }
        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(cts.Token);
                throw new HttpRequestException($"Download failed: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} from {new Uri(url).Host}. {Trim(body, 500)}".Trim());
            }
            var total = resp.Content.Headers.ContentLength;
            await using var src = await resp.Content.ReadAsStreamAsync(cts.Token);
            await using var dst = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            long done = 0, nextReport = 0;
            int read;
            try
            {
                while ((read = await src.ReadAsync(buffer, cts.Token)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                    done += read;
                    if (done >= nextReport)
                    {
                        log(total is > 0
                            ? $"  {done / 1048576.0:0.0} / {total.Value / 1048576.0:0.0} MB ({done * 100 / total.Value}%)"
                            : $"  {done / 1048576.0:0.0} MB");
                        nextReport = done + (total is > 0 ? Math.Max(total.Value / 5, 1) : 10 * 1048576);
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Download from {new Uri(url).Host} timed out after {timeout.TotalMinutes:0} minutes.");
            }
            if (total is > 0 && done != total)
                throw new IOException($"Download incomplete: received {done} of {total} bytes.");
            log($"Downloaded {done / 1048576.0:0.0} MB");
        }
    }

    private static async Task<string> Sha512Async(string file, CancellationToken ct)
    {
        await using var fs = File.OpenRead(file);
        var hash = await SHA512.HashDataAsync(fs, ct);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Extracts caddy(.exe) from a release archive (.zip on Windows, .tar.gz elsewhere).</summary>
    internal static void ExtractBinary(string archive, CaddyPlatform platform, string destination)
    {
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(archive);
            var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals(platform.BinaryName, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"{Path.GetFileName(archive)} does not contain {platform.BinaryName}.");
            entry.ExtractToFile(destination, overwrite: true);
            return;
        }
        using var fs = File.OpenRead(archive);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var tar = new TarReader(gz);
        while (tar.GetNextEntry() is { } e)
        {
            if (e.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) continue;
            var name = e.Name.Replace('\\', '/');
            if (name == platform.BinaryName || name.EndsWith("/" + platform.BinaryName, StringComparison.Ordinal))
            {
                e.ExtractToFile(destination, overwrite: true);
                return;
            }
        }
        throw new InvalidOperationException($"{Path.GetFileName(archive)} does not contain {platform.BinaryName}.");
    }

    /// <summary>File.Move with retries: antivirus scanners briefly lock freshly written executables on Windows.</summary>
    private static async Task MoveWithRetryAsync(string from, string to, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(from, to, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 20)
            {
                await Task.Delay(500, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"Could not move {from} to {to}: {ex.Message}. Is another process (antivirus, a running caddy) holding the file?", ex);
            }
        }
    }

    private static string Trim(string s, int max = 1000)
    {
        s = s.Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }
}
