using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text.Json;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using CaddyManager.Platform.Background;
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
    /// <summary>"github-release" | "caddyserver-build" | "upload" | "dev-copy" ("rollback" when the origin of a restored binary is unknown)</summary>
    public string Source { get; init; } = "";
    public string? Url { get; init; }
    public string? Sha512 { get; init; }
    public List<string> Plugins { get; init; } = new();
    public string Platform { get; init; } = "";
    public long FileSize { get; init; }
}

/// <summary>
/// Manages the Caddy binary: inspection (version / modules), GitHub release checks (Caddy and the manager itself), the
/// caddyserver.com plugin registry, and the verified install/update/upload/rollback job.
/// </summary>
public sealed partial class CaddyBinaryManager(
    AppPaths paths,
    IStore store,
    IJobRunner jobs,
    OutboundHttp http,
    IServiceProvider services,
    ILogger<CaddyBinaryManager> logger) : ICaddyBinaryManager
{
    public const string JobKind = "caddy-install";
    public const string MetadataFileName = "caddy-install.json";
    /// <summary>Metadata of caddy(.exe).previous (moved there together with the binary on every swap).</summary>
    public const string PreviousMetadataFileName = "caddy-install.previous.json";
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

    private readonly SemaphoreSlim _previousGate = new(1, 1);
    private (DateTime WriteTime, long Size)? _previousKey;
    private string? _previousVersion;

    private readonly SemaphoreSlim _managerGate = new(1, 1);
    /// <summary>Result of the last manager release check (replaced as a whole, so readers see a consistent snapshot).</summary>
    private volatile ManagerReleaseCache? _manager;

    private readonly Lock _jobLock = new();

    private string MetadataFile => Path.Combine(paths.CaddyBinDir, MetadataFileName);
    private string PreviousMetadataFile => Path.Combine(paths.CaddyBinDir, PreviousMetadataFileName);

    /// <summary>Version of Caddy Proxy Manager itself (informational version of the host exe, without build metadata).</summary>
    public static string ManagerVersion { get; } = ReadManagerVersion();

    private static string ReadManagerVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(CaddyBinaryManager).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info)) return info.Split('+')[0].Trim();
        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
    }

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

    private void WriteMetadata(InstallMetadata meta) => WriteMetadataFile(MetadataFile, meta);

    public InstallMetadata? ReadPreviousMetadata()
    {
        try
        {
            return File.Exists(PreviousMetadataFile)
                ? JsonSerializer.Deserialize<InstallMetadata>(File.ReadAllText(PreviousMetadataFile), MetadataJson)
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read {File}", PreviousMetadataFile);
            return null;
        }
    }

    /// <summary>Writes (or, for null, deletes) the metadata of caddy(.exe).previous.</summary>
    private void WritePreviousMetadata(InstallMetadata? meta)
    {
        if (meta is not null) WriteMetadataFile(PreviousMetadataFile, meta);
        else if (File.Exists(PreviousMetadataFile)) File.Delete(PreviousMetadataFile);
    }

    private static void WriteMetadataFile(string file, InstallMetadata meta)
    {
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(meta, MetadataJson));
        File.Move(tmp, file, overwrite: true);
    }

    // ------------------------------------------------------------------ previous binary (rollback)

    /// <summary>
    /// Version of caddy(.exe).previous, read by running "&lt;previous&gt; version" (cached by file time and size).
    /// Falls back to the stored metadata when it cannot be run. Null when there is no previous binary.
    /// </summary>
    public async Task<string?> GetPreviousVersionAsync(CancellationToken ct = default)
    {
        var fi = new FileInfo(paths.CaddyExeBackup);
        if (!fi.Exists) return null;
        var key = (fi.LastWriteTimeUtc, fi.Length);
        if (_previousKey == key && _previousVersion is not null) return _previousVersion;
        await _previousGate.WaitAsync(ct);
        try
        {
            fi.Refresh();
            if (!fi.Exists) return null;
            key = (fi.LastWriteTimeUtc, fi.Length);
            if (_previousKey == key && _previousVersion is not null) return _previousVersion;
            string? version = null;
            try
            {
                var r = await RunBinaryAsync(paths.CaddyExeBackup, ["version"], null, TimeSpan.FromSeconds(30), ct);
                if (r.ExitCode == 0) version = CaddyOutputParser.ParseVersion(r.StdOut) ?? CaddyOutputParser.ParseVersion(r.Combined);
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
            {
                logger.LogDebug(ex, "Could not run {File} version", paths.CaddyExeBackup);
            }
            version ??= ReadPreviousMetadata()?.Version is { Length: > 0 } v ? v : null;
            _previousVersion = version;
            _previousKey = version is null ? null : key;
            return version;
        }
        finally
        {
            _previousGate.Release();
        }
    }

    private void InvalidatePrevious()
    {
        _previousVersion = null;
        _previousKey = null;
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
                var latest = await FetchGitHubReleaseAsync(GitHubLatestUrl, "the latest Caddy release", "caddyserver/caddy", ct);
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

    /// <summary>
    /// GitHub media type for every release request: the standard JSON plus body_html (GitHub's own rendering of the notes)
    /// and body_text. https://docs.github.com/en/rest/releases/releases#get-the-latest-release
    /// </summary>
    public const string GitHubReleaseMediaType = "application/vnd.github.full+json";

    /// <summary>GET api.github.com/repos/{owner}/{repo}/releases/latest with rate-limit handling.</summary>
    private Task<ReleaseInfo> FetchGitHubReleaseAsync(string url, string what, string repo, CancellationToken ct) =>
        FetchGitHubAsync(url, what, repo, body => CaddyOutputParser.ParseGitHubRelease(body), ct);

    /// <summary>
    /// GET on api.github.com with rate-limit handling (shared by all GitHub checks). <paramref name="parse"/> turns the
    /// response body into the result; JSON / format errors become InvalidOperationException.
    /// </summary>
    private async Task<T> FetchGitHubAsync<T>(string url, string what, string repo, Func<string, T> parse, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd(GitHubReleaseMediaType);
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
            if (resp.StatusCode == HttpStatusCode.NotFound)
                throw new InvalidOperationException(
                    $"The GitHub repository {repo} does not exist or has no published release (HTTP 404 from api.github.com{new Uri(url).AbsolutePath}). " +
                    "Check the repository name (Settings → Updates); drafts, pre-releases and private repositories are not visible.");
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"GitHub returned HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} for {what}: {Trim(body, 300)}");
            try
            {
                return parse(body);
            }
            catch (Exception ex) when (ex is JsonException or FormatException)
            {
                throw new InvalidOperationException($"Unexpected response from GitHub: {ex.Message}", ex);
            }
        }
    }

    private static string? Header(HttpResponseMessage resp, string name) =>
        resp.Headers.TryGetValues(name, out var v) ? v.FirstOrDefault() : null;

    // ------------------------------------------------------------------ manager self-update check

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9\-]{0,38})/[A-Za-z0-9._\-]{1,100}$")]
    private static partial Regex GitHubRepo();

    /// <summary>
    /// Normalises BinarySettings.ManagerReleaseRepo ("owner/repo", also accepts https://github.com/owner/repo[.git]).
    /// Returns null for empty input; throws ArgumentException when it is not a GitHub repository.
    /// </summary>
    public static string? NormalizeReleaseRepo(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = input.Trim();
        if (Uri.TryCreate(s, UriKind.Absolute, out var uri))
        {
            if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && !uri.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"'{s}' is not a github.com repository. Enter it as owner/repo (e.g. my-org/caddy-proxy-manager).");
            s = uri.AbsolutePath.Trim('/');
            var parts = s.Split('/');
            if (parts.Length >= 2) s = parts[0] + "/" + parts[1];
        }
        if (s.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        if (!GitHubRepo().IsMatch(s))
            throw new ArgumentException($"'{input.Trim()}' is not a GitHub repository name. Enter it as owner/repo (e.g. my-org/caddy-proxy-manager).");
        return s;
    }

    /// <summary>The official Caddy Proxy Manager repository, checked when BinarySettings.ManagerReleaseRepo is empty.</summary>
    public const string DefaultManagerReleaseRepo = "Pimzino/caddy-proxy-manager";

    /// <summary>Releases requested per check (one request to the list endpoint; drafts and pre-releases are filtered out).</summary>
    private const int ManagerReleasesPerPage = 30;
    private const int MaxNewerReleases = 20;

    /// <summary>Stable releases of <paramref name="Repo"/>, newest first (null until GitHub answered once).</summary>
    private sealed record ManagerReleaseCache(string Repo, List<ReleaseInfo>? Releases, DateTime FetchedAt, DateTime FailedAt,
        DateTime? CheckedAt, string? Error);

    /// <summary>
    /// The repository the manager checks for its own updates: null (and no error) when BinarySettings.CheckManagerUpdates is
    /// off; the official repository when ManagerReleaseRepo is empty; null with <paramref name="error"/> when the stored
    /// repository name is invalid.
    /// </summary>
    private string? ResolveManagerRepo(BinarySettings settings, out string? error)
    {
        error = null;
        if (!settings.CheckManagerUpdates) return null;
        try { return NormalizeReleaseRepo(settings.ManagerReleaseRepo) ?? DefaultManagerReleaseRepo; }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Stable releases of <paramref name="repo"/>, newest first (one request to /releases?per_page=30), cached for an hour.
    /// Without <paramref name="force"/> failures are logged, remembered and the cached list returned (after a failure or while
    /// GitHub rate-limits this server no request is made for a while). With <paramref name="force"/> GitHub is always asked and
    /// a failure is remembered and thrown.
    /// </summary>
    private async Task<ManagerReleaseCache> GetManagerReleasesAsync(string repo, bool force, CancellationToken ct)
    {
        var cache = _manager;
        if (!force && cache?.Repo == repo && cache.Releases is not null && DateTime.UtcNow - cache.FetchedAt < LatestCacheTtl) return cache;
        await _managerGate.WaitAsync(ct);
        try
        {
            cache = _manager;
            if (cache?.Repo != repo) cache = new ManagerReleaseCache(repo, null, default, default, null, null);
            if (!force)
            {
                if (cache.Releases is not null && DateTime.UtcNow - cache.FetchedAt < LatestCacheTtl) return _manager = cache;
                if (DateTime.UtcNow - cache.FailedAt < LatestFailureBackoff) return _manager = cache;
                if (_rateLimitResetAt is { } reset && reset > DateTime.UtcNow)
                    return _manager = cache.Releases is null && cache.Error is null
                        ? cache with { Error = $"Not checked yet: the GitHub API rate limit for this server resets at {reset:yyyy-MM-dd HH:mm} UTC." }
                        : cache;
            }
            try
            {
                var releases = await FetchGitHubAsync(
                    $"https://api.github.com/repos/{repo}/releases?per_page={ManagerReleasesPerPage}", $"the releases of {repo}", repo,
                    body => CaddyOutputParser.ParseGitHubReleaseList(body), ct);
                if (releases.Count == 0)
                    throw new InvalidOperationException(
                        $"The GitHub repository {repo} has no published release (drafts and pre-releases are not counted).");
                var now = DateTime.UtcNow;
                _rateLimitResetAt = null;
                return _manager = cache with
                {
                    Releases = SortNewestFirst(releases.Select(r => r with { Version = TrimV(r.Version) })),
                    FetchedAt = now, FailedAt = default, CheckedAt = now, Error = null,
                };
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _manager = cache with { FailedAt = DateTime.UtcNow, Error = ex.Message };
                if (force) throw;
                logger.LogWarning("Checking GitHub ({Repo}) for a new Caddy Proxy Manager release failed: {Error}", repo, ex.Message);
                return _manager;
            }
        }
        finally
        {
            _managerGate.Release();
        }
    }

    private static string TrimV(string version) =>
        version.StartsWith('v') || version.StartsWith('V') ? version[1..] : version;

    /// <summary>Newest version first; tags that are not versions go last (in GitHub's order, which is newest first).</summary>
    private static List<ReleaseInfo> SortNewestFirst(IEnumerable<ReleaseInfo> releases) =>
        releases.Select((r, i) => (Release: r, Index: i, Ok: CaddyVersion.TryParse(r.Version, out var v), V: v))
            .OrderByDescending(x => x.Ok)
            .ThenByDescending(x => x.V)
            .ThenBy(x => x.Index)
            .Select(x => x.Release)
            .ToList();

    /// <summary>
    /// Newest stable release of the manager itself (null when checks are off, the repository setting is invalid or GitHub
    /// has not answered yet). Cached like the Caddy check; without <paramref name="force"/> failures are logged and the cached
    /// value returned; with it they are thrown.
    /// </summary>
    public async Task<ReleaseInfo?> GetManagerLatestAsync(bool force = false, CancellationToken ct = default)
    {
        var repo = ResolveManagerRepo(store.GetSettings<BinarySettings>(), out var error);
        if (error is not null) logger.LogWarning("Manager update check skipped: {Error}", error);
        if (repo is null) return null;
        return (await GetManagerReleasesAsync(repo, force, ct)).Releases?.FirstOrDefault();
    }

    /// <summary>
    /// GET /api/system/manager-update: newest stable release and the changelog since the installed version. Never throws for
    /// GitHub failures: they are reported in <see cref="ManagerUpdateInfo.Error"/> together with the last successful answer.
    /// </summary>
    public async Task<ManagerUpdateInfo> GetManagerUpdateAsync(bool force, CancellationToken ct = default)
    {
        var settings = store.GetSettings<BinarySettings>();
        var current = TrimV(ManagerVersion);
        if (!settings.CheckManagerUpdates)
        {
            string? configured = null;
            try { configured = NormalizeReleaseRepo(settings.ManagerReleaseRepo) ?? DefaultManagerReleaseRepo; }
            catch (ArgumentException) { /* reported once checks are switched on */ }
            return new ManagerUpdateInfo
            {
                Enabled = false, Repo = configured, RepoIsDefault = string.Equals(configured, DefaultManagerReleaseRepo, StringComparison.OrdinalIgnoreCase),
                ReleasesUrl = configured is null ? null : $"https://github.com/{configured}/releases", CurrentVersion = current,
            };
        }
        var repo = ResolveManagerRepo(settings, out var error);
        if (repo is null) return new ManagerUpdateInfo { Enabled = true, CurrentVersion = current, Error = error };

        ManagerReleaseCache cache;
        try
        {
            cache = await GetManagerReleasesAsync(repo, force, ct);
        }
        catch (Exception) when (force && !ct.IsCancellationRequested)
        {
            cache = _manager is { } c && c.Repo == repo ? c : new ManagerReleaseCache(repo, null, default, default, null, "The check failed.");
        }
        var releases = cache.Releases ?? [];
        var newer = releases.Where(r => CaddyVersion.IsNewer(r.Version, current)).Take(MaxNewerReleases).ToList();
        return new ManagerUpdateInfo
        {
            Enabled = true,
            Repo = repo,
            RepoIsDefault = string.Equals(repo, DefaultManagerReleaseRepo, StringComparison.OrdinalIgnoreCase),
            ReleasesUrl = $"https://github.com/{repo}/releases",
            CurrentVersion = current,
            UpdateAvailable = newer.Count > 0,
            Latest = releases.FirstOrDefault(),
            NewerReleases = newer,
            CheckedAt = cache.CheckedAt,
            Error = cache.Error,
        };
    }

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
        var previousVersion = await GetPreviousVersionAsync(ct);
        var managerLatest = await GetManagerLatestAsync(false, ct);
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
            CanRollback = CanRollback,
            PreviousVersion = previousVersion,
            ManagerVersion = ManagerVersion,
            ManagerLatestVersion = managerLatest?.Version,
            ManagerLatestUrl = managerLatest?.Url,
            ManagerUpdateAvailable = managerLatest is not null && CaddyVersion.IsNewer(managerLatest.Version, ManagerVersion),
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
            Environment = CaddyHostSupport.CaddyEnvironment(paths, store.GetSettings<BinarySettings>()),
            WorkingDirectory = paths.CaddyBinDir,
        }, ct);

    // ------------------------------------------------------------------ install / update / upload / rollback

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
        // The bootstrapper registers the service and applies the configuration itself once its install job is done.
        var postInstall = reason != "bootstrap";
        return StartJob(title, reason, (log, _) => InstallAsync(requested, plugins, log, postInstall));
    }

    /// <summary>
    /// Offline / air-gapped install of a file that was uploaded to (or placed in) <see cref="AppPaths.CaddyStagingDir"/>:
    /// caddy.exe itself or the official Windows release archive (.zip) for this platform. The job owns the file and
    /// deletes it (and its upload directory) when it finishes, successfully or not.
    /// Throws FileNotFoundException, ArgumentException (malformed SHA-512) or InvalidOperationException (job running).
    /// </summary>
    public JobInfo StartInstallFromFile(string stagedFile, string? expectedSha512 = null) =>
        StartInstallFromFile(stagedFile, expectedSha512, displayName: null);

    public JobInfo StartInstallFromFile(string stagedFile, string? expectedSha512, string? displayName)
    {
        if (string.IsNullOrWhiteSpace(stagedFile) || !File.Exists(stagedFile))
            throw new FileNotFoundException($"The uploaded Caddy file was not found ({stagedFile}).", stagedFile);
        try
        {
            var sha = ExecutableFormat.NormalizeSha512(expectedSha512);
            var name = string.IsNullOrWhiteSpace(displayName) ? Path.GetFileName(stagedFile) : displayName.Trim();
            return StartJob($"Install Caddy from uploaded file {name}", "upload",
                (log, _) => InstallFromFileAsync(stagedFile, sha, name, log));
        }
        catch
        {
            DeleteUpload(stagedFile);
            throw;
        }
    }

    /// <summary>
    /// Swaps the previous binary (caddy.exe.previous) back in through the verified pipeline. The binary that was active
    /// becomes the new "previous", so a rollback can itself be undone.
    /// </summary>
    public JobInfo StartRollback()
    {
        if (!CanRollback)
            throw new InvalidOperationException(
                $"There is no previous Caddy binary to roll back to ({Path.GetFileName(paths.CaddyExeBackup)} does not exist). " +
                "A previous binary is kept after every update.");
        return StartJob("Roll back to the previous Caddy binary", "rollback", (log, _) => RollbackToPreviousAsync(log));
    }

    public bool CanRollback => File.Exists(paths.CaddyExeBackup);

    /// <summary>False while an install/update/upload/rollback job runs (only one at a time).</summary>
    public bool CanStartJob => !jobs.IsRunning(JobKind);

    private JobInfo StartJob(string title, string reason, Func<Action<string>, CancellationToken, Task> work)
    {
        lock (_jobLock)
        {
            if (jobs.IsRunning(JobKind))
                throw new InvalidOperationException("A Caddy install/update job is already running. Wait for it to finish.");
            logger.LogInformation("Starting Caddy binary job ({Reason}): {Title}", reason, title);
            return jobs.Start(JobKind, title, work);
        }
    }

    private enum InstallMode { Download, Upload, Rollback }

    /// <summary>A binary in the job's staging directory, ready for the verified swap.</summary>
    private sealed record StagedBinary
    {
        public required string Path { get; init; }
        public required string Source { get; init; }
        public string? Url { get; init; }
        public string? Sha512 { get; init; }
        /// <summary>Fail when the binary reports another version (downloads of a pinned release).</summary>
        public string? ExpectedVersion { get; init; }
        /// <summary>Fail when one of these packages is missing (custom builds).</summary>
        public IReadOnlyList<string> RequiredPlugins { get; init; } = [];
        /// <summary>Human readable origin for events ("uploaded file caddy_2.11.4_windows_amd64.zip").</summary>
        public string? Origin { get; init; }
    }

    internal Task InstallAsync(CaddyVersion? requested, IReadOnlyList<string> plugins, Action<string> log, bool postInstall = true) =>
        RunPipelineAsync(InstallMode.Download, log, postInstall, ownedUpload: null,
            (stagingDir, ct) => DownloadAsync(requested, plugins, stagingDir, log, ct));

    internal Task InstallFromFileAsync(string upload, string? expectedSha512, string displayName, Action<string> log) =>
        RunPipelineAsync(InstallMode.Upload, log, postInstall: true, ownedUpload: upload,
            (stagingDir, ct) => StageUploadAsync(upload, expectedSha512, displayName, stagingDir, log, ct));

    internal Task RollbackToPreviousAsync(Action<string> log) =>
        RunPipelineAsync(InstallMode.Rollback, log, postInstall: true, ownedUpload: null,
            (stagingDir, ct) => StagePreviousAsync(stagingDir, log, ct));

    private async Task<StagedBinary> DownloadAsync(CaddyVersion? requested, IReadOnlyList<string> plugins, string stagingDir,
        Action<string> log, CancellationToken ct)
    {
        var platform = CaddyPlatform.Current;
        var staged = Path.Combine(stagingDir, CaddyPlatform.BinaryName);
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
            var asset = platform.ReleaseAssetName(version);
            log($"Installing Caddy {version} for {platform} from the official GitHub release ({asset})");

            var checksumsUrl = CaddyPlatform.ReleaseDownloadUrl(version, CaddyPlatform.ChecksumsAssetName(version));
            log("Downloading " + checksumsUrl);
            var checksumsText = await GetStringAsync(checksumsUrl, $"Caddy release {version} (checksums file)", ct);
            var sums = CaddyOutputParser.ParseChecksums(checksumsText);
            if (!sums.TryGetValue(asset, out var expectedSha))
                throw new InvalidOperationException($"Caddy release {version} has no build for {platform} ({asset} is not listed in the checksums file).");

            var url = CaddyPlatform.ReleaseDownloadUrl(version, asset);
            var archive = Path.Combine(stagingDir, asset);
            await DownloadFileAsync(url, archive, log, TimeSpan.FromMinutes(15), ct);
            var sha = await Sha512Async(archive, ct);
            if (!string.Equals(sha, expectedSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"SHA-512 checksum mismatch for {asset} (expected {expectedSha[..16]}…, got {sha[..16]}…). The download was corrupted or tampered with; nothing was changed.");
            log("SHA-512 checksum verified");
            ExtractBinary(archive, platform, staged);
            return new StagedBinary { Path = staged, Source = "github-release", Url = url, Sha512 = sha, ExpectedVersion = version.ToString() };
        }

        if (requested is not null)
            log($"Note: builds with plugins always use the latest Caddy release; the requested version {requested} is ignored.");
        var buildUrl = platform.BuildServerUrl(plugins);
        log($"Requesting a custom build for {platform} with: {string.Join(", ", plugins)}");
        log("The caddyserver.com build server compiles the binary on demand; this can take a few minutes.");
        await DownloadFileAsync(buildUrl, staged, log, TimeSpan.FromMinutes(20), ct);
        return new StagedBinary
        {
            Path = staged, Source = "caddyserver-build", Url = buildUrl, Sha512 = await Sha512Async(staged, ct), RequiredPlugins = plugins,
        };
    }

    /// <summary>Checks an uploaded caddy(.exe) / release archive and puts the binary into the job's staging directory.</summary>
    private async Task<StagedBinary> StageUploadAsync(string upload, string? expectedSha512, string displayName, string stagingDir,
        Action<string> log, CancellationToken ct)
    {
        var platform = CaddyPlatform.Current;
        var size = new FileInfo(upload).Length;
        log($"Checking the uploaded file {displayName} ({size / 1048576.0:0.0} MB)");
        var sha = await Sha512Async(upload, ct);
        log($"SHA-512: {sha}");
        if (expectedSha512 is not null)
        {
            if (!string.Equals(sha, expectedSha512, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"SHA-512 checksum mismatch for {displayName}: expected {expectedSha512[..16]}…, the uploaded file has {sha[..16]}…. " +
                    "The file is not the one you expected (corrupted or tampered with); nothing was changed.");
            log("SHA-512 checksum verified");
        }
        else
        {
            log("No expected SHA-512 was given; compare the value above with caddy_<version>_checksums.txt from the release page.");
        }

        var staged = Path.Combine(stagingDir, CaddyPlatform.BinaryName);
        switch (ExecutableFormat.DetectKind(upload))
        {
            case UploadKind.TarGz:
                throw new InvalidOperationException(
                    $"{displayName} is a .tar.gz archive, which Caddy publishes for Linux and macOS. " +
                    $"Upload {CaddyPlatform.BinaryName} or {platform.ReleaseAssetPattern} from https://github.com/caddyserver/caddy/releases.");
            case UploadKind.Zip:
                log($"Extracting {CaddyPlatform.BinaryName} from the release archive");
                ExtractBinary(upload, platform, staged);
                break;
            case UploadKind.Executable:
                File.Copy(upload, staged, overwrite: true);
                break;
            default:
                throw new InvalidOperationException(
                    $"{displayName} is neither a Caddy executable nor a release archive (.zip). " +
                    $"Upload {CaddyPlatform.BinaryName} or {platform.ReleaseAssetPattern} " +
                    "from https://github.com/caddyserver/caddy/releases.");
        }

        var target = ExecutableFormat.Detect(staged)
                     ?? throw new InvalidOperationException($"The {CaddyPlatform.BinaryName} in {displayName} is not a recognised executable.");
        if (!target.Matches(platform))
            throw new InvalidOperationException(
                $"{displayName} contains a Caddy binary for {target}, but this server needs {platform}. " +
                $"Download {platform.ReleaseAssetPattern} instead.");
        log($"Binary format: {target} (matches this server)");
        return new StagedBinary { Path = staged, Source = "upload", Url = displayName, Sha512 = sha, Origin = $"uploaded file {displayName}" };
    }

    /// <summary>Copies caddy(.exe).previous into the job's staging directory (the original stays until the swap).</summary>
    private Task<StagedBinary> StagePreviousAsync(string stagingDir, Action<string> log, CancellationToken ct)
    {
        if (!File.Exists(paths.CaddyExeBackup))
            throw new InvalidOperationException($"There is no previous Caddy binary to roll back to ({paths.CaddyExeBackup} does not exist).");
        var staged = Path.Combine(stagingDir, CaddyPlatform.BinaryName);
        log($"Staging the previous binary {paths.CaddyExeBackup}");
        File.Copy(paths.CaddyExeBackup, staged, overwrite: true);
        var meta = ReadPreviousMetadata();
        return Task.FromResult(new StagedBinary
        {
            Path = staged,
            Source = meta?.Source is { Length: > 0 } s ? s : "rollback",
            Url = meta?.Url,
            Sha512 = meta?.Sha512,
            Origin = "the previous binary",
        });
    }

    /// <summary>
    /// The verified swap shared by every install path: acquire → version / list-modules → validate the current config →
    /// stop Caddy → current becomes .previous → staged moves in → start → wait for the admin API → roll back on failure →
    /// post-install bootstrap (service registration/repair + apply).
    /// </summary>
    private async Task RunPipelineAsync(InstallMode mode, Action<string> log, bool postInstall, string? ownedUpload,
        Func<string, CancellationToken, Task<StagedBinary>> acquire)
    {
        using var overall = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var ct = overall.Token;
        var platform = CaddyPlatform.Current;
        Directory.CreateDirectory(paths.CaddyStagingDir);
        Directory.CreateDirectory(paths.CaddyBinDir);
        var stagingDir = Path.Combine(paths.CaddyStagingDir, Entity.NewId());
        Directory.CreateDirectory(stagingDir);
        var sink = services.GetService<IEventSink>();
        var swapped = false;
        try
        {
            var staged = await acquire(stagingDir, ct);

            log("Checking the new binary (caddy version, caddy list-modules)");
            string stagedVersion;
            List<CaddyModuleInfo> modules;
            try
            {
                (stagedVersion, modules) = await InspectAsync(staged.Path, ct);
            }
            catch (InvalidOperationException ex) when (mode == InstallMode.Upload)
            {
                throw new InvalidOperationException($"The uploaded binary does not run on this server: {ex.Message}", ex);
            }
            var stagedPluginList = CaddyOutputParser.PluginPackages(modules);
            log($"New binary reports {stagedVersion} with {modules.Count} modules" +
                (stagedPluginList.Count > 0 ? $"; plugins: {string.Join(", ", stagedPluginList)}" : "; no plugins"));
            if (staged.ExpectedVersion is not null && CaddyVersion.Compare(stagedVersion, staged.ExpectedVersion) != 0)
                throw new InvalidOperationException($"The downloaded binary reports version {stagedVersion}, expected {staged.ExpectedVersion}.");
            var stagedPlugins = new HashSet<string>(stagedPluginList, StringComparer.OrdinalIgnoreCase);
            var missing = staged.RequiredPlugins.Select(CaddyOutputParser.PackageWithoutVersion).Where(p => !stagedPlugins.Contains(p)).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException($"The custom build does not contain the requested plugin(s): {string.Join(", ", missing)}.");
            if (mode != InstallMode.Download)
            {
                var desired = store.GetSettings<BinarySettings>().Plugins.Select(CaddyOutputParser.PackageWithoutVersion)
                    .Where(p => p.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!desired.SetEquals(stagedPlugins))
                    log($"Note: this binary's plugins ({(stagedPlugins.Count == 0 ? "none" : string.Join(", ", stagedPlugins))}) differ from the desired " +
                        $"plugin list ({(desired.Count == 0 ? "none" : string.Join(", ", desired))}); the Plugins page will show them as out of sync.");
            }

            if (File.Exists(paths.CaddyConfigFile))
            {
                log("Validating the current configuration with the new binary");
                var val = await RunBinaryAsync(staged.Path, ["validate", "--config", paths.CaddyConfigFile], null, TimeSpan.FromSeconds(90), ct);
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
                if (oldVersion is not null && mode == InstallMode.Upload && CaddyVersion.Compare(stagedVersion, oldVersion) < 0)
                    log($"Note: this downgrades Caddy from {oldVersion} to {stagedVersion}.");
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

            var currentMeta = ReadMetadata();
            if (hadBinary)
            {
                log($"Keeping the current binary ({oldVersion ?? "unknown version"}) as {Path.GetFileName(paths.CaddyExeBackup)} for rollback");
                await MoveWithRetryAsync(paths.CaddyExe, paths.CaddyExeBackup, ct);
                WritePreviousMetadata(currentMeta is null ? null : currentMeta with { Version = oldVersion ?? currentMeta.Version });
            }
            await MoveWithRetryAsync(staged.Path, paths.CaddyExe, ct);
            swapped = true;
            InvalidateInstalled();
            InvalidatePrevious();
            WriteMetadata(new InstallMetadata
            {
                Version = stagedVersion,
                InstalledAt = DateTime.UtcNow,
                Source = staged.Source,
                Url = staged.Url,
                Sha512 = staged.Sha512,
                Plugins = stagedPluginList,
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
                    await RestorePreviousAsync(host, currentMeta, log, ct);
                    sink?.Raise(EventSeverity.Error, "update",
                        mode == InstallMode.Rollback
                            ? $"Rolling Caddy back to {stagedVersion} failed; {oldVersion ?? "the version that was running"} was restored"
                            : $"Caddy update to {stagedVersion} failed and was rolled back to {oldVersion ?? "the previous version"}",
                        ex.Message, key: "caddy-update-failed", alertRule: "updateAvailable");
                    throw new InvalidOperationException($"Caddy {stagedVersion} failed to start; the previous version was restored. {ex.Message}", ex);
                }
            }
            else if (hadBinary && !wasRunning)
            {
                log("Caddy was not running before, so it was left stopped.");
            }

            if (postInstall) await PostInstallAsync(log, ct);

            var origin = staged.Origin is null ? null : $" ({staged.Origin})";
            sink?.Raise(EventSeverity.Info, "update",
                mode == InstallMode.Rollback ? $"Caddy rolled back from {oldVersion ?? "unknown"} to {stagedVersion}"
                : hadBinary ? $"Caddy updated from {oldVersion ?? "unknown"} to {stagedVersion}{origin}"
                : $"Caddy {stagedVersion} installed{origin}",
                stagedPluginList.Count > 0 ? "Plugins: " + string.Join(", ", stagedPluginList) : null);
            log("Done");
        }
        catch (Exception ex) when (!swapped)
        {
            sink?.Raise(EventSeverity.Error, "update", mode switch
                {
                    InstallMode.Upload => "Installing the uploaded Caddy binary failed; the current installation was not changed",
                    InstallMode.Rollback => "Rolling back the Caddy binary failed; the current installation was not changed",
                    _ => "Installing Caddy failed; the current installation was not changed",
                }, ex.Message, key: "caddy-update-failed", alertRule: "updateAvailable");
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
            if (ownedUpload is not null) DeleteUpload(ownedUpload);
        }
    }

    /// <summary>
    /// After a successful install from any source: boot config, Windows service registration/repair (binary path,
    /// recovery, environment) and apply the current configuration. Problems are logged, never fatal: the binary is in place.
    /// </summary>
    private async Task PostInstallAsync(Action<string> log, CancellationToken ct)
    {
        try
        {
            if (services.GetService<CaddyBootstrapper>() is { } bootstrapper)
            {
                await bootstrapper.AfterInstallAsync(log, ct);
            }
            else if (services.GetService<ICaddyConfigService>() is { } config)
            {
                log("Applying the managed configuration");
                var r = await config.ApplyAsync("caddy installed", ct);
                log(r.Success ? "Configuration applied" : $"Warning: applying the configuration failed: {r.Error}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log("Warning: post-install steps failed: " + ex.Message);
            logger.LogWarning(ex, "Post-install steps after a Caddy binary change failed");
        }
    }

    /// <summary>Puts caddy(.exe).previous back after the new binary failed to start.</summary>
    private async Task RestorePreviousAsync(ICaddyHost host, InstallMetadata? previousMeta, Action<string> log, CancellationToken ct)
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
        InvalidatePrevious();
        if (previousMeta is not null) WriteMetadata(previousMeta);
        else if (File.Exists(MetadataFile)) File.Delete(MetadataFile);
        WritePreviousMetadata(null); // the failed binary is discarded; there is no older binary any more
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

    /// <summary>Deletes an uploaded file the job owns, and its per-upload directory, when they are inside the staging directory.</summary>
    internal void DeleteUpload(string file)
    {
        try
        {
            var staging = Path.GetFullPath(paths.CaddyStagingDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(file);
            if (!full.StartsWith(staging, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return; // never delete files the manager did not stage itself
            if (File.Exists(full)) File.Delete(full);
            var dir = Path.GetDirectoryName(full);
            if (dir is not null && !string.Equals(dir + Path.DirectorySeparatorChar, staging, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not delete the uploaded file {File}: {Error}", file, ex.Message);
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
                candidates.Add(Path.Combine(dir.FullName, ".dev", "bin", Path.GetFileName(paths.CaddyExe)));
        }
        var found = candidates.FirstOrDefault(File.Exists);
        if (found is null) return false;
        Directory.CreateDirectory(paths.CaddyBinDir);
        File.Copy(found, paths.CaddyExe, overwrite: false);
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

    private async Task DownloadFileAsync(string url, string destination, Action<string> log, TimeSpan timeout, CancellationToken ct)
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

    /// <summary>Extracts caddy.exe from a Windows release archive (.zip).</summary>
    internal static void ExtractBinary(string archive, CaddyPlatform platform, string destination)
    {
        using var zip = ZipFile.OpenRead(archive);
        var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals(CaddyPlatform.BinaryName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException(
                        $"{Path.GetFileName(archive)} does not contain {CaddyPlatform.BinaryName}. Use the release archive for {platform} " +
                        $"({platform.ReleaseAssetPattern}).");
        entry.ExtractToFile(destination, overwrite: true);
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
