using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using CaddyManager.Telemetry.Traffic;
using Microsoft.Extensions.DependencyInjection;

namespace CaddyManager.Telemetry.Tests;

/// <summary>
/// End to end (SEC-2, TEL-1, TEL-2): the Host header is chosen by the client, and Caddy logs every request whatever its
/// Host. A real Caddy v2.11.4 runs the generated configuration for one exact host and one wildcard host; thousands of
/// requests with random Host headers, IP literals, names one label too deep for the wildcard, and Host headers of 1,100
/// and 8,000 characters go through it, together with known traffic for the configured names. Statistics must keep one
/// bucket per configured name (the wildcard as one), file everything else under "(other)", keep saving (an over-long
/// Host used to poison every save), stay small on disk, answer reports, pick up a host added later as soon as the
/// configuration is applied, and count nothing twice after a restart. Artifact: host-flood.json.
/// </summary>
public sealed class HostFloodE2ETests
{
    private const string Shop = "shop.flood.test";
    private const string Apps = "*.apps.flood.test";
    private const string Late = "late.flood.test";
    private const int RandomHosts = 3000;
    private const int IpHosts = 100;
    private const int ShopRequests = 200;
    private const int AppRequests = 300;
    private const int TooDeep = 20;
    private const int WildcardParent = 20;
    private const int LateRequests = 50;
    private static readonly int[] LongHostLengths = [1_100, 8_000];

    [CaddyFact]
    public async Task RandomHostHeadersAggregateIntoOther_StatsKeepSaving_AndStayBounded()
    {
        var report = E2EArtifacts.Report(nameof(RandomHostHeadersAggregateIntoOther_StatsKeepSaving_AndStayBounded));
        using var env = new TempEnv();
        var port = Net.FreeTcpPort();
        GeneratedConfig.Settings(env, port);
        GeneratedConfig.AddResponseHost(env, 200, "shop", Shop);
        GeneratedConfig.AddResponseHost(env, 200, "app", Apps);
        var generated = GeneratedConfig.Generate(env);
        Assert.Empty(generated.Warnings);
        report["statsSink"] = GeneratedConfig.StatsSink(generated.Config).DeepClone();
        using var caddy = new CaddyProcess(env.Paths, generated.Config.ToJsonString());
        await caddy.WaitForPortAsync(port, TimeSpan.FromSeconds(20));

        var feed = new FakeConfigChangeFeed();
        var sp = env.Services(o =>
        {
            o.FlushInterval = TimeSpan.FromMilliseconds(200);
            o.BlobFlushInterval = TimeSpan.FromSeconds(1);
        }, extra: s => s.AddSingleton<IConfigChangeFeed>(feed));
        var ingestion = sp.GetRequiredService<TrafficIngestion>();
        var telemetry = sp.GetRequiredService<IServerTelemetry>();
        var sw = Stopwatch.StartNew();

        // ---- the flood, plus known traffic for the configured names
        using var http = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = 16, UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        var hosts = new List<string>();
        for (var i = 0; i < RandomHosts; i++) hosts.Add($"{Guid.NewGuid():N}.flood-{i % 97}.example");
        for (var i = 0; i < IpHosts; i++) hosts.Add($"10.{i % 7}.{i % 13}.{i}");
        for (var i = 0; i < ShopRequests; i++) hosts.Add($"SHOP.Flood.Test:{port}"); // mixed case and port: still the shop
        for (var i = 0; i < AppRequests; i++) hosts.Add($"{Guid.NewGuid():N}.apps.flood.test");
        for (var i = 0; i < TooDeep; i++) hosts.Add($"x{i}.y.apps.flood.test"); // "*" is one label in Caddy: not the wildcard
        for (var i = 0; i < WildcardParent; i++) hosts.Add("apps.flood.test");   // nor its parent
        var statuses = new Dictionary<int, int>();
        await Parallel.ForEachAsync(hosts, new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (h, ct) =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/");
            req.Headers.Host = h;
            using var resp = await http.SendAsync(req, ct);
            await resp.Content.ReadAsByteArrayAsync(ct);
            lock (statuses) statuses[(int)resp.StatusCode] = statuses.GetValueOrDefault((int)resp.StatusCode) + 1;
        });
        // Host headers far beyond any DNS name (Go accepts them; one of 1,100 characters used to stop all saving).
        var longStatuses = new JsonArray();
        foreach (var length in LongHostLengths)
            longStatuses.Add(await RawRequestAsync(port, new string('a', length - ".example".Length) + ".example"));
        report["floodStatuses"] = new JsonObject(statuses.Select(kv => KeyValuePair.Create(kv.Key.ToString(), (JsonNode?)kv.Value)));
        report["longHostStatusLines"] = longStatuses;
        report["sendMs"] = sw.ElapsedMilliseconds;

