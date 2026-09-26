using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Xunit.Abstractions;
using static CaddyManager.Cluster.Tests.ClusterE2ETests;
using static CaddyManager.Cluster.Tests.ClusterKeyRotationE2ETests;

namespace CaddyManager.Cluster.Tests;

/// <summary>
/// End to end against a real node's POST /api/cluster/rpc: unauthenticated junk cannot flood the node's log (one warning per
/// address and window, then 429 without reading the body), request bodies are bounded also when chunked, a legitimate
/// request larger than Kestrel's default 30 MB limit is accepted, and a sync the node refuses at the HTTP level (413) is
/// not re-sent every heartbeat. Writes e2e-artifacts/cluster-rpc-limits-e2e.json.
/// </summary>
public sealed class ClusterRpcLimitsE2ETests(ITestOutputHelper output)
{
    private const int RejectionLimit = 20;
    private static readonly TimeSpan RejectionWindow = TimeSpan.FromSeconds(4);
    private readonly JsonObject _report = E2EArtifacts.Report(nameof(ClusterRpcLimitsE2ETests) + "." + nameof(RpcEndpointIsBoundedAgainstFloodsAndLargeBodies));
    private readonly JsonArray _steps = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    [Fact]
    public async Task RpcEndpointIsBoundedAgainstFloodsAndLargeBodies()
    {
        Assert.True(DevCaddy.Path is not null, "The development Caddy binary .dev/bin/caddy is required (copy .dev from the main checkout).");
        _report["steps"] = _steps;
        await using var upstream = await Upstream.StartAsync();
        Manager? primary = null, node = null;
        try
        {
            var p = Manager.CreateAsync("primary-lim");
            var n = Manager.CreateAsync("node-lim", clusterOptions: o =>
            {
                o.RpcRejectionLimit = RejectionLimit;
                o.RpcRejectionWindow = RejectionWindow;
            });
            primary = await p;
            node = await n;
            var P = primary;
            var N = node;
            var added = await P.Api.PostAsJsonAsync("api/servers", new { name = "node-lim", url = N.Url }).OkJsonAsync("POST /api/servers");
            var nodeId = added.GetProperty("server").GetProperty("id").GetString()!;
            var token = added.GetProperty("joinToken").GetString()!;
            await N.Api.PostAsJsonAsync("api/cluster/join", new { token }).OkJsonAsync("join");
            await WaitInSync(P, nodeId, null, "initial sync");
            var nodeOptions = N.Services.GetRequiredService<ClusterService>().Options;

            await Step("an unauthenticated flood logs one warning, then gets 429 without being read; the node is usable again after the window", async () =>
            {
                int Warnings() => N.Logs.Lines.Count(l => l.Contains(" Warning ") && l.Contains("Rejected cluster RPC"));
                var before = Warnings();
                var codes = new Dictionary<int, int>();
                using var junk = N.NewClient();
                for (var i = 0; i < 200; i++)
                {
                    HttpContent body = i % 2 == 0
                        ? new StringContent("{not json", Encoding.UTF8, "application/json")
                        : JsonContent.Create(ClusterCrypto.SealRequest(ClusterCrypto.DeriveKey(ClusterCrypto.NewSecret()), nodeId, "{}"u8.ToArray(), DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
                    using var resp = await junk.PostAsync("api/cluster/rpc", body);
                    codes[(int)resp.StatusCode] = codes.GetValueOrDefault((int)resp.StatusCode) + 1;
                }
                var warnings = Warnings() - before;
                // Before the fix: 200 × 401 and 200 warnings (each one also an Application event log record on Windows).
                Assert.Equal(RejectionLimit, codes.GetValueOrDefault(401));
                Assert.Equal(200 - RejectionLimit, codes.GetValueOrDefault(429));
                Assert.True(warnings <= 1, $"{warnings} warnings logged for 200 rejected requests");

                // After the window the address is served again; the next warning summarises what was suppressed.
                await Task.Delay(RejectionWindow + TimeSpan.FromMilliseconds(300));
                using (var resp = await junk.PostAsync("api/cluster/rpc", new StringContent("{not json", Encoding.UTF8, "application/json")))
                    Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
                var summary = N.Logs.Lines.Last(l => l.Contains(" Warning ") && l.Contains("Rejected cluster RPC"));
                Assert.Contains("were not logged", summary);
                var (ok, _) = await RpcAsync(N, nodeId, KeyOf(token), Hello());
                Assert.Equal(HttpStatusCode.OK, ok);
                var synced = await WaitInSync(P, nodeId, null, "the primary reaches the node again");
                return new JsonObject
                {
                    ["requests"] = 200, ["status401"] = codes.GetValueOrDefault(401), ["status429"] = codes.GetValueOrDefault(429), ["warningsLogged"] = warnings,
                    ["summaryWarning"] = summary[(summary.IndexOf("Rejected", StringComparison.Ordinal))..], ["validAfterWindow"] = (int)ok,
                    ["primaryStatus"] = synced.GetProperty("status").GetString(),
                };
            });

            await Step("request bodies are bounded, chunked ones too (413); a legitimate request above Kestrel's default 30 MB is accepted", async () =>
            {
                nodeOptions.MaxRpcBodyBytes = 1024 * 1024;
                using var client = N.NewClient();
                var big = new string('A', 2 * 1024 * 1024);
                var json = $"{{\"v\":1,\"nodeId\":\"{nodeId}\",\"ts\":0,\"nonce\":\"\",\"ct\":\"{big}\"}}";
                // Chunked (no Content-Length): before the fix only Content-Length was checked, the 2 MB body was read and parsed
                // (401). The node answers 413 and closes the connection while the body is still being sent, so the status
                // line is read from a raw socket (HttpClient reports the reset instead of the early response).
                var chunkedStatus = await ChunkedPostStatusAsync(N.UiPort, Encoding.UTF8.GetBytes(json));
                Assert.Equal(413, chunkedStatus);
                var sizedStatus = await ChunkedPostStatusAsync(N.UiPort, Encoding.UTF8.GetBytes(json), chunked: false);
                Assert.Equal(413, sizedStatus);

                // Default limit (64 MB): a valid request of ~32 MB (base64 envelope), sent chunked like the primary's JsonContent.
                nodeOptions.MaxRpcBodyBytes = new ClusterOptions().MaxRpcBodyBytes;
                var padded = Hello();
                padded["args"]!["padding"] = new string('x', 24 * 1024 * 1024);
                var request = ClusterCrypto.SealRequest(KeyOf(token), nodeId, Encoding.UTF8.GetBytes(padded.ToJsonString()), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(request);
                Assert.True(bytes.Length > 30_000_000, $"{bytes.Length} bytes");
                using var large = new StreamContent(new MemoryStream(bytes));
                large.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                using var largeRequest = new HttpRequestMessage(HttpMethod.Post, "api/cluster/rpc") { Content = large };
                largeRequest.Headers.TransferEncodingChunked = true;
                largeRequest.Headers.Add("X-CPM-Request", "1");
                using var largeResp = await client.SendAsync(largeRequest);
                // Before the fix Kestrel's 30,000,000-byte default rejected it while reading (an HTTP error, not a sync result).
                Assert.Equal(HttpStatusCode.OK, largeResp.StatusCode);
                return new JsonObject
                {
                    ["limitBytes"] = 1024 * 1024, ["chunked2MB"] = chunkedStatus, ["contentLength2MB"] = sizedStatus,
                    ["validRequestBytes"] = bytes.Length, ["validRequest"] = (int)largeResp.StatusCode,
                };
            });

            await Step("a sync the node refuses at the HTTP level (413) is throttled like a rejected configuration, not re-sent every heartbeat", async () =>
            {
                nodeOptions.MaxRpcBodyBytes = 16 * 1024;
                for (var i = 0; i < 30; i++)
                    await P.Api.PostAsJsonAsync("api/hosts", new
                    {
                        kind = "proxy", domains = new[] { $"bulk{i}.cluster.test" }, tls = "none", compression = false,
                        upstreams = new[] { new { scheme = "http", host = "127.0.0.1", port = upstream.Port } },
                    }).OkJsonAsync("POST /api/hosts bulk" + i);
                var worker = P.Services.GetRequiredService<ClusterWorker>();
                var revision = (await worker.CurrentBundleAsync()).Revision;
                var failed = await Wait.ForValueAsync(async () =>
                {
                    var s = await Server(P, nodeId);
                    var e = s.GetProperty("sync").TryGetProperty("lastError", out var le) && le.ValueKind == System.Text.Json.JsonValueKind.String ? le.GetString()! : "";
                    return (e.Contains("413") && P.Store.Col<ClusterNode>().FindById(nodeId).FailedRevision == revision, s);
                }, SyncTimeout, () => "sync of the current revision refused with 413\n" + P.LogTail());
                var lastSyncAt = P.Store.Col<ClusterNode>().FindById(nodeId).LastSyncAt;
                // Several heartbeats (500 ms) pass without another attempt (before the fix: a full re-upload every heartbeat).
                var seen = new HashSet<DateTime?>();
                await Wait.UntilAsync(() =>
                {
                    seen.Add(P.Store.Col<ClusterNode>().FindById(nodeId).LastSeenAt);
                    return Task.FromResult(seen.Count > 4);
                }, TimeSpan.FromSeconds(20), () => "four more heartbeats");
                Assert.Equal(lastSyncAt, P.Store.Col<ClusterNode>().FindById(nodeId).LastSyncAt);

                nodeOptions.MaxRpcBodyBytes = new ClusterOptions().MaxRpcBodyBytes;
                await P.Api.PostAsync($"api/servers/{nodeId}/sync", null).OkJsonAsync("POST /api/servers/{id}/sync");
                var synced = await WaitInSync(P, nodeId, null, "in sync after the limit was raised");
                var (code, _) = await WaitHttp(N.HttpPort, "bulk19.cluster.test", "/bulk");
                return new JsonObject
                {
                    ["error"] = failed.GetProperty("sync").GetProperty("lastError").GetString(), ["heartbeatsWithoutResend"] = seen.Count,
                    ["revision"] = Rev(synced), ["nodeServesBulkHost"] = code,
                };
            });
            _report["result"] = "passed";
        }
        catch (Exception ex)
        {
            _report["result"] = "failed";
            _report["error"] = ex.ToString();
            if (primary is not null) _report["primaryLog"] = primary.LogTail(150);
            if (node is not null) _report["nodeLog"] = node.LogTail(150);
            throw;
        }
        finally
        {
            _report["totalMs"] = _clock.ElapsedMilliseconds;
            output.WriteLine("Artifact: " + E2EArtifacts.Write("cluster-rpc-limits-e2e.json", _report));
            if (node is not null) await node.DisposeAsync();
            if (primary is not null) await primary.DisposeAsync();
        }
    }

    /// <summary>
    /// POSTs <paramref name="body"/> to /api/cluster/rpc over a raw loopback socket (chunked, or with Content-Length) and
    /// returns the status code of the response, which may arrive (and the connection close) before the body was sent.
    /// </summary>
    private static async Task<int> ChunkedPostStatusAsync(int port, byte[] body, bool chunked = true)
    {
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var stream = tcp.GetStream();
        var header = "POST /api/cluster/rpc HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Type: application/json\r\nX-CPM-Request: 1\r\n" +
                     (chunked ? "Transfer-Encoding: chunked\r\n" : $"Content-Length: {body.Length}\r\n") + "Connection: close\r\n\r\n";
        var send = Task.Run(async () =>
        {
            try
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
                for (var offset = 0; offset < body.Length; offset += 64 * 1024)
                {
                    var count = Math.Min(64 * 1024, body.Length - offset);
                    if (chunked) await stream.WriteAsync(Encoding.ASCII.GetBytes($"{count:X}\r\n"));
                    await stream.WriteAsync(body.AsMemory(offset, count));
                    if (chunked) await stream.WriteAsync("\r\n"u8.ToArray());
                }
                if (chunked) await stream.WriteAsync("0\r\n\r\n"u8.ToArray());
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
            {
                // the node stopped reading and closed the connection: expected
            }
        });
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var statusLine = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)) ?? "";
        tcp.Close();
        await send;
        var parts = statusLine.Split(' ');
        return parts.Length > 1 && int.TryParse(parts[1], out var code) ? code : throw new InvalidOperationException($"No HTTP status line: '{statusLine}'");
    }

    private async Task Step(string name, Func<Task<JsonObject>> body)
    {
        var sw = Stopwatch.StartNew();
        var entry = new JsonObject { ["step"] = name };
        _steps.Add(entry);
        try
        {
            entry["verified"] = await body();
            entry["ok"] = true;
            output.WriteLine($"[{sw.ElapsedMilliseconds,6} ms] {name}");
        }
        catch
        {
            entry["ok"] = false;
            throw;
        }
        finally
        {
            entry["ms"] = sw.ElapsedMilliseconds;
        }
    }
}
