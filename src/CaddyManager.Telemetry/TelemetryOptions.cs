namespace CaddyManager.Telemetry;

/// <summary>
/// Tunables for the Telemetry module. Defaults match the specification; they exist mainly so tests can shorten timers
/// or run the background services by hand. Configure with services.Configure&lt;TelemetryOptions&gt;().
/// </summary>
public sealed class TelemetryOptions
{
    /// <summary>Run ResourceSampler and StatsIngesterService (disable in tests that drive them directly).</summary>
    public bool EnableBackgroundServices { get; set; } = true;

    /// <summary>Resource sample period (SPEC: 2 s).</summary>
    public TimeSpan SampleInterval { get; set; } = TimeSpan.FromSeconds(2);
    /// <summary>How long samples stay in the in-memory ring buffer (SPEC: 10 minutes).</summary>
    public TimeSpan SampleWindow { get; set; } = TimeSpan.FromMinutes(10);
    /// <summary>Caddy status (for its PID) is cached this long between samples.</summary>
    public TimeSpan CaddyStatusCacheDuration { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>GetInfoAsync result cache.</summary>
    public TimeSpan InfoCacheDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How often the stats log is tailed (SPEC: every second).</summary>
    public TimeSpan IngestInterval { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>Maximum time aggregated counters stay in memory before a batched write (SPEC: ≤ 5 s).</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>How often expired buckets are deleted.</summary>
    public TimeSpan RetentionInterval { get; set; } = TimeSpan.FromHours(1);
}
