namespace CaddyManager.Ops;

/// <summary>
/// Tunables for the Ops module. Defaults match the specification; they exist mainly so tests
/// (and unusual deployments) can shorten timers. Configure with services.Configure&lt;OpsOptions&gt;().
/// </summary>
public sealed class OpsOptions
{
    /// <summary>Login / setup attempts allowed per remote IP per minute (then 429).</summary>
    public int LoginAttemptsPerMinute { get; set; } = 10;

    /// <summary>How long a validated user snapshot is trusted before the cookie principal is re-checked against the DB.</summary>
    public TimeSpan PrincipalCacheDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Run MonitorService and RetentionService (disable in tests that do not need them).</summary>
    public bool EnableBackgroundServices { get; set; } = true;

    public TimeSpan MonitorStartDelay { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan MonitorInterval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan CertificateCheckInterval { get; set; } = TimeSpan.FromHours(6);
    public TimeSpan ReadinessInterval { get; set; } = TimeSpan.FromHours(24);
    /// <summary>Consecutive failed Caddy health checks before "caddy-down" is raised (avoids alerting on blips).</summary>
    public int CaddyDownThreshold { get; set; } = 2;
    public int AutoRestartMaxAttempts { get; set; } = 3;
    public TimeSpan AutoRestartWindow { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan RetentionInterval { get; set; } = TimeSpan.FromHours(24);
    public int EventRetentionDays { get; set; } = 90;
    public int AuditRetentionDays { get; set; } = 365;

    /// <summary>Upper bound for each independent part of GET /api/dashboard.</summary>
    public TimeSpan DashboardPartTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
