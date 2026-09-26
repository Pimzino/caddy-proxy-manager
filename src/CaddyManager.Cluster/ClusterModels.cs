using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;

namespace CaddyManager.Cluster;

/// <summary>
/// This server's cluster membership (Cluster-owned settings document). <see cref="Role"/> is only ever Standalone or
/// Node here: a server is a Primary because it has ClusterNode rows (see ClusterService.Role).
/// </summary>
public sealed class ClusterSettings : ISettingsDocument
{
    public ClusterRole Role { get; set; } = ClusterRole.Standalone;
    /// <summary>Node only: the id the primary gave this server (from the join token).</summary>
    public string? NodeId { get; set; }
    /// <summary>Node only: the primary's display name (from the join token, refreshed by every hello).</summary>
    public string? PrimaryName { get; set; }
    /// <summary>Node only: the shared 32-byte cluster secret (base64), protected with ISecretProtector.</summary>
    public string? SecretProtected { get; set; }
    public DateTime? JoinedAt { get; set; }
    /// <summary>Node only: when the primary last sent an authenticated request.</summary>
    public DateTime? LastPrimaryContactAt { get; set; }
    /// <summary>Node only: revision of the bundle last applied successfully.</summary>
    public string? AppliedRevision { get; set; }
    public DateTime? AppliedAt { get; set; }
    /// <summary>Node only: revision waiting for a Caddy binary rebuild (desired plugins missing) before it is applied.</summary>
    public string? PendingRevision { get; set; }
    public string? PendingJobId { get; set; }
    /// <summary>Node only: error of the last failed sync (cleared by the next successful one).</summary>
    public string? LastSyncError { get; set; }
    /// <summary>Node only: revision <see cref="LastSyncError"/> belongs to (reported in hello so the primary can throttle it).</summary>
    public string? LastSyncErrorRevision { get; set; }
    /// <summary>
    /// Node only: the primary instance (PrimaryInstanceId) this node obeys, pinned at the first authenticated RPC after
    /// joining. RPCs from another instance holding the same key (a cloned or restored primary running alongside the
    /// original) are refused. Joining again clears it.
    /// </summary>
    public string? PinnedPrimaryId { get; set; }
    /// <summary>
    /// Node only: the primary's clock minus this node's clock (seconds), as measured from accepted RPCs. After a manager
    /// restart, requests signed before the restart (on the primary's clock) are refused, so a captured request cannot be
    /// replayed once the in-memory nonce cache is gone.
    /// </summary>
    public long? PrimaryClockOffsetSeconds { get; set; }

    /// <summary>Primary only: random id of this primary instance, sent with every RPC (nodes pin it).</summary>
    public string? PrimaryInstanceId { get; set; }
    /// <summary>
    /// Primary only: the machine <see cref="PrimaryInstanceId"/> was created on. A database restored on (or cloned to) a
    /// machine with another name gets a new id, so the nodes do not obey two primaries at once.
    /// </summary>
    public string? PrimaryInstanceMachine { get; set; }
}

/// <summary>A managed node, as known by the primary.</summary>
public sealed class ClusterNode : Entity
{
    public string Name { get; set; } = "";
    /// <summary>Manager URL of the node, e.g. https://proxy2.corp.local:81 (no trailing slash).</summary>
    public string Url { get; set; } = "";
    /// <summary>Shared 32-byte cluster secret (base64), protected with ISecretProtector.</summary>
    public string SecretProtected { get; set; } = "";
    /// <summary>
    /// A new secret issued by "Regenerate token" that the node has not confirmed yet (the `rekey` RPC could not reach it).
    /// Until then the node still trusts <see cref="SecretProtected"/>; the rotation is retried on every contact.
    /// </summary>
    public string? PendingSecretProtected { get; set; }
    /// <summary>SHA-256 fingerprint (AA:BB:...) of the node's HTTPS certificate pinned on add/first contact (TOFU).</summary>
    public string? PinnedFingerprint { get; set; }
    public DateTime? TokenIssuedAt { get; set; }

    public ServerStatus Status { get; set; } = ServerStatus.Pending;
    public DateTime? LastSeenAt { get; set; }
    public string? LastError { get; set; }
    public int ConsecutiveFailures { get; set; }
    /// <summary>server-offline:&lt;id&gt; event raised and not yet recovered.</summary>
    public bool OfflineRaised { get; set; }

    /// <summary>Cached ServerInfo / ResourceSample from the last heartbeat (JSON, so the store never depends on record shapes).</summary>
    public string? InfoJson { get; set; }
    public string? LatestJson { get; set; }

    public string? DesiredRevision { get; set; }
    public string? AppliedRevision { get; set; }
    /// <summary>Revision the node has accepted but is still applying (Caddy rebuild with the desired plugins).</summary>
    public string? PendingRevision { get; set; }
    public DateTime? LastSyncAt { get; set; }
    public string? LastSyncError { get; set; }
    /// <summary>Revision the node could not apply (Caddy rejected it); not pushed again automatically before ClusterOptions.FailedSyncRetry.</summary>
    public string? FailedRevision { get; set; }
    public List<string> SyncWarnings { get; set; } = new();
    /// <summary>server-sync:&lt;id&gt; event raised and not yet recovered.</summary>
    public bool SyncErrorRaised { get; set; }
}
