using CaddyManager.Core.Models;

namespace CaddyManager.Core.Contracts;

// ------------------------------------------------------------------ Caddy runtime

public enum CaddyRunState { NotInstalled, Stopped, Starting, Running, Stopping, Unknown }

public sealed record CaddyStatus
{
    /// <summary>caddy.exe present in AppPaths.CaddyBinDir.</summary>
    public bool BinaryInstalled { get; init; }
    /// <summary>Windows service "Caddy" registered (always true in dev process mode when binary exists).</summary>
    public bool ServiceInstalled { get; init; }
    public CaddyRunState State { get; init; }
    public int? ProcessId { get; init; }
    public string? Version { get; init; }
    public bool AdminReachable { get; init; }
    public DateTime? StartedAt { get; init; }
    public string BinaryPath { get; init; } = "";
    public string ConfigPath { get; init; } = "";
    /// <summary>"windows-service" or "process" (dev).</summary>
    public string HostMode { get; init; } = "";
    public string? ServiceStartType { get; init; }
    public string? LastError { get; init; }
}

public sealed record InstalledBinary
{
    public string Version { get; init; } = "";          // "v2.11.4"
    public string Path { get; init; } = "";
    public DateTime? InstalledAt { get; init; }
    /// <summary>Non-standard packages compiled in (from `caddy list-modules --packages`).</summary>
    public List<string> Plugins { get; init; } = new();
    public List<string> Modules { get; init; } = new();
}

public sealed record ReleaseInfo
{
    public string Version { get; init; } = "";          // "v2.11.4"
    public DateTime? PublishedAt { get; init; }
    public string Url { get; init; } = "";               // release notes URL
    public string? Notes { get; init; }                  // markdown body (truncated)
}

public sealed record BinaryOverview
{
    public InstalledBinary? Installed { get; init; }
    public ReleaseInfo? Latest { get; init; }
    public bool UpdateAvailable { get; init; }
    public DateTime? LastCheckedAt { get; init; }
    /// <summary>Plugins configured in BinarySettings (desired state).</summary>
    public List<string> DesiredPlugins { get; init; } = new();
    /// <summary>True when installed plugins differ from desired plugins (rebuild needed).</summary>
    public bool PluginsOutOfSync { get; init; }
    public string Platform { get; init; } = "";          // "windows/amd64"
    /// <summary>A previous binary (caddy.exe.previous) exists and POST /api/caddy/binary/rollback is possible.</summary>
    public bool CanRollback { get; init; }
    public string? PreviousVersion { get; init; }
    /// <summary>Version of Caddy Proxy Manager itself.</summary>
    public string? ManagerVersion { get; init; }
    public string? ManagerLatestVersion { get; init; }
    public string? ManagerLatestUrl { get; init; }
    public bool ManagerUpdateAvailable { get; init; }
}

public sealed record PluginPackage
{
    public string Path { get; init; } = "";
    public string? Repo { get; init; }
    public long Downloads { get; init; }
    public List<string> Modules { get; init; } = new();
}

// ------------------------------------------------------------------ Config apply

public sealed record ApplyResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? RevisionId { get; init; }
    /// <summary>True when Caddy was not running so the config was only written to disk.</summary>
    public bool WrittenOnly { get; init; }
    public List<string> Warnings { get; init; } = new();
}

public sealed record ValidationResult
{
    public bool Valid { get; init; }
    public string? Error { get; init; }
    public string? Output { get; init; }
}

public sealed record UpstreamHealth
{
    public string Address { get; init; } = "";
    public int NumRequests { get; init; }
    public int Fails { get; init; }
    public bool Healthy { get; init; } = true;
}

// ------------------------------------------------------------------ Certificates

public enum CertificateKind { Custom, Acme, Internal, InternalRoot }

/// <summary>Unified view over uploaded/path certs and certs Caddy manages in its storage.</summary>
public sealed record CertificateInfo
{
    public string Id { get; init; } = "";               // Certificate.Id for custom; storage-relative key otherwise
    public CertificateKind Kind { get; init; }
    public string Name { get; init; } = "";
    public List<string> Subjects { get; init; } = new();
    public string Issuer { get; init; } = "";
    public DateTime NotBefore { get; init; }
    public DateTime NotAfter { get; init; }
    public int DaysRemaining { get; init; }
    public string? CertPath { get; init; }
    public string? KeyPath { get; init; }
    public string? Source { get; init; }                // "uploaded" | "filePath" | issuer dir name
    public List<string> UsedByHostIds { get; init; } = new();
    public string? Error { get; init; }                 // e.g. file missing/unreadable
    public string? Notes { get; init; }
}

