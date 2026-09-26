using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Infrastructure;
using CaddyManager.Core.Models;
using CaddyManager.Telemetry.Traffic;
using LiteDB;
using Microsoft.Extensions.DependencyInjection;

namespace CaddyManager.Telemetry.Tests;

/// <summary>
/// Persistence of traffic statistics with real files, the real LiteDB telemetry database and the IServerTelemetry API;
/// log lines are written in the exact format of the generated cpm_stats sink (no Caddy needed to produce them). Faults
/// that cannot be provoked from outside (a failing database write or read) are injected at the store.
///
/// Ways it could fail (written before the code):
///  1. A save that fails keeps everything in memory and retries forever: memory grows without bound, the cursor never
///     moves, and a restart replays into the same failure (TEL-1). Or: after the failure, lines are lost or counted twice.
///  2. A failure while a line is applied (a database read) skips the rest of the 64 KB chunk and leaves the saved offset
///     behind the lines already counted, so a restart counts them again, possibly from the middle of a line (TEL-7);
///     or the line is applied to some of its six buckets twice.
///  3. A line that fails every time blocks all later lines forever.
///  4. Bucket ids depend on the current culture: a calendar change (th-TH) creates a second document for the same bucket
///     and the report throws on the duplicate (TEL-8). Or the database's string collation follows the culture it was
///     created under, so an id written under th-TH is not found again by id (seen on Windows' ICU only).
///  5. Unique-client hashes can be reversed: unkeyed (the same hash in every installation), the key stored inside the
///     statistics database, or stored unprotected (TEL-9). Or the key changes on every start, so the same client counts
///     again after a restart.
///  6. Statistics still go to the configuration database, or old collections stay there (TEL-4).
///  7. In Caddyfile mode the report claims statistics are enabled and shows zero traffic (TEL-10).
///  8. A flush before the first request (fresh install, Caddy not serving yet) fails: date arithmetic on "no request yet"
///     (DateTime.MinValue) overflows, every flush fails and the save-failure alert fires with no traffic at all.
/// </summary>
public sealed class TrafficPersistenceTests
{
    [Fact]
    public void FlushBeforeTheFirstRequestSucceeds()
    {
        using var env = new TempEnv();
        using var ingestion = env.NewIngestion(TimeSpan.Zero);
        ingestion.PollOnce(); // no stats log yet
        ingestion.Flush();
        ingestion.Flush();
        Assert.Equal(0, ingestion.FlushFailures);
        Assert.True(env.Traffic.Saves >= 1, "the cursor is saved even before the first request");
    }

    private static readonly DateTime Base = new DateTime(DateTime.UtcNow.Ticks - DateTime.UtcNow.Ticks % TimeSpan.TicksPerHour, DateTimeKind.Utc);

    private static void Configure(TempEnv env, params string[] domains)
    {
        foreach (var d in domains)
            env.Store.Col<SiteHost>().Insert(new SiteHost { Kind = HostKind.Response, Domains = [d], Tls = TlsMode.None });
    }

    /// <summary>n lines for the host from clients c0..c(clients-1), spread over the minutes after Base.</summary>
    private static IEnumerable<string> Lines(int n, string host, int clients, int firstIndex = 0, int minutes = 1) =>
        Enumerable.Range(firstIndex, n).Select(i => StatsLines.Line(
            Base.AddMinutes(i % minutes).AddMilliseconds(i), host, $"192.0.2.{i % clients + 1}"));

    private static async Task<TrafficReport> DayReport(TempEnv env, string? host = null)
    {
        await using var sp = env.Services();
        return await sp.GetRequiredService<IServerTelemetry>().GetTrafficAsync(new TrafficQuery { Range = TrafficRange.Day, Host = host });
    }

