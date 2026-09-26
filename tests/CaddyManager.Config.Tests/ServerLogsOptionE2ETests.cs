using System.Text.Json;
using System.Text.Json.Nodes;
using CaddyManager.Core.Models;

namespace CaddyManager.Config.Tests;

/// <summary>
/// LOG-1: a "logs" key in the advanced server options JSON is merged into the generated servers.*.logs instead of
/// replacing it, so per-host access logs and traffic statistics keep working. Real Caddy. Artifact: server-logs-option.json.
/// </summary>
public sealed class ServerLogsOptionE2ETests
{
    /// <summary>
    /// Ways it could fail:
    /// (1) the user's logs object replaces the generated one: logger_names is lost and the per-host access log stops
    ///     receiving entries (everything goes to the base access logger), silently;
    /// (2) the user's own keys (should_log_credentials) are dropped instead of merged;
    /// (3) skip_unmapped_hosts from the user stops statistics for unknown hosts;
    /// (4) {"logs": null} turns access logging off, so the statistics log stays empty while the Traffic page says enabled;
    /// (5) none of this is reported as a warning.
    /// </summary>
    [CaddyFact]
    public async Task Logs_server_option_is_merged_and_never_disables_per_host_logs_or_statistics()
    {
        var report = E2EArtifacts.Report(nameof(Logs_server_option_is_merged_and_never_disables_per_host_logs_or_statistics));
        using var c = new LiveCaddy(s => s.ServerOptionsJson =
            """{"logs":{"should_log_credentials":true,"logger_names":{"logs-a.test":["bogus"]},"skip_unmapped_hosts":true}}""");
        c.Add(new SiteHost { Kind = HostKind.Response, Domains = ["logs-a.test"], Tls = TlsMode.None, AccessLog = true, ResponseStatus = 200, ResponseBody = "a" });
        await c.StartAsync();
        var apply = await c.ApplyAsync();
        report["warnings"] = new JsonArray(apply.Warnings.Select(w => (JsonNode)w).ToArray());
        Assert.Contains(apply.Warnings, w => w.Contains("logs.logger_names", StringComparison.Ordinal)); // (5)
        Assert.Contains(apply.Warnings, w => w.Contains("logs.skip_unmapped_hosts", StringComparison.Ordinal));
        var running = await c.RunningConfigAsync();
        var logs = running["apps"]!["http"]!["servers"]!.AsObject().Select(kv => kv.Value!["logs"]).First(l => l?["logger_names"] is not null)!;
        report["serverLogs"] = logs.DeepClone();
        Assert.NotEqual("bogus", logs["logger_names"]!["logs-a.test"]![0]!.GetValue<string>()); // (1)
        Assert.True(logs["should_log_credentials"]!.GetValue<bool>()); // (2)
        Assert.Null(logs["skip_unmapped_hosts"]); // (3)

        var hostLog = Path.Combine(c.S.Paths.AccessLogDir, "logs-a.test.log");
        Assert.Equal(200, (await c.HttpAsync("logs-a.test", "/merged", ("Cookie", "session=cpm-visible-cookie"))).Status);
        Assert.Equal(404, (await c.HttpAsync("unmapped.test", "/unknown")).Status);
        await Wait.Until(() => Task.FromResult(Lines(hostLog).Any(l => l.Contains("/merged", StringComparison.Ordinal))
                                              && Lines(c.S.Paths.StatsLogFile).Any(l => l.Contains("/unknown", StringComparison.Ordinal))),
            TimeSpan.FromSeconds(10), () => "per-host log:\n" + string.Join('\n', Lines(hostLog)) + "\nstats:\n" + string.Join('\n', Lines(c.S.Paths.StatsLogFile)));
        // (2) should_log_credentials really applies: the per-host log shows the cookie
        Assert.Contains(Lines(hostLog), l => l.Contains("cpm-visible-cookie", StringComparison.Ordinal));

        // (4) logs: null is ignored while access logging is needed
        c.UpdateSettings(s => s.ServerOptionsJson = """{"logs":null}""");
        apply = await c.ApplyAsync();
        report["warningsNull"] = new JsonArray(apply.Warnings.Select(w => (JsonNode)w).ToArray());
        Assert.Contains(apply.Warnings, w => w.Contains("Server option 'logs' must be a JSON object", StringComparison.Ordinal));
        var before = Lines(c.S.Paths.StatsLogFile).Count;
        Assert.Equal(200, (await c.HttpAsync("logs-a.test", "/after-null")).Status);
        await Wait.Until(() => Task.FromResult(Lines(c.S.Paths.StatsLogFile).Count > before && Lines(hostLog).Any(l => l.Contains("/after-null", StringComparison.Ordinal))),
            TimeSpan.FromSeconds(10), () => "statistics / per-host log stopped after {\"logs\":null}");
        report["passed"] = true;
        E2EArtifacts.Write("server-logs-option.json", report);
    }

    private static List<string> Lines(string file)
    {
        if (!File.Exists(file)) return [];
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(fs).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
    }
}