// ------------------------------------------------------------------ Readiness

public enum CheckStatus { Pass, Warn, Fail, Info, Skipped }

public sealed record ReadinessCheck
{
    public string Id { get; init; } = "";               // stable, e.g. "firewall.tcp443"
    public string Category { get; init; } = "";         // "Firewall" | "Network" | "Domain" | "Ports" | "Connectivity" | "System" | "Caddy" | "DNS"
    public string Title { get; init; } = "";
    public CheckStatus Status { get; init; }
    public string Summary { get; init; } = "";
    public string? Details { get; init; }
    public string? Remediation { get; init; }
    /// <summary>PowerShell the admin can copy/run to remediate.</summary>
    public string? Script { get; init; }
    /// <summary>True when POST /api/readiness/fix/{id} can apply the remediation automatically.</summary>
    public bool Fixable { get; init; }
}

public sealed record MachineInfo
{
    public string Hostname { get; init; } = "";
    public string? Fqdn { get; init; }
    public string OsDescription { get; init; } = "";
    public bool IsWindows { get; init; }
    public bool DomainJoined { get; init; }
    public string? Domain { get; init; }
    public string? ComputerDn { get; init; }            // distinguished name in AD when resolvable
    public List<string> IpAddresses { get; init; } = new();
    public List<NetworkProfileInfo> NetworkProfiles { get; init; } = new();
}

public sealed record NetworkProfileInfo
{
    public string InterfaceAlias { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>"Public" | "Private" | "DomainAuthenticated"</summary>
    public string Category { get; init; } = "";
}

public sealed record ReadinessReport
{
    public DateTime RanAt { get; init; }
    public MachineInfo Machine { get; init; } = new();
    public List<ReadinessCheck> Checks { get; init; } = new();
    public int Pass => Checks.Count(c => c.Status == CheckStatus.Pass);
    public int Warn => Checks.Count(c => c.Status == CheckStatus.Warn);
    public int Fail => Checks.Count(c => c.Status == CheckStatus.Fail);
}

// ------------------------------------------------------------------ Jobs

public enum JobState { Running, Succeeded, Failed }

public sealed record JobInfo
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Title { get; init; } = "";
    public JobState State { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }
    public List<string> Log { get; init; } = new();
    public string? Error { get; init; }
}

// ------------------------------------------------------------------ Paging

public sealed record Page<T>(IReadOnlyList<T> Items, int Total);

// ================================================================== Round 3: servers, telemetry, cluster

/// <summary>Static-ish facts about one server (this one or a cluster node).</summary>
public sealed record ServerInfo
{
    public string Hostname { get; init; } = "";
    public string? Fqdn { get; init; }
    /// <summary>RuntimeInformation.OSDescription.</summary>
    public string Os { get; init; } = "";
    public bool IsWindows { get; init; }
    /// <summary>"x64" | "arm64" | ...</summary>
    public string Architecture { get; init; } = "";
    public string ManagerVersion { get; init; } = "";
    public string? CaddyVersion { get; init; }
    public CaddyRunState CaddyState { get; init; } = CaddyRunState.Unknown;
    public DateTime? CaddyStartedAt { get; init; }
    /// <summary>Non-standard packages compiled into the installed Caddy.</summary>
    public List<string> CaddyPlugins { get; init; } = new();
    public int ProcessorCount { get; init; }
    public long TotalMemoryBytes { get; init; }
    public long SystemUptimeSeconds { get; init; }
    public long ManagerUptimeSeconds { get; init; }
    public string DataDir { get; init; } = "";
    public List<string> IpAddresses { get; init; } = new();
    /// <summary>AD domain when joined.</summary>
    public string? Domain { get; init; }
    public DateTime CollectedAt { get; init; }
}

public sealed record DiskUsage
{
    /// <summary>Drive/mount name, e.g. "C:\" or "/".</summary>
    public string Name { get; init; } = "";
    /// <summary>What lives there, e.g. "Data (C:\ProgramData\CaddyProxyManager)" or "System".</summary>
    public string Label { get; init; } = "";
    public long TotalBytes { get; init; }
    public long FreeBytes { get; init; }
}