        var expectedOther = RandomHosts + IpHosts + TooDeep + WildcardParent + LongHostLengths.Length;
        var sentBeforeLate = hosts.Count + LongHostLengths.Length;
        await IngestUntilAsync(ingestion, sentBeforeLate);

        // ---- a host added to the configuration later: its own bucket from the moment the configuration is applied
        env.Store.Col<SiteHost>().Insert(new SiteHost { Kind = HostKind.Response, Domains = [Late], Tls = TlsMode.None, ResponseStatus = 200, ResponseBody = "late" });
        feed.RaiseApplied("host added");
        for (var i = 0; i < LateRequests; i++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/");
            req.Headers.Host = Late;
            using var resp = await http.SendAsync(req);
        }
        var total = sentBeforeLate + LateRequests;
        await IngestUntilAsync(ingestion, total);
        ingestion.Flush();

        var day = await telemetry.GetTrafficAsync(new TrafficQuery { Range = TrafficRange.Day });
        var hour = await telemetry.GetTrafficAsync(new TrafficQuery { Range = TrafficRange.Hour });
        var appsFilter = await telemetry.GetTrafficAsync(new TrafficQuery { Range = TrafficRange.Day, Host = "Anything.Apps.Flood.Test" });
        var longFilter = await telemetry.GetTrafficAsync(new TrafficQuery { Range = TrafficRange.Day, Host = new string('b', 5000) });
        var storedHosts = Enum.GetValues<BucketScale>().ToDictionary(s => s,
            s => env.Traffic.Col(s).FindAll().Select(b => b.Host).Distinct().OrderBy(h => h, StringComparer.Ordinal).ToList());
        var linesIngested = ingestion.LinesIngested;
        var malformed = ingestion.MalformedLines;
        var failures = ingestion.FlushFailures;
        var loggedHostLengths = LoggedHostLengths(env);

        // ---- restart: a new instance continues from the stored cursors; nothing is counted twice
        await sp.DisposeAsync();
        var sp2 = env.Services(o => o.FlushInterval = TimeSpan.FromMilliseconds(200));
        var ingestion2 = sp2.GetRequiredService<TrafficIngestion>();
        for (var i = 0; i < 3; i++) { ingestion2.PollOnce(); await Task.Delay(250); }
        ingestion2.Flush();
        var afterRestart = await sp2.GetRequiredService<IServerTelemetry>().GetTrafficAsync(new TrafficQuery { Range = TrafficRange.Day });
        await sp2.DisposeAsync();

        env.Traffic.Dispose(); // checkpoint the log into the data file before measuring
        var dbFiles = Directory.GetFiles(Path.GetDirectoryName(TrafficStore.DbFile(env.Paths))!, "telemetry*.db");
        var dbBytes = dbFiles.Sum(f => new FileInfo(f).Length);
        var managerCollections = env.Store.Database.GetCollectionNames().OrderBy(n => n, StringComparer.Ordinal).ToList();