    [Fact]
    public async Task FailedSaves_DropMemoryAndRereadFromTheStoredCursor_NothingLostOrDoubled()
    {
        using var env = new TempEnv();
        Configure(env, "a.test");
        var ingestion = env.NewIngestion(TimeSpan.Zero, o => o.BlobFlushInterval = TimeSpan.Zero);
        var failSaves = 2;
        env.Traffic.BeforeSave = () => { if (failSaves-- > 0) throw new LiteException(0, "injected write failure"); };

        StatsLines.Append(env.Paths, Lines(500, "a.test", 50, minutes: 3));
        ingestion.PollOnce();                                    // read 500, save fails → memory dropped
        Assert.Equal(1, ingestion.FlushFailures);
        Assert.Equal(0, ingestion.CachedBuckets);
        Assert.Null(env.Traffic.LoadCursor());
        StatsLines.Append(env.Paths, Lines(300, "a.test", 80, firstIndex: 500, minutes: 3));
        ingestion.PollOnce();                                    // re-read all 800, second failure
        Assert.Equal(2, ingestion.FlushFailures);
        Assert.Equal(0, ingestion.CachedBuckets);
        await Task.Delay(50);
        ingestion.PollOnce();                                    // re-read all 800, saved
        Assert.Equal(0, ingestion.FlushFailures);
        ingestion.Flush();

        var day = await DayReport(env);
        Assert.Equal(800, day.Totals.Requests);
        Assert.Equal(80, day.Totals.UniqueClients);
        Assert.Equal(800, Assert.Single(day.TopHosts).Requests);
        Assert.Equal(800, ingestion.LinesIngested);
        Assert.Equal(new FileInfo(env.Paths.StatsLogFile).Length, env.Traffic.LoadCursor()!.Offset);
        ingestion.Dispose();

        // A restart reads nothing again.
        using var again = env.NewIngestion(TimeSpan.Zero);
        again.PollOnce();
        again.Flush();
        Assert.Equal(800, (await DayReport(env)).Totals.Requests);
    }

    [Fact]
    public async Task ReadFailureWhileApplyingALine_RetriesThatLine_ThenSkipsALineThatAlwaysFails()
    {
        using var env = new TempEnv();
        Configure(env, "a.test");
        using var ingestion = env.NewIngestion(TimeSpan.Zero);
        // 3,000 lines (~ 900 KB, many 64 KB chunks) over 30 minutes: a new minute bucket is read every 100 lines.
        StatsLines.Append(env.Paths, Lines(3000, "a.test", 10, minutes: 30));
        var reads = 0;
        env.Traffic.BeforeRead = () =>
        {
            reads++;
            if (reads is 40 or 41) throw new LiteException(0, "injected read failure");
        };
        ingestion.PollOnce(); // fails part-way: the line is retried by the next poll, nothing after it is skipped
        var afterFirst = ingestion.LinesIngested;
        Assert.InRange(afterFirst, 1, 2999);
        ingestion.PollOnce(); // fails again at the same line
        Assert.Equal(afterFirst, ingestion.LinesIngested);
        ingestion.PollOnce(); // succeeds from that line on
        ingestion.Flush();
        Assert.Equal(3000, ingestion.LinesIngested);
        Assert.Equal(0, ingestion.MalformedLines);
        Assert.Equal(new FileInfo(env.Paths.StatsLogFile).Length, env.Traffic.LoadCursor()!.Offset);
        var day = await DayReport(env);
        Assert.Equal(3000, day.Totals.Requests);
        Assert.Equal(10, day.Totals.UniqueClients);

        // A line whose buckets can never be read is skipped after three attempts (counted as unreadable).
        env.Traffic.BeforeRead = () => throw new LiteException(0, "injected permanent failure");
        StatsLines.Append(env.Paths, [StatsLines.Line(Base.AddHours(-3), "a.test", "192.0.2.1")]);
        for (var i = 0; i < 3; i++) ingestion.PollOnce();
        env.Traffic.BeforeRead = null;
        StatsLines.Append(env.Paths, Lines(10, "a.test", 10, firstIndex: 3000));
        ingestion.PollOnce();
        ingestion.Flush();
        Assert.Equal(3011, ingestion.LinesIngested);
        Assert.Equal(1, ingestion.MalformedLines);
        Assert.Equal(3010, (await DayReport(env)).Totals.Requests);
    }