/// <summary>One resource sample (taken every 2 s; the last 10 minutes are kept in memory).</summary>
public sealed record ResourceSample
{
    public DateTime At { get; init; }
    /// <summary>Whole-machine CPU 0–100.</summary>
    public double CpuPercent { get; init; }
    public long MemoryUsedBytes { get; init; }
    public long MemoryTotalBytes { get; init; }
    public List<DiskUsage> Disks { get; init; } = new();
    /// <summary>Sum over up, non-loopback interfaces.</summary>
    public double NetworkRxBytesPerSec { get; init; }
    public double NetworkTxBytesPerSec { get; init; }
    /// <summary>Caddy process CPU 0–100 (of the whole machine), null when Caddy is not running.</summary>
    public double? CaddyCpuPercent { get; init; }
    /// <summary>Caddy process working set / RSS.</summary>
    public long? CaddyMemoryBytes { get; init; }
    public double ManagerCpuPercent { get; init; }
    public long ManagerMemoryBytes { get; init; }
    /// <summary>Established TCP connections to Caddy's HTTP/HTTPS ports; null when unavailable.</summary>
    public int? ActiveConnections { get; init; }
    /// <summary>HTTP requests per second handled by Caddy since the previous sample (from traffic ingestion).</summary>
    public double RequestsPerSecond { get; init; }
}

/// <summary>Traffic statistics window: hour = last 60 min in 1-minute buckets, day = 24 h hourly, week = 7 d hourly, month = 30 d daily.</summary>
public enum TrafficRange { Hour, Day, Week, Month }

public sealed record TrafficQuery
{
    public TrafficRange Range { get; init; } = TrafficRange.Day;
    /// <summary>Limit to one host name (lower case, no port); null = all.</summary>
    public string? Host { get; init; }
}

public sealed record TrafficTotals
{
    public long Requests { get; init; }
    /// <summary>Request body bytes read (Caddy `bytes_read`).</summary>
    public long BytesIn { get; init; }
    /// <summary>Response body bytes sent (Caddy `size`, after compression).</summary>
    public long BytesOut { get; init; }
    /// <summary>Distinct client IPs (Caddy `client_ip`, honours trusted proxies). Exact below 1024 per bucket, HyperLogLog estimate above.</summary>
    public long UniqueClients { get; init; }
    public long Status2xx { get; init; }
    public long Status3xx { get; init; }
    public long Status4xx { get; init; }
    public long Status5xx { get; init; }
    /// <summary>Requests with no status (aborted connections, status 0) or 1xx.</summary>
    public long StatusOther { get; init; }
    public double AvgDurationMs { get; init; }
}

public sealed record TrafficPoint
{
    /// <summary>Bucket start (UTC).</summary>
    public DateTime At { get; init; }
    public long Requests { get; init; }
    public long BytesIn { get; init; }
    public long BytesOut { get; init; }
    public long UniqueClients { get; init; }
    public long Status4xx { get; init; }
    public long Status5xx { get; init; }
}

public sealed record TrafficHostRow
{
    public string Host { get; init; } = "";
    public long Requests { get; init; }
    public long BytesIn { get; init; }
    public long BytesOut { get; init; }
    public long UniqueClients { get; init; }
    public long Status4xx { get; init; }
    public long Status5xx { get; init; }
}

public sealed record TrafficClientRow
{
    public string Ip { get; init; } = "";
    public long Requests { get; init; }
    public long BytesOut { get; init; }
    public DateTime LastSeen { get; init; }
}

public sealed record StatusCount(int Code, long Count);

public sealed record TrafficReport
{
    public TrafficRange Range { get; init; }
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    /// <summary>"minute" | "hour" | "day"</summary>
    public string BucketSize { get; init; } = "hour";
    public string? Host { get; init; }
    /// <summary>False when CaddySettings.TrafficStatsEnabled is off (the rest is empty).</summary>
    public bool Enabled { get; init; } = true;
    public DateTime? LastIngestAt { get; init; }
    public TrafficTotals Totals { get; init; } = new();
    /// <summary>One point per bucket, oldest first, zero-filled.</summary>
    public List<TrafficPoint> Series { get; init; } = new();
    /// <summary>Busiest hosts (max 20) in the window.</summary>
    public List<TrafficHostRow> TopHosts { get; init; } = new();
    /// <summary>Busiest client IPs (max 20, approximate top-k) in the window.</summary>
    public List<TrafficClientRow> TopClients { get; init; } = new();
    /// <summary>Individual status codes with counts, descending.</summary>
    public List<StatusCount> StatusCodes { get; init; } = new();
    public List<string> Notes { get; init; } = new();
}

