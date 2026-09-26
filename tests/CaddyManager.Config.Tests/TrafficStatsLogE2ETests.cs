using System.Text.Json;
using System.Text.Json.Nodes;
using CaddyManager.Core.Models;

namespace CaddyManager.Config.Tests;

/// <summary>
/// The traffic statistics log (logging.logs.cpm_stats) with the real Caddy binary: every HTTP request lands there once,
/// filtered, while per-host access logs keep working and caddy.log stays free of access entries. Artifact: stats-log.json.
/// </summary>
public sealed class TrafficStatsLogE2ETests
{
    /// <summary>
    /// Ways it could fail:
    /// (1) a server without `logs` logs nothing (requests of hosts without an access log missing from the stats file);
    /// (2) skip_unmapped_hosts still set: requests for unknown hosts (default site) are not logged;
    /// (3) the stats sink's include does not match named access loggers (http.log.access.cpm_access_*), so requests of
    ///     hosts WITH an access log are missing from the stats file; or a request is written twice;
    /// (4) the filter encoder field paths are wrong (request&gt;headers / resp_headers / request&gt;tls still present), or the
    ///     filter drops fields the statistics need (host, client_ip, status, size, bytes_read, duration, method, uri);
    /// (5) the per-host access log loses its entries (e.g. an upper-case Host routed by the vars handler) or receives
    ///     other hosts' requests;
    /// (6) caddy.log (logs.default) receives access entries;
    /// (7) Caddy rejects the writer options (roll_compression, mode) of v2.11.4's timberjack writer.
    /// </summary>
    [CaddyFact]
    public async Task Every_request_is_logged_once_filtered_and_per_host_logs_keep_working()
    {
        var report = E2EArtifacts.Report(nameof(Every_request_is_logged_once_filtered_and_per_host_logs_keep_working));
        using var c = new LiveCaddy();
        // stats-a: its own access log, internal TLS (HTTPS and plain HTTP); stats-b: no access log, HTTP only.
        var a = new SiteHost
        {
            Id = "hosta", Kind = HostKind.Response, Domains = ["stats-a.test"], Tls = TlsMode.Internal, ForceHttps = false,
            AccessLog = true, ResponseStatus = 200, ResponseBody = "a",
        };
        var b = new SiteHost { Id = "hostb", Kind = HostKind.Response, Domains = ["stats-b.test"], Tls = TlsMode.None, ResponseStatus = 404, ResponseBody = "b" };
        c.Add(a);
        c.Add(b);
        await c.StartAsync();
        var apply = await c.ApplyAsync(); // (7)
        report["applyWarnings"] = new JsonArray(apply.Warnings.Select(w => (JsonNode)w).ToArray());
        var running = await c.RunningConfigAsync();
        report["statsSink"] = running["logging"]!["logs"]!["cpm_stats"]!.DeepClone();
        report["serverLogs"] = new JsonObject
        {
            ["srv0"] = running["apps"]!["http"]!["servers"]!["srv0"]!["logs"]?.DeepClone(),
            ["srv1"] = running["apps"]!["http"]!["servers"]!["srv1"]!["logs"]?.DeepClone(),
        };

        var sent = new List<(string Host, string Uri, string Via)>();
        async Task Http(string host, string uri)
        {
            var r = await c.HttpAsync(host, uri, ("Cookie", "session=cpm-secret-cookie"), ("X-Probe", "cpm-probe-header"));
            Assert.NotEqual(0, r.Status);
            sent.Add((host.ToLowerInvariant(), uri, "http"));
        }
        await Http("stats-a.test", "/one?x=1");
        await Http("STATS-A.TEST", "/upper"); // (5) routed case-insensitively, logged through the vars handler
        await Http("stats-b.test", "/two");
        await Http("stats-b.test", "/three");
        await Http("unknown-host.test", "/nobody"); // (2)
        var tls = await TlsProbe.UntilAsync(c.HttpsPort, "stats-a.test", r => r.Status == 200, TimeSpan.FromSeconds(30));
        Assert.Equal(200, tls.Status);
        sent.Add(("stats-a.test", "/", "https"));
        report["requestsSent"] = new JsonArray(sent.Select(s => (JsonNode)new JsonObject { ["host"] = s.Host, ["uri"] = s.Uri, ["via"] = s.Via }).ToArray());

        var statsFile = c.S.Paths.StatsLogFile;
        var hostLog = Path.Combine(c.S.Paths.AccessLogDir, "stats-a.test.log");
        // The TLS probe may have needed retries while the internal certificate was issued: count what reached Caddy.
        await Wait.Until(() => Task.FromResult(ReadJsonLines(statsFile).Count >= sent.Count), TimeSpan.FromSeconds(15),
            () => $"stats log has {ReadJsonLines(statsFile).Count} entries, expected {sent.Count}:\n" + string.Join('\n', ReadLines(statsFile)));
        await Task.Delay(500);
        var stats = ReadJsonLines(statsFile);
        var perHost = ReadJsonLines(hostLog);
        var caddyLog = ReadLines(c.S.Paths.CaddyProcessLog);

        report["statsEntries"] = new JsonArray(stats.Select(e => e.DeepClone()).ToArray());
        report["perHostEntries"] = perHost.Count;
        report["perHostHosts"] = new JsonArray(perHost.Select(e => (JsonNode)(e["request"]?["host"]?.GetValue<string>() ?? "")).ToArray());

        // (1)(2)(3) one entry per request (HTTP ones exactly; the HTTPS probe once per successful attempt)
        foreach (var (host, uri, via) in sent.Where(s => s.Via == "http"))
            Assert.Single(stats, e => e["request"]!["host"]!.GetValue<string>().Equals(host, StringComparison.OrdinalIgnoreCase)
                && e["request"]!["uri"]!.GetValue<string>() == uri && e["request"]!["proto"]!.GetValue<string>() == "HTTP/1.1");
        // the HTTPS request (served by srv0, TLS details filtered out below)
        Assert.Contains(stats, e => e["request"]!["host"]!.GetValue<string>().StartsWith("stats-a.test", StringComparison.Ordinal) && e["request"]!["uri"]!.GetValue<string>() == "/");
        // (4) filtered: no headers / TLS details; the fields the statistics use are all there
        foreach (var e in stats)
        {
            Assert.Null(e["request"]!["headers"]);
            Assert.Null(e["request"]!["tls"]);
            Assert.Null(e["resp_headers"]);
            foreach (var f in new[] { "client_ip", "remote_ip", "host", "method", "proto", "uri" }) Assert.NotNull(e["request"]![f]);
            foreach (var f in new[] { "ts", "bytes_read", "size", "status", "duration" }) Assert.NotNull(e[f]);
            Assert.StartsWith("http.log.access", e["logger"]!.GetValue<string>());
        }
        // Caddy still has the log open: read with ReadWrite|Delete sharing (File.ReadAllText fails on Windows).
        var raw = string.Join('\n', ReadLines(statsFile));
        Assert.DoesNotContain("cpm-secret-cookie", raw);
        Assert.DoesNotContain("cpm-probe-header", raw);
        Assert.Contains(stats, e => e["request"]!["host"]!.GetValue<string>() == "unknown-host.test" && e["status"]!.GetValue<int>() == 404);
        // (5) the per-host log: only stats-a requests, all of them (incl. the upper-case Host), unfiltered
        Assert.All(perHost, e => Assert.StartsWith("stats-a.test", e["request"]!["host"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(perHost, e => e["request"]!["uri"]!.GetValue<string>() == "/upper");
        Assert.Contains(perHost, e => e["request"]!["uri"]!.GetValue<string>() == "/one?x=1");
        Assert.Contains(perHost, e => e["request"]!["tls"] is not null); // proves (4) filters only the stats sink
        Assert.Equal(stats.Count(e => e["request"]!["host"]!.GetValue<string>().StartsWith("stats-a.test", StringComparison.OrdinalIgnoreCase)), perHost.Count);
        // (6)
        var accessInCaddyLog = caddyLog.Count(l => l.Contains("\"logger\":\"http.log.access", StringComparison.Ordinal));
        report["caddyLogAccessEntries"] = accessInCaddyLog;
        Assert.Equal(0, accessInCaddyLog);
        report["passed"] = true;
        E2EArtifacts.Write("stats-log.json", report);
    }

    private static List<string> ReadLines(string file)
    {
        if (!File.Exists(file)) return [];
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(fs).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static List<JsonNode> ReadJsonLines(string file)
    {
        var list = new List<JsonNode>();
        foreach (var line in ReadLines(file))
        {
            try
            {
                if (JsonNode.Parse(line) is { } n) list.Add(n);
            }
            catch (JsonException)
            {
                // partial line while Caddy writes
            }
        }
        return list;
    }
}
