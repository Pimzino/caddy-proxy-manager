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
