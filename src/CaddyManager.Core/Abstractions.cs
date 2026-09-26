using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using LiteDB;

namespace CaddyManager.Core;

// Cross-module service contracts. Implementations:
//   Core     : IStore, ISecretProtector, IJobRunner
//   Config   : ICaddyConfigService, ICaddyAdminClient, ICertificateInventory
//   Platform : ICaddyHost, ICaddyBinaryManager, IReadinessService
//   Ops      : IAuditLog, IEventSink, INotifier, ICurrentUser
//   Round 3  : Config → IConfigChangeFeed, ICertificateMaterialStore; Cluster → IClusterRole; Telemetry → IServerTelemetry

/// <summary>LiteDB-backed persistence. Collections are named after the entity type.</summary>
public interface IStore
{
    ILiteCollection<T> Col<T>() where T : Entity;
    T GetSettings<T>() where T : class, ISettingsDocument, new();
    void SaveSettings<T>(T settings) where T : class, ISettingsDocument, new();
    /// <summary>Raised after SaveSettings with the settings type.</summary>
    event Action<Type>? SettingsChanged;
    ILiteDatabase Database { get; }
}

/// <summary>Protects secrets at rest (DPAPI LocalMachine on Windows; AES key file elsewhere).</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedValue);
}

/// <summary>Long-running operations (binary download/update) observable via GET /api/jobs/{id}.</summary>
public interface IJobRunner
{
    /// <summary>Starts work in the background. The callback receives a log function.</summary>
    JobInfo Start(string kind, string title, Func<Action<string>, CancellationToken, Task> work);
    JobInfo? Get(string id);
    IReadOnlyList<JobInfo> Recent(int take = 20);
    /// <summary>True if a job of this kind is currently running.</summary>
    bool IsRunning(string kind);
}

// ----------------------------------------------------------------- Config module

public interface ICaddyConfigService
{
    /// <summary>Generate the full Caddy JSON config from the store (Managed mode) — no side effects.</summary>
    string BuildConfigJson();
    /// <summary>
    /// Build (or adapt Caddyfile in Caddyfile mode), validate, push to the Caddy admin API (POST /load),
    /// persist to AppPaths.CaddyConfigFile and record a ConfigRevision. If Caddy is not running,
    /// validates with the binary and writes the file only (WrittenOnly = true).
    /// Serialised — concurrent calls queue.
    /// </summary>
    Task<ApplyResult> ApplyAsync(string reason, CancellationToken ct = default);
    /// <summary>Validate a JSON config using `caddy validate` (or admin API adapt for Caddyfile).</summary>
    Task<ValidationResult> ValidateAsync(string json, CancellationToken ct = default);
    /// <summary>Ensure AppPaths.CaddyConfigFile exists (writes a minimal bootable config if missing).</summary>
    void EnsureBootConfig();
}

public interface ICaddyAdminClient
{
    string BaseUrl { get; }
    Task<bool> IsReachableAsync(CancellationToken ct = default);
    Task<string?> GetConfigAsync(CancellationToken ct = default);
    /// <summary>POST /load. Throws CaddyAdminException with Caddy's error message on failure.</summary>
    Task LoadAsync(string json, CancellationToken ct = default);
    /// <summary>POST /adapt (Caddyfile -> JSON). Returns JSON and warnings.</summary>
    Task<(string Json, List<string> Warnings)> AdaptCaddyfileAsync(string caddyfile, CancellationToken ct = default);
    Task<List<UpstreamHealth>> GetUpstreamsAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}

public sealed class CaddyAdminException(string message) : Exception(message);

public interface ICertificateInventory
{
    /// <summary>All certificates: custom (store) + ACME/internal (scanned from Caddy storage).</summary>
    Task<List<CertificateInfo>> ListAsync(CancellationToken ct = default);
}

// ----------------------------------------------------------------- Platform module

/// <summary>Runs Caddy: Windows service "Caddy" in production, child process in development.</summary>
public interface ICaddyHost
{
    string HostMode { get; }
    Task<CaddyStatus> GetStatusAsync(CancellationToken ct = default);
    /// <summary>Register (or repair) the Caddy service. No-op in process mode.</summary>
    Task InstallServiceAsync(CancellationToken ct = default);
    Task UninstallServiceAsync(CancellationToken ct = default);
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task RestartAsync(CancellationToken ct = default);
}

public interface ICaddyBinaryManager
{
    Task<InstalledBinary?> GetInstalledAsync(CancellationToken ct = default);
    /// <summary>Latest stable release from GitHub (cached; force = bypass cache).</summary>
    Task<ReleaseInfo?> GetLatestAsync(bool force = false, CancellationToken ct = default);
    Task<BinaryOverview> GetOverviewAsync(CancellationToken ct = default);
    /// <summary>
    /// Download (with desired plugins via caddyserver.com build API, or official release when none),
    /// verify, validate current config against it, stop Caddy, swap, start, health-check, roll back on failure.
    /// Returns a job.
    /// </summary>
    JobInfo StartInstallOrUpdate(string? version = null);
    /// <summary>Offline/air-gapped install: a caddy.exe (or release zip) already uploaded to a staging path goes through the same verified swap pipeline.</summary>
    JobInfo StartInstallFromFile(string stagedFile, string? expectedSha512 = null) => throw new NotSupportedException();
    /// <summary>Swap back to the previous binary (caddy.exe.previous) through the same verified pipeline.</summary>
    JobInfo StartRollback() => throw new NotSupportedException();
    /// <summary>True when a previous binary exists for rollback.</summary>
    bool CanRollback => false;
    /// <summary>Run the caddy binary with args (e.g. "validate", "version"). Returns exit code + combined output.</summary>
    Task<(int ExitCode, string Output)> RunCaddyAsync(IEnumerable<string> args, string? stdin = null, CancellationToken ct = default);
    Task<List<PluginPackage>> GetPluginCatalogAsync(string? query = null, CancellationToken ct = default);
}

