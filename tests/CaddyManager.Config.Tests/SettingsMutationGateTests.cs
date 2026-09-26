using System.Net;
using System.Text.Json.Nodes;
using CaddyManager.Config.Endpoints;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace CaddyManager.Config.Tests;

/// <summary>
/// CL-2 (Config half): IConfigMutationLock is the Config module's ConfigMutationGate, and PUT /api/settings/caddy reads the
/// settings it rewrites under that lock, so a cluster replication that holds the lock (NodeSync on a node) is never
/// reverted by a concurrent node-local settings change. Real HTTP (TestServer) and a real LiteDB store; the replication
/// is simulated by writing replicated settings while holding the lock, as NodeSync does.
/// </summary>
public sealed class SettingsMutationGateTests
{
    private sealed class NodeRole : IClusterRole
    {
        public ClusterRole Role => ClusterRole.Node;
        public string? PrimaryName => "primary-1";
    }

    /// <summary>
    /// Ways it could fail:
    /// (1) IConfigMutationLock is not registered, or is a different lock than the one the Config endpoints use;
    /// (2) the settings PUT does not wait for the lock (it persists while replication is half-way);
    /// (3) the PUT builds the new document from settings read before it got the lock, so the replicated values written
    ///     meanwhile are reverted (and the node would report a revision it no longer runs);
    /// (4) on a managed node the PUT of a node-local field is refused as a replicated change because it compares against
    ///     stale settings;
    /// (5) the lock is not released (double dispose releases twice and lets two mutations in, or the PUT keeps it).
    /// </summary>
    [Fact]
    public async Task Node_local_settings_change_waits_for_replication_and_keeps_the_replicated_values()
    {
        var report = E2EArtifacts.Report(nameof(Node_local_settings_change_waits_for_replication_and_keeps_the_replicated_values));
        await using var api = ApiHost.Start(configure: s => s.AddSingleton<IClusterRole>(new NodeRole()));
        var gate = api.App.Services.GetRequiredService<IConfigMutationLock>();
        Assert.Same(api.App.Services.GetRequiredService<ConfigMutationGate>(), gate); // (1)

        var held = await gate.AcquireAsync();
        var newPort = Net.FreeTcpPort();
        var put = api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new { httpsPort = newPort });
        await Task.Delay(500);
        Assert.False(put.IsCompleted, "the settings PUT did not wait for the configuration lock"); // (2)
        Assert.Null(await gate.TryAcquireAsync(TimeSpan.FromMilliseconds(50)));

        // Replication (revision R2) while the PUT waits: replicated settings change.
        var replicated = api.Store.GetSettings<CaddySettings>();
        replicated.AcmeEmail = "replicated@example.com";
        replicated.TrustedProxies = ["10.0.0.0/8"];
        api.Store.SaveSettings(replicated);
        held.Dispose();
        held.Dispose(); // (5) idempotent

        var r = await put;
        var text = await r.Content.ReadAsStringAsync();
        report["putStatus"] = (int)r.StatusCode;
        report["putResponse"] = JsonNode.Parse(text);
        Assert.True(r.StatusCode == HttpStatusCode.OK, text); // (4)
        var final = api.Store.GetSettings<CaddySettings>();
        report["final"] = new JsonObject
        {
            ["httpsPort"] = final.HttpsPort,
            ["acmeEmail"] = final.AcmeEmail,
            ["trustedProxies"] = new JsonArray(final.TrustedProxies.Select(p => (JsonNode)p).ToArray()),
        };
        Assert.Equal(newPort, final.HttpsPort);
        Assert.Equal("replicated@example.com", final.AcmeEmail); // (3)
        Assert.Equal(["10.0.0.0/8"], final.TrustedProxies);

        using (var again = await gate.TryAcquireAsync(TimeSpan.FromSeconds(1))) Assert.NotNull(again); // (5)
        using (var third = await gate.TryAcquireAsync(TimeSpan.FromSeconds(1))) Assert.NotNull(third);
        report["passed"] = true;
        E2EArtifacts.Write("settings-mutation-gate.json", report);
    }
}
