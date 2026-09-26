using System.Text.Json;
using System.Text.Json.Nodes;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Cluster;

/// <summary>
/// Node side of POST /api/cluster/rpc. Rejections are deliberately uninformative to the caller (404 when this server is
/// not a node; 401 without detail for an unknown node id, a timestamp outside ±MaxClockSkew, a request signed before this
/// manager started (replay after a restart), a reused nonce or anything that does not decrypt) and logged here with the
/// reason — at most one warning per remote address and window (<see cref="RpcRejectionThrottle"/>), so unauthenticated
/// callers cannot flood the manager log or the Windows event log. An address that keeps failing gets 429 without its
/// request being read. Request bodies are limited to ClusterOptions.MaxRpcBodyBytes (chunked ones too: Kestrel enforces
/// the limit while reading).
/// </summary>
public sealed class NodeRpcHandler(
    ClusterService cluster,
    NodeSync sync,
    NonceCache nonces,
    RpcRejectionThrottle throttle,
    IServiceProvider services,
    TimeProvider time,
    ILogger<NodeRpcHandler> logger)
{
    /// <summary>Error code of a reply refused because the request came from another primary instance than the pinned one.</summary>
    public const string PrimaryConflictCode = "primaryConflict";
    /// <summary>Tolerance of the replay-after-restart check (timestamps are whole seconds; the measured offset may jitter).</summary>
    private const long ReplayGraceSeconds = 2;
    private DateTime _conflictLoggedAt;

    public async Task<IResult> HandleAsync(HttpContext http)
    {
        var settings = cluster.Settings;
        if (settings.Role != ClusterRole.Node || settings.NodeId is null) return ApiResults.NotFound("Cluster endpoint");
        var remote = http.Connection.RemoteIpAddress?.ToString() ?? "?";
        var options = cluster.Options;
        if (throttle.IsBlocked(remote, options.RpcRejectionLimit, options.RpcRejectionWindow))
            return Results.Problem(title: "Too many rejected requests", statusCode: StatusCodes.Status429TooManyRequests);

        byte[]? key;
        try { key = cluster.NodeKey(); }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
        {
            logger.LogError("The stored cluster key cannot be decrypted (database restored on another machine?): join the cluster again");
            key = null;
        }
        if (key is null) return Reject(remote, "no usable cluster key on this node");

        // Kestrel's default request limit (30 MB) is below the largest bundle; the feature also bounds chunked bodies.
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = options.MaxRpcBodyBytes;
        RpcEnvelope? envelope;
        try
        {
            if (http.Request.ContentLength > options.MaxRpcBodyBytes) return TooLarge(remote, http.Request.ContentLength.Value);
            envelope = await JsonSerializer.DeserializeAsync<RpcEnvelope>(http.Request.Body, cancellationToken: http.RequestAborted);
        }
        catch (JsonException)
        {
            return Reject(remote, "malformed envelope");
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return TooLarge(remote, null);
        }
        if (envelope is null || envelope.V != 1) return Reject(remote, "malformed envelope");
        if (!string.Equals(envelope.NodeId, settings.NodeId, StringComparison.Ordinal)) return Reject(remote, $"unknown node id '{Trim(envelope.NodeId)}'");
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        if (Math.Abs(now - envelope.Ts) > (long)options.MaxClockSkew.TotalSeconds)
            return Reject(remote, $"timestamp {envelope.Ts} is {now - envelope.Ts} s away from this server's clock");
        var plain = ClusterCrypto.Open(key, envelope, ClusterCrypto.RequestAad(envelope.NodeId, envelope.Ts, envelope.Nonce));
        if (plain is null) return Reject(remote, "decryption failed (wrong key or tampered request)");
        // The nonce cache lives in memory. A request signed (on the primary's clock, whose offset to this clock was measured
        // before the restart) before this manager started may have been accepted already: refuse it as a possible replay.
        if (settings.PrimaryClockOffsetSeconds is { } offset && envelope.Ts < nonces.StartedAtUnix + offset - ReplayGraceSeconds)
            return Reject(remote, $"request signed before this manager started (timestamp {envelope.Ts}): possible replay");
        if (!nonces.TryUse(envelope.Nonce, options.NonceRetention)) return Reject(remote, "replayed nonce");

        cluster.TouchPrimaryContact();
        cluster.ObservePrimaryClock(envelope.Ts - now);
        JsonObject reply;
        var op = "?";
        try
        {
            var request = JsonNode.Parse(plain) as JsonObject ?? throw new JsonException("not an object");
            op = request["op"]?.GetValue<string>() ?? "";
            if (request["primaryId"]?.GetValue<string>() is { Length: > 0 } primaryId && !cluster.AcceptPrimaryInstance(primaryId))
            {
                LogConflict(remote, op);
                reply = new JsonObject
                {
                    ["ok"] = false,
                    ["code"] = PrimaryConflictCode,
                    ["error"] = $"This server obeys another instance of the primary '{cluster.PrimaryName}' that uses the same key (a cloned or " +
                                "restored primary running alongside the original?). To manage it from this instance, regenerate its join token " +
                                "here and join the node again with it.",
                };
            }
            else
            {
                var result = await DispatchAsync(op, request["args"] as JsonObject ?? new JsonObject(), http.RequestAborted);
                reply = new JsonObject { ["ok"] = true, ["result"] = result };
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !http.RequestAborted.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Cluster RPC {Op} failed", op);
            reply = new JsonObject { ["ok"] = false, ["error"] = Scrub(ex is RpcFault or InvalidOperationException ? ex.Message : $"{op} failed: {ex.Message}") };
        }

        // Sealed with the key the request was verified with: after a `rekey` only this answer still uses the previous key.
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
                var pending = sync.EffectivePendingRevision();
                // The primary's current revision equals what runs here (e.g. a rejected change was undone there): the last
                // sync error no longer applies.
                if (args["desiredRevision"]?.GetValue<string>() is { Length: > 0 } desired && cluster.Settings is { LastSyncError: not null } current &&
                    current.AppliedRevision == desired && pending is null)
                    cluster.UpdateSettings(s =>
                    {
                        s.LastSyncError = null;
                        s.LastSyncErrorRevision = null;
                    });
                var s = cluster.Settings;
                var samples = cluster.GetLocalSamples(null);
                return Node(new HelloResult
                {
                    Name = cluster.ServerName,
                    AppliedRevision = s.AppliedRevision,
                    PendingRevision = pending,
                    LastSyncError = s.LastSyncError,
                    LastSyncErrorRevision = s.LastSyncError is null ? null : s.LastSyncErrorRevision,
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
            case "rekey":
            {
                // Key rotation ("Regenerate token" on the primary): the new secret replaces the current one at once. Only the
                // answer to this request is still sealed with the previous key.
                byte[] secret;
                try { secret = Convert.FromBase64String(args["secret"]?.GetValue<string>() ?? ""); }
                catch (FormatException) { throw new RpcFault("rekey needs a base64 secret."); }
                if (secret.Length != ClusterCrypto.SecretLength) throw new RpcFault("rekey needs a 32-byte secret.");
                cluster.Rekey(secret);
                Audit("keyRotated", "cluster", cluster.PrimaryName ?? "", "The cluster primary rotated this node's key; the previous key and join token no longer work");
                return new JsonObject { ["rotated"] = true };
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
        Log(remote, reason);
        return Results.Problem(title: "Unauthorized", statusCode: StatusCodes.Status401Unauthorized);
    }

    private IResult TooLarge(string remote, long? length)
    {
        var max = cluster.Options.MaxRpcBodyBytes;
        Log(remote, $"request body {(length is { } l ? $"of {l} bytes " : "")}exceeds the limit of {max} bytes");
        return Results.Problem(title: "Request too large", detail: $"The request exceeds this node's limit of {max / 1048576.0:0.#} MB.",
            statusCode: StatusCodes.Status413PayloadTooLarge);
    }

    private void Log(string remote, string reason)
    {
        if (throttle.Record(remote, cluster.Options.RpcRejectionWindow) is { } summary)
            logger.LogWarning("Rejected cluster RPC from {Remote}: {Reason}{Summary}", remote, reason, summary);
        else
            logger.LogDebug("Rejected cluster RPC from {Remote}: {Reason}", remote, reason);
    }

    private void LogConflict(string remote, string op)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (now - _conflictLoggedAt < TimeSpan.FromMinutes(1)) return;
        _conflictLoggedAt = now;
        logger.LogWarning("Refused cluster RPC {Op} from {Remote}: it comes from another instance of the primary than the one this node obeys " +
                          "(a cloned or restored primary using the same key?)", op, remote);
    }

    private string Scrub(string text) => services.GetService<ISecretScrubber>()?.Scrub(text) ?? text;

    private static string Trim(string s) => s.Length > 40 ? s[..40] + "…" : s;
}

/// <summary>Answer of the `hello` RPC (heartbeat).</summary>
public sealed record HelloResult
{
    public string Name { get; init; } = "";
    public string? AppliedRevision { get; init; }
    public string? PendingRevision { get; init; }
    public string? LastSyncError { get; init; }
    /// <summary>Revision <see cref="LastSyncError"/> belongs to (the primary throttles pushing exactly that revision again).</summary>
    public string? LastSyncErrorRevision { get; init; }
    public ServerInfo? Info { get; init; }
    public ResourceSample? Latest { get; init; }
}

/// <summary>
/// Bounds what unauthenticated callers can make a node do, per remote address: after <c>limit</c> rejected RPCs within a
/// window the address gets 429 without its requests being read. Logging is aggregated: one warning per address and window
/// (the next one says how many were suppressed) and at most <see cref="MaxWarningsPerWindow"/> warnings per window over all
/// addresses; everything else is logged at debug level.
/// </summary>
public sealed class RpcRejectionThrottle(TimeProvider time)
{
    public const int MaxWarningsPerWindow = 10;
    private const int MaxTrackedAddresses = 10_000;

    private sealed class Window
    {
        public DateTime Start;
        public int Count;
        public int Suppressed;
    }

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Window> _windows = new(StringComparer.Ordinal);
    private DateTime _globalStart;
    private int _globalWarnings;

    /// <summary>True while <paramref name="remote"/> has caused <paramref name="limit"/> rejections in its current window.</summary>
    public bool IsBlocked(string remote, int limit, TimeSpan window)
    {
        var now = time.GetUtcNow().UtcDateTime;
        lock (_lock)
            return _windows.TryGetValue(remote, out var w) && now - w.Start < window && w.Count >= limit;
    }

    /// <summary>Counts a rejection. Returns the suffix of a warning to log, or null when it is logged at debug level only.</summary>
    public string? Record(string remote, TimeSpan window)
    {
        var now = time.GetUtcNow().UtcDateTime;
        lock (_lock)
        {
            if (now - _globalStart >= window)
            {
                _globalStart = now;
                _globalWarnings = 0;
            }
            if (!_windows.TryGetValue(remote, out var w))
            {
                if (_windows.Count >= MaxTrackedAddresses)
                    foreach (var stale in _windows.Where(kv => now - kv.Value.Start >= window).Select(kv => kv.Key).ToList()) _windows.Remove(stale);
                if (_windows.Count >= MaxTrackedAddresses) return null;
                _windows[remote] = w = new Window { Start = now };
            }
            var suffix = "";
            if (now - w.Start >= window)
            {
                if (w.Suppressed > 0) suffix = $" ({w.Suppressed} more rejected requests from this address in the previous {window.TotalSeconds:0} s were not logged)";
                w.Start = now;
                w.Count = 0;
                w.Suppressed = 0;
            }
            w.Count++;
            if (w.Count > 1 || _globalWarnings >= MaxWarningsPerWindow)
            {
                w.Suppressed++;
                return null;
            }
            _globalWarnings++;
            return suffix + "; further rejections from this address are summarised";
        }
    }
}
