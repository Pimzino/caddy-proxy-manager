using System.Diagnostics;
using System.Text.Json.Nodes;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Telemetry.Traffic;
using Microsoft.Extensions.DependencyInjection;

namespace CaddyManager.Telemetry.Tests;

/// <summary>
/// End to end (TEL-4): steady traffic from many clients through a real Caddy v2.11.4 into the hosted ingester while
/// viewers poll the traffic report several times a second. The statistics must go to their own database (manager.db is
/// not written at all), counters must be saved every flush interval but the large sketches / top-clients documents only
/// every blob interval, report polling must not add saves, and reports must still show the exact unique clients and top
/// clients although most of that state is not saved yet. Artifact: traffic-write-load.json.
/// </summary>
public sealed class TrafficWriteLoadE2ETests
{
    private const int Clients = 400;
    private static readonly TimeSpan Flush = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan BlobFlush = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(7);

    [CaddyFact]
    public async Task CountersEveryFlush_BlobsOncePerInterval_ReportsExactAndFree()
    {
        var report = E2EArtifacts.Report(nameof(CountersEveryFlush_BlobsOncePerInterval_ReportsExactAndFree));
        using var env = new TempEnv();
        var port = Net.FreeTcpPort();
        GeneratedConfig.Settings(env, port, s => s.TrustedProxies = ["127.0.0.1/32"]);
        foreach (var d in new[] { "one.load.test", "two.load.test", "three.load.test" })
            GeneratedConfig.AddResponseHost(env, 200, "ok", d);
        var generated = GeneratedConfig.Generate(env);
        Assert.Empty(generated.Warnings);
        using var caddy = new CaddyProcess(env.Paths, generated.Config.ToJsonString());
        await caddy.WaitForPortAsync(port, TimeSpan.FromSeconds(20));

        await using var sp = env.Services(o =>
        {
            o.IngestInterval = TimeSpan.FromMilliseconds(100);
            o.FlushInterval = Flush;
            o.BlobFlushInterval = BlobFlush;
        });
        var hosted = sp.GetRequiredService<StatsIngesterService>();
        var ingestion = sp.GetRequiredService<TrafficIngestion>();
        var telemetry = sp.GetRequiredService<IServerTelemetry>();
        var store = env.Traffic;
        // manager.db and its LiteDB log file (manager-log.db), if any.
        var dbDir = Path.GetDirectoryName(env.Paths.DbFile)!;
        (long Length, DateTime Write) Manager() => Directory.GetFiles(dbDir, "manager*.db")
            .Aggregate((Length: 0L, Write: DateTime.MinValue), (a, f) =>
                (a.Length + new FileInfo(f).Length, File.GetLastWriteTimeUtc(f) > a.Write ? File.GetLastWriteTimeUtc(f) : a.Write));
        var managerBefore = Manager();
        await hosted.StartAsync(CancellationToken.None);

        using var http = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = 8, UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        string[] hosts = ["one.load.test", "two.load.test", "three.load.test"];
        var sent = 0;
        var reportCalls = 0;
        var sw = Stopwatch.StartNew();
        using var stop = new CancellationTokenSource(Duration);
        var viewers = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await telemetry.GetTrafficAsync(new TrafficQuery { Range = TrafficRange.Hour });
                Interlocked.Increment(ref reportCalls);
                await Task.Delay(50);
            }
        });
        var savesAtStart = store.Saves;
        await Parallel.ForEachAsync(Enumerable.Range(0, 8), async (worker, _) =>
        {
            for (var i = worker; !stop.IsCancellationRequested; i += 8)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "/");
                req.Headers.Host = hosts[i % hosts.Length];
                req.Headers.TryAddWithoutValidation("X-Forwarded-For", $"198.51.{i % Clients / 250}.{i % Clients % 250 + 1}");
                using var resp = await http.SendAsync(req);
                Interlocked.Increment(ref sent);
                await Task.Delay(2);
            }
        });
        await viewers;
        var elapsed = sw.Elapsed;

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (ingestion.LinesIngested < sent && DateTime.UtcNow < deadline) await Task.Delay(100);
        await hosted.StopAsync(CancellationToken.None); // final save of everything
        var blobSavesUnderLoad = store.BlobSaves;
        var savesUnderLoad = store.Saves - savesAtStart;

        // Read, but not everything saved: right after a blob save, new clients arrive; counters are saved (and the
        // counter cursor moves) at the next flush, the sketches not for BlobFlush. The report overlays the unsaved ones.
        const int late = 50;
        for (var i = 0; i < late; i++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/");
            req.Headers.Host = hosts[0];
            req.Headers.TryAddWithoutValidation("X-Forwarded-For", $"203.0.113.{i + 1}");
            using var resp = await http.SendAsync(req);
        }
        var lateStart = DateTime.UtcNow;
        while (ingestion.LinesIngested < sent + late && DateTime.UtcNow < deadline) { ingestion.PollOnce(); await Task.Delay(50); }
        await Task.Delay(Flush);
        ingestion.PollOnce();
        var counterCursor = store.LoadCursor()!;
        var blobCursor = store.LoadBlobCursor()!;
        var live = await telemetry.GetTrafficAsync(new TrafficQuery { Range = TrafficRange.Day });
        var lateMs = (DateTime.UtcNow - lateStart).TotalMilliseconds;
        ingestion.Flush();
        var saved = await telemetry.GetTrafficAsync(new TrafficQuery { Range = TrafficRange.Day });
        var distinctSent = Math.Min(sent, Clients);

        var managerAfter = Manager();
        var saves = savesUnderLoad;
        var blobBytesPerSave = store.BlobSaves == 0 ? 0 : store.BlobBytesWritten / store.BlobSaves;
        // What rewriting the complete documents at every save would have written (the previous behaviour).
        var fullRewriteEstimate = store.Saves * blobBytesPerSave + store.CounterBytesWritten;
        var written = store.CounterBytesWritten + store.BlobBytesWritten;
        report["elapsedMs"] = (long)elapsed.TotalMilliseconds;
        report["requests"] = sent;
        report["distinctClients"] = distinctSent;
        report["reportCalls"] = reportCalls;
        report["saves"] = store.Saves;
        report["blobSaves"] = store.BlobSaves;
        report["counterDocsWritten"] = store.CounterDocsWritten;
        report["counterBytesWritten"] = store.CounterBytesWritten;
        report["blobDocsWritten"] = store.BlobDocsWritten;
        report["blobBytesWritten"] = store.BlobBytesWritten;
        report["fullRewriteEstimateBytes"] = fullRewriteEstimate;
        report["reduction"] = written == 0 ? 0 : Math.Round(fullRewriteEstimate / (double)written, 1);
        report["blobSavesUnderLoad"] = blobSavesUnderLoad;
        report["savesUnderLoad"] = savesUnderLoad;
        report["lateClients"] = new JsonObject
        {
            ["requests"] = live.Totals.Requests, ["uniqueClients"] = live.Totals.UniqueClients, ["ms"] = (long)lateMs,
            ["counterCursor"] = $"{counterCursor.FileId}@{counterCursor.Offset}", ["blobCursor"] = $"{blobCursor.FileId}@{blobCursor.Offset}",
        };
        report["afterStop"] = new JsonObject { ["requests"] = saved.Totals.Requests, ["uniqueClients"] = saved.Totals.UniqueClients };
        report["managerDb"] = new JsonObject
        {
            ["bytesBefore"] = managerBefore.Length, ["bytesAfter"] = managerAfter.Length,
            ["lastWriteUnchanged"] = managerBefore.Write == managerAfter.Write,
        };
        // Per minute bucket (server total): requests and unique clients as stored — pinpoints where clients go missing.
        report["minuteBuckets"] = new JsonArray(store.Col(BucketScale.Minute).Find(b => b.Host == TrafficBucketDoc.Total)
            .OrderBy(b => b.Start).Select(b => (JsonNode)new JsonObject
            {
                ["id"] = b.Id, ["requests"] = b.Requests,
                ["uniqueClients"] = store.Blobs(BucketScale.Minute).FindById(b.Id) is { } blob ? ClientSketch.Deserialize(blob.Clients).Count : -1,
            }).ToArray());
        E2EArtifacts.Write("traffic-write-load.json", report);

        Assert.True(sent > 1000, $"only {sent} requests");
        Assert.True(lateMs < BlobFlush.TotalMilliseconds, "the late phase must finish within one blob interval");
        // The sketches were saved only up to before the late clients, yet the report counted them exactly.
        Assert.Equal(sent + late, counterCursor.LinesIngested);
        Assert.True(blobCursor.Offset < counterCursor.Offset || blobCursor.FileId != counterCursor.FileId,
            "the blob cursor was expected to lag the counter cursor");
        Assert.Equal(sent + late, live.Totals.Requests);
        Assert.Equal(distinctSent + late, live.Totals.UniqueClients);
        Assert.Equal(sent + late, saved.Totals.Requests);
        Assert.Equal(distinctSent + late, saved.Totals.UniqueClients);
        // Under load: counters every flush interval; blobs at most once per blob interval (+ the first save and the last).
        Assert.InRange(blobSavesUnderLoad, 2, (long)(elapsed / BlobFlush) + 3);
        Assert.True(saves >= (long)(elapsed / Flush) / 2, $"only {saves} saves");
        // Report polling (every 50 ms) added no saves beyond the flush cadence.
        Assert.True(saves <= (long)(elapsed / Flush) + (long)(elapsed / BlobFlush) + 5, $"{saves} saves for {reportCalls} reports");
        // What is written every flush is small; the large part only at the blob cadence (here 3 s against 200 ms: the
        // product's 1 min against 5 s cuts it further).
        Assert.True(store.CounterBytesWritten / store.CounterDocsWritten < 1024, "counter documents should be small");
        Assert.True(fullRewriteEstimate > 3 * written, $"wrote {written:N0} bytes; full rewrites would be {fullRewriteEstimate:N0}");
        Assert.Equal(managerBefore, managerAfter);
    }
}
