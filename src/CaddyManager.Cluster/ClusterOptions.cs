namespace CaddyManager.Cluster;

/// <summary>
/// Tunables for the Cluster module. Defaults match the specification; they exist so tests (and unusual deployments) can
/// shorten timers. Configure with services.Configure&lt;ClusterOptions&gt;().
/// </summary>
public sealed class ClusterOptions
{
    /// <summary>Run the primary's heartbeat/sync worker.</summary>
    public bool EnableBackgroundServices { get; set; } = true;
    /// <summary>How often the primary sends `hello` to every node.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);
    /// <summary>Quiet period after a configuration change before it is pushed (several quick edits → one sync).</summary>
    public TimeSpan SyncDebounce { get; set; } = TimeSpan.FromSeconds(2);
    /// <summary>Timeout of query RPCs (hello, info, samples, traffic, status, job).</summary>
    public TimeSpan QueryTimeout { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>Timeout of RPCs that change the node (sync applies the configuration through Caddy; restart; update).</summary>
    public TimeSpan SyncTimeout { get; set; } = TimeSpan.FromSeconds(120);
    /// <summary>
    /// How long a revision the node rejected is not pushed again automatically (a new revision or "Sync now" is pushed at
    /// once). Keeps a persistent failure from filling the node's configuration history and event log every heartbeat.
    /// </summary>
    public TimeSpan FailedSyncRetry { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>Consecutive failed heartbeats before the node is reported offline (event server-offline:&lt;id&gt;).</summary>
    public int OfflineThreshold { get; set; } = 3;
    /// <summary>Accepted clock difference between primary and node for an RPC timestamp.</summary>
    public TimeSpan MaxClockSkew { get; set; } = TimeSpan.FromSeconds(300);
    /// <summary>How long a node remembers RPC nonces (must exceed twice the clock skew window).</summary>
    public TimeSpan NonceRetention { get; set; } = TimeSpan.FromMinutes(10);
    /// <summary>How often a node polls the Caddy rebuild job it started for missing plugins.</summary>
    public TimeSpan PendingJobPoll { get; set; } = TimeSpan.FromSeconds(2);
}
