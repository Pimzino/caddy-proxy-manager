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
/// certificate changes, and works again after an admin re-pins. Regenerating the join token invalidates the old key until
/// the node joins again with the new token. Writes e2e-artifacts/cluster-https-pin-e2e.json.
/// </summary>
public sealed class ClusterHttpsPinE2ETests(ITestOutputHelper output)
{
    private readonly JsonObject _report = E2EArtifacts.Report(nameof(ClusterHttpsPinE2ETests) + "." + nameof(HttpsNodeIsPinnedAndTokenRegenerationRequiresRejoin));
    private readonly JsonArray _steps = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    [Fact]
    public async Task HttpsNodeIsPinnedAndTokenRegenerationRequiresRejoin()
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

            await Step("regenerating the token invalidates the old key; the node works again after leave + join with the new token", async () =>
            {
                int OfflineWarnings() => P.Store.Col<Core.Models.EventEntry>()
                    .Find(e => e.Key == "server-offline:" + nodeId).Count(e => e.Severity == Core.Models.EventSeverity.Warning);
                var offlineBefore = OfflineWarnings();
                var regenerated = await P.Api.PostAsync($"api/servers/{nodeId}/token", null).OkJsonAsync("POST /api/servers/{id}/token");
                var newToken = regenerated.GetProperty("joinToken").GetString()!;
                Assert.NotEqual(token, newToken);
                Assert.Equal(nodeId, JoinToken.Parse(newToken).NodeId);
                var pending = await Wait.ForValueAsync(async () =>
                {
                    var s = await Server(P, nodeId);
                    var error = s.TryGetProperty("lastError", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString()! : "";
                    return (s.GetProperty("status").GetString() == "pending" && error.Contains("authentication failed"), s);
                }, TimeSpan.FromSeconds(30), () => "old key rejected by the node\n" + P.LogTail());
                // A node that answers but rejects the key is not "offline": no new server-offline warning.
                Assert.Equal(offlineBefore, OfflineWarnings());

                using (var joinAgain = await N.Api.PostAsJsonAsync("api/cluster/join", new { token = newToken }))
                    Assert.Equal(HttpStatusCode.Conflict, joinAgain.StatusCode); // still a node: leave first
                await N.Api.PostAsync("api/cluster/leave", null).OkJsonAsync("POST /api/cluster/leave");
                using (var leaveAgain = await N.Api.PostAsync("api/cluster/leave", null))
                    Assert.Equal(HttpStatusCode.Conflict, leaveAgain.StatusCode);
                using (var badToken = await N.Api.PostAsJsonAsync("api/cluster/join", new { token = "cpmj1.bogus" }))
                    Assert.Equal(HttpStatusCode.BadRequest, badToken.StatusCode);
                await N.Api.PostAsJsonAsync("api/cluster/join", new { token = newToken }).OkJsonAsync("POST /api/cluster/join (new token)");
                var synced = await WaitInSync(P, nodeId, null, "online with the new key");
                return new JsonObject
                {
                    ["statusWithOldKey"] = pending.GetProperty("status").GetString(), ["errorWithOldKey"] = pending.GetProperty("lastError").GetString(),
                    ["newOfflineWarnings"] = OfflineWarnings() - offlineBefore, ["statusAfterRejoin"] = synced.GetProperty("status").GetString(), ["revision"] = Rev(synced),
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