// ------------------------------------------------------------------ Cluster

public enum ClusterRole
{
    /// <summary>Single server (no nodes).</summary>
    Standalone,
    /// <summary>Manages one or more nodes and replicates its configuration to them.</summary>
    Primary,
    /// <summary>Managed by a primary: hosts, certificates, access lists, streams and Caddy settings are replicated and read-only here.</summary>
    Node,
}

public enum ServerStatus { Online, Offline, Pending, Error }

public sealed record ServerSyncState
{
    /// <summary>Revision (hash) of the configuration the primary wants on the node.</summary>
    public string? DesiredRevision { get; init; }
    /// <summary>Revision the node reports as applied.</summary>
    public string? AppliedRevision { get; init; }
    public bool InSync { get; init; }
    public DateTime? LastSyncAt { get; init; }
    public string? LastError { get; init; }
    public List<string> Warnings { get; init; } = new();
}

/// <summary>A row on the Servers page. Id "local" is always this server.</summary>
public sealed record ServerSummary
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsLocal { get; init; }
    /// <summary>Manager URL of a node (e.g. https://proxy2.corp.local:81); null for the local server.</summary>
    public string? Url { get; init; }
    public ServerStatus Status { get; init; }
    public DateTime? LastSeenAt { get; init; }
    public string? LastError { get; init; }
    public ServerInfo? Info { get; init; }
    public ResourceSample? Latest { get; init; }
    /// <summary>Replication state (nodes only).</summary>
    public ServerSyncState? Sync { get; init; }
    public DateTime? AddedAt { get; init; }
    /// <summary>SHA-256 fingerprint of the node's HTTPS certificate pinned by the primary (https node URLs only).</summary>
    public string? Fingerprint { get; init; }
    /// <summary>When the node's current join token was issued.</summary>
    public DateTime? TokenIssuedAt { get; init; }
}

public sealed record ClusterStatus
{
    public ClusterRole Role { get; init; }
    /// <summary>This server's display name (UiSettings.DisplayName or the host name).</summary>
    public string ServerName { get; init; } = "";
    /// <summary>Node only: the primary's display name.</summary>
    public string? PrimaryName { get; init; }
    /// <summary>Node only: when the primary last contacted this node.</summary>
    public DateTime? LastPrimaryContactAt { get; init; }
    /// <summary>Node only: revision of the configuration last applied from the primary.</summary>
    public string? AppliedRevision { get; init; }
    public int NodeCount { get; init; }
    public StorageBackend StorageBackend { get; init; }
    /// <summary>e.g. "Local storage: each server obtains its own certificates" when nodes exist.</summary>
    public List<string> Warnings { get; init; } = new();
}

// ------------------------------------------------------------------ DNS challenge delegation check

public enum DelegationStatus
{
    /// <summary>_acme-challenge.&lt;domain&gt; is a CNAME that resolves (directly or through a chain) to the expected name.</summary>
    Ok,
    /// <summary>No CNAME at _acme-challenge.&lt;domain&gt;.</summary>
    Missing,
    /// <summary>A CNAME exists but points elsewhere (or a TXT/other record sits there instead).</summary>
    Wrong,
    /// <summary>The lookup failed (timeout, SERVFAIL...).</summary>
    Error,
}

public sealed record DelegationCheck
{
    public string Domain { get; init; } = "";
    /// <summary>The record to create: _acme-challenge.&lt;domain without "*."&gt;.</summary>
    public string RecordName { get; init; } = "";
    /// <summary>The CNAME target it must point to (the effective override domain).</summary>
    public string ExpectedTarget { get; init; } = "";
    public DelegationStatus Status { get; init; }
    /// <summary>CNAME chain found, in order (empty when none).</summary>
    public List<string> Found { get; init; } = new();
    public string? Detail { get; init; }
}

public sealed record DelegationCheckResult
{
    /// <summary>Resolvers queried ("system" when the OS resolvers were used).</summary>
    public List<string> Resolvers { get; init; } = new();
    public List<DelegationCheck> Checks { get; init; } = new();
    public DateTime CheckedAt { get; init; }
}
