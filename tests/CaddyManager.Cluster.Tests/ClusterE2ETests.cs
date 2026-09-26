using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CaddyManager.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace CaddyManager.Cluster.Tests;

/// <summary>
/// End to end: two complete managers (primary + node) on real Kestrel loopback ports, each driving its own real Caddy
/// v2.11.4 process. Enrollment, replication, TLS with a replicated custom certificate, the node's read-only mode and
/// node-local settings, envelope security, proxied telemetry, offline detection/recovery, removal and the CLI are all
/// exercised through the public HTTP API and the real Caddy listeners. Writes e2e-artifacts/cluster-e2e.json.
/// </summary>
public sealed class ClusterE2ETests(ITestOutputHelper output)
{
    internal static readonly TimeSpan SyncTimeout = TimeSpan.FromSeconds(60);
    private readonly JsonObject _report = E2EArtifacts.Report(nameof(ClusterE2ETests) + "." + nameof(PrimaryAndNodeReplicateAndServeTraffic));
    private readonly JsonArray _steps = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    /// <summary>A request the node accepted (an on-path observer's capture), replayed after the node's manager restarted.</summary>
    private (RpcEnvelope Envelope, byte[] Key)? _captured;

    [Fact]
    public async Task PrimaryAndNodeReplicateAndServeTraffic()
    {
        Assert.True(DevCaddy.Path is not null, "The development Caddy binary .dev/bin/caddy is required (copy .dev from the main checkout).");
        _report["steps"] = _steps;
        await using var upstream = await Upstream.StartAsync();
        Manager? primary = null, node = null;
        try
        {
            await Step("start two managers with their own Caddy", async () =>
            {
                // The primary runs the plugin build (ntlm transport, layer4, cloudflare DNS), the node the standard build: a
                // configuration only the primary's Caddy accepts is used below to exercise a failing sync.
                var p = Manager.CreateAsync("primary-a", caddyBinary: DevCaddy.PluginsPath);
                var n = Manager.CreateAsync("node-b");
                primary = await p;
                node = await n;
                // Both run the real Telemetry module (no stand-in): what the primary shows for the node comes from the node.
                Assert.Equal(typeof(CaddyManager.Telemetry.ServerTelemetry).FullName, primary.TelemetryImplementation);
                Assert.Equal(typeof(CaddyManager.Telemetry.ServerTelemetry).FullName, node.TelemetryImplementation);
                return new JsonObject
                {
                    ["primary"] = Describe(primary), ["node"] = Describe(node), ["upstreamPort"] = upstream.Port,
                    ["telemetry"] = primary.TelemetryImplementation, ["testCertificateMaterialStore"] = primary.UsesTestMaterialStore,
                };
            });
            var P = primary!;
            var N = node!;

            // ---- enrollment
            string nodeId = "", token = "";
            await Step("primary adds the node (join token) and becomes Primary", async () =>
            {
                var added = await P.Api.PostAsJsonAsync("api/servers", new { name = "node-b", url = N.Url }).OkJsonAsync("POST /api/servers");
                nodeId = added.GetProperty("server").GetProperty("id").GetString()!;
                token = added.GetProperty("joinToken").GetString()!;
                Assert.StartsWith(JoinToken.Prefix, token);
                var parsed = JoinToken.Parse(token);
                Assert.Equal("primary-a", parsed.Primary);
                Assert.Equal(nodeId, parsed.NodeId);
                Assert.Equal(32, parsed.Secret.Length);
                var cluster = await P.Api.GetFromJsonAsync<JsonElement>("api/cluster");
                Assert.Equal("primary", cluster.GetProperty("role").GetString());
                Assert.Equal(1, cluster.GetProperty("nodeCount").GetInt32());
                // A node that has not joined answers 404 to RPC: the primary reports it as not joined yet.
                return new JsonObject { ["nodeId"] = nodeId, ["tokenLength"] = token.Length };
            });

            await Step("a configuration change before the node joined is not pushed and raises no sync alert (not joined = pending)", async () =>
            {
                // Before the fix every change queued a push to every node: the node answered 404 and the primary raised a
                // "Configuration sync failed" (configFailure) alert and flipped the row to error.
                var created = await P.Api.PostAsJsonAsync("api/hosts", new
                {
                    kind = "response", domains = new[] { "prejoin.cluster.test" }, tls = "none", responseStatus = 200, responseBody = "prejoin",
                }).OkJsonAsync("POST /api/hosts (before the node joined)");
                var hostId = created.GetProperty("item").GetProperty("id").GetString()!;
                // The debounced push (200 ms) and several heartbeats (500 ms) pass; the row must stay pending throughout.
                var statuses = new HashSet<string>();
                var watch = Stopwatch.StartNew();
                while (watch.Elapsed < TimeSpan.FromSeconds(3))
                {
                    statuses.Add((await Server(P, nodeId)).GetProperty("status").GetString()!);
                    await Task.Delay(100);
                }
                var summary = await Server(P, nodeId);
                Assert.Equal(["pending"], statuses);
                Assert.Contains("not a cluster node", summary.GetProperty("lastError").GetString());
                Assert.Empty(Events(P, "server-sync:" + nodeId));
                Assert.False(summary.GetProperty("sync").TryGetProperty("lastError", out var syncError) && syncError.ValueKind == JsonValueKind.String,
                    "no sync error recorded for a node that has not joined: " + syncError);
                await P.Api.DeleteAsync($"api/hosts/{hostId}").OkJsonAsync("DELETE /api/hosts/{id} (prejoin)");
                return new JsonObject
                {
                    ["statusesWhileNotJoined"] = new JsonArray(statuses.Select(x => (JsonNode)x).ToArray()),
                    ["lastError"] = summary.GetProperty("lastError").GetString(), ["syncEvents"] = 0,
                };
            });

            await Step("node joins with the token; the primary syncs it", async () =>
            {
                var joined = await N.Api.PostAsJsonAsync("api/cluster/join", new { token }).OkJsonAsync("POST /api/cluster/join");
                Assert.Equal("node", joined.GetProperty("role").GetString());
                Assert.Equal("primary-a", joined.GetProperty("primaryName").GetString());
                var summary = await WaitInSync(P, nodeId, null, "initial sync");
                var status = await N.Api.GetFromJsonAsync<JsonElement>("api/cluster");
                Assert.Equal(summary.GetProperty("sync").GetProperty("appliedRevision").GetString(), status.GetProperty("appliedRevision").GetString());
                Assert.True(status.TryGetProperty("lastPrimaryContactAt", out _));
                return new JsonObject { ["revision"] = Rev(summary), ["status"] = summary.GetProperty("status").GetString() };
            });

            // ---- replication of a proxy host → the node's Caddy serves it
            string revisionAfterHost = "";
            await Step("proxy host created on the primary is served by the node's Caddy", async () =>
            {
                var before = Rev(await Server(P, nodeId));
                await P.Api.PostAsJsonAsync("api/hosts", new
                {
                    kind = "proxy", domains = new[] { "app.cluster.test" }, tls = "none", compression = false,
                    upstreams = new[] { new { scheme = "http", host = "127.0.0.1", port = upstream.Port } },
                }).OkJsonAsync("POST /api/hosts (primary)");
                var synced = await WaitInSync(P, nodeId, before, "host replication");
                revisionAfterHost = Rev(synced);
                var (code, body) = await WaitHttp(N.HttpPort, "app.cluster.test", "/hello");
                var (pCode, pBody) = await HttpGet(P.HttpPort, "app.cluster.test", "/hello");
                var nodeHosts = await N.Api.GetFromJsonAsync<JsonElement>("api/hosts");
                Assert.Contains(nodeHosts.EnumerateArray(), h => h.GetProperty("domains")[0].GetString() == "app.cluster.test");
                return new JsonObject
                {
                    ["revisionBefore"] = before, ["revisionAfter"] = revisionAfterHost, ["nodeHttpStatus"] = code, ["nodeBody"] = body,
                    ["primaryHttpStatus"] = pCode, ["primaryBody"] = pBody,
                };
            });

            // ---- replicated custom certificate → node serves it over TLS (SNI)
            await Step("custom PEM certificate uploaded on the primary is served by the node over TLS (SNI)", async () =>
            {
                using var cert = SelfSigned("secure.cluster.test");
                var before = Rev(await Server(P, nodeId));
                var created = await P.Api.PostAsJsonAsync("api/certificates/pem", new
                {
                    name = "secure", certPem = cert.ExportCertificatePem(), keyPem = cert.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem(),
                }).OkJsonAsync("POST /api/certificates/pem");
                var certId = created.GetProperty("item").GetProperty("id").GetString()!;
                await P.Api.PostAsJsonAsync("api/hosts", new
                {
                    kind = "proxy", domains = new[] { "secure.cluster.test" }, tls = "custom", certificateId = certId, compression = false,
                    upstreams = new[] { new { scheme = "http", host = "127.0.0.1", port = upstream.Port } },
                }).OkJsonAsync("POST /api/hosts (custom TLS)");
                var synced = await WaitInSync(P, nodeId, before, "certificate replication");
                var (thumbprint, body) = await Wait.ForValueAsync(async () =>
                {
                    var r = await TlsGet(N.HttpsPort, "secure.cluster.test", "/tls");
                    return (r.Thumbprint == cert.Thumbprint && r.Body.Contains("upstream-ok /tls"), r);
                }, TimeSpan.FromSeconds(30), () => "node serving the replicated certificate");
                var nodeCerts = await N.Api.GetFromJsonAsync<JsonElement>("api/certificates");
                var replicated = nodeCerts.EnumerateArray().First(c => c.GetProperty("id").GetString() == certId);
                Assert.Equal("uploaded", replicated.GetProperty("source").GetString());
                return new JsonObject
                {
                    ["certificateId"] = certId, ["expectedThumbprint"] = cert.Thumbprint, ["servedThumbprint"] = thumbprint,
                    ["body"] = body.Contains("upstream-ok /tls") ? "upstream-ok /tls" : body, ["nodeCertificateSource"] = "uploaded", ["revision"] = Rev(synced),
                };
            });

            // ---- the node is read-only for replicated resources; node-local settings stay local
            await Step("node rejects replicated-resource mutations with 409; roles enforced", async () =>
            {
                var result = new JsonObject();
                using (var plugins = await N.Api.PutAsJsonAsync("api/caddy/plugins", new { plugins = Array.Empty<string>() }))
                {
                    var problem = await plugins.JsonAsync();
                    Assert.Equal(HttpStatusCode.Conflict, plugins.StatusCode);
                    Assert.Equal("Managed by the cluster primary", problem.GetProperty("title").GetString());
                    Assert.Contains("primary-a", problem.GetProperty("detail").GetString());
                    result["putPlugins"] = 409;
                }
                using (var host = await N.Api.PostAsJsonAsync("api/hosts", new
                       {
                           kind = "response", domains = new[] { "stray.cluster.test" }, tls = "none", responseStatus = 200, responseBody = "stray",
                       }))
                {
                    var problem = await host.JsonAsync();
                    Assert.Equal(HttpStatusCode.Conflict, host.StatusCode);
                    Assert.Equal("Managed by the cluster primary", problem.GetProperty("title").GetString());
                    Assert.Contains("primary-a", problem.GetProperty("detail").GetString());
                    result["postHost"] = 409;
                }
                // Caddy settings: a replicated field (trusted proxies) is the primary's, also when sent together with a
                // node-local one; the node-local-only change is exercised (200) in the next step.
                foreach (var (name, body) in new (string, object)[]
                         {
                             ("putSettingsReplicatedField", new { trustedProxies = new[] { "10.9.9.0/24" } }),
                             ("putSettingsReplicatedAndNodeLocal", new { httpPort = Net.FreePort(), trustedProxies = new[] { "10.9.9.0/24" } }),
                         })
                {
                    using var put = await N.Api.PutAsJsonAsync("api/settings/caddy", body);
                    var problem = await put.JsonAsync();
                    Assert.True(put.StatusCode == HttpStatusCode.Conflict, $"{name}: expected 409, got {(int)put.StatusCode} {problem}");
                    Assert.Equal("Managed by the cluster primary", problem.GetProperty("title").GetString());
                    result[name] = 409;
                }
                var unchanged = await N.Api.GetFromJsonAsync<JsonElement>("api/settings/caddy");
                Assert.Empty(unchanged.GetProperty("trustedProxies").EnumerateArray());
                Assert.Equal(N.HttpPort, unchanged.GetProperty("httpPort").GetInt32());
                // A node joins again only with a token of its own primary (e.g. after a key rotation): a token issued by another
                // primary needs "leave" first.
                var otherPrimary = new JoinToken("another-primary", "another-node-id", ClusterCrypto.NewSecret()).Encode();
                using (var join = await N.Api.PostAsJsonAsync("api/cluster/join", new { token = otherPrimary }))
                {
                    Assert.Equal(HttpStatusCode.Conflict, join.StatusCode);
                    Assert.Contains("Leave that cluster first", (await join.JsonAsync()).GetProperty("detail").GetString());
                }
                using (var add = await N.Api.PostAsJsonAsync("api/servers", new { name = "x", url = "http://127.0.0.1:1" }))
                    Assert.Equal(HttpStatusCode.Conflict, add.StatusCode);
                using (var primaryJoin = await P.Api.PostAsJsonAsync("api/cluster/join", new { token }))
                    Assert.Equal(HttpStatusCode.Conflict, primaryJoin.StatusCode);
                using (var anonymous = await P.NewClient().GetAsync("api/servers"))
                    Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
                result["nodeJoinOtherPrimary"] = 409;
                result["nodeAddServer"] = 409;
                result["primaryJoin"] = 409;
                result["anonymousServers"] = 401;
                return result;
            });

            await Step("node-local port change on the node is allowed and survives the next sync", async () =>
            {
                var newPort = Net.FreePort();
                await N.Api.PutAsJsonAsync("api/settings/caddy", new { httpPort = newPort }).OkJsonAsync("PUT /api/settings/caddy httpPort (node)");
                var (code, _) = await WaitHttp(newPort, "app.cluster.test", "/moved");
                var forced = await P.Api.PostAsync($"api/servers/{nodeId}/sync", null).OkJsonAsync("POST /api/servers/{id}/sync");
                Assert.False(forced.GetProperty("sync").TryGetProperty("lastError", out var syncError) && syncError.ValueKind == JsonValueKind.String,
                    "forced sync failed: " + syncError);
                var settings = await N.Api.GetFromJsonAsync<JsonElement>("api/settings/caddy");
                Assert.Equal(newPort, settings.GetProperty("httpPort").GetInt32());
                var (after, body) = await WaitHttp(newPort, "app.cluster.test", "/after-sync");
                // Replace semantics: whatever was created locally on the node is gone after the sync.
                var primaryHosts = (await P.Api.GetFromJsonAsync<JsonElement>("api/hosts")).GetArrayLength();
                var nodeHosts = await N.Api.GetFromJsonAsync<JsonElement>("api/hosts");
                Assert.Equal(primaryHosts, nodeHosts.GetArrayLength());
                Assert.DoesNotContain(nodeHosts.EnumerateArray(), h => h.GetProperty("domains")[0].GetString() == "stray.cluster.test");
                var primarySettings = await P.Api.GetFromJsonAsync<JsonElement>("api/settings/caddy");
                Assert.Equal(P.HttpPort, primarySettings.GetProperty("httpPort").GetInt32());
                return new JsonObject
                {
                    ["putNodeLocalOnly"] = 200, ["nodeHttpPort"] = newPort, ["statusBeforeSync"] = code, ["statusAfterForcedSync"] = after, ["body"] = body,
                    ["nodeHosts"] = nodeHosts.GetArrayLength(), ["primaryHosts"] = primaryHosts, ["revision"] = Rev(forced),
                };
            });

            // ---- a configuration the node's Caddy rejects
            await Step("configuration rejected by the node's Caddy: server-sync event, node rolls back and keeps serving; undone → Recovered", async () =>
            {
                var before = Rev(await Server(P, nodeId));
                // Valid for the primary's plugin build only: the standard node build has no http.reverse_proxy.transport.http_ntlm.
                const string advanced = """[{"handle":[{"handler":"reverse_proxy","transport":{"protocol":"http_ntlm"},"upstreams":[{"dial":"127.0.0.1:9"}]}]}]""";
                var created = await P.Api.PostAsJsonAsync("api/hosts", new
                {
                    kind = "proxy", domains = new[] { "ntlm.cluster.test" }, tls = "none", compression = false, advancedRoutesJson = advanced,
                    upstreams = new[] { new { scheme = "http", host = "127.0.0.1", port = upstream.Port } },
                }).OkJsonAsync("POST /api/hosts (ntlm, primary)");
                var hostId = created.GetProperty("item").GetProperty("id").GetString()!;
                var failed = await Wait.ForValueAsync(async () =>
                {
                    var s = await Server(P, nodeId);
                    var e = s.GetProperty("sync").TryGetProperty("lastError", out var le) && le.ValueKind == JsonValueKind.String ? le.GetString()! : "";
                    return (s.GetProperty("status").GetString() == "error" && e.Contains("http_ntlm"), s);
                }, SyncTimeout, () => "sync failure reported\n" + P.LogTail());
                var syncEvent = await Wait.ForAsync(() => Task.FromResult(Events(P, "server-sync:" + nodeId).FirstOrDefault(e => e.Severity == EventSeverity.Warning)),
                    TimeSpan.FromSeconds(10), () => "server-sync event");
                Assert.Equal("cluster", syncEvent.Category);
                // The node restored its previous data and still runs the previous revision.
                var nodeHosts = await N.Api.GetFromJsonAsync<JsonElement>("api/hosts");
                Assert.DoesNotContain(nodeHosts.EnumerateArray(), h => h.GetProperty("domains")[0].GetString() == "ntlm.cluster.test");
                var nodeStatus = await N.Api.GetFromJsonAsync<JsonElement>("api/cluster");
                Assert.Equal(before, nodeStatus.GetProperty("appliedRevision").GetString());
                Assert.Contains(nodeStatus.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("last configuration sync failed"));
                var (served, _) = await WaitHttp(await NodeHttpPort(N), "app.cluster.test", "/still-serving");
                var nodeSyncs = N.Store.Col<AuditEntry>().FindAll().Count(a => a.ObjectType == "cluster" && a.Action == "syncFailed");
                // The same rejected revision is not pushed again every heartbeat (FailedSyncRetry): let 3 more heartbeats pass.
                var seen = new HashSet<DateTime?>();
                await Wait.UntilAsync(() =>
                {
                    seen.Add(P.Store.Col<ClusterNode>().FindById(nodeId).LastSeenAt);
                    return Task.FromResult(seen.Count > 3);
                }, TimeSpan.FromSeconds(20), () => "three more heartbeats");
                var nodeSyncsLater = N.Store.Col<AuditEntry>().FindAll().Count(a => a.ObjectType == "cluster" && a.Action == "syncFailed");
                Assert.Equal(nodeSyncs, nodeSyncsLater);

                await P.Api.DeleteAsync($"api/hosts/{hostId}").OkJsonAsync("DELETE /api/hosts/{id}");
                var recovered = await Wait.ForAsync(() => Task.FromResult(Events(P, "server-sync:" + nodeId).FirstOrDefault(e => e.Severity == EventSeverity.Recovered)),
                    SyncTimeout, () => "server-sync Recovered event\n" + P.LogTail());
                var back = await WaitInSync(P, nodeId, null, "in sync after undo");
                var cleared = await N.Api.GetFromJsonAsync<JsonElement>("api/cluster");
                Assert.DoesNotContain(cleared.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("last configuration sync failed"));
                return new JsonObject
                {
                    ["rejectedError"] = failed.GetProperty("sync").GetProperty("lastError").GetString(), ["event"] = syncEvent.Message,
                    ["nodeKeptRevision"] = before, ["nodeStillServing"] = served, ["rejectedPushes"] = nodeSyncsLater,
                    ["recoveredEvent"] = recovered.Message, ["revisionAfterUndo"] = Rev(back),
                };
            });

            // ---- envelope security against the real node endpoint
            await Step("RPC envelope: tampered, wrong key, replayed nonce, skewed clock and unknown node → 401; not a node → 404", async () =>
            {
                var secret = JoinToken.Parse(token).Secret;
                var key = ClusterCrypto.DeriveKey(secret);
                var hello = Encoding.UTF8.GetBytes("""{"op":"hello","args":{}}""");
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var raw = N.NewClient();
                var anonymous = N.NewClient(csrf: false);

                var valid = ClusterCrypto.SealRequest(key, nodeId, hello, now);
                _captured = (valid, key);
                using var ok = await raw.PostAsJsonAsync("api/cluster/rpc", valid);
                Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
                var reply = (await ok.Content.ReadFromJsonAsync<RpcEnvelope>())!;
                var plain = ClusterCrypto.Open(key, reply, ClusterCrypto.ResponseAad(nodeId, reply.Ts, reply.Nonce, valid.Nonce));
                Assert.NotNull(plain);
                Assert.True(JsonNode.Parse(plain!)!["ok"]!.GetValue<bool>());
                // The response is bound to the request nonce: it does not authenticate for another request.
                Assert.Null(ClusterCrypto.Open(key, reply, ClusterCrypto.ResponseAad(nodeId, reply.Ts, reply.Nonce, "AAAAAAAAAAAAAAAA")));

                var original = ClusterCrypto.SealRequest(key, nodeId, hello, now);
                var ct = Convert.FromBase64String(original.Ct);
                ct[3] ^= 0x40;
                var tampered = original with { Ct = Convert.ToBase64String(ct) };
                var wrongKey = ClusterCrypto.SealRequest(ClusterCrypto.DeriveKey(ClusterCrypto.NewSecret()), nodeId, hello, now);
                var skewed = ClusterCrypto.SealRequest(key, nodeId, hello, now - 400);
                var unknown = ClusterCrypto.SealRequest(key, "not-" + nodeId, hello, now);
                var codes = new JsonObject();
                foreach (var (name, env) in new[] { ("replayed", valid), ("tampered", tampered), ("wrongKey", wrongKey), ("skewed400s", skewed), ("unknownNode", unknown) })
                {
                    using var resp = await raw.PostAsJsonAsync("api/cluster/rpc", env);
                    var problem = await resp.JsonAsync();
                    Assert.True(resp.StatusCode == HttpStatusCode.Unauthorized, $"{name}: expected 401, got {(int)resp.StatusCode}");
                    Assert.False(problem.TryGetProperty("detail", out _), $"{name}: a 401 must not explain why");
                    codes[name] = (int)resp.StatusCode;
                }
                using (var garbage = await raw.PostAsync("api/cluster/rpc", new StringContent("{not json", Encoding.UTF8, "application/json")))
                    codes["malformed"] = (int)garbage.StatusCode;
                Assert.Equal(401, codes["malformed"]!.GetValue<int>());
                using (var noHeader = await anonymous.PostAsJsonAsync("api/cluster/rpc", ClusterCrypto.SealRequest(key, nodeId, hello, now)))
                    codes["withoutCsrfHeader"] = (int)noHeader.StatusCode;
                Assert.Equal(400, codes["withoutCsrfHeader"]!.GetValue<int>());
                using (var notNode = await P.NewClient().PostAsJsonAsync("api/cluster/rpc", valid))
                    codes["primaryNotANode"] = (int)notNode.StatusCode;
                Assert.Equal(404, codes["primaryNotANode"]!.GetValue<int>());
                codes["valid"] = 200;
                return codes;
            });

            // ---- proxied telemetry and remote Caddy control
            await Step("server details, samples, traffic, Caddy restart and jobs are proxied to the node (the node's real telemetry)", async () =>
            {
                // Requests only the node's Caddy receives, for a name no host serves (the default site answers 404).
                const string probeHost = "telemetry-probe.cluster.test";
                const int probes = 7;
                var nodePort = await NodeHttpPort(N);
                for (var i = 0; i < probes; i++) Assert.Equal(404, (await HttpGet(nodePort, probeHost, "/probe/" + i)).Code);

                var detail = await P.Api.GetFromJsonAsync<JsonElement>($"api/servers/{nodeId}");
                Assert.Equal("online", detail.GetProperty("status").GetString());
                // Server facts are the node's: its data directory and the host name the node reports for itself.
                var info = detail.GetProperty("info");
                var nodeOwn = (await N.Api.GetFromJsonAsync<JsonElement>("api/servers/local")).GetProperty("info");
                Assert.Equal(N.DataDir, info.GetProperty("dataDir").GetString());
                Assert.False(string.IsNullOrEmpty(info.GetProperty("hostname").GetString()));
                Assert.Equal(nodeOwn.GetProperty("hostname").GetString(), info.GetProperty("hostname").GetString());
                Assert.Equal("running", info.GetProperty("caddyState").GetString());

                // Samples come from the node's sampler: its data disk is labelled with the node's data directory.
                var samples = await Wait.ForValueAsync(async () =>
                {
                    var s = await P.Api.GetFromJsonAsync<JsonElement>($"api/servers/{nodeId}/samples");
                    return (s.GetArrayLength() >= 2, s);
                }, TimeSpan.FromSeconds(10), () => "node samples");
                var nodeDataLabel = $"Data ({N.DataDir})";
                Assert.All(samples.EnumerateArray(), s =>
                    Assert.Contains(s.GetProperty("disks").EnumerateArray(), d => d.GetProperty("label").GetString()!.Contains(nodeDataLabel)));
                Assert.Contains(samples.EnumerateArray(), s => s.TryGetProperty("caddyMemoryBytes", out var m) && m.ValueKind == JsonValueKind.Number && m.GetInt64() > 0);
                var since = samples[samples.GetArrayLength() - 2].GetProperty("at").GetDateTime().ToUniversalTime();
                var recent = await P.Api.GetFromJsonAsync<JsonElement>($"api/servers/{nodeId}/samples?since={Uri.EscapeDataString(since.ToString("O"))}");
                Assert.NotEqual(0, recent.GetArrayLength());
                Assert.All(recent.EnumerateArray(), s => Assert.True(s.GetProperty("at").GetDateTime().ToUniversalTime() > since, "since filters samples on the node"));

                // Traffic comes from the node's stats log: exactly the probes, which the primary's Caddy never saw.
                var remote = await Wait.ForValueAsync(async () =>
                {
                    var t = await P.Api.GetFromJsonAsync<JsonElement>($"api/servers/{nodeId}/traffic?range=hour&host={probeHost}");
                    return (t.GetProperty("totals").GetProperty("requests").GetInt64() == probes, t);
                }, TimeSpan.FromSeconds(30), () => $"{probes} probe requests in the node's traffic");
                Assert.Equal(probes, remote.GetProperty("totals").GetProperty("status4xx").GetInt64());
                Assert.Equal(probeHost, remote.GetProperty("host").GetString());
                var nodeView = await N.Api.GetFromJsonAsync<JsonElement>($"api/servers/local/traffic?range=hour&host={probeHost}");
                Assert.Equal(nodeView.GetProperty("totals").GetRawText(), remote.GetProperty("totals").GetRawText());
                var primaryOwn = await P.Api.GetFromJsonAsync<JsonElement>($"api/servers/local/traffic?range=hour&host={probeHost}");
                Assert.Equal(0, primaryOwn.GetProperty("totals").GetProperty("requests").GetInt64());
                var traffic = await P.Api.GetFromJsonAsync<JsonElement>($"api/servers/{nodeId}/traffic?range=week");
                Assert.Equal("week", traffic.GetProperty("range").GetString());
                Assert.True(traffic.GetProperty("totals").GetProperty("requests").GetInt64() >= probes);
                using (var bad = await P.Api.GetAsync($"api/servers/{nodeId}/traffic?range=year"))
                    Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
                var list = await P.Api.GetFromJsonAsync<JsonElement>("api/servers");
                Assert.Equal("local", list[0].GetProperty("id").GetString());
                Assert.True(list[0].GetProperty("isLocal").GetBoolean());
                Assert.Equal(P.DataDir, list[0].GetProperty("info").GetProperty("dataDir").GetString());
                Assert.Equal(nodeId, list[1].GetProperty("id").GetString());

                var restartWatch = Stopwatch.StartNew();
                var restarted = await P.Api.PostAsync($"api/servers/{nodeId}/caddy/restart", null).OkJsonAsync("POST /api/servers/{id}/caddy/restart");
                Assert.Equal("running", restarted.GetProperty("state").GetString());
                var (code, _) = await WaitHttp(await NodeHttpPort(N), "app.cluster.test", "/after-restart");
                using var job = await P.Api.GetAsync($"api/servers/{nodeId}/jobs/does-not-exist");
                Assert.Equal(HttpStatusCode.BadGateway, job.StatusCode);
                Assert.Contains("Job was not found", (await job.JsonAsync()).GetProperty("detail").GetString());
                return new JsonObject
                {
                    ["telemetry"] = N.TelemetryImplementation,
                    ["nodeInfo"] = new JsonObject
                    {
                        ["hostname"] = info.GetProperty("hostname").GetString(), ["dataDir"] = info.GetProperty("dataDir").GetString(),
                        ["caddyVersion"] = info.TryGetProperty("caddyVersion", out var cv) ? cv.GetString() : null,
                    },
                    ["samples"] = samples.GetArrayLength(), ["samplesSince"] = recent.GetArrayLength(), ["sampleDiskLabel"] = nodeDataLabel,
                    ["probeHost"] = probeHost, ["probeRequests"] = probes, ["remoteTrafficHour"] = JsonNode.Parse(remote.GetProperty("totals").GetRawText()),
                    ["primaryOwnTrafficForProbeHost"] = primaryOwn.GetProperty("totals").GetProperty("requests").GetInt64(),
                    ["trafficRange"] = traffic.GetProperty("range").GetString(), ["remoteRestartMs"] = restartWatch.ElapsedMilliseconds,
                    ["httpAfterRestart"] = code, ["unknownJob"] = 502,
                };
            });

            // ---- offline detection and recovery
            await Step("node stopped → server-offline event after 3 failed heartbeats; restarted → Recovered; a request captured before the restart cannot be replayed", async () =>
            {
                // The replay check below tolerates 2 s of clock jitter: a request signed within the last 2 s before the node's
                // manager started is not covered. A real restart takes longer; this in-process one is fast, so let the
                // captured request age first.
                var capturedTs = (_captured ?? throw new InvalidOperationException("no captured request")).Envelope.Ts;
                while (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - capturedTs < 3) await Task.Delay(100);
                var stoppedAt = Stopwatch.StartNew();
                await N.StopAsync();
                var offline = await Wait.ForAsync(() => Task.FromResult(Events(P, "server-offline:" + nodeId).FirstOrDefault(e => e.Severity == EventSeverity.Warning)),
                    TimeSpan.FromSeconds(30), () => "server-offline event\n" + P.LogTail());
                var offlineMs = stoppedAt.ElapsedMilliseconds;
                var summary = await Server(P, nodeId);
                Assert.Equal("offline", summary.GetProperty("status").GetString());
                var failures = P.Store.Col<ClusterNode>().FindById(nodeId).ConsecutiveFailures;
                Assert.True(failures >= 3, $"offline after {failures} failures");

                await N.StartAsync();
                var recovered = await Wait.ForAsync(() => Task.FromResult(Events(P, "server-offline:" + nodeId).FirstOrDefault(e => e.Severity == EventSeverity.Recovered)),
                    TimeSpan.FromSeconds(30), () => "Recovered event\n" + P.LogTail());
                var back = await WaitInSync(P, nodeId, null, "after node restart");
                var (code, _) = await WaitHttp(await NodeHttpPort(N), "app.cluster.test", "/after-manager-restart");

                // Replay protection survives the restart: the nonce cache is gone, but a request signed before the node's
                // manager started is refused. Before the fix the captured request was accepted once more (HTTP 200).
                var (captured, key) = _captured ?? throw new InvalidOperationException("no captured request");
                var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - captured.Ts;
                Assert.True(age < 300, $"the captured request is {age} s old: still inside the 300 s window, only the restart protects");
                using var replay = await N.NewClient().PostAsJsonAsync("api/cluster/rpc", captured);
                Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
                var fresh = ClusterCrypto.SealRequest(key, nodeId, Encoding.UTF8.GetBytes("{\"op\":\"hello\",\"args\":{}}"), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                using var accepted = await N.NewClient().PostAsJsonAsync("api/cluster/rpc", fresh);
                Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
                var offset = N.Store.GetSettings<ClusterSettings>().PrimaryClockOffsetSeconds;
                Assert.NotNull(offset);
                return new JsonObject
                {
                    ["offlineAfterMs"] = offlineMs, ["failuresWhenOffline"] = failures, ["offlineEvent"] = offline.Message,
                    ["offlineAlertCategory"] = offline.Category, ["recoveredEvent"] = recovered.Message, ["statusAfterRestart"] = back.GetProperty("status").GetString(),
                    ["httpAfterManagerRestart"] = code, ["replayAfterRestartAgeSeconds"] = age, ["replayAfterRestart"] = (int)replay.StatusCode,
                    ["freshRequestAfterRestart"] = (int)accepted.StatusCode, ["primaryClockOffsetSeconds"] = offset,
                };
            });

            // ---- removal: node is standalone and editable again
            await Step("DELETE server → node leaves (standalone, editable), primary becomes standalone", async () =>
            {
                using (var del = await P.Api.DeleteAsync($"api/servers/{nodeId}"))
                    Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
                var nodeStatus = await N.Api.GetFromJsonAsync<JsonElement>("api/cluster");
                var primaryStatus = await P.Api.GetFromJsonAsync<JsonElement>("api/cluster");
                Assert.Equal("standalone", nodeStatus.GetProperty("role").GetString());
                Assert.Equal("standalone", primaryStatus.GetProperty("role").GetString());
                await N.Api.PostAsJsonAsync("api/hosts", new
                {
                    kind = "response", domains = new[] { "local-only.cluster.test" }, tls = "none", responseStatus = 200, responseBody = "local",
                }).OkJsonAsync("POST /api/hosts on the former node");
                using (var plugins = await N.Api.PutAsJsonAsync("api/caddy/plugins", new { plugins = Array.Empty<string>() }))
                    Assert.Equal(HttpStatusCode.OK, plugins.StatusCode);
                var (code, body) = await WaitHttp(await NodeHttpPort(N), "local-only.cluster.test", "/");
                var audit = P.Store.Col<AuditEntry>().FindAll().Where(a => a.ObjectType == "server").Select(a => a.Action).ToList();
                Assert.Contains("created", audit);
                Assert.Contains("deleted", audit);
                return new JsonObject
                {
                    ["nodeRole"] = "standalone", ["primaryRole"] = "standalone", ["localHostServed"] = code, ["body"] = body,
                    ["primaryServerAudit"] = new JsonArray(audit.Select(a => (JsonNode)a).ToArray()),
                };
            });

            // ---- CLI join / status on a stopped node
            await Step("CLI: cluster join/status with the service stopped", async () =>
            {
                var added = await P.Api.PostAsJsonAsync("api/servers", new { name = "node-b", url = N.Url }).OkJsonAsync("POST /api/servers (again)");
                var newId = added.GetProperty("server").GetProperty("id").GetString()!;
                var newToken = added.GetProperty("joinToken").GetString()!;
                // The old token's key no longer matches anything: a fresh node id and secret.
                Assert.NotEqual(nodeId, newId);

                // While the service runs the database is in use. Windows enforces the file sharing (the CLI answers "Stop the
                // CaddyProxyManager service first"); macOS/Linux do not, so a read-only status still works there — recorded only.
                var (lockedCode, _, lockedErr) = await Cli("cluster", "status", "--data-dir", N.DataDir);
                await N.StopAsync();
                var (joinCode, joinOut, joinErr) = await Cli("cluster", "join", newToken, "--data-dir", N.DataDir);
                Assert.True(joinCode == 0, joinErr);
                var (statusCode, statusOut, _) = await Cli("cluster", "status", "--data-dir", N.DataDir);
                Assert.Equal(0, statusCode);
                Assert.Contains("Role:     Node", statusOut);
                Assert.Contains("Primary:  primary-a", statusOut);
                var (badCode, _, badErr) = await Cli("cluster", "join", "cpmj1.not-a-token", "--data-dir", N.DataDir);
                Assert.Equal(2, badCode);

                await N.StartAsync();
                var synced = await WaitInSync(P, newId, null, "sync after CLI join");
                // The host created while standalone is replaced by the primary's configuration.
                var nodeHosts = await N.Api.GetFromJsonAsync<JsonElement>("api/hosts");
                Assert.DoesNotContain(nodeHosts.EnumerateArray(), h => h.GetProperty("domains")[0].GetString() == "local-only.cluster.test");
                var cliAudit = N.Store.Col<AuditEntry>().FindAll().Any(a => a.UserName.StartsWith("cli:") && a.Action == "joined");
                Assert.True(cliAudit);
                return new JsonObject
                {
                    ["statusWhileRunningExit"] = lockedCode, ["statusWhileRunningStderr"] = lockedErr.Trim(),
                    ["joinExit"] = joinCode, ["joinStdout"] = joinOut.Trim(), ["statusStdout"] = statusOut.Trim(), ["invalidTokenExit"] = badCode,
                    ["invalidTokenStderr"] = badErr.Split('\n')[0].Trim(), ["revision"] = Rev(synced), ["cliAudit"] = cliAudit,
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
            var path = E2EArtifacts.Write("cluster-e2e.json", _report);
            output.WriteLine("Artifact: " + path);
            if (node is not null) await node.DisposeAsync();
            if (primary is not null) await primary.DisposeAsync();
        }
    }

    // ------------------------------------------------------------------ helpers (shared with the other E2E classes)

    private async Task Step(string name, Func<Task<JsonObject>> body)
    {
        var sw = Stopwatch.StartNew();
        var entry = new JsonObject { ["step"] = name };
        _steps.Add(entry);
        try
        {
            entry["verified"] = await body();
            entry["ms"] = sw.ElapsedMilliseconds;
            entry["ok"] = true;
            output.WriteLine($"[{sw.ElapsedMilliseconds,6} ms] {name}");
        }
        catch
        {
            entry["ms"] = sw.ElapsedMilliseconds;
            entry["ok"] = false;
            throw;
        }
    }

    internal static JsonObject Describe(Manager m) => new()
    {
        ["ui"] = m.Url, ["httpPort"] = m.HttpPort, ["httpsPort"] = m.HttpsPort, ["admin"] = $"127.0.0.1:{m.AdminPort}", ["dataDir"] = m.DataDir,
    };

    internal static string Rev(JsonElement summary) =>
        summary.GetProperty("sync").TryGetProperty("appliedRevision", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString()! : "";

    internal static async Task<JsonElement> Server(Manager p, string id)
    {
        var list = await p.Api.GetFromJsonAsync<JsonElement>("api/servers");
        return list.EnumerateArray().First(s => s.GetProperty("id").GetString() == id);
    }

    /// <summary>
    /// Waits until the node is online, its last contact succeeded (no lastError) and it is in sync with a revision other
    /// than <paramref name="notRevision"/>.
    /// </summary>
    internal static Task<JsonElement> WaitInSync(Manager p, string id, string? notRevision, string what) =>
        Wait.ForValueAsync(async () =>
        {
            var s = await Server(p, id);
            var sync = s.GetProperty("sync");
            var healthy = s.GetProperty("status").GetString() == "online" && !s.TryGetProperty("lastError", out _);
            return (healthy && sync.GetProperty("inSync").GetBoolean() && Rev(s) != (notRevision ?? ""), s);
        }, SyncTimeout, () => $"{what}: node in sync\n{p.LogTail()}");

    internal static List<EventEntry> Events(Manager p, string key) =>
        p.Store.Col<EventEntry>().Find(e => e.Key == key).OrderBy(e => e.CreatedAt).ToList();

    internal static async Task<int> NodeHttpPort(Manager n) =>
        (await n.Api.GetFromJsonAsync<JsonElement>("api/settings/caddy")).GetProperty("httpPort").GetInt32();

    internal static async Task<(int Code, string Body)> HttpGet(int port, string host, string path)
    {
        using var c = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
        using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}{path}");
        req.Headers.Host = host;
        using var resp = await c.SendAsync(req);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }

    internal static Task<(int Code, string Body)> WaitHttp(int port, string host, string path) =>
        Wait.ForValueAsync(async () =>
        {
            var r = await HttpGet(port, host, path);
            return (r.Code == 200 && (r.Body.Contains("upstream-ok " + path) || r.Body == "local"), r);
        }, TimeSpan.FromSeconds(30), () => $"{host}{path} on 127.0.0.1:{port} answered by the upstream");

    internal static async Task<(string Thumbprint, string Body)> TlsGet(int port, string sni, string path)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        string thumbprint = "";
        await using var ssl = new SslStream(tcp.GetStream(), false);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = sni,
            RemoteCertificateValidationCallback = (_, cert, _, _) =>
            {
                if (cert is not null) thumbprint = new X509Certificate2(cert).Thumbprint;
                return true;
            },
        });
        await ssl.WriteAsync(Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: {sni}\r\nConnection: close\r\n\r\n"));
        using var reader = new StreamReader(ssl, Encoding.UTF8);
        return (thumbprint, await reader.ReadToEndAsync());
    }

    internal static X509Certificate2 SelfSigned(string dns)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={dns}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dns);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    internal static async Task<(int Code, string Stdout, string Stderr)> Cli(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await ClusterCli.TryRunAsync(args, stdout, stderr);
        Assert.NotNull(code);
        return (code!.Value, stdout.ToString(), stderr.ToString());
    }

    /// <summary>An in-test HTTP upstream answering "upstream-ok &lt;path&gt;".</summary>
    internal sealed class Upstream : IAsyncDisposable
    {
        private WebApplication _app = null!;
        public int Port { get; private set; }

        public static async Task<Upstream> StartAsync()
        {
            var u = new Upstream { Port = Net.FreePort() };
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseKestrel(k => k.Listen(IPAddress.Loopback, u.Port));
            var app = builder.Build();
            app.Run(ctx => ctx.Response.WriteAsync("upstream-ok " + ctx.Request.Path));
            await app.StartAsync();
            u._app = app;
            return u;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
