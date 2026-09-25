using System.Text.Json.Nodes;
using CaddyManager.Config.Admin;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace CaddyManager.Config.Tests;

/// <summary>
/// Caddy swaps its admin endpoint BEFORE provisioning/starting the apps of a new config and does not swap it back
/// when the load fails (caddy.go v2.11.4 provisionContext → replaceLocalAdminServer), although the old sites keep
/// running (research #13). The manager must move the admin endpoint back after a rejected load.
/// </summary>
public sealed class AdminEndpointE2ETests
{
    /// <summary>
    /// Ways it could fail: (1) after a rejected config that also changes the admin address, Caddy's admin API stays on
    /// the NEW address while settings and caddy.json keep the old one, so the manager loses contact (shown as down,
    /// auto-restarts); (2) the restore reloads a config that is not the last good one (sites change); (3) the old sites
    /// stop serving; (4) the next apply fails; (5) the same happens for a START failure (listener cannot bind), not
    /// only a provisioning error; (6) the manager can no longer find Caddy when the restore itself was interrupted;
    /// (7) the control shows Caddy really leaves the admin API on the rejected address.
    /// </summary>
    [CaddyFact]
    public async Task Rejected_config_with_a_new_admin_address_leaves_caddy_reachable_on_the_old_one()
    {
        var report = E2EArtifacts.Report(nameof(Rejected_config_with_a_new_admin_address_leaves_caddy_reachable_on_the_old_one));
        using var c = new LiveCaddy();
        var host = new SiteHost { Kind = HostKind.Response, Domains = ["ok.test"], Tls = TlsMode.None, ResponseStatus = 200, ResponseBody = "good", Compression = false };
        c.Add(host);
        await c.StartAsync();
        await c.ApplyAsync("good");
        var goodConfig = File.ReadAllText(c.S.Paths.CaddyConfigFile);
        var oldAdmin = $"127.0.0.1:{c.AdminPort}";
        var factory = c.S.Provider.GetRequiredService<CaddyAdminClient>();

        async Task<JsonObject> Scenario(string label, Action<CaddySettings> change, Action<SiteHost>? breakHost)
        {
            var newAdmin = $"127.0.0.1:{Net.FreeTcpPort()}";
            var before = c.S.Store.GetSettings<CaddySettings>();
            c.UpdateSettings(s => { s.AdminListen = newAdmin; change(s); });
            if (breakHost is not null) { breakHost(host); c.S.Store.Col<SiteHost>().Update(host); }
            var result = await c.S.Config.ApplyAsync(label);
            // What ConfigTransaction does after a failed apply: the settings/host change is reverted.
            c.S.Store.SaveSettings(before);
            host.AdvancedRoutesJson = null;
            c.S.Store.Col<SiteHost>().Update(host);

            var oldReachable = await factory.ForAddress(oldAdmin).IsReachableAsync();
            var newReachable = await factory.ForAddress(newAdmin).IsReachableAsync();
            var running = oldReachable ? JsonNode.Parse((await factory.ForAddress(oldAdmin).GetConfigAsync())!) : null;
            var site = await c.HttpAsync("ok.test", "/");
            var obs = new JsonObject
            {
                ["scenario"] = label, ["applySuccess"] = result.Success, ["error"] = result.Error,
                ["oldAdminReachable"] = oldReachable, ["newAdminReachable"] = newReachable,
                ["runningAdminListen"] = running?["admin"]?["listen"]?.GetValue<string>(), ["siteStatus"] = site.Status, ["siteBody"] = site.Body,
            };
            Assert.False(result.Success, label + ": the broken config was accepted");
            Assert.True(oldReachable, label + ": admin API not back on " + oldAdmin);            // (1)(5)
            Assert.False(newReachable, label + ": admin API still on " + newAdmin);
            Assert.Equal(oldAdmin, running!["admin"]!["listen"]!.GetValue<string>());
            Assert.Equal(goodConfig, File.ReadAllText(c.S.Paths.CaddyConfigFile));               // (2)
            Assert.Equal(200, site.Status);                                                        // (3)
            Assert.Equal("good", site.Body);
            Assert.True((await c.S.Config.ApplyAsync(label + " fixed")).Success);                  // (4)
            return obs;
        }

        var scenarios = new JsonArray
        {
            // provisioning error (unknown handler in advanced routes)
            await Scenario("provisioning error", _ => { }, h => h.AdvancedRoutesJson = """[{"handle":[{"handler":"no_such_handler_e2e"}]}]"""),
            // start error: a bind address that is not on this machine (TEST-NET-1) cannot be listened on
            await Scenario("start error", s => s.BindAddresses = ["192.0.2.1"], null),
        };
        report["scenarios"] = scenarios;

        // (6) an interrupted restore: Caddy left on the rejected address; the next apply must still find it.
        var strandedAdmin = $"127.0.0.1:{Net.FreeTcpPort()}";
        var stranded = JsonNode.Parse(goodConfig)!;
        stranded["admin"]!["listen"] = strandedAdmin;
        var bogus = stranded.DeepClone();
        bogus["apps"]!["http"]!["servers"]!["srv1"]!["routes"]!.AsArray().Insert(0, new JsonObject { ["handle"] = new JsonArray(new JsonObject { ["handler"] = "no_such_handler_e2e" }) });
        // CONTROL (7): the raw admin API call leaves Caddy on the rejected address.
        await Assert.ThrowsAsync<CaddyAdminException>(() => factory.ForAddress(oldAdmin).LoadAsync(bogus.ToJsonString()));
        var controlOld = await factory.ForAddress(oldAdmin).IsReachableAsync();
        var controlNew = await factory.ForAddress(strandedAdmin).IsReachableAsync();
        report["control"] = new JsonObject { ["oldAdminReachable"] = controlOld, ["rejectedAdminReachable"] = controlNew };
        Assert.False(controlOld);
        Assert.True(controlNew);
        // The failed revision records the rejected admin address; the service finds Caddy there and moves it back.
        c.S.Store.Col<ConfigRevision>().Insert(new ConfigRevision { Json = bogus.ToJsonString(), Reason = "interrupted", Success = false, Error = "e2e" });
        var recovered = await c.S.Config.ApplyAsync("after interrupted restore");
        report["interruptedRestoreApply"] = new JsonObject { ["success"] = recovered.Success, ["error"] = recovered.Error, ["writtenOnly"] = recovered.WrittenOnly };
        Assert.True(recovered.Success, recovered.Error);
        Assert.False(recovered.WrittenOnly, "the manager did not find the running Caddy");
        Assert.True(await factory.ForAddress(oldAdmin).IsReachableAsync());
        Assert.False(await factory.ForAddress(strandedAdmin).IsReachableAsync());

        E2EArtifacts.Write("admin-endpoint-rollback.json", report);
        await c.Admin.StopAsync();
    }
}
