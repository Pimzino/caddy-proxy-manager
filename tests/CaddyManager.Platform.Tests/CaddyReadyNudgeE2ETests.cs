using System.Text.Json.Nodes;
using CaddyManager.Core;
using CaddyManager.Platform.Hosting;
using CaddyManager.Platform.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaddyManager.Platform.Tests;

/// <summary>
/// The building block of the START_PENDING fix (CaddyServiceStartPendingE2ETests, Windows only) that runs on every OS:
/// CaddyHostSupport.ReloadUnchangedConfigAsync against the real Caddy binary. On Windows its only purpose is the
/// notify.Ready() at the end of caddy.Load (https://github.com/caddyserver/caddy/blob/v2.11.4/caddy.go); here we prove it
/// has no other effect on a running Caddy.
///
/// Ways it can fail:
///  1. The re-post forces a full reload (as ICaddyAdminClient.LoadAsync does with Cache-Control: must-revalidate), so every
///     nudge re-provisions all apps. Detected by Caddy's own log: "config is unchanged" and no second "server running"
///     (the admin API logs "load complete" for every /load, also when nothing was reloaded, so that line proves nothing).
///  2. The config read back and re-posted differs from the running one (re-encoding), so it is reloaded or changed.
///  3. Caddy's admin API rejects the request (Host/Origin checks), so the nudge silently does nothing.
///  4. A Caddy running without any config (GET /config/ returns null) gets "null" loaded.
///  5. Serving is interrupted by the nudge.
/// Control: the same config posted with must-revalidate logs another "server running", so check 1 can detect a reload.
/// Artifact: caddy-ready-nudge.json (records the Caddy version).
/// </summary>
[Trait("Category", "Caddy")]
public class CaddyReadyNudgeE2ETests
{
    private static int Count(string log, string msg) =>
        log.Split('\n').Count(l => l.Contains($"\"msg\":\"{msg}\"", StringComparison.Ordinal));

    private static string ReadLog(string file)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs);
        return r.ReadToEnd();
    }

    [Fact]
    public async Task ReloadUnchangedConfigDoesNotReloadAndSkipsAnEmptyConfig()
    {
        var ct = TestContext.Current.CancellationToken;
        using var env = new TempEnvironment();
        DevCaddy.InstallInto(env.Paths);
        var adminPort = DevCaddy.FreeTcpPort();
        var httpPort = DevCaddy.FreeTcpPort();
        var log = Path.Combine(env.Root, "caddy-runtime.log");
        using var svc = new PlatformServices(env, adminPort, DevCaddy.MinimalConfig(adminPort, httpPort, log));
        var host = svc.Get<ICaddyHost>();
        var support = svc.Get<CaddyHostSupport>();
        var report = E2EArtifacts.Report(nameof(ReloadUnchangedConfigDoesNotReloadAndSkipsAnEmptyConfig));
        var version = await ProcessRunner.RunAsync(env.Paths.CaddyExe, ["version"], new ProcessOptions { Timeout = TimeSpan.FromSeconds(20) }, ct);
        report["caddyVersion"] = version.StdOut.Trim();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            await host.StartAsync(ct);
            var before = await svc.Admin.GetConfigAsync(ct);
            var loadsBefore = Count(ReadLog(log), "server running");

            // (1)(2)(3)(5)
            var posted = await support.ReloadUnchangedConfigAsync(ct);
            await Task.Delay(300, ct);
            var afterLog = ReadLog(log);
            var after = await svc.Admin.GetConfigAsync(ct);
            var body = await http.GetStringAsync($"http://127.0.0.1:{httpPort}/", ct);
            report["nudge"] = new JsonObject
            {
                ["posted"] = posted, ["serverRunningBefore"] = loadsBefore, ["serverRunningAfter"] = Count(afterLog, "server running"),
                ["configIsUnchanged"] = Count(afterLog, "config is unchanged"), ["configEqual"] = before == after, ["http"] = body,
            };
            Assert.True(posted);
            Assert.Equal(1, Count(afterLog, "config is unchanged"));
            Assert.Equal(loadsBefore, Count(afterLog, "server running"));
            Assert.Equal(before, after);
            Assert.Equal("hello", body);

            // Control for (1): a forced reload of the same config is visible in the log.
            using (var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{adminPort}/load")
                   {
                       Content = new StringContent(before!, System.Text.Encoding.UTF8, "application/json"),
                   })
            {
                req.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { MustRevalidate = true };
                using var resp = await http.SendAsync(req, ct);
                Assert.True(resp.IsSuccessStatusCode);
            }
            await Task.Delay(300, ct);
            var forcedLoads = Count(ReadLog(log), "server running");
            report["controlForcedReloadServerRunning"] = forcedLoads;
            Assert.True(forcedLoads > loadsBefore, "Control failed: a forced reload did not log 'server running' again, so check (1) proves nothing.");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
            E2EArtifacts.Write("caddy-ready-nudge.json", report);
        }

        // (4) An admin API whose config is null (what Caddy's GET /config/ returns without a config): nothing is posted.
        // (Not a real bare Caddy: that would bind the default admin port 2019, which a developer's Caddy may use.)
        using var nullAdmin = new MiniHttpServer((_, _) => (200, System.Text.Encoding.UTF8.GetBytes("null\n")));
        var sc = new ServiceCollection();
        sc.AddSingleton<ICaddyAdminClient>(new FakeAdminClient(nullAdmin.Base));
        using (var sp = sc.BuildServiceProvider())
        {
            var nullSupport = new CaddyHostSupport(env.Paths, sp, NullLogger<CaddyHostSupport>.Instance);
            var postedNull = await nullSupport.ReloadUnchangedConfigAsync(ct);
            var requests = nullAdmin.Requests;
            report["nullConfig"] = new JsonObject { ["posted"] = postedNull, ["requests"] = string.Join(" | ", requests) };
            Assert.False(postedNull);
            Assert.DoesNotContain(requests, r => r.StartsWith("POST", StringComparison.Ordinal));
            Assert.Contains("GET /config/", requests);
        }
        E2EArtifacts.Write("caddy-ready-nudge.json", report);
    }
}
