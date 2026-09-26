using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit.Abstractions;
using static CaddyManager.Cluster.Tests.ClusterE2ETests;

namespace CaddyManager.Cluster.Tests;

/// <summary>
/// End to end over HTTPS: the node's management UI uses a self-signed certificate. The primary pins its SHA-256 fingerprint
/// when the node is added (TOFU), talks to it only while the presented certificate matches, reports it when the node's
/// certificate changes, and works again after an admin re-pins. Regenerating the join token rotates the node's key over
/// that channel (the old key stops working at once; the node stays in sync) and the new token can be used to join again.
/// Writes e2e-artifacts/cluster-https-pin-e2e.json.
/// </summary>
public sealed class ClusterHttpsPinE2ETests(ITestOutputHelper output)
{
    private readonly JsonObject _report = E2EArtifacts.Report(nameof(ClusterHttpsPinE2ETests) + "." + nameof(HttpsNodeIsPinnedAndTokenRegenerationRotatesTheKey));
    private readonly JsonArray _steps = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    [Fact]
    public async Task HttpsNodeIsPinnedAndTokenRegenerationRotatesTheKey()
    {
        Assert.True(DevCaddy.Path is not null, "The development Caddy binary .dev/bin/caddy is required (copy .dev from the main checkout).");
        _report["steps"] = _steps;
        using var cert1 = Pfx(SelfSigned("node-c.cluster.test"));
        using var cert2 = Pfx(SelfSigned("node-c.cluster.test"));
        Manager? primary = null, node = null;
        try
        {
            await Step("start a primary and a node whose UI uses a self-signed HTTPS certificate", async () =>
            {
                var p = Manager.CreateAsync("primary-https");
                var n = Manager.CreateAsync("node-c", cert1);
                primary = await p;
                node = await n;
                return new JsonObject { ["nodeUrl"] = node.Url, ["certificate1"] = NodeClient.FingerprintOf(cert1), ["certificate2"] = NodeClient.FingerprintOf(cert2) };
            });
            var P = primary!;
            var N = node!;

            string nodeId = "", token = "";
            await Step("adding the https node pins its certificate (TOFU); sync works over the untrusted-but-pinned certificate", async () =>
            {
                var added = await P.Api.PostAsJsonAsync("api/servers", new { name = "node-c", url = N.Url + "/" }).OkJsonAsync("POST /api/servers");
                nodeId = added.GetProperty("server").GetProperty("id").GetString()!;
                token = added.GetProperty("joinToken").GetString()!;
                var fingerprint = added.GetProperty("fingerprint").GetString();
                Assert.True(ClusterCrypto.SameFingerprint(NodeClient.FingerprintOf(cert1), fingerprint), $"pinned {fingerprint}");
                Assert.Equal(N.Url, added.GetProperty("server").GetProperty("url").GetString()); // normalised (no trailing slash)
                await N.Api.PostAsJsonAsync("api/cluster/join", new { token }).OkJsonAsync("POST /api/cluster/join");
                var synced = await WaitInSync(P, nodeId, null, "sync over pinned https");
                return new JsonObject { ["pinned"] = fingerprint, ["revision"] = Rev(synced) };
            });

            await Step("a different node certificate is refused until an admin re-pins", async () =>
            {
                await N.StopAsync();
                N.UiCertificate = cert2;
                await N.StartAsync();
                var refused = await Wait.ForValueAsync(async () =>
                {
                    var s = await Server(P, nodeId);
                    var error = s.TryGetProperty("lastError", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString()! : "";
                    return (error.Contains("pinned fingerprint"), s);
                }, TimeSpan.FromSeconds(30), () => "pin mismatch reported\n" + P.LogTail());
                var repinned = await P.Api.PutAsJsonAsync($"api/servers/{nodeId}", new { repin = true }).OkJsonAsync("PUT /api/servers/{id} repin");
                var pin = P.Store.Col<ClusterNode>().FindById(nodeId).PinnedFingerprint;
                Assert.True(ClusterCrypto.SameFingerprint(NodeClient.FingerprintOf(cert2), pin));
                var back = await WaitInSync(P, nodeId, null, "online after re-pin");
                var audit = P.Store.Col<Core.Models.AuditEntry>().FindAll().Last(a => a.ObjectType == "server" && a.Action == "updated");
                Assert.Contains("pinned certificate", audit.Details);
                return new JsonObject
                {
                    ["errorWhileMismatched"] = refused.GetProperty("lastError").GetString(), ["newPin"] = pin,
                    ["statusAfterRepin"] = back.GetProperty("status").GetString(), ["repinAudit"] = audit.Details,
                };
            });

            await Step("regenerating the token rotates the node's key over the pinned channel: the old key stops working at once, the node stays in sync", async () =>
            {
                int OfflineWarnings() => P.Store.Col<Core.Models.EventEntry>()
                    .Find(e => e.Key == "server-offline:" + nodeId).Count(e => e.Severity == Core.Models.EventSeverity.Warning);
                var offlineBefore = OfflineWarnings();
                var oldKey = ClusterCrypto.DeriveKey(JoinToken.Parse(token).Secret);
                var regenerated = await P.Api.PostAsync($"api/servers/{nodeId}/token", null).OkJsonAsync("POST /api/servers/{id}/token");
                var newToken = regenerated.GetProperty("joinToken").GetString()!;
                Assert.NotEqual(token, newToken);
                Assert.Equal(nodeId, JoinToken.Parse(newToken).NodeId);
                // Before the fix the node kept trusting the old key (whoever held the old token kept control of it) and the
                // primary lost contact until someone ran leave + join on the node.
                Assert.True(regenerated.GetProperty("rotated").GetBoolean(), "the node confirmed the new key");
                var summary = await Server(P, nodeId);
                Assert.False(summary.GetProperty("keyRotationPending").GetBoolean());
                using (var old = await RawRpc(N, nodeId, oldKey))
                    Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
                using (var current = await RawRpc(N, nodeId, ClusterCrypto.DeriveKey(JoinToken.Parse(newToken).Secret)))
                    Assert.Equal(HttpStatusCode.OK, current.StatusCode);
                var synced = await WaitInSync(P, nodeId, null, "still in sync with the new key");
                Assert.Equal(offlineBefore, OfflineWarnings());
                var audit = N.Store.Col<Core.Models.AuditEntry>().FindAll().Any(a => a.ObjectType == "cluster" && a.Action == "keyRotated");
                Assert.True(audit, "the node audits the key rotation");

                // The token is still useful: the node joins again with it (a node may re-join its own primary) ...
                await N.Api.PostAsJsonAsync("api/cluster/join", new { token = newToken }).OkJsonAsync("POST /api/cluster/join (re-join, same primary)");
                var rejoined = await WaitInSync(P, nodeId, null, "in sync after re-join");
                // ... and after leaving, too.
                await N.Api.PostAsync("api/cluster/leave", null).OkJsonAsync("POST /api/cluster/leave");
                using (var leaveAgain = await N.Api.PostAsync("api/cluster/leave", null))
                    Assert.Equal(HttpStatusCode.Conflict, leaveAgain.StatusCode);
                using (var badToken = await N.Api.PostAsJsonAsync("api/cluster/join", new { token = "cpmj1.bogus" }))
                    Assert.Equal(HttpStatusCode.BadRequest, badToken.StatusCode);
                await N.Api.PostAsJsonAsync("api/cluster/join", new { token = newToken }).OkJsonAsync("POST /api/cluster/join (after leave)");
                var back = await WaitInSync(P, nodeId, null, "online after leave + join");
                return new JsonObject
                {
                    ["rotated"] = true, ["oldKeyAfterRotation"] = 401, ["newKey"] = 200, ["statusAfterRotation"] = synced.GetProperty("status").GetString(),
                    ["newOfflineWarnings"] = OfflineWarnings() - offlineBefore, ["statusAfterRejoin"] = rejoined.GetProperty("status").GetString(),
                    ["statusAfterLeaveAndJoin"] = back.GetProperty("status").GetString(), ["revision"] = Rev(back),
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
            output.WriteLine("Artifact: " + E2EArtifacts.Write("cluster-https-pin-e2e.json", _report));
            if (node is not null) await node.DisposeAsync();
            if (primary is not null) await primary.DisposeAsync();
        }
    }

    /// <summary>A `hello` sealed with <paramref name="key"/>, posted to the node like the primary would.</summary>
    private static async Task<HttpResponseMessage> RawRpc(Manager node, string nodeId, byte[] key)
    {
        var hello = System.Text.Encoding.UTF8.GetBytes("{\"op\":\"hello\",\"args\":{}}");
        return await node.NewClient().PostAsJsonAsync("api/cluster/rpc", ClusterCrypto.SealRequest(key, nodeId, hello, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    }

    /// <summary>Kestrel on macOS needs a certificate whose key is not ephemeral: round-trip through PKCS#12.</summary>
    private static X509Certificate2 Pfx(X509Certificate2 cert)
    {
        using (cert) return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), null);
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
