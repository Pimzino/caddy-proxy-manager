using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Infrastructure;
using CaddyManager.Core.Models;
using CaddyManager.Telemetry.Traffic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CaddyManager.Telemetry.Tests;

/// <summary>
/// End to end: a real Caddy v2.11.4 runs the configuration the Config module generates for three proxy hosts and writes
/// the stats access log through the generated cpm_stats sink (roll_size_mb lowered to 1 so it rotates several times), a
/// known mix of requests goes through it, and the ingester — stopped, restarted and "crashed" while traffic keeps
/// flowing — must end up with exactly the numbers the client sent. Artifact: traffic-ingest.json.
/// </summary>
public sealed class TrafficIngestE2ETests
{
    private const int Requests = 12_000;
    private const int DistinctClients = 500;
    private const int UploadResponseBytes = 42;

    private sealed record Planned(string HostHeader, string HostKey, HttpMethod Method, string Path, int BodyBytes, string Client, int Status, int ResponseBytes);

    /// <summary>Deterministic request mix over three proxy hosts: the upstream answers 404/500/uploads/pages by path.</summary>
    private static Planned Plan(int i, int port)
    {
        var client = $"10.0.{i % DistinctClients / 200}.{i % DistinctClients % 200 + 1}";
        var a = $"A.Test:{port}";      // mixed case + port → "a.test"
        const string b = "b.test";      // no port
        var v6 = $"[::1]:{port}";       // IPv6 literal with port → "[::1]"
        return (i % 20) switch
        {
            0 => new Planned(a, "a.test", HttpMethod.Get, "/missing", 0, client, 404, "not found".Length),
            1 => new Planned(b, "b.test", HttpMethod.Get, "/fail", 0, client, 500, "boom".Length),
            >= 2 and <= 6 => new Planned(b, "b.test", HttpMethod.Post, "/upload", i % 13 * 37 + 1, client, 200, UploadResponseBytes),
            7 => new Planned(v6, "[::1]", HttpMethod.Get, "/", 0, client, 200, "hello-world".Length),
            _ => i % 2 == 1
                ? new Planned(a, "a.test", HttpMethod.Get, "/page", 0, client, 200, "hello-world".Length)
                : new Planned(b, "b.test", HttpMethod.Get, "/page", 0, client, 200, "hello-world".Length),
        };
    }

