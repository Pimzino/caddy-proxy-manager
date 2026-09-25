using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit.Abstractions;
using static CaddyManager.Cluster.Tests.ClusterE2ETests;

namespace CaddyManager.Cluster.Tests;

/// <summary>
/// End to end: with a heartbeat far longer than the test, a configuration change on the primary reaches the node through
/// the debounced push on IConfigChangeFeed.Applied (several quick changes → one push), not through the heartbeat.
/// Writes e2e-artifacts/cluster-push-e2e.json.
/// </summary>
public sealed class ClusterPushE2ETests(ITestOutputHelper output)
{
    [Fact]
    public async Task ConfigurationChangeIsPushedWithoutWaitingForTheHeartbeat()
    {
        Assert.True(DevCaddy.Path is not null, "The development Caddy binary .dev/bin/caddy is required (copy .dev from the main checkout).");
        var report = E2EArtifacts.Report(nameof(ClusterPushE2ETests) + "." + nameof(ConfigurationChangeIsPushedWithoutWaitingForTheHeartbeat));
        await using var upstream = await Upstream.StartAsync();
        Manager? primary = null, node = null;
        try
        {
            void Slow(ClusterOptions o)
            {
                o.HeartbeatInterval = TimeSpan.FromMinutes(10);
                o.SyncDebounce = TimeSpan.FromSeconds(3);
            }
            var p = Manager.CreateAsync("primary-push", clusterOptions: Slow);
            var n = Manager.CreateAsync("node-push", clusterOptions: Slow);
            primary = await p;
            node = await n;
            var P = primary;
            var N = node;
            report["configChangeFeed"] = P.FakeFeed is null ? "Config module" : "test stand-in (Config module in this build has none; raised by the test)";

            var added = await P.Api.PostAsJsonAsync("api/servers", new { name = "node-push", url = N.Url }).OkJsonAsync("POST /api/servers");
            var nodeId = added.GetProperty("server").GetProperty("id").GetString()!;
            await N.Api.PostAsJsonAsync("api/cluster/join", new { token = added.GetProperty("joinToken").GetString() }).OkJsonAsync("join");
            // Initial state: the explicit "Sync now" (the first heartbeat right after adding happened before the node joined).
            var initial = await P.Api.PostAsync($"api/servers/{nodeId}/sync", null).OkJsonAsync("POST /api/servers/{id}/sync");
            var initialRevision = Rev(initial);
            Assert.NotEqual("", initialRevision);

            int NodeSyncs() => N.Store.Col<Core.Models.AuditEntry>().FindAll().Count(a => a.ObjectType == "cluster" && a.Action == "synced");
            var syncsBefore = NodeSyncs();
            var clock = Stopwatch.StartNew();
            foreach (var domain in new[] { "push1.cluster.test", "push2.cluster.test", "push3.cluster.test" })
            {
                await P.Api.PostAsJsonAsync("api/hosts", new
                {
                    kind = "proxy", domains = new[] { domain }, tls = "none", compression = false,
                    upstreams = new[] { new { scheme = "http", host = "127.0.0.1", port = upstream.Port } },
                }).OkJsonAsync("POST /api/hosts " + domain);
                P.FakeFeed?.Raise("Host created");
            }
            var synced = await WaitInSync(P, nodeId, initialRevision, "pushed after the change");
            var pushMs = clock.ElapsedMilliseconds;
            Assert.True(pushMs < 60_000, $"pushed after {pushMs} ms (heartbeat is 10 minutes)");
            var (code, body) = await WaitHttp(N.HttpPort, "push3.cluster.test", "/pushed");
            // Debounced: the three changes arrive as one sync on the node.
            var nodeSyncs = NodeSyncs() - syncsBefore;

            report["result"] = "passed";
            report["verified"] = new JsonObject
            {
                ["initialRevision"] = initialRevision, ["pushedRevision"] = Rev(synced), ["pushAfterMs"] = pushMs, ["heartbeatInterval"] = "00:10:00",
                ["syncDebounce"] = "00:00:03", ["nodeSyncsForThreeChanges"] = nodeSyncs, ["nodeHttpStatus"] = code, ["nodeBody"] = body,
            };
            Assert.Equal(1, nodeSyncs); // one debounced push for three changes
        }
        catch (Exception ex)
        {
            report["result"] = "failed";
            report["error"] = ex.ToString();
            if (primary is not null) report["primaryLog"] = primary.LogTail(150);
            if (node is not null) report["nodeLog"] = node.LogTail(150);
            throw;
        }
        finally
        {
            output.WriteLine("Artifact: " + E2EArtifacts.Write("cluster-push-e2e.json", report));
            if (node is not null) await node.DisposeAsync();
            if (primary is not null) await primary.DisposeAsync();
        }
    }
}