    [Fact]
    public async Task BucketIdsDoNotDependOnTheCulture_AndReportsSurviveDuplicateBuckets()
    {
        using var env = new TempEnv();
        Configure(env, "a.test");
        var culture = CultureInfo.CurrentCulture;
        try
        {
            // The Thai culture uses the Buddhist calendar: "yyyy" is 543 years ahead.
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            using (var ingestion = env.NewIngestion(TimeSpan.Zero))
            {
                StatsLines.Append(env.Paths, Lines(100, "a.test", 5, minutes: 2));
                ingestion.PollOnce();
                ingestion.Flush();
            }
            CultureInfo.CurrentCulture = new CultureInfo("en-US");
            using (var ingestion = env.NewIngestion(TimeSpan.Zero))
            {
                StatsLines.Append(env.Paths, Lines(100, "a.test", 5, firstIndex: 100, minutes: 2));
                ingestion.PollOnce();
                ingestion.Flush();
            }
        }
        finally { CultureInfo.CurrentCulture = culture; }

        foreach (var scale in Enum.GetValues<BucketScale>())
        {
            var docs = env.Traffic.Col(scale).FindAll().ToList();
            Assert.Equal(docs.Count, docs.Select(d => (d.Host, d.Start)).Distinct().Count());
            Assert.All(docs, d => Assert.Equal(TrafficBucketDoc.MakeId(d.Host, d.Start), d.Id));
            Assert.All(docs, d => Assert.EndsWith(d.Start.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture), d.Id));
        }
        Assert.Equal(200, (await DayReport(env)).Totals.Requests);