public interface IReadinessService
{
    ReadinessReport? LastReport { get; }
    Task<ReadinessReport> RunAsync(CancellationToken ct = default);
    /// <summary>Apply automatic fix for a Fixable check. Returns a message.</summary>
    Task<string> FixAsync(string checkId, CancellationToken ct = default);
    /// <summary>PowerShell script to create the required firewall rules in a domain GPO.</summary>
    string BuildGpoScript();
}

// ----------------------------------------------------------------- Ops module

public interface ICurrentUser
{
    string? UserId { get; }
    string UserName { get; }
    UserRole? Role { get; }
    string? RemoteIp { get; }
}

public interface IAuditLog
{
    void Record(string action, string objectType, string? objectId = null, string? objectName = null, string? details = null);
    /// <summary>
    /// Record an action taken outside a web request by a known identity (e.g. a Windows administrator using the tray,
    /// via the local control pipe), so it is not attributed to "system".
    /// </summary>
    void RecordAs(string userName, string action, string objectType, string? objectId = null, string? objectName = null, string? details = null);
}

/// <summary>Raise operational events. Handles persistence, cooldown and notification fan-out.</summary>
public interface IEventSink
{
    /// <summary>
    /// key: dedup key for cooldown. When a later event with the same key has Severity=Recovered,
    /// a recovery notice is sent (if enabled) and the key's alert state clears.
    /// alertRule: which NotificationSettings.Alert* toggle governs notification (e.g. "caddyDown").
    /// </summary>
    void Raise(EventSeverity severity, string category, string message, string? details = null, string? key = null, string? alertRule = null);
}

public interface INotifier
{
    /// <summary>Send to all configured channels (SMTP, webhook). Returns per-channel errors (empty = success).</summary>
    Task<List<string>> SendAsync(string subject, string body, CancellationToken ct = default);
}

// ----------------------------------------------------------------- Round 3

/// <summary>Implemented by the Config module (CaddyConfigService). Lets other modules react to configuration changes.</summary>
public interface IConfigChangeFeed
{
    /// <summary>
    /// Raised after every successful ICaddyConfigService.ApplyAsync (including WrittenOnly results, i.e. the stored
    /// configuration changed even though Caddy was not running). Arguments: result, reason. Handlers must not block.
    /// </summary>
    event Action<ApplyResult, string>? Applied;
}

/// <summary>Implemented by the Config module: writes certificate material into the certificate store with the store's ACL.</summary>
public interface ICertificateMaterialStore
{
    /// <summary>Writes fullchain.pem + privkey.pem for the certificate id under the resolved store; returns their paths.</summary>
    (string CertPath, string KeyPath) WritePem(string certificateId, string certificatePem, string privateKeyPem);
    /// <summary>Removes the certificate's directory from the store (no error when missing).</summary>
    void Delete(string certificateId);
}

/// <summary>
/// Implemented by the Cluster module. When this server is a managed node, replicated resources (hosts, streams, access
/// lists, certificates, Caddy settings except CaddySettings.NodeLocalProperties, desired plugins) are read-only here:
/// mutating endpoints answer 409 via <see cref="Infrastructure.ApiResults"/>-style problem
/// { title: "Managed by the cluster primary", detail: "This server is a node managed by '&lt;PrimaryName&gt;'. Make this change on the primary." }.
/// Resolve optionally (GetService) so modules/tests without the Cluster module keep working.
/// </summary>
public interface IClusterRole
{
    ClusterRole Role { get; }
    bool IsManagedNode => Role == ClusterRole.Node;
    string? PrimaryName { get; }
}

/// <summary>Implemented by the Telemetry module: facts, live resource samples and traffic statistics of THIS server.</summary>
public interface IServerTelemetry
{
    Task<ServerInfo> GetInfoAsync(CancellationToken ct = default);
    /// <summary>Samples from the in-memory ring buffer (last 10 minutes), oldest first; only those newer than <paramref name="since"/> when given.</summary>
    IReadOnlyList<ResourceSample> GetSamples(DateTime? since = null);
    Task<TrafficReport> GetTrafficAsync(TrafficQuery query, CancellationToken ct = default);
}

/// <summary>
/// Implemented by the Config module: replaces every configured secret (DNS provider secrets, storage secrets, EAB key...),
/// including URL-encoded forms, with "***". Use it on any text that may contain Caddy output before it leaves the process
/// (log viewer, events, notifications, status errors).
/// </summary>
public interface ISecretScrubber
{
    string Scrub(string text);
}

/// <summary>
/// Implemented by the Config module (ConfigMutationGate): serialises configuration mutations (persist → apply → rollback)
/// so other modules (e.g. cluster replication) never read or write half-applied configuration.
/// </summary>
public interface IConfigMutationLock
{
    /// <summary>Waits for the lock. Dispose the result to release it.</summary>
    Task<IDisposable> AcquireAsync(CancellationToken ct = default);
    /// <summary>Returns null when the lock is not free within <paramref name="timeout"/>.</summary>
    Task<IDisposable?> TryAcquireAsync(TimeSpan timeout, CancellationToken ct = default);
}
