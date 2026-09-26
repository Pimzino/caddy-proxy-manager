using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CaddyManager.Core.Infrastructure;
using CaddyManager.Core.Models;
using Xunit.Abstractions;
using static CaddyManager.Cluster.Tests.ClusterE2ETests;

namespace CaddyManager.Cluster.Tests;

/// <summary>
/// End to end, two complete managers with real Caddy: "Regenerate token" is a real key rotation (the node gets the new key
/// over the encrypted channel; while it cannot be reached the rotation stays pending, the node keeps trusting only its
/// current key, and the rotation completes at the next contact — also when the node joined again with the new token
/// meanwhile); a node joins its own primary again with a new token without leaving; the node obeys only the primary
/// instance it pinned (a restored/cloned primary with the same key is refused until the node joins again); removing an
/// unreachable node warns that it still trusts its key. Writes e2e-artifacts/cluster-key-rotation-e2e.json.
/// </summary>
public sealed class ClusterKeyRotationE2ETests(ITestOutputHelper output)
{
    private readonly JsonObject _report = E2EArtifacts.Report(nameof(ClusterKeyRotationE2ETests) + "." + nameof(KeyRotationRejoinAndPrimaryIdentity));
    private readonly JsonArray _steps = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    [Fact]
    public async Task KeyRotationRejoinAndPrimaryIdentity()
    {
        Assert.True(DevCaddy.Path is not null, "The development Caddy binary .dev/bin/caddy is required (copy .dev from the main checkout).");
        _report["steps"] = _steps;
        Manager? primary = null, node = null;
        try
        {
            var p = Manager.CreateAsync("primary-rot");
            var n = Manager.CreateAsync("node-rot");
            primary = await p;
            node = await n;
            var P = primary;
            var N = node;
            _report["testConfigMutationLock"] = P.UsesTestMutationLock;

            string nodeId = "", token = "";
            await Step("node joins; it pins the primary instance at the first request", async () =>
            {
                var added = await P.Api.PostAsJsonAsync("api/servers", new { name = "node-rot", url = N.Url }).OkJsonAsync("POST /api/servers");
                nodeId = added.GetProperty("server").GetProperty("id").GetString()!;
                token = added.GetProperty("joinToken").GetString()!;
                await N.Api.PostAsJsonAsync("api/cluster/join", new { token }).OkJsonAsync("join");
                var synced = await WaitInSync(P, nodeId, null, "initial sync");
                var primaryId = P.Services.GetRequiredService<ClusterService>().PrimaryInstanceId;
                Assert.Equal(primaryId, N.Store.GetSettings<ClusterSettings>().PinnedPrimaryId);
                return new JsonObject { ["revision"] = Rev(synced), ["primaryInstanceId"] = primaryId };
            });

            await Step("another primary instance holding the node's key is refused (hello, sync, leave); the pinned one keeps working", async () =>
            {
                var key = KeyOf(token);
                var result = new JsonObject();
                foreach (var op in new[] { "hello", "leave" })
                {
                    var (status, reply) = await RpcAsync(N, nodeId, key, new JsonObject { ["op"] = op, ["args"] = new JsonObject(), ["primaryId"] = "a-cloned-primary" });
                    Assert.Equal(HttpStatusCode.OK, status);
                    Assert.False(reply!["ok"]!.GetValue<bool>(), $"{op} from another primary instance must be refused");
                    Assert.Equal(NodeRpcHandler.PrimaryConflictCode, reply["code"]!.GetValue<string>());
                    result[op] = reply["error"]!.GetValue<string>();
                }
                var status2 = await N.Api.GetFromJsonAsync<JsonElement>("api/cluster");
                Assert.Equal("node", status2.GetProperty("role").GetString()); // the refused "leave" changed nothing
                var synced = await WaitInSync(P, nodeId, null, "the pinned primary still manages the node");
                result["roleAfterRefusedLeave"] = "node";
                result["statusForPinnedPrimary"] = synced.GetProperty("status").GetString();
                return result;
            });

            string token2 = "";
            await Step("regenerate while the node is down: rotation pending, the node keeps only its current key; completed at the next contact", async () =>
            {
                await N.StopAsync();
                var regenerated = await P.Api.PostAsync($"api/servers/{nodeId}/token", null).OkJsonAsync("POST /api/servers/{id}/token (node down)");
                token2 = regenerated.GetProperty("joinToken").GetString()!;
                Assert.False(regenerated.GetProperty("rotated").GetBoolean());
                var pending = await Server(P, nodeId);
                Assert.True(pending.GetProperty("keyRotationPending").GetBoolean());
                var audit = P.Store.Col<AuditEntry>().FindAll().Where(a => a.ObjectType == "server" && a.Action == "tokenRegenerated").OrderBy(a => a.CreatedAt).Last();
                Assert.Contains("still accepts the previous key", audit.Details);
                // The node (stopped) still holds the first secret: nothing claims the old key was revoked.
                using (var store = new LiteStore(N.Paths))
                {
                    var stored = new SecretProtector(N.Paths).Unprotect(store.GetSettings<ClusterSettings>().SecretProtected!);
                    Assert.Equal(Convert.ToBase64String(JoinToken.Parse(token).Secret), stored);
                }

                await N.StartAsync();
                var rotated = await Wait.ForValueAsync(async () =>
                {
                    var s = await Server(P, nodeId);
                    return (!s.GetProperty("keyRotationPending").GetBoolean(), s);
                }, TimeSpan.FromSeconds(30), () => "rotation completed at the next heartbeat\n" + P.LogTail());
                var (oldStatus, _) = await RpcAsync(N, nodeId, KeyOf(token), Hello());
                Assert.Equal(HttpStatusCode.Unauthorized, oldStatus);
                var (newStatus, newReply) = await RpcAsync(N, nodeId, KeyOf(token2), Hello());
                Assert.Equal(HttpStatusCode.OK, newStatus);
                Assert.True(newReply!["ok"]!.GetValue<bool>());
                var synced = await WaitInSync(P, nodeId, null, "in sync with the rotated key");
                return new JsonObject
                {
                    ["rotatedWhileDown"] = false, ["pendingAudit"] = audit.Details, ["oldKeyAfterRotation"] = (int)oldStatus,
                    ["newKeyAfterRotation"] = (int)newStatus, ["status"] = synced.GetProperty("status").GetString(),
                    ["keyRotationPending"] = rotated.GetProperty("keyRotationPending").GetBoolean(),
                };
            });

            string token3 = "";
            await Step("rotation pending and the node joined again with the new token from the CLI (still a node): the primary notices and completes it", async () =>
            {
                await N.StopAsync();
                var regenerated = await P.Api.PostAsync($"api/servers/{nodeId}/token", null).OkJsonAsync("POST /api/servers/{id}/token (node down again)");
                token3 = regenerated.GetProperty("joinToken").GetString()!;
                Assert.False(regenerated.GetProperty("rotated").GetBoolean());
                // Before the fix "cluster join" refused (exit 1) on a server that is already a node.
                var (code, stdout, stderr) = await Cli("cluster", "join", token3, "--data-dir", N.DataDir);
                Assert.True(code == 0, stderr);
                Assert.Contains("again", stdout);
                await N.StartAsync();
                // The primary's rekey (sealed with the key it still considers current) is refused; the new key works.
                await Wait.UntilAsync(async () => !(await Server(P, nodeId)).GetProperty("keyRotationPending").GetBoolean(),
                    TimeSpan.FromSeconds(30), () => "rotation completed with the key the node already uses\n" + P.LogTail());
                var synced = await WaitInSync(P, nodeId, null, "in sync after CLI re-join");
                var (oldStatus, _) = await RpcAsync(N, nodeId, KeyOf(token2), Hello());
                Assert.Equal(HttpStatusCode.Unauthorized, oldStatus);
                var rotatedAudit = P.Store.Col<AuditEntry>().FindAll().Where(a => a.ObjectType == "server" && a.Action == "keyRotated").OrderBy(a => a.CreatedAt).Last();
                Assert.Contains("already uses", rotatedAudit.Details);
                return new JsonObject
                {
                    ["cliJoinExit"] = code, ["cliJoinStdout"] = stdout.Trim(), ["previousKey"] = (int)oldStatus,
                    ["rotationAudit"] = rotatedAudit.Details, ["status"] = synced.GetProperty("status").GetString(),
                };
            });

            await Step("primary database used on another machine = a new primary instance: the node refuses it (alert) until it joins again", async () =>
            {
                await P.StopAsync();
                using (var store = new LiteStore(P.Paths))
                {
                    var s = store.GetSettings<ClusterSettings>();
                    s.PrimaryInstanceMachine = "RESTORED-ON-ANOTHER-HOST";
                    store.SaveSettings(s);
                }
                await P.StartAsync();
                var newId = P.Services.GetRequiredService<ClusterService>().PrimaryInstanceId;
                Assert.NotEqual(N.Store.GetSettings<ClusterSettings>().PinnedPrimaryId, newId);
                var refused = await Wait.ForValueAsync(async () =>
                {
                    var s = await Server(P, nodeId);
                    var error = s.TryGetProperty("lastError", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString()! : "";
                    return (s.GetProperty("status").GetString() == "error" && error.Contains("another instance"), s);
                }, TimeSpan.FromSeconds(30), () => "conflict reported\n" + P.LogTail());
                var conflictEvent = await Wait.ForAsync(() => Task.FromResult(Events(P, "server-sync:" + nodeId).FirstOrDefault(e => e.Severity == EventSeverity.Warning)),
                    TimeSpan.FromSeconds(10), () => "server-sync warning for the conflict");

                // The admin regenerates the token on the new instance (the node refuses its rekey) and joins the node again.
                var regenerated = await P.Api.PostAsync($"api/servers/{nodeId}/token", null).OkJsonAsync("POST /api/servers/{id}/token (new instance)");
                Assert.False(regenerated.GetProperty("rotated").GetBoolean());
                var token4 = regenerated.GetProperty("joinToken").GetString()!;
                await N.Api.PostAsJsonAsync("api/cluster/join", new { token = token4 }).OkJsonAsync("POST /api/cluster/join (re-join, new primary instance)");
                var synced = await WaitInSync(P, nodeId, null, "the new instance manages the node after the re-join");
                Assert.Equal(newId, N.Store.GetSettings<ClusterSettings>().PinnedPrimaryId);
                Assert.False(synced.GetProperty("keyRotationPending").GetBoolean());
                var recovered = await Wait.ForAsync(() => Task.FromResult(Events(P, "server-sync:" + nodeId).FirstOrDefault(e => e.Severity == EventSeverity.Recovered)),
                    TimeSpan.FromSeconds(30), () => "server-sync Recovered");
                return new JsonObject
                {
                    ["newPrimaryInstanceId"] = newId, ["conflictError"] = refused.GetProperty("lastError").GetString(), ["conflictEvent"] = conflictEvent.Message,
                    ["recoveredEvent"] = recovered.Message, ["statusAfterRejoin"] = synced.GetProperty("status").GetString(),
                };
            });

            await Step("removing an unreachable node warns that it still trusts its key", async () =>
            {
                await N.StopAsync();
                using (var del = await P.Api.DeleteAsync($"api/servers/{nodeId}"))
                    Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
                var warning = Events(P, ClusterWorker.RemovedKeyPrefix + nodeId).Single();
                Assert.Equal(EventSeverity.Warning, warning.Severity);
                Assert.Contains("cluster leave", warning.Details);
                var audit = P.Store.Col<AuditEntry>().FindAll().Where(a => a.ObjectType == "server" && a.Action == "deleted").OrderBy(a => a.CreatedAt).Last();
                Assert.Contains("still trusts its cluster key", audit.Details);
                return new JsonObject { ["event"] = warning.Message, ["eventDetails"] = warning.Details, ["audit"] = audit.Details };
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
            output.WriteLine("Artifact: " + E2EArtifacts.Write("cluster-key-rotation-e2e.json", _report));
            if (node is not null) await node.DisposeAsync();
            if (primary is not null) await primary.DisposeAsync();
        }
    }

    internal static byte[] KeyOf(string token) => ClusterCrypto.DeriveKey(JoinToken.Parse(token).Secret);

    internal static JsonObject Hello() => new() { ["op"] = "hello", ["args"] = new JsonObject() };

    /// <summary>Posts an RPC sealed with <paramref name="key"/> to the node, like a primary; returns the status and the decrypted reply.</summary>
    internal static async Task<(HttpStatusCode Status, JsonNode? Reply)> RpcAsync(Manager node, string nodeId, byte[] key, JsonObject plaintext)
    {
        var request = ClusterCrypto.SealRequest(key, nodeId, Encoding.UTF8.GetBytes(plaintext.ToJsonString()), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        using var client = node.NewClient();
        using var resp = await client.PostAsJsonAsync("api/cluster/rpc", request);
        if (resp.StatusCode != HttpStatusCode.OK) return (resp.StatusCode, null);
        var envelope = (await resp.Content.ReadFromJsonAsync<RpcEnvelope>())!;
        var plain = ClusterCrypto.Open(key, envelope, ClusterCrypto.ResponseAad(nodeId, envelope.Ts, envelope.Nonce, request.Nonce));
        Assert.NotNull(plain);
        return (resp.StatusCode, JsonNode.Parse(plain!));
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
