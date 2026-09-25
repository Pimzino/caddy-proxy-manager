using System.Text.Json;
using System.Text.Json.Nodes;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Cluster;

/// <summary>
/// Node side of POST /api/cluster/rpc. Rejections are deliberately uninformative to the caller (404 when this server is
/// not a node; 401 without detail for an unknown node id, a timestamp outside ±MaxClockSkew, a reused nonce or anything
/// that does not decrypt) and logged here with the reason.
/// </summary>
public sealed class NodeRpcHandler(
    ClusterService cluster,
    NodeSync sync,
    NonceCache nonces,
    IServiceProvider services,
    TimeProvider time,
    ILogger<NodeRpcHandler> logger)
{
    private const int MaxBodyBytes = 64 * 1024 * 1024;

    public async Task<IResult> HandleAsync(HttpContext http)
    {
        var settings = cluster.Settings;
        if (settings.Role != ClusterRole.Node || settings.NodeId is null) return ApiResults.NotFound("Cluster endpoint");
        byte[]? key;
        try { key = cluster.NodeKey(); }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
        {
            logger.LogError("The stored cluster key cannot be decrypted (database restored on another machine?): join the cluster again");
            key = null;
        }
        if (key is null) return Reject(http.Connection.RemoteIpAddress?.ToString() ?? "?", "no usable cluster key on this node");

        var remote = http.Connection.RemoteIpAddress?.ToString() ?? "?";
        RpcEnvelope? envelope;
        try
        {
            if (http.Request.ContentLength > MaxBodyBytes) return Reject(remote, "request too large");
            envelope = await JsonSerializer.DeserializeAsync<RpcEnvelope>(http.Request.Body, cancellationToken: http.RequestAborted);
        }
        catch (JsonException)
        {
            return Reject(remote, "malformed envelope");
        }
        if (envelope is null || envelope.V != 1) return Reject(remote, "malformed envelope");
        if (!string.Equals(envelope.NodeId, settings.NodeId, StringComparison.Ordinal)) return Reject(remote, $"unknown node id '{Trim(envelope.NodeId)}'");
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        if (Math.Abs(now - envelope.Ts) > (long)cluster.Options.MaxClockSkew.TotalSeconds)
            return Reject(remote, $"timestamp {envelope.Ts} is {now - envelope.Ts} s away from this server's clock");
        var plain = ClusterCrypto.Open(key, envelope, ClusterCrypto.RequestAad(envelope.NodeId, envelope.Ts, envelope.Nonce));
        if (plain is null) return Reject(remote, "decryption failed (wrong key or tampered request)");
        if (!nonces.TryUse(envelope.Nonce, cluster.Options.NonceRetention)) return Reject(remote, "replayed nonce");

        cluster.TouchPrimaryContact();
        JsonObject reply;
        string op = "?";
        try
        {
            var request = JsonNode.Parse(plain) as JsonObject ?? throw new JsonException("not an object");
            op = request["op"]?.GetValue<string>() ?? "";
            var result = await DispatchAsync(op, request["args"] as JsonObject ?? new JsonObject(), http.RequestAborted);
            reply = new JsonObject { ["ok"] = true, ["result"] = result };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !http.RequestAborted.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Cluster RPC {Op} failed", op);
            reply = new JsonObject { ["ok"] = false, ["error"] = ex is RpcFault or InvalidOperationException ? ex.Message : $"{op} failed: {ex.Message}" };
        }

        var response = ClusterCrypto.SealResponse(key, envelope.NodeId, JsonSerializer.SerializeToUtf8Bytes(reply),
            time.GetUtcNow().ToUnixTimeSeconds(), envelope.Nonce);
        return Results.Json(response);
    }

    private sealed class RpcFault(string message) : Exception(message);

    private async Task<JsonNode?> DispatchAsync(string op, JsonObject args, CancellationToken ct)
    {
        switch (op)
        {
            case "hello":
            {
                if (args["primaryName"]?.GetValue<string>() is { Length: > 0 } primaryName && primaryName != cluster.PrimaryName)
                    cluster.UpdateSettings(s => s.PrimaryName = primaryName);
                // The primary's current revision equals what runs here (e.g. a rejected change was undone there): the last
                // sync error no longer applies.
                if (args["desiredRevision"]?.GetValue<string>() is { Length: > 0 } desired && cluster.Settings is { LastSyncError: not null } current &&
                    current.AppliedRevision == desired && current.PendingRevision is null)
                    cluster.UpdateSettings(s => s.LastSyncError = null);
                var s = cluster.Settings;
                var samples = cluster.GetLocalSamples(null);
                return Node(new HelloResult
                {
                    Name = cluster.ServerName,
                    AppliedRevision = s.AppliedRevision,
                    PendingRevision = s.PendingRevision,
                    LastSyncError = s.LastSyncError,
                    Info = await cluster.GetLocalInfoAsync(ct),
                    Latest = samples.Count > 0 ? samples[^1] : null,
                });
            }
            case "info":
                return Node(await cluster.GetLocalInfoAsync(ct));
            case "samples":
            {
                DateTime? since = args["since"] is JsonValue v && v.TryGetValue<DateTime>(out var d) ? d.ToUniversalTime() : null;
                return Node(cluster.GetLocalSamples(since));
            }
            case "traffic":
            {
                var query = args["query"]?.Deserialize<TrafficQuery>(JsonDefaults.Api) ?? new TrafficQuery();
                return Node(await cluster.GetLocalTrafficAsync(query, ct));
            }
            case "sync":
            {
                if (args["bundle"] is not JsonObject bundle || args["revision"]?.GetValue<string>() is not { Length: > 0 } revision)
                    throw new RpcFault("sync needs a bundle and its revision.");
                var force = args["force"]?.GetValue<bool>() ?? false;
                // Not bound to the request: a sync must not be abandoned half-way when the primary gives up waiting.
                return Node(await sync.ApplyAsync(bundle, revision, force, CancellationToken.None));
            }
            case "caddy.status":
                return Node(await Required<ICaddyHost>().GetStatusAsync(ct));
            case "caddy.restart":
            {
                var host = Required<ICaddyHost>();
                await host.RestartAsync(CancellationToken.None);
                Audit("restarted", "caddy", "Caddy", "Restarted by the cluster primary");
                return Node(await host.GetStatusAsync(ct));
            }
            case "caddy.update":
            {
                var version = args["version"]?.GetValue<string>();
                var job = Required<ICaddyBinaryManager>().StartInstallOrUpdate(string.IsNullOrWhiteSpace(version) ? null : version.Trim());
                Audit("install", "caddyBinary", version ?? "latest", "Started by the cluster primary");
                return Node(job);
            }
            case "job":
            {
                var id = args["id"]?.GetValue<string>() ?? "";
                return Node(Required<IJobRunner>().Get(id) ?? throw new RpcFault("Job was not found."));
            }
            case "leave":
            {
                var primary = cluster.PrimaryName;
                cluster.Leave();
                Audit("left", "cluster", primary ?? "", "Removed from the cluster by the primary; this server is standalone again");
                return new JsonObject { ["role"] = "standalone" };
            }
            default:
                throw new RpcFault($"Unknown operation '{op}'. Update Caddy Proxy Manager on this server.");
        }
    }

    private T Required<T>() where T : notnull =>
        services.GetService<T>() ?? throw new RpcFault($"{typeof(T).Name} is not available on this server.");

    private void Audit(string action, string objectType, string name, string details) =>
        services.GetService<IAuditLog>()?.Record(action, objectType, null, name, details);

    private static JsonNode? Node<T>(T value) => JsonSerializer.SerializeToNode(value, JsonDefaults.Api);

    private IResult Reject(string remote, string reason)
    {
        logger.LogWarning("Rejected cluster RPC from {Remote}: {Reason}", remote, reason);
        return Results.Problem(title: "Unauthorized", statusCode: StatusCodes.Status401Unauthorized);
    }

    private static string Trim(string s) => s.Length > 40 ? s[..40] + "…" : s;
}

/// <summary>Answer of the `hello` RPC (heartbeat).</summary>
public sealed record HelloResult
{
    public string Name { get; init; } = "";
    public string? AppliedRevision { get; init; }
    public string? PendingRevision { get; init; }
    public string? LastSyncError { get; init; }
    public ServerInfo? Info { get; init; }
    public ResourceSample? Latest { get; init; }
}