        // Two documents for one bucket start (as an older build could leave behind) are summed, not an error.
        var hour = env.Traffic.Col(BucketScale.Hour).FindById(TrafficBucketDoc.MakeId(TrafficBucketDoc.Total, Base))!;
        hour.Id = TrafficBucketDoc.Total + "|legacy";
        env.Traffic.Col(BucketScale.Hour).Insert(hour);
        var day = await DayReport(env);
        Assert.Equal(400, day.Totals.Requests);
        Assert.Equal(400, day.Series.Single(p => p.At == Base).Requests);
    }

    /// <summary>The unkeyed hash used before (FNV-1a + MurmurHash3 fmix64) — what an attacker would enumerate.</summary>
    private static ulong UnkeyedHash(string s)
    {
        var h = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(s)) { h ^= b; h *= 1099511628211UL; }
        h ^= h >> 33; h *= 0xff51afd7ed558ccdUL; h ^= h >> 33; h *= 0xc4ceb9fe1a85ec53UL; h ^= h >> 33;
        return h;
    }

    private static ulong StoredHash(TempEnv env)
    {
        var blob = env.Traffic.Blobs(BucketScale.Day).FindById(TrafficBucketDoc.MakeId(TrafficBucketDoc.Total,
            new DateTime(Base.Ticks - Base.Ticks % TimeSpan.TicksPerDay, DateTimeKind.Utc)))!;
        Assert.Equal(1, blob.Clients![0]); // exact mode: 1, count, hashes
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(blob.Clients.AsSpan(1)));
        return BinaryPrimitives.ReadUInt64LittleEndian(blob.Clients.AsSpan(5));
    }

    [Fact]
    public async Task ClientHashesAreKeyedPerInstallation_KeyKeptProtectedOutsideTheStatisticsDatabase()
    {
        const string client = "203.0.113.7";
        using var a = new TempEnv();
        using var b = new TempEnv();
        foreach (var env in new[] { a, b })
        {
            using var ingestion = env.NewIngestion(TimeSpan.Zero);
            StatsLines.Append(env.Paths, [StatsLines.Line(Base, "x.test", client)]);
            ingestion.PollOnce();
            ingestion.Flush();
        }
        var hashA = StoredHash(a);
        var hashB = StoredHash(b);
        var report = E2EArtifacts.Report(nameof(ClientHashesAreKeyedPerInstallation_KeyKeptProtectedOutsideTheStatisticsDatabase));
        report["client"] = client;
        report["unkeyedHash"] = UnkeyedHash(client).ToString("x16");
        report["installA"] = hashA.ToString("x16");
        report["installB"] = hashB.ToString("x16");

        Assert.NotEqual(UnkeyedHash(client), hashA);
        Assert.NotEqual(hashA, hashB);
        // The key: in its own file next to the database, protected with ISecretProtector; nothing key-like in the database.
        var keyFile = ClientHasher.KeyFile(a.Paths);
        Assert.Equal(Path.GetDirectoryName(TrafficStore.DbFile(a.Paths)), Path.GetDirectoryName(keyFile));
        var stored = File.ReadAllText(keyFile);
        Assert.StartsWith("enc:v1:", stored);
        Assert.Equal(32, Convert.FromBase64String(new SecretProtector(a.Paths).Unprotect(stored)).Length);
        Assert.Equal(["traffic_cursor", "traffic_day", "traffic_day_blobs", "traffic_hour", "traffic_hour_blobs", "traffic_minute", "traffic_minute_blobs"],
            a.Traffic.CollectionNames().OrderBy(n => n, StringComparer.Ordinal));

        // Same installation after a restart: same key, so the same client is still one unique client.
        using (var ingestion = a.NewIngestion(TimeSpan.Zero))
        {
            StatsLines.Append(a.Paths, [StatsLines.Line(Base.AddMinutes(1), "x.test", client)]);
            ingestion.PollOnce();
            ingestion.Flush();
        }
        Assert.Equal(hashA, StoredHash(a));
        var day = await DayReport(a);
        report["afterRestart"] = new JsonObject { ["requests"] = day.Totals.Requests, ["uniqueClients"] = day.Totals.UniqueClients };
        E2EArtifacts.Write("client-hash-keying.json", report);
        Assert.Equal(2, day.Totals.Requests);
        Assert.Equal(1, day.Totals.UniqueClients);
    }

    [Fact]
    public void StatisticsLiveInTheirOwnDatabase_AndOldCollectionsLeaveTheConfigurationDatabase()
    {
        using var env = new TempEnv();
        // What an earlier development build left in manager.db.
        env.Store.Database.GetCollection("traffic_day").Insert(new BsonDocument { ["_id"] = "*|202609250000", ["Requests"] = 5 });
        env.Store.Database.GetCollection("traffic_cursor").Insert(new BsonDocument { ["_id"] = "stats", ["Offset"] = 123 });
        Assert.Contains("traffic_day", env.Store.Database.GetCollectionNames());

        using (var ingestion = env.NewIngestion(TimeSpan.Zero))
        {
            StatsLines.Append(env.Paths, Lines(10, "a.test", 2));
            ingestion.PollOnce();
            ingestion.Flush();
        }
        Assert.DoesNotContain(env.Store.Database.GetCollectionNames(), n => n.StartsWith("traffic_", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(env.Paths.DataDir, "db", "telemetry.db")));
        // The old cursor was dropped with the rest: reading started at the beginning of the log.
        Assert.Equal(10, env.Traffic.LoadCursor()!.LinesIngested);
    }

    [Fact]
    public async Task CaddyfileMode_ReportSaysStatisticsAreNotCollected()
    {
        using var env = new TempEnv();
        Configure(env, "a.test");
        using (var ingestion = env.NewIngestion(TimeSpan.Zero))
        {
            StatsLines.Append(env.Paths, Lines(10, "a.test", 2));
            ingestion.PollOnce();
            ingestion.Flush();
        }
        var settings = env.Store.GetSettings<CaddySettings>();
        settings.Mode = ConfigMode.Caddyfile;
        env.Store.SaveSettings(settings);
        var off = await DayReport(env);
        Assert.False(off.Enabled);
        Assert.Contains(off.Notes, n => n.Contains("Caddyfile mode", StringComparison.Ordinal));
        Assert.Equal(0, off.Totals.Requests);

        settings.Mode = ConfigMode.Managed;
        env.Store.SaveSettings(settings);
        var on = await DayReport(env);
        Assert.True(on.Enabled);
        Assert.Equal(10, on.Totals.Requests);
    }
}