        report["requestsSent"] = total;
        report["expected"] = new JsonObject
        {
            [Shop] = ShopRequests, [Apps] = AppRequests, [Late] = LateRequests, [HostMatcher.Other] = expectedOther,
        };
        report["actual"] = new JsonObject
        {
            ["linesIngested"] = linesIngested,
            ["malformedLines"] = malformed,
            ["flushFailures"] = failures,
            ["dayTotals"] = day.Totals.Requests,
            ["hourTotals"] = hour.Totals.Requests,
            ["topHosts"] = new JsonArray(day.TopHosts.Select(h => (JsonNode)new JsonObject { ["host"] = h.Host, ["requests"] = h.Requests }).ToArray()),
            ["hourTopHosts"] = new JsonArray(hour.TopHosts.Select(h => (JsonNode)new JsonObject { ["host"] = h.Host, ["requests"] = h.Requests }).ToArray()),
            ["filterAnythingAppsFloodTest"] = new JsonObject { ["host"] = appsFilter.Host, ["requests"] = appsFilter.Totals.Requests },
            ["filter5000Chars"] = new JsonObject { ["requests"] = longFilter.Totals.Requests, ["hostLength"] = longFilter.Host?.Length },
            ["storedHostKeys"] = new JsonObject(storedHosts.Select(kv => KeyValuePair.Create(kv.Key.ToString(), (JsonNode?)new JsonArray(kv.Value.Select(h => (JsonNode)h).ToArray())))),
            ["longestLoggedHost"] = loggedHostLengths.Max(),
            ["dayTotalsAfterRestart"] = afterRestart.Totals.Requests,
            ["telemetryDbBytes"] = dbBytes,
            ["telemetryDbFiles"] = new JsonArray(dbFiles.Select(f => (JsonNode)Path.GetFileName(f)).ToArray()),
            ["managerDbCollections"] = new JsonArray(managerCollections.Select(n => (JsonNode)n).ToArray()),
        };
        report["ms"] = sw.ElapsedMilliseconds;
        E2EArtifacts.Write("host-flood.json", report);

        // Caddy logged the requests exactly as sent, including the long Host headers.
        Assert.Contains(LongHostLengths[^1], loggedHostLengths);
        Assert.Equal(total, linesIngested);
        Assert.Equal(0, malformed);
        Assert.Equal(0, failures);
        // One bucket per configured name, the wildcard as one, everything else in "(other)" — at every scale.
        string[] keys = [HostMatcher.Other, TrafficBucketDoc.Total, Apps, Late, Shop];
        foreach (var (_, stored) in storedHosts) Assert.Equal(keys.OrderBy(k => k, StringComparer.Ordinal), stored);
        foreach (var r in new[] { day, hour })
        {
            Assert.Equal(total, r.Totals.Requests);
            var rows = r.TopHosts.ToDictionary(h => h.Host, h => h.Requests);
            Assert.Equal(4, rows.Count);
            Assert.Equal(ShopRequests, rows[Shop]);
            Assert.Equal(AppRequests, rows[Apps]);
            Assert.Equal(LateRequests, rows[Late]);
            Assert.Equal(expectedOther, rows[HostMatcher.Other]);
        }
        Assert.Equal(Apps, appsFilter.Host);
        Assert.Equal(AppRequests, appsFilter.Totals.Requests);
        Assert.Equal(0, longFilter.Totals.Requests);
        Assert.Equal(total, afterRestart.Totals.Requests);
        // Bounded on disk: the old per-Host buckets cost about 2.4 KB of database per distinct Host (≈ 8 MB here).
        Assert.True(dbBytes < 1_500_000, $"telemetry database is {dbBytes:N0} bytes");
        Assert.DoesNotContain(managerCollections, n => n.StartsWith("traffic_", StringComparison.Ordinal));
    }

    private static async Task IngestUntilAsync(TrafficIngestion ingestion, long lines)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            ingestion.PollOnce();
            if (ingestion.LinesIngested >= lines) return;
            await Task.Delay(200);
        }
        throw new TimeoutException($"ingested {ingestion.LinesIngested} of {lines} lines");
    }

    /// <summary>One HTTP/1.1 request over a raw socket (HttpClient validates Host); returns the status line.</summary>
    private static async Task<string> RawRequestAsync(int port, string host)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n"));
        using var reader = new StreamReader(stream, Encoding.ASCII);
        var status = await reader.ReadLineAsync() ?? "";
        await reader.ReadToEndAsync();
        return status;
    }

    /// <summary>Lengths of request.host in every stats log line (the raw evidence of what Caddy logged).</summary>
    private static List<int> LoggedHostLengths(TempEnv env)
    {
        var result = new List<int>();
        foreach (var file in Directory.GetFiles(env.Paths.StatsLogDir, "requests*.log"))
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            while (reader.ReadLine() is { } line)
                if (JsonNode.Parse(line)?["request"]?["host"]?.GetValue<string>() is { } h) result.Add(h.Length);
        }
        return result;
    }
}
