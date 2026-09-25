using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using CaddyManager.Core;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Cluster;

/// <summary>An RPC to a node failed. <see cref="Unreachable"/> distinguishes transport problems from answers of the node.</summary>
public sealed class NodeRpcException(string message, bool unreachable = false, bool unauthorized = false) : Exception(message)
{
    public bool Unreachable { get; } = unreachable;
    public bool Unauthorized { get; } = unauthorized;
}

/// <summary>
/// Primary → node transport: encrypted envelopes POSTed to &lt;node url&gt;/api/cluster/rpc.
/// Each node gets its own HTTP handler (never through the outbound proxy: node URLs are internal) whose TLS check accepts
/// a valid chain OR a certificate whose SHA-256 fingerprint equals the one pinned for that node. A handler per node (rather
/// than one shared handler) is needed because pooled connections are validated once per connection: a shared pool could
/// reuse a connection validated against another node's pin.
/// </summary>
public sealed class NodeClient(ClusterService cluster, TimeProvider time, ILogger<NodeClient> logger) : IDisposable
{
    public const string RpcPath = "api/cluster/rpc";
    private sealed record Channel(string Url, string? Pin, HttpClient Client);
    private readonly ConcurrentDictionary<string, Channel> _channels = new();

    /// <summary>Calls <paramref name="op"/> on the node and returns the result node (null for a void result).</summary>
    public async Task<JsonNode?> CallAsync(ClusterNode node, string op, object? args, TimeSpan timeout, CancellationToken ct = default)
    {
        byte[] key;
        try { key = cluster.NodeKey(node); }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
        {
            // e.g. the database was restored on another machine (DPAPI LocalMachine keys do not travel).
            throw new NodeRpcException("The stored cluster key of this server cannot be decrypted here. Regenerate its join token and join " +
                                       "the node again.", unauthorized: true);
        }
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["op"] = op,
            ["args"] = args switch { null => null, JsonNode n => n.DeepClone(), _ => JsonSerializer.SerializeToNode(args, JsonDefaults.Api) },
        });
        var request = ClusterCrypto.SealRequest(key, node.Id, plaintext, time.GetUtcNow().ToUnixTimeSeconds());

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        HttpResponseMessage resp;
        try
        {
            var http = await ChannelForAsync(node, cts.Token);
            using var msg = new HttpRequestMessage(HttpMethod.Post, RpcPath) { Content = JsonContent.Create(request) };
            msg.Headers.Add("X-CPM-Request", "1");
            resp = await http.SendAsync(msg, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new NodeRpcException($"The node did not answer within {timeout.TotalSeconds:0} s ({node.Url}).", unreachable: true);
        }
        catch (HttpRequestException ex)
        {
            throw new NodeRpcException($"Cannot reach the node at {node.Url}: {Describe(ex)}", unreachable: true);
        }

        using (resp)
        {
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                throw new NodeRpcException("The node rejected the request (authentication failed). It may have left the cluster, joined with " +
                                           "another token, or its clock differs from the primary's by more than 5 minutes.", unauthorized: true);
            if (resp.StatusCode == HttpStatusCode.NotFound)
                throw new NodeRpcException($"The server at {node.Url} is not a cluster node: it has not joined yet (run \"cluster join\" or paste " +
                                           "the join token under Settings → Cluster on it) or it left the cluster.", unauthorized: true);
            if ((int)resp.StatusCode is >= 300 and < 400)
                throw new NodeRpcException($"The node redirects to {resp.Headers.Location} (it probably requires HTTPS for its management UI): " +
                                           "change this server's URL to that address.");
            if (!resp.IsSuccessStatusCode)
                throw new NodeRpcException($"The node answered HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} ({node.Url}).");

            RpcEnvelope? envelope;
            try { envelope = await resp.Content.ReadFromJsonAsync<RpcEnvelope>(cts.Token); }
            catch (JsonException) { envelope = null; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new NodeRpcException($"The node did not answer within {timeout.TotalSeconds:0} s ({node.Url}).", unreachable: true);
            }
            if (envelope is null || envelope.NodeId != node.Id)
                throw new NodeRpcException("The node sent an invalid response.");
            var plain = ClusterCrypto.Open(key, envelope, ClusterCrypto.ResponseAad(node.Id, envelope.Ts, envelope.Nonce, request.Nonce));
            if (plain is null) throw new NodeRpcException("The node's response could not be authenticated.");

            var reply = JsonNode.Parse(plain) as JsonObject ?? throw new NodeRpcException("The node sent an invalid response.");
            if (reply["ok"]?.GetValue<bool>() != true)
                throw new NodeRpcException(reply["error"]?.GetValue<string>() ?? "The node reported an error.");
            return reply["result"];
        }
    }

    /// <summary>Typed convenience wrapper.</summary>
    public async Task<T?> CallAsync<T>(ClusterNode node, string op, object? args, TimeSpan timeout, CancellationToken ct = default)
    {
        var result = await CallAsync(node, op, args, timeout, ct);
        return result is null ? default : result.Deserialize<T>(JsonDefaults.Api);
    }

    /// <summary>Forgets the node's connection pool (after its URL or pin changed, or it was removed).</summary>
    public void Reset(string nodeId)
    {
        if (_channels.TryRemove(nodeId, out var c)) Retire(c.Client);
    }

    /// <summary>Disposes a replaced client once calls still using it have finished (disposing at once would abort them).</summary>
    private static void Retire(HttpClient client) =>
        _ = Task.Delay(TimeSpan.FromMinutes(3)).ContinueWith(_ => client.Dispose(), TaskScheduler.Default);

    private async Task<HttpClient> ChannelForAsync(ClusterNode node, CancellationToken ct)
    {
        var pin = node.PinnedFingerprint;
        // Trust on first use: an https node contacted without a pin gets the fingerprint of the certificate it presents now.
        if (pin is null && node.Url.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
        {
            pin = await ProbeFingerprintAsync(node.Url, TimeSpan.FromSeconds(5), ct);
            if (pin is not null)
            {
                cluster.UpdateNode(node.Id, n => n.PinnedFingerprint ??= pin);
                logger.LogInformation("Pinned the HTTPS certificate of node {Name} ({Url}): {Fingerprint}", node.Name, node.Url, pin);
            }
        }
        if (_channels.TryGetValue(node.Id, out var existing) && existing.Url == node.Url && existing.Pin == pin) return existing.Client;

        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            AllowAutoRedirect = false,
            UseCookies = false,
        };
        var pinned = pin;
        handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, _, errors) =>
            errors == SslPolicyErrors.None ||
            (pinned is not null && cert is not null && ClusterCrypto.SameFingerprint(FingerprintOf(cert), pinned));
        var client = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(node.Url.TrimEnd('/') + "/"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CaddyProxyManager-Cluster/1.0");
        var channel = new Channel(node.Url, pin, client);
        _channels.AddOrUpdate(node.Id, channel, (_, old) =>
        {
            Retire(old.Client);
            return channel;
        });
        return client;
    }

    public static string FingerprintOf(X509Certificate cert) => ClusterCrypto.Fingerprint(SHA256.HashData(cert.GetRawCertData()));

    /// <summary>
    /// SHA-256 fingerprint of the certificate an https URL presents (any certificate is accepted for this probe; nothing is
    /// sent). Null for http URLs or when the server cannot be reached.
    /// </summary>
    public static async Task<string?> ProbeFingerprintAsync(string url, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(uri.Host, uri.Port, cts.Token);
            string? fingerprint = null;
            await using var ssl = new SslStream(tcp.GetStream(), false, (_, cert, _, _) =>
            {
                if (cert is not null) fingerprint = FingerprintOf(cert);
                return true;
            });
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = uri.IdnHost,
            }, cts.Token);
            return fingerprint;
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
        {
            return null;
        }
    }

    private static string Describe(HttpRequestException ex)
    {
        var inner = ex.InnerException;
        while (inner?.InnerException is not null && inner is not System.Security.Authentication.AuthenticationException) inner = inner.InnerException;
        return inner is System.Security.Authentication.AuthenticationException
            ? "its HTTPS certificate is not trusted and does not match the pinned fingerprint (re-pin it if the certificate was replaced)"
            : ex.Message;
    }

    public void Dispose()
    {
        foreach (var c in _channels.Values) c.Client.Dispose();
        _channels.Clear();
    }
}
