using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Cluster;

/// <summary>
/// Primary side background work: a `hello` heartbeat to every node (HeartbeatInterval), a push (`sync`) when the
/// configuration changed (IConfigChangeFeed.Applied, debounced) and whenever a node reports a revision other than the
/// current one, pending key rotations, and the server-offline / server-sync events. Nodes are handled independently (one
/// slow node never delays another); work on one node is serialised. Bundles are built only from committed configuration
/// (under the Config module's IConfigMutationLock).
/// </summary>
public sealed class ClusterWorker(
    ClusterService cluster,
    NodeClient client,
    IStore store,
    ISecretProtector secrets,
    IServiceProvider services,
    TimeProvider time,
    ILogger<ClusterWorker> logger) : BackgroundService
{
    public const string OfflineKeyPrefix = "server-offline:";
    public const string SyncKeyPrefix = "server-sync:";
    public const string RemovedKeyPrefix = "server-removed:";

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _nodeLocks = new();
    private readonly ConcurrentDictionary<string, DateTime> _nextHeartbeat = new();
    /// <summary>Nodes that must receive the current configuration as soon as no other work runs for them.</summary>
    private readonly ConcurrentDictionary<string, byte> _pushPending = new();
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly Lock _bundleLock = new();
    private BundleSnapshot? _bundle;
    /// <summary>Last bundle built from committed configuration (reused while a change is being applied).</summary>
    private BundleSnapshot? _lastBuilt;
    private DateTime _bundleAt;
    private DateTime? _syncRequestedAt;
    private IConfigChangeFeed? _feed;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!cluster.Options.EnableBackgroundServices) return;
        await Task.Yield();
        // Implemented by the Config module; optional so the Cluster module works (heartbeat-driven resync) without it.
        _feed = services.GetService<IConfigChangeFeed>();
        if (_feed is not null) _feed.Applied += OnConfigApplied;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { Tick(stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Cluster worker tick failed"); }
                try { await _wake.WaitAsync(NextDelay(), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            if (_feed is not null) _feed.Applied -= OnConfigApplied;
        }
    }

    private void OnConfigApplied(ApplyResult result, string reason)
    {
        if (cluster.Role != ClusterRole.Primary) return;
        _syncRequestedAt = time.GetUtcNow().UtcDateTime;
        InvalidateBundle();
        Wake();
    }

    /// <summary>Heartbeat this node (or all) as soon as possible.</summary>
    public void Kick(string? nodeId = null)
    {
        if (nodeId is null) _nextHeartbeat.Clear();
        else _nextHeartbeat.TryRemove(nodeId, out _);
        Wake();
    }

    /// <summary>Push the current configuration to every node after the debounce period.</summary>
    public void RequestSync()
    {
        _syncRequestedAt = time.GetUtcNow().UtcDateTime;
        InvalidateBundle();
        Wake();
    }

    private void Wake()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    private TimeSpan NextDelay()
    {
        var now = time.GetUtcNow().UtcDateTime;
        var next = now + cluster.Options.HeartbeatInterval;
        foreach (var t in _nextHeartbeat.Values) if (t < next) next = t;
        if (_syncRequestedAt is { } requested && requested + cluster.Options.SyncDebounce < next) next = requested + cluster.Options.SyncDebounce;
        if (!_pushPending.IsEmpty && now + TimeSpan.FromMilliseconds(250) < next) next = now + TimeSpan.FromMilliseconds(250);
        var delay = next - now;
        return delay < TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : delay;
    }

    /// <summary>
    /// The node answered with its current key at least once: pushes are only useful then. A node that has not joined (or
    /// left, or waits for a new token) is left to the heartbeat, which reports it as pending without alerting.
    /// </summary>
    private static bool HasAuthenticated(ClusterNode n) => n.LastSeenAt is { } seen && !(n.TokenIssuedAt > seen);

    private void Tick(CancellationToken ct)
    {
        var nodes = cluster.Nodes();
        var now = time.GetUtcNow().UtcDateTime;
        foreach (var id in _nextHeartbeat.Keys.Where(id => nodes.All(n => n.Id != id)).ToList()) _nextHeartbeat.TryRemove(id, out _);
        foreach (var id in _pushPending.Keys.Where(id => nodes.All(n => n.Id != id)).ToList()) _pushPending.TryRemove(id, out _);

        if (_syncRequestedAt is { } requested && now >= requested + cluster.Options.SyncDebounce)
        {
            _syncRequestedAt = null;
            InvalidateBundle();
            foreach (var n in nodes.Where(HasAuthenticated)) _pushPending[n.Id] = 0;
        }
        foreach (var node in nodes)
        {
            if (_pushPending.ContainsKey(node.Id))
            {
                // Work already running for the node (e.g. a heartbeat that compared against the previous revision): push
                // when it has finished.
                if (_nodeLocks.TryGetValue(node.Id, out var busy) && busy.CurrentCount == 0) continue;
                _pushPending.TryRemove(node.Id, out _);
                _ = RunForNodeAsync(node.Id, (n, token) => SyncAsync(n, force: false, token), ct);
                continue;
            }
            if (_nextHeartbeat.TryGetValue(node.Id, out var due) && due > now) continue;
            _nextHeartbeat[node.Id] = now + cluster.Options.HeartbeatInterval;
            _ = RunForNodeAsync(node.Id, (n, token) => HeartbeatCoreAsync(n, queryOnly: false, token), ct);
        }
    }

    /// <summary>Runs work for one node unless work for it is already running (the background loop never piles up).</summary>
    private async Task RunForNodeAsync(string nodeId, Func<ClusterNode, CancellationToken, Task> work, CancellationToken ct)
    {
        var gate = _nodeLocks.GetOrAdd(nodeId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct)) return;
        try
        {
            if (cluster.FindNode(nodeId) is { } node) await work(node, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Cluster work for node {Node} failed", nodeId);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Live query for the server details (GET /api/servers/{id}): `hello` now and return the fresh node row; the cached row
    /// when other work for the node does not finish within QueryTimeout. Never pushes the configuration itself (a stale
    /// node gets a background push) and never counts towards the offline threshold.
    /// </summary>
    public async Task<ClusterNode?> HeartbeatAsync(string nodeId, CancellationToken ct = default)
    {
        var gate = _nodeLocks.GetOrAdd(nodeId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(cluster.Options.QueryTimeout, ct)) return cluster.FindNode(nodeId);
        try
        {
            if (cluster.FindNode(nodeId) is not { } node) return null;
            await HeartbeatCoreAsync(node, queryOnly: true, ct);
            return cluster.FindNode(nodeId);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Push the configuration to the node now (the "Sync now" action) and return the fresh node row.</summary>
    public async Task<ClusterNode?> SyncNowAsync(string nodeId, CancellationToken ct = default)
    {
        var gate = _nodeLocks.GetOrAdd(nodeId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (cluster.FindNode(nodeId) is not { } node) return null;
            InvalidateBundle();
            if (node.PendingSecretProtected is not null) node = await TryCompleteKeyRotationAsync(node, ct);
            await SyncAsync(node, force: true, ct);
            return cluster.FindNode(nodeId);
        }
        finally
        {
            gate.Release();
        }
    }

    // ------------------------------------------------------------------ key rotation

    /// <summary>
    /// "Regenerate token": issues a new secret and hands it to the node (`rekey`, sealed with the current key). The node
    /// switches at once; this server switches when the node confirmed. When the node cannot be reached the rotation stays
    /// pending — the node still trusts its current key — and is retried at every contact. Returns the join token of the new
    /// secret (to join the node again, e.g. after it left) and whether the node already uses it.
    /// </summary>
    public async Task<(string Token, bool Rotated)?> RotateKeyAsync(string nodeId, CancellationToken ct = default)
    {
        var gate = _nodeLocks.GetOrAdd(nodeId, _ => new SemaphoreSlim(1, 1));
        var locked = await gate.WaitAsync(cluster.Options.QueryTimeout, ct);
        try
        {
            if (cluster.FindNode(nodeId) is not { } existing) return null;
            // An earlier rotation may have reached the node without its answer reaching us: settle it first, so the node's
            // current key is known before it is replaced again.
            if (locked && existing.PendingSecretProtected is not null) await TryCompleteKeyRotationAsync(existing, ct);
            var token = cluster.BeginKeyRotation(nodeId);
            client.Reset(nodeId);
            if (!locked || cluster.FindNode(nodeId) is not { } node) return (token, false);
            var after = await TryCompleteKeyRotationAsync(node, ct);
            return (token, after.PendingSecretProtected is null);
        }
        finally
        {
            if (locked) gate.Release();
        }
    }

    /// <summary>Completes a pending key rotation when the node can be reached; returns the fresh node row.</summary>
    private async Task<ClusterNode> TryCompleteKeyRotationAsync(ClusterNode node, CancellationToken ct)
    {
        if (node.PendingSecretProtected is not { } pending) return node;
        string? error;
        try
        {
            await client.CallAsync(node, "rekey", new { secret = Convert.ToBase64String(cluster.PendingSecret(node)) }, cluster.Options.QueryTimeout, ct);
            return Rotated(node, pending, "the node confirmed its new key");
        }
        catch (NodeRpcException ex) when (ex.Unauthorized)
        {
            // The node may already use the new key: its answer to an earlier rekey was lost, or it joined again with the new token.
            try
            {
                await client.CallAsync(node, "info", null, cluster.Options.QueryTimeout, usePendingKey: true, ct);
                return Rotated(node, pending, "the node already uses its new key");
            }
            catch (NodeRpcException) { error = ex.Message; }
        }
        catch (NodeRpcException ex) { error = ex.Message; }
        logger.LogInformation("Key rotation of node {Name} is pending: {Error}", node.Name, error);
        return cluster.FindNode(node.Id) ?? node;
    }

    private ClusterNode Rotated(ClusterNode node, string pending, string how)
    {
        var updated = cluster.CompleteKeyRotation(node.Id, pending) ?? node;
        logger.LogInformation("Key of node {Name} rotated: {How}", node.Name, how);
        services.GetService<IAuditLog>()?.Record("keyRotated", "server", node.Id, node.Name, $"New cluster key in use ({how}); the previous key and token no longer work");
        return updated;
    }

    // ------------------------------------------------------------------ heartbeat and sync

    private async Task HeartbeatCoreAsync(ClusterNode node, bool queryOnly, CancellationToken ct)
    {
        if (!queryOnly && node.PendingSecretProtected is not null) node = await TryCompleteKeyRotationAsync(node, ct);
        var bundle = await CurrentBundleAsync(ct);
        HelloResult? hello;
        try
        {
            hello = await client.CallAsync<HelloResult>(node, "hello",
                new { primaryName = cluster.ServerName, desiredRevision = bundle.Revision }, cluster.Options.QueryTimeout, ct);
        }
        catch (NodeRpcException ex) when (ex.Unauthorized)
        {
            RecordRejected(node, ex.Message);
            return;
        }
        catch (NodeRpcException ex) when (ex.Conflict)
        {
            RecordConflict(node, ex.Message, bundle.Warnings);
            return;
        }
        catch (NodeRpcException ex)
        {
            // A live query from the API falls back to the cached data; only the background heartbeat counts failures.
            if (!queryOnly) RecordFailure(node, ex.Message);
            return;
        }
        if (hello is null)
        {
            if (!queryOnly) RecordFailure(node, "The node sent an empty answer.");
            return;
        }

        var recovered = false;
        var syncRecovered = false;
        var inSync = hello.AppliedRevision == bundle.Revision && hello.PendingRevision is null;
        // The node could not apply exactly the current revision (e.g. its Caddy rebuild failed after a pending sync): alert
        // once and let FailedSyncRetry throttle pushing it again.
        var failedHere = !inSync && hello.PendingRevision is null && hello.LastSyncError is { Length: > 0 } && hello.LastSyncErrorRevision == bundle.Revision;
        var updated = cluster.UpdateNode(node.Id, n =>
        {
            recovered = n.OfflineRaised;
            n.OfflineRaised = false;
            n.ConsecutiveFailures = 0;
            n.LastSeenAt = time.GetUtcNow().UtcDateTime;
            n.LastError = null;
            n.InfoJson = hello.Info is null ? n.InfoJson : JsonSerializer.Serialize(hello.Info, JsonDefaults.Api);
            n.LatestJson = hello.Latest is null ? n.LatestJson : JsonSerializer.Serialize(hello.Latest, JsonDefaults.Api);
            n.DesiredRevision = bundle.Revision;
            n.AppliedRevision = hello.AppliedRevision;
            n.PendingRevision = hello.PendingRevision;
            if (inSync)
            {
                // The node runs exactly the current configuration (e.g. a rejected change was undone on the primary).
                syncRecovered = n.SyncErrorRaised;
                n.SyncErrorRaised = false;
                n.LastSyncError = null;
                n.FailedRevision = null;
            }
            else if (hello.LastSyncError is { Length: > 0 } nodeError) n.LastSyncError = nodeError;
            n.Status = n.LastSyncError is null ? ServerStatus.Online : ServerStatus.Error;
        });
        if (updated is null) return;
        if (recovered)
            Sink()?.Raise(EventSeverity.Recovered, "cluster", $"Server '{updated.Name}' is reachable again", updated.Url,
                key: OfflineKeyPrefix + updated.Id, alertRule: "serverOffline");
        if (syncRecovered)
            Sink()?.Raise(EventSeverity.Recovered, "cluster", $"Server '{updated.Name}' runs the current configuration again", null,
                key: SyncKeyPrefix + updated.Id, alertRule: "configFailure");
        if (failedHere && updated.FailedRevision != bundle.Revision)
            RecordSyncFailure(updated, hello.LastSyncError!, bundle.Warnings, bundle.Revision);

        if (inSync || updated.PendingRevision == bundle.Revision) return;
        if (queryOnly)
        {
            // Never push from a viewer's request (a sync can take as long as the node's Caddy reload): queue it.
            if (HasAuthenticated(updated)) _pushPending[updated.Id] = 0;
            Wake();
            return;
        }
        await SyncAsync(updated, force: false, ct, bundle);
    }

    private async Task SyncAsync(ClusterNode node, bool force, CancellationToken ct, BundleSnapshot? bundle = null)
    {
        bundle ??= await CurrentBundleAsync(ct);
        // The node rejected exactly this configuration recently: wait for a real change, "Sync now" (force) or the retry
        // period. Applies to heartbeat and change-feed pushes alike (the primary may apply an unchanged configuration again).
        if (!force && cluster.FindNode(node.Id) is { } current && current.FailedRevision == bundle.Revision &&
            current.LastSyncAt is { } failedAt && time.GetUtcNow().UtcDateTime - failedAt < cluster.Options.FailedSyncRetry)
            return;
        cluster.UpdateNode(node.Id, n => n.DesiredRevision = bundle.Revision);
        SyncResult? result;
        try
        {
            result = await client.CallAsync<SyncResult>(node, "sync",
                new JsonObject { ["bundle"] = bundle.Bundle.DeepClone(), ["revision"] = bundle.Revision, ["force"] = force },
                cluster.Options.SyncTimeout, ct);
        }
        catch (NodeRpcException ex) when (ex.Unauthorized)
        {
            // Not joined (yet), left, or waiting for a new token: the same "pending" state the heartbeat reports, no alert.
            RecordRejected(node, ex.Message);
            return;
        }
        catch (NodeRpcException ex) when (ex.Conflict)
        {
            RecordConflict(node, ex.Message, bundle.Warnings);
            return;
        }
        catch (NodeRpcException ex)
        {
            if (ex.Unreachable) RecordFailure(node, ex.Message);
            // An answer of the node that is not a sync result (HTTP 413, 5xx...) comes back for the same bundle: throttle
            // it like a rejected configuration instead of re-sending the whole bundle every heartbeat.
            RecordSyncFailure(node, ex.Message, bundle.Warnings, failedRevision: ex.Unreachable ? null : bundle.Revision);
            return;
        }
        if (result is null)
        {
            RecordSyncFailure(node, "The node sent an empty answer.", bundle.Warnings, failedRevision: null);
            return;
        }
        if (!result.Apply.Success)
        {
            RecordSyncFailure(node, result.Apply.Error ?? "The node could not apply the configuration.", bundle.Warnings, bundle.Revision);
            return;
        }

        var recovered = false;
        var reachable = false;
        var now = time.GetUtcNow().UtcDateTime;
        var updated = cluster.UpdateNode(node.Id, n =>
        {
            // The node answered: the same as a successful heartbeat for reachability.
            reachable = n.OfflineRaised;
            n.OfflineRaised = false;
            n.ConsecutiveFailures = 0;
            n.LastSeenAt = now;
            n.LastError = null;
            n.AppliedRevision = result.AppliedRevision;
            n.PendingRevision = result.Pending ? bundle.Revision : null;
            n.LastSyncAt = now;
            n.SyncWarnings = [.. bundle.Warnings, .. result.Warnings.Where(w => w != "Already applied."),
                .. result.Apply.Warnings.Where(w => !result.Warnings.Contains(w))];
            if (result.Pending)
            {
                // Accepted, but not applied yet (Caddy rebuild): an earlier failure (e.g. of the previous rebuild) stays
                // reported until the node runs the configuration — no "works again" for a retry that may fail the same way.
                n.Status = n.LastSyncError is null ? ServerStatus.Online : ServerStatus.Error;
                return;
            }
            recovered = n.SyncErrorRaised;
            n.SyncErrorRaised = false;
            n.LastSyncError = null;
            n.FailedRevision = null;
            n.Status = ServerStatus.Online;
        });
        logger.LogInformation("Configuration {Revision} pushed to node {Name}{Pending}", NodeSync.Short(bundle.Revision), node.Name,
            result.Pending ? " (pending Caddy rebuild)" : "");
        if (reachable && updated is not null)
            Sink()?.Raise(EventSeverity.Recovered, "cluster", $"Server '{updated.Name}' is reachable again", updated.Url,
                key: OfflineKeyPrefix + updated.Id, alertRule: "serverOffline");
        if (recovered && updated is not null)
            Sink()?.Raise(EventSeverity.Recovered, "cluster", $"Configuration sync to server '{updated.Name}' works again", null,
                key: SyncKeyPrefix + updated.Id, alertRule: "configFailure");
    }

    /// <summary>Reachable but not (or no longer) joined with this node's key: not "offline", and not a configuration failure.</summary>
    private void RecordRejected(ClusterNode node, string error)
    {
        // Pending while waiting for the node to join (or for a new token), an error otherwise.
        var wasOffline = false;
        var rejected = cluster.UpdateNode(node.Id, n =>
        {
            wasOffline = n.OfflineRaised;
            n.OfflineRaised = false;
            n.ConsecutiveFailures = 0;
            n.LastError = error;
            n.Status = !HasAuthenticated(n) ? ServerStatus.Pending : ServerStatus.Error;
        });
        if (wasOffline && rejected is not null)
            Sink()?.Raise(EventSeverity.Recovered, "cluster", $"Server '{rejected.Name}' is reachable again", error,
                key: OfflineKeyPrefix + rejected.Id, alertRule: "serverOffline");
    }

    /// <summary>The node obeys another primary instance with the same key (cloned or restored primary): a configuration failure.</summary>
    private void RecordConflict(ClusterNode node, string error, List<string> bundleWarnings)
    {
        cluster.UpdateNode(node.Id, n =>
        {
            n.OfflineRaised = false;
            n.ConsecutiveFailures = 0;
            n.LastError = error;
        });
        RecordSyncFailure(node, error, bundleWarnings, failedRevision: null);
    }

    private void RecordFailure(ClusterNode node, string error)
    {
        var raise = false;
        var updated = cluster.UpdateNode(node.Id, n =>
        {
            n.ConsecutiveFailures++;
            n.LastError = error;
            if (n.ConsecutiveFailures >= cluster.Options.OfflineThreshold)
            {
                n.Status = ServerStatus.Offline;
                raise = !n.OfflineRaised;
                n.OfflineRaised = true;
            }
        });
        logger.LogDebug("Heartbeat to node {Name} failed: {Error}", node.Name, error);
        if (raise && updated is not null)
        {
            logger.LogWarning("Node {Name} ({Url}) is offline: {Error}", updated.Name, updated.Url, error);
            Sink()?.Raise(EventSeverity.Warning, "cluster", $"Server '{updated.Name}' is offline",
                $"{updated.ConsecutiveFailures} consecutive heartbeats to {updated.Url} failed. Last error: {error}. The server keeps serving its last applied configuration.",
                key: OfflineKeyPrefix + updated.Id, alertRule: "serverOffline");
        }
    }

    private void RecordSyncFailure(ClusterNode node, string error, List<string> bundleWarnings, string? failedRevision)
    {
        var raise = false;
        var updated = cluster.UpdateNode(node.Id, n =>
        {
            raise = !n.SyncErrorRaised || n.LastSyncError != error;
            n.SyncErrorRaised = true;
            n.LastSyncAt = time.GetUtcNow().UtcDateTime;
            n.LastSyncError = error;
            n.FailedRevision = failedRevision;
            n.SyncWarnings = bundleWarnings.ToList();
            if (n.ConsecutiveFailures < cluster.Options.OfflineThreshold) n.Status = ServerStatus.Error;
        });
        logger.LogWarning("Configuration sync to node {Name} failed: {Error}", node.Name, error);
        if (raise && updated is not null)
            Sink()?.Raise(EventSeverity.Warning, "cluster", $"Configuration sync to server '{updated.Name}' failed", error,
                key: SyncKeyPrefix + updated.Id, alertRule: "configFailure");
    }

    /// <summary>
    /// The bundle for the current configuration (rebuilt at most once a second, or right after a change), built only from
    /// committed configuration: while a change is being persisted/applied/rolled back (IConfigMutationLock held for longer
    /// than BundleLockTimeout) the last bundle built is reused.
    /// </summary>
    public async Task<BundleSnapshot> CurrentBundleAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        lock (_bundleLock)
            if (_bundle is { } cached && now - _bundleAt <= TimeSpan.FromSeconds(1)) return cached;
        IDisposable? held = null;
        if (services.GetService<IConfigMutationLock>() is { } mutation)
        {
            held = await mutation.TryAcquireAsync(cluster.Options.BundleLockTimeout, ct);
            if (held is null)
            {
                lock (_bundleLock)
                    if (_lastBuilt is { } last) return last;
                held = await mutation.AcquireAsync(ct);
            }
        }
        try
        {
            var built = ClusterBundle.Build(store, secrets);
            lock (_bundleLock)
            {
                _bundle = built;
                _lastBuilt = built;
                _bundleAt = time.GetUtcNow().UtcDateTime;
            }
            return built;
        }
        finally
        {
            held?.Dispose();
        }
    }

    private void InvalidateBundle()
    {
        lock (_bundleLock) _bundle = null;
    }

    private IEventSink? Sink() => services.GetService<IEventSink>();

    public override void Dispose()
    {
        _wake.Dispose();
        base.Dispose();
    }
}