    [CaddyFact]
    public async Task ExactCountsAcrossRotationsRestartAndCrash_AndHyperLogLogAccuracy()
    {
        var report = E2EArtifacts.Report(nameof(ExactCountsAcrossRotationsRestartAndCrash_AndHyperLogLogAccuracy));
        using (var env = new TempEnv())
        {
            await using var upstream = await Upstream.StartAsync();
            var port = Net.FreeTcpPort();
            // The requests come from 127.0.0.1 with X-Forwarded-For: trusting loopback (a product setting) makes Caddy log
            // that address as request.client_ip. The generator adds trusted_proxies (static, strict); client_ip_headers is
            // left to Caddy's default, X-Forwarded-For
            // (https://caddyserver.com/docs/json/apps/http/servers/client_ip_headers/, v2.11.4).
            GeneratedConfig.Settings(env, port, s => s.TrustedProxies = ["127.0.0.1/32"]);
            foreach (var domain in new[] { "a.test", "b.test", "::1" }) GeneratedConfig.AddProxyHost(env, upstream.Port, domain);
            var generated = GeneratedConfig.Generate(env);
            Assert.Empty(generated.Warnings);
            var sink = GeneratedConfig.StatsSink(generated.Config);
            // Only test-specific change: the product rolls the stats log at 10 MB; 1 MB makes the 12,000 requests below
            // rotate it several times, including while no ingester runs and across the "crash".
            Assert.Equal(10, sink["writer"]!["roll_size_mb"]!.GetValue<int>());
            sink["writer"]!["roll_size_mb"] = 1;
            report["sinkSource"] = "generator";
            report["configTweaks"] = new JsonArray("logging.logs.cpm_stats.writer.roll_size_mb: 10 -> 1 (force rotations)");
            report["statsSink"] = sink.DeepClone();
            using var caddy = new CaddyProcess(env.Paths, generated.Config.ToJsonString());
            await caddy.WaitForPortAsync(port, TimeSpan.FromSeconds(20));

            var plan = Enumerable.Range(0, Requests).Select(i => Plan(i, port)).ToList();
            using var http = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = 16, UseProxy = false })
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}"),
                Timeout = TimeSpan.FromSeconds(30),
            };
            var phases = new JsonArray();
            var sw = Stopwatch.StartNew();

            // Phase 1: the hosted ingester runs normally, then shuts down cleanly (final read + flush).
            var sp1 = env.Services(o =>
            {
                o.IngestInterval = TimeSpan.FromMilliseconds(200);
                o.FlushInterval = TimeSpan.FromSeconds(1);
            });
            var hosted = sp1.GetRequiredService<StatsIngesterService>();
            await hosted.StartAsync(CancellationToken.None);
            await SendAsync(http, plan, 0, 4000);
            await Task.Delay(1500);
            await hosted.StopAsync(CancellationToken.None);
            var ingA = sp1.GetRequiredService<TrafficIngestion>();
            phases.Add(Phase("A: hosted ingester, clean stop", ingA, env, sw));
            await sp1.DisposeAsync();

            // Phase 2: no ingester while traffic flows — Caddy rotates the file the saved cursor points to.
            await SendAsync(http, plan, 4000, 8000);
            await Task.Delay(500);
            phases.Add(new JsonObject { ["phase"] = "B: no ingester", ["files"] = Files(env), ["ms"] = sw.ElapsedMilliseconds });

            // Phase 3: new instance resumes from the stored cursors (file now a backup) and keeps reading while traffic
            // flows. Counters and their cursor are saved every 100 ms, the sketches and top clients (blobs) never (their
            // interval is an hour), so the blob cursor stays where phase A left it, rotations behind the counter cursor.
            // Then the instance "crashes": whatever was not saved is lost with the process.
            var ingB = env.NewIngestion(flushInterval: TimeSpan.FromMilliseconds(100), o => o.BlobFlushInterval = TimeSpan.FromHours(1));
            var blobCursorBefore = env.Traffic.LoadBlobCursor();
            var traffic = SendAsync(http, plan, 8000, 11_000);
            var crashAt = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < crashAt && !traffic.IsCompleted)
            {
                ingB.PollOnce();
                await Task.Delay(100);
            }
            ingB.PollOnce();
            phases.Add(Phase("C: resumed, counters saved often, sketches never, then crashed", ingB, env, sw));
            ingB.Dispose();
            var counterCursorAtCrash = env.Traffic.LoadCursor()!;
            var blobCursorAtCrash = env.Traffic.LoadBlobCursor()!;
            report["cursorsAtCrash"] = new JsonObject
            {
                ["counters"] = $"{counterCursorAtCrash.FileId}@{counterCursorAtCrash.Offset}",
                ["blobs"] = $"{blobCursorAtCrash.FileId}@{blobCursorAtCrash.Offset}",
                ["linesInCounters"] = counterCursorAtCrash.LinesIngested,
            };
            // The replay after the crash must span rotated files: the blob cursor did not move, the counters did.
            Assert.Equal(blobCursorBefore!.FileId, blobCursorAtCrash.FileId);
            Assert.Equal(blobCursorBefore.Offset, blobCursorAtCrash.Offset);
            Assert.NotEqual(blobCursorAtCrash.FileId, counterCursorAtCrash.FileId);

            // Phase 4: a third instance resumes from the stored cursors — replaying the sketches from the blob cursor up to
            // the counter cursor, across the rotated files — while the last requests arrive, and reads to the end.
            await traffic;
            var sp2 = env.Services(o => o.FlushInterval = TimeSpan.FromSeconds(1));
            var ingC = sp2.GetRequiredService<TrafficIngestion>();
            var telemetry = sp2.GetRequiredService<IServerTelemetry>();
            ingC.PollOnce();
            var replayedWithoutNewLines = ingC.LinesIngested == counterCursorAtCrash.LinesIngested;
            await SendAsync(http, plan, 11_000, Requests);
            TrafficReport day = null!;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                ingC.PollOnce();
                day = await telemetry.GetTrafficAsync(new TrafficQuery { Range = TrafficRange.Day });
                if (day.Totals.Requests >= Requests) break;
                await Task.Delay(250);
            }
            // Keep polling a little longer: nothing may be counted twice later.
            for (var i = 0; i < 5; i++) { ingC.PollOnce(); await Task.Delay(200); }
            day = await telemetry.GetTrafficAsync(new TrafficQuery { Range = TrafficRange.Day });
            var hour = await telemetry.GetTrafficAsync(new TrafficQuery { Range = TrafficRange.Hour });
            var month = await telemetry.GetTrafficAsync(new TrafficQuery { Range = TrafficRange.Month });
            var hostA = await telemetry.GetTrafficAsync(new TrafficQuery { Range = TrafficRange.Hour, Host = "A.TEST" });
            phases.Add(Phase("D: resumed after crash, read to end", ingC, env, sw));

            var backups = Directory.GetFiles(env.Paths.StatsLogDir, "requests-*.log");
            var expected = Expected(plan);
            report["requests"] = Requests;
            report["distinctClients"] = DistinctClients;
            report["phases"] = phases;
            report["rotations"] = backups.Length;
            report["expected"] = expected.Json;
            report["actual"] = new JsonObject
            {
                ["day"] = Totals(day.Totals),
                ["hour"] = Totals(hour.Totals),
                ["month"] = Totals(month.Totals),
                ["hosts"] = new JsonArray(day.TopHosts.Select(h => (JsonNode)new JsonObject
                {
                    ["host"] = h.Host, ["requests"] = h.Requests, ["bytesIn"] = h.BytesIn, ["bytesOut"] = h.BytesOut,
                    ["uniqueClients"] = h.UniqueClients, ["4xx"] = h.Status4xx, ["5xx"] = h.Status5xx,
                }).ToArray()),
                ["statusCodes"] = new JsonArray(day.StatusCodes.Select(s => (JsonNode)new JsonObject { ["code"] = s.Code, ["count"] = s.Count }).ToArray()),
                ["hostFilterHour"] = new JsonObject { ["host"] = hostA.Host, ["requests"] = hostA.Totals.Requests, ["uniqueClients"] = hostA.Totals.UniqueClients },
                ["topClient"] = day.TopClients.FirstOrDefault() is { } tc ? new JsonObject { ["ip"] = tc.Ip, ["requests"] = tc.Requests } : null,
                ["linesIngested"] = ingC.LinesIngested,
                ["malformedLines"] = ingC.MalformedLines,
                ["filesMissed"] = ingC.FilesMissed,
                ["firstPollAfterCrashAddedNoLines"] = replayedWithoutNewLines,
                ["seriesPoints"] = new JsonObject { ["hour"] = hour.Series.Count, ["day"] = day.Series.Count, ["month"] = month.Series.Count },
            };
            await sp2.DisposeAsync();
            E2EArtifacts.Write("traffic-ingest.json", report);

            Assert.True(backups.Length >= 2, $"expected ≥ 2 rotations, saw {backups.Length}");
            Assert.Equal(0, ingC.MalformedLines);
            Assert.Equal(0, ingC.FilesMissed);
            Assert.Equal(Requests, ingC.LinesIngested);
            foreach (var r in new[] { day, hour, month })
            {
                Assert.True(r.Enabled);
                Assert.Equal(Requests, r.Totals.Requests);
                Assert.Equal(expected.BytesIn, r.Totals.BytesIn);
                Assert.Equal(expected.BytesOut, r.Totals.BytesOut);
                Assert.Equal(expected.S2xx, r.Totals.Status2xx);
                Assert.Equal(expected.S4xx, r.Totals.Status4xx);
                Assert.Equal(expected.S5xx, r.Totals.Status5xx);
                Assert.Equal(0, r.Totals.Status3xx);
                Assert.Equal(0, r.Totals.StatusOther);
                Assert.Equal(DistinctClients, r.Totals.UniqueClients);
                Assert.Equal(r.Series.Sum(p => p.Requests), r.Totals.Requests);
            }
            Assert.Equal(60, hour.Series.Count);
            Assert.Equal(24, day.Series.Count);
            Assert.Equal(30, month.Series.Count);
            foreach (var (host, e) in expected.Hosts)
            {
                var row = Assert.Single(day.TopHosts, h => h.Host == host);
                Assert.Equal(e.Requests, row.Requests);
                Assert.Equal(e.BytesIn, row.BytesIn);
                Assert.Equal(e.BytesOut, row.BytesOut);
                Assert.Equal(e.Clients, row.UniqueClients);
                Assert.Equal(e.S4xx, row.Status4xx);
                Assert.Equal(e.S5xx, row.Status5xx);
            }
            Assert.Equal(expected.Hosts.Count, day.TopHosts.Count);
            Assert.Equal("a.test", hostA.Host);
            Assert.Equal(expected.Hosts["a.test"].Requests, hostA.Totals.Requests);
            Assert.Equal(expected.Codes.OrderBy(k => k.Key).Select(k => (k.Key, k.Value)),
                day.StatusCodes.OrderBy(s => s.Code).Select(s => (s.Code, s.Count)));
            // Each client sent Requests / DistinctClients requests: the heavy-hitter summary must report exactly that for
            // its top entry (under 200 distinct clients per bucket... there are 500, so counts are upper bounds ≥ 24).
            Assert.True(day.TopClients.Count > 0 && day.TopClients[0].Requests >= Requests / DistinctClients);
        }

        report["largeCardinality"] = await LargeCardinalityAsync();
        E2EArtifacts.Write("traffic-ingest.json", report);
    }

    /// <summary>
    /// 100,000 log lines from 50,000 synthetic client IPs, spread over five minutes so every minute bucket holds a
    /// HyperLogLog sketch (20,000 lines each) and the day total is a merge of overlapping sketches. Must be within ±3 %.
    /// </summary>
    private static async Task<JsonObject> LargeCardinalityAsync()
    {
        const int clients = 50_000;
        const int lines = 100_000;
        using var env = new TempEnv();
        // The installation key decides which registers the clients land in: a fixed key makes the estimate (and this
        // artifact) the same on every run. A random key gives a different estimate each time, ±1.6 % typical.
        ClientHasher.SaveKey(env.Paths, new SecretProtector(env.Paths), TestKeys.Fixed);
        var baseTs = Math.Floor(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60.0) * 60 - 600; // 10 minutes ago, minute aligned
        await using (var w = new StreamWriter(env.Paths.StatsLogFile, false, new UTF8Encoding(false)))
        {
            for (var j = 0; j < lines; j++)
            {
                var c = j % clients;
                var ts = baseTs + j / 20_000 * 60 + j % 20_000 * 0.001;
                await w.WriteAsync(
                    $"{{\"level\":\"info\",\"ts\":{ts.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)},\"logger\":\"http.log.access\",\"msg\":\"handled request\"," +
                    $"\"request\":{{\"remote_ip\":\"127.0.0.1\",\"remote_port\":\"50000\",\"client_ip\":\"172.{16 + c / 65536}.{c / 256 % 256}.{c % 256}\"," +
                    $"\"proto\":\"HTTP/1.1\",\"method\":\"GET\",\"host\":\"big.test\",\"uri\":\"/\"}},\"bytes_read\":0,\"user_id\":\"\"," +
                    $"\"duration\":0.0001,\"size\":2,\"status\":200}}\n");
            }
        }
        var sw = Stopwatch.StartNew();
        using var ingestion = env.NewIngestion();
        ingestion.PollOnce();
        ingestion.Flush();
        var ingestMs = sw.ElapsedMilliseconds;
        var sp = env.Services();
        var telemetry = sp.GetRequiredService<IServerTelemetry>();
        var day = await telemetry.GetTrafficAsync(new TrafficQuery { Range = TrafficRange.Day });
        var hour = await telemetry.GetTrafficAsync(new TrafficQuery { Range = TrafficRange.Hour });
        var minutes = hour.Series.Where(p => p.Requests > 0).Select(p => (JsonNode)new JsonObject
        {
            ["at"] = p.At.ToString("O"), ["requests"] = p.Requests, ["uniqueEstimate"] = p.UniqueClients,
        }).ToArray();
        var error = (day.Totals.UniqueClients - clients) / (double)clients;
        var result = new JsonObject
        {
            ["lines"] = lines,
            ["trueDistinct"] = clients,
            ["estimateDay"] = day.Totals.UniqueClients,
            ["estimateHour"] = hour.Totals.UniqueClients,
            ["relativeError"] = Math.Round(error, 5),
            ["minuteBuckets"] = new JsonArray(minutes),
            ["ingestMs"] = ingestMs,
        };
        // Statistics turned off in Caddy settings: the report says so and carries no data.
        var settings = env.Store.GetSettings<CaddySettings>();
        settings.TrafficStatsEnabled = false;
        env.Store.SaveSettings(settings);
        var off = await telemetry.GetTrafficAsync(new TrafficQuery { Range = TrafficRange.Day });
        result["disabledReport"] = new JsonObject { ["enabled"] = off.Enabled, ["requests"] = off.Totals.Requests, ["points"] = off.Series.Count };
        await sp.DisposeAsync();
        // Also on its own, before the assertions, so a failing run leaves the numbers behind.
        E2EArtifacts.Write("traffic-ingest-cardinality.json", result);

        Assert.False(off.Enabled);
        Assert.Equal(0, off.Totals.Requests);
        Assert.Empty(off.Series);
        Assert.Equal(lines, day.Totals.Requests);
        Assert.Equal(lines, hour.Totals.Requests);
        Assert.InRange(Math.Abs(error), 0, 0.03);
        Assert.Equal(day.Totals.UniqueClients, hour.Totals.UniqueClients);
        Assert.Equal(5, minutes.Length);
        // Single sketches: ±5 % (≈ 3 standard errors of p = 12) — the ±3 % requirement applies to the merged total above.
        foreach (var m in hour.Series.Where(p => p.Requests > 0))
            Assert.InRange(m.UniqueClients, 20_000 * 0.95, 20_000 * 1.05);
        return result;
    }

    private static async Task SendAsync(HttpClient http, List<Planned> plan, int from, int to)
    {
        await Parallel.ForEachAsync(Enumerable.Range(from, to - from), new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (i, ct) =>
        {
            var p = plan[i];
            using var req = new HttpRequestMessage(p.Method, p.Path);
            req.Headers.Host = p.HostHeader;
            req.Headers.TryAddWithoutValidation("X-Forwarded-For", p.Client);
            if (p.BodyBytes > 0) req.Content = new ByteArrayContent(new byte[p.BodyBytes]);
            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsByteArrayAsync(ct);
            if ((int)resp.StatusCode != p.Status || body.Length != p.ResponseBytes)
                throw new InvalidOperationException($"request {i} {p.Method} {p.HostHeader}{p.Path}: {(int)resp.StatusCode} {body.Length} bytes");
        });
    }

    private static JsonObject Phase(string name, TrafficIngestion ing, TempEnv env, Stopwatch sw) => new()
    {
        ["phase"] = name,
        ["linesIngestedPersistedOrInMemory"] = ing.LinesIngested,
        ["filesCompletedByThisInstance"] = ing.FilesCompleted,
        ["files"] = Files(env),
        ["ms"] = sw.ElapsedMilliseconds,
    };

    private static JsonArray Files(TempEnv env) => new(Directory.GetFiles(env.Paths.StatsLogDir)
        .OrderBy(f => f, StringComparer.Ordinal)
        .Select(f => (JsonNode)$"{Path.GetFileName(f)} ({new FileInfo(f).Length} bytes)").ToArray());

    private static JsonObject Totals(TrafficTotals t) => new()
    {
        ["requests"] = t.Requests, ["bytesIn"] = t.BytesIn, ["bytesOut"] = t.BytesOut, ["uniqueClients"] = t.UniqueClients,
        ["2xx"] = t.Status2xx, ["3xx"] = t.Status3xx, ["4xx"] = t.Status4xx, ["5xx"] = t.Status5xx, ["other"] = t.StatusOther,
        ["avgDurationMs"] = t.AvgDurationMs,
    };

    private sealed class HostExpectation
    {
        public long Requests, BytesIn, BytesOut, S4xx, S5xx;
        public HashSet<string> ClientSet = new();
        public long Clients => ClientSet.Count;
    }

    private sealed record Expectation(long BytesIn, long BytesOut, long S2xx, long S4xx, long S5xx,
        Dictionary<string, HostExpectation> Hosts, Dictionary<int, long> Codes, JsonObject Json);

    private static Expectation Expected(List<Planned> plan)
    {
        var hosts = new Dictionary<string, HostExpectation>();
        var codes = new Dictionary<int, long>();
        long bytesIn = 0, bytesOut = 0, s2 = 0, s4 = 0, s5 = 0;
        foreach (var p in plan)
        {
            bytesIn += p.BodyBytes;
            bytesOut += p.ResponseBytes;
            if (p.Status / 100 == 2) s2++;
            if (p.Status / 100 == 4) s4++;
            if (p.Status / 100 == 5) s5++;
            codes[p.Status] = codes.GetValueOrDefault(p.Status) + 1;
            if (!hosts.TryGetValue(p.HostKey, out var h)) hosts[p.HostKey] = h = new HostExpectation();
            h.Requests++;
            h.BytesIn += p.BodyBytes;
            h.BytesOut += p.ResponseBytes;
            if (p.Status / 100 == 4) h.S4xx++;
            if (p.Status / 100 == 5) h.S5xx++;
            h.ClientSet.Add(p.Client);
        }
        var json = new JsonObject
        {
            ["requests"] = plan.Count, ["bytesIn"] = bytesIn, ["bytesOut"] = bytesOut, ["2xx"] = s2, ["4xx"] = s4, ["5xx"] = s5,
            ["uniqueClients"] = plan.Select(p => p.Client).Distinct().Count(),
            ["hosts"] = new JsonObject(hosts.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)new JsonObject
            {
                ["requests"] = kv.Value.Requests, ["bytesIn"] = kv.Value.BytesIn, ["bytesOut"] = kv.Value.BytesOut,
                ["uniqueClients"] = kv.Value.Clients, ["4xx"] = kv.Value.S4xx, ["5xx"] = kv.Value.S5xx,
            }))),
        };
        return new Expectation(bytesIn, bytesOut, s2, s4, s5, hosts, codes, json);
    }

    /// <summary>
    /// Upstream of the three proxy hosts: /missing → 404 "not found", /fail → 500 "boom", /upload reads the whole request
    /// body and answers exactly 42 bytes, anything else → 200 "hello-world".
    /// </summary>
    private sealed class Upstream : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public int Port { get; }

        private Upstream(WebApplication app, int port)
        {
            _app = app;
            Port = port;
        }

        public static async Task<Upstream> StartAsync()
        {
            var port = Net.FreeTcpPort();
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
            var app = builder.Build();
            app.Run(async ctx =>
            {
                using var ms = new MemoryStream();
                await ctx.Request.Body.CopyToAsync(ms);
                var (status, body) = ctx.Request.Path.Value switch
                {
                    "/missing" => (404, "not found"),
                    "/fail" => (500, "boom"),
                    "/upload" => (200, new string('y', UploadResponseBytes)),
                    _ => (200, "hello-world"),
                };
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "text/plain";
                ctx.Response.ContentLength = body.Length;
                await ctx.Response.Body.WriteAsync(Encoding.ASCII.GetBytes(body));
            });
            await app.StartAsync();
            return new Upstream(app, port);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
