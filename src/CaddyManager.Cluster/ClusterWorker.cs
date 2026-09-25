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
/// current one, and the server-offline / server-sync events. Nodes are handled independently (one slow node never delays
/// another); work on one node is serialised.
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

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _nodeLocks = new();
    private readonly ConcurrentDictionary<string, DateTime> _nextHeartbeat = new();
    /// <summary>Nodes that must receive the current configuration as soon as no other work runs for them.</summary>
    private readonly ConcurrentDictionary<string, byte> _pushPending = new();
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly Lock _bundleLock = new();
    private BundleSnapshot? _bundle;
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
            foreach (var n in nodes) _pushPending[n.Id] = 0;
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
            _ = RunForNodeAsync(node.Id, HeartbeatCoreAsync, ct);
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
    /// Heartbeat now and return the fresh node row; the cached row when other work for the node (e.g. a long sync) does not
    /// finish within QueryTimeout.
    /// </summary>
    public async Task<ClusterNode?> HeartbeatAsync(string nodeId, CancellationToken ct = default)
    {
        var gate = _nodeLocks.GetOrAdd(nodeId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(cluster.Options.QueryTimeout, ct)) return cluster.FindNode(nodeId);
        try
        {
            if (cluster.FindNode(nodeId) is not { } node) return null;
            await HeartbeatCoreAsync(node, ct);
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
            await SyncAsync(node, force: true, ct);
            return cluster.FindNode(nodeId);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task HeartbeatCoreAsync(ClusterNode node, CancellationToken ct)
    {
        var bundle = CurrentBundle();
        HelloResult? hello;
        try
        {
            hello = await client.CallAsync<HelloResult>(node, "hello",
                new { primaryName = cluster.ServerName, desiredRevision = bundle.Revision }, cluster.Options.QueryTimeout, ct);
        }
        catch (NodeRpcException ex) when (ex.Unauthorized)
        {
            // Reachable but not (or no longer) joined with this node's key: not "offline". Pending while waiting for the node to
            // join with a newly issued token, an error otherwise.
            var wasOffline = false;
            var rejected = cluster.UpdateNode(node.Id, n =>
            {
                wasOffline = n.OfflineRaised;
                n.OfflineRaised = false;
                n.ConsecutiveFailures = 0;
                n.LastError = ex.Message;
                n.Status = n.LastSeenAt is null || n.TokenIssuedAt > n.LastSeenAt ? ServerStatus.Pending : ServerStatus.Error;
            });
            if (wasOffline && rejected is not null)
                Sink()?.Raise(EventSeverity.Recovered, "cluster", $"Server '{rejected.Name}' is reachable again", ex.Message,
                    key: OfflineKeyPrefix + rejected.Id, alertRule: "serverOffline");
            return;
        }
        catch (NodeRpcException ex)
        {
            RecordFailure(node, ex.Message);
            return;
        }
        if (hello is null)
        {
            RecordFailure(node, "The node sent an empty answer.");
            return;
        }

        var recovered = false;
        var syncRecovered = false;
        var inSync = hello.AppliedRevision == bundle.Revision && hello.PendingRevision is null;
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

        if (inSync || updated.PendingRevision == bundle.Revision) return;
        if (updated.FailedRevision == bundle.Revision && updated.LastSyncAt is { } failedAt &&
            time.GetUtcNow().UtcDateTime - failedAt < cluster.Options.FailedSyncRetry)
            return; // the node rejected exactly this configuration recently: wait for a change, "Sync now" or the retry period
        await SyncAsync(updated, force: false, ct, bundle);
    }

    private async Task SyncAsync(ClusterNode node, bool force, CancellationToken ct, BundleSnapshot? bundle = null)
    {
        bundle ??= CurrentBundle();
        cluster.UpdateNode(node.Id, n => n.DesiredRevision = bundle.Revision);
        SyncResult? result;
        try
        {
            result = await client.CallAsync<SyncResult>(node, "sync",
                new JsonObject { ["bundle"] = bundle.Bundle.DeepClone(), ["revision"] = bundle.Revision, ["force"] = force },
                cluster.Options.SyncTimeout, ct);
        }
        catch (NodeRpcException ex)
        {
            if (ex.Unreachable) RecordFailure(node, ex.Message);
            RecordSyncFailure(node, ex.Message, bundle.Warnings, failedRevision: null);
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
        var updated = cluster.UpdateNode(node.Id, n =>
        {
            recovered = n.SyncErrorRaised;
            n.SyncErrorRaised = false;
            // The node answered: the same as a successful heartbeat for reachability.
            reachable = n.OfflineRaised;
            n.OfflineRaised = false;
            n.ConsecutiveFailures = 0;
            n.LastSeenAt = time.GetUtcNow().UtcDateTime;
            n.LastError = null;
            n.AppliedRevision = result.AppliedRevision;
            n.PendingRevision = result.Pending ? bundle.Revision : null;
            n.LastSyncAt = time.GetUtcNow().UtcDateTime;
            n.LastSyncError = null;
            n.FailedRevision = null;
            n.SyncWarnings = [.. bundle.Warnings, .. result.Warnings.Where(w => w != "Already applied."),
                .. result.Apply.Warnings.Where(w => !result.Warnings.Contains(w))];
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

    /// <summary>The bundle for the current configuration (rebuilt at most once a second, or right after a change).</summary>
    public BundleSnapshot CurrentBundle()
    {
        lock (_bundleLock)
        {
            var now = time.GetUtcNow().UtcDateTime;
            if (_bundle is null || now - _bundleAt > TimeSpan.FromSeconds(1))
            {
                _bundle = ClusterBundle.Build(store, secrets);
                _bundleAt = now;
            }
            return _bundle;
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
