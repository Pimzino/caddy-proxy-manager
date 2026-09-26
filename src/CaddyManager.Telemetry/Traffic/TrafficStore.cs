using System.Globalization;
using CaddyManager.Core;
using LiteDB;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaddyManager.Telemetry.Traffic;

public enum BucketScale { Minute, Hour, Day }

/// <summary>
/// The counters of one aggregated bucket: server total (Host = <see cref="Total"/>) or one host, for one minute/hour/day
/// (UTC). Small (a few hundred bytes) so it can be written every flush; the unique-client sketch and the top clients live
/// in the bucket's <see cref="TrafficBlobDoc"/>.
/// </summary>
public sealed class TrafficBucketDoc
{
    /// <summary>"&lt;host&gt;|&lt;yyyyMMddHHmm start&gt;" (unique within the scale's collection).</summary>
    public string Id { get; set; } = "";
    public DateTime Start { get; set; }
    /// <summary>Host key (<see cref="HostMatcher"/>), or <see cref="Total"/> for the server total.</summary>
    public string Host { get; set; } = Total;
    public long Requests { get; set; }
    public long BytesIn { get; set; }
    public long BytesOut { get; set; }
    public long Status2xx { get; set; }
    public long Status3xx { get; set; }
    public long Status4xx { get; set; }
    public long Status5xx { get; set; }
    public long StatusOther { get; set; }
    public double DurationSeconds { get; set; }
    /// <summary>Status code (as string, LiteDB document keys are strings) → count.</summary>
    public Dictionary<string, long> StatusCodes { get; set; } = new();

    /// <summary>
    /// Host key of server-total buckets. Not "": LiteDB's BsonMapper stores empty strings as null by default
    /// (EmptyStringToNull), which would break equality queries. "*" is never a host key (HostMatcher only returns
    /// configured names, "*.&lt;domain&gt;" wildcards and "(other)").
    /// </summary>
    public const string Total = "*";

    /// <summary>Culture-independent: the id must not change with the service account's calendar (th-TH, fa-IR, ...).</summary>
    public static string MakeId(string host, DateTime start) => host + "|" + start.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
}

/// <summary>The heavy part of a bucket (same Id as its <see cref="TrafficBucketDoc"/>), written at most once a minute.</summary>
public sealed class TrafficBlobDoc
{
    public string Id { get; set; } = "";
    public DateTime Start { get; set; }
    public string Host { get; set; } = TrafficBucketDoc.Total;
    /// <summary>Serialized <see cref="ClientSketch"/>.</summary>
    public byte[]? Clients { get; set; }
    /// <summary>Space-Saving summary; day buckets only.</summary>
    public List<TopClientCounter>? TopClients { get; set; }
}

/// <summary>Where the stats log tailer is: the file (identity) being read and the offset after the last complete line.</summary>
public sealed class TrafficCursorDoc
{
    public string Id { get; set; } = CursorId;
    public string? FileId { get; set; }
    public long Offset { get; set; }
    /// <summary>Last-write time of that file when last read (finds backups rotated after it when it is gone).</summary>
    public DateTime FileLastWriteUtc { get; set; }
    /// <summary>When new lines were last read (UTC).</summary>
    public DateTime? LastIngestAt { get; set; }
    public long LinesIngested { get; set; }
    public long MalformedLines { get; set; }
    /// <summary>Rotated files that were deleted before they could be read completely (data lost).</summary>
    public long FilesMissed { get; set; }

    /// <summary>Position up to which the counters (TrafficBucketDoc) are stored.</summary>
    internal const string CursorId = "stats";
    /// <summary>Position up to which the sketches and top clients (TrafficBlobDoc) are stored (never after "stats").</summary>
    internal const string BlobCursorId = "blobs";

    public TrafficCursorDoc Clone() => (TrafficCursorDoc)MemberwiseClone();
}

/// <summary>
/// LiteDB persistence of traffic statistics in their own database file, <see cref="DbFile"/> (db\telemetry.db next to
/// manager.db): statistics are written every few seconds and can grow large, so they must not share the configuration
/// database — its lock, its checkpoints and its backups. Collections: traffic_{minute,hour,day} (counters),
/// traffic_{minute,hour,day}_blobs (sketches, top clients), traffic_cursor. Every save is ONE transaction: the counters
/// written with the "stats" cursor and the blobs with the "blobs" cursor always describe exactly the log lines before
/// their cursor, so a restart resumes without losing or double counting anything (see TrafficIngestion).
/// </summary>
public sealed class TrafficStore : IDisposable
{
    private static readonly string[] LegacyCollections = ["traffic_minute", "traffic_hour", "traffic_day", "traffic_cursor"];

    private readonly LiteDatabase _db;

    /// <summary>SPEC retention: minute 48 h (server total), hour 35 d, day 400 d. Per-host minute buckets only feed the 1-hour view.</summary>
    public static readonly TimeSpan MinuteRetention = TimeSpan.FromHours(48);
    public static readonly TimeSpan MinuteHostRetention = TimeSpan.FromHours(2);
    public static readonly TimeSpan HourRetention = TimeSpan.FromDays(35);
    public static readonly TimeSpan DayRetention = TimeSpan.FromDays(400);

    public static string DbFile(AppPaths paths) => Path.Combine(paths.DataDir, "db", "telemetry.db");

    /// <summary>Writes so far (diagnostics and tests): documents and their BSON size.</summary>
    public long CounterDocsWritten { get; private set; }
    public long CounterBytesWritten { get; private set; }
    public long BlobDocsWritten { get; private set; }
    public long BlobBytesWritten { get; private set; }
    public long Saves { get; private set; }
    /// <summary>Saves that included the blobs (sketches, top clients).</summary>
    public long BlobSaves { get; private set; }

    /// <summary>Fault injection for tests: called inside every save transaction before anything is written.</summary>
    internal Action? BeforeSave { get; set; }
    /// <summary>Fault injection for tests: called before every single-bucket read.</summary>
    internal Action? BeforeRead { get; set; }

    /// <param name="legacy">The configuration store: statistics collections an earlier development build kept in
    /// manager.db are dropped from it (once — afterwards there is nothing left to drop).</param>
    public TrafficStore(AppPaths paths, IStore? legacy = null, ILogger<TrafficStore>? logger = null)
    {
        var file = DbFile(paths);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        _db = new LiteDatabase(new ConnectionString { Filename = file, Connection = ConnectionType.Direct }, new BsonMapper());
        _db.UtcDate = true;
        foreach (var scale in Enum.GetValues<BucketScale>())
        {
            var col = Col(scale);
            col.EnsureIndex(b => b.Start);
            col.EnsureIndex(b => b.Host);
            var blobs = Blobs(scale);
            blobs.EnsureIndex(b => b.Start);
            blobs.EnsureIndex(b => b.Host);
        }
        if (legacy is not null) DropLegacy(legacy, logger ?? NullLogger<TrafficStore>.Instance);
    }

    private static void DropLegacy(IStore legacy, ILogger logger)
    {
        try
        {
            var names = legacy.Database.GetCollectionNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var dropped = LegacyCollections.Where(names.Contains).Where(n => legacy.Database.DropCollection(n)).ToList();
            if (dropped.Count > 0)
                logger.LogInformation("Removed the traffic statistics collections {Collections} from the configuration database " +
                    "(statistics are kept in their own database now; counting starts again)", string.Join(", ", dropped));
        }
        catch (Exception ex) { logger.LogWarning(ex, "Could not remove old traffic statistics from the configuration database"); }
    }

    public ILiteCollection<TrafficBucketDoc> Col(BucketScale scale) => _db.GetCollection<TrafficBucketDoc>(Name(scale));

    public ILiteCollection<TrafficBlobDoc> Blobs(BucketScale scale) => _db.GetCollection<TrafficBlobDoc>(Name(scale) + "_blobs");

    private static string Name(BucketScale scale) => scale switch
    {
        BucketScale.Minute => "traffic_minute",
        BucketScale.Hour => "traffic_hour",
        _ => "traffic_day",
    };

    internal List<string> CollectionNames() => _db.GetCollectionNames().ToList();

    private ILiteCollection<TrafficCursorDoc> Cursors => _db.GetCollection<TrafficCursorDoc>("traffic_cursor");

    public TrafficCursorDoc? LoadCursor() => Cursors.FindById(TrafficCursorDoc.CursorId);

    public TrafficCursorDoc? LoadBlobCursor() => Cursors.FindById(TrafficCursorDoc.BlobCursorId);

    /// <summary>True when a host key can be stored and queried (LiteDB index keys must stay below 1,023 bytes).</summary>
    public static bool IsValidHostKey(string host) => host.Length is > 0 and <= HostMatcher.MaxHostLength;

    public TrafficBucketDoc? Find(BucketScale scale, string host, DateTime start)
    {
        BeforeRead?.Invoke();
        return IsValidHostKey(host) ? Col(scale).FindById(TrafficBucketDoc.MakeId(host, start)) : null;
    }

    public TrafficBlobDoc? FindBlob(BucketScale scale, string host, DateTime start) =>
        IsValidHostKey(host) ? Blobs(scale).FindById(TrafficBucketDoc.MakeId(host, start)) : null;

    /// <summary>
    /// Upserts counters with the counter cursor and — when <paramref name="blobCursor"/> is given — blobs with the blob
    /// cursor, all in one transaction.
    /// </summary>
    public void Save(IReadOnlyCollection<(BucketScale Scale, TrafficBucketDoc Doc)> counters, TrafficCursorDoc cursor,
        IReadOnlyCollection<(BucketScale Scale, TrafficBlobDoc Doc)>? blobs = null, TrafficCursorDoc? blobCursor = null)
    {
        foreach (var (_, d) in counters)
            if (!IsValidHostKey(d.Host)) throw new ArgumentException($"Invalid traffic host key ({d.Host.Length} characters).");
        // LiteDB 5 transactions are per thread; this method is synchronous so begin/commit happen on the same thread.
        var own = _db.BeginTrans();
        try
        {
            BeforeSave?.Invoke();
            foreach (var group in counters.GroupBy(b => b.Scale))
                Col(group.Key).Upsert(group.Select(b => b.Doc));
            Cursors.Upsert(cursor);
            long blobBytes = 0;
            if (blobCursor is not null)
            {
                foreach (var group in (blobs ?? []).GroupBy(b => b.Scale))
                    Blobs(group.Key).Upsert(group.Select(b => b.Doc));
                blobCursor.Id = TrafficCursorDoc.BlobCursorId;
                Cursors.Upsert(blobCursor);
                blobBytes = (blobs ?? []).Sum(b => (long)BsonSize(b.Doc));
            }
            if (own) _db.Commit();
            Saves++;
            CounterDocsWritten += counters.Count;
            CounterBytesWritten += counters.Sum(c => (long)BsonSize(c.Doc));
            if (blobCursor is not null)
            {
                BlobSaves++;
                BlobDocsWritten += blobs?.Count ?? 0;
                BlobBytesWritten += blobBytes;
            }
        }
        catch
        {
            if (own) _db.Rollback();
            throw;
        }
    }

    private int BsonSize<T>(T doc) => BsonSerializer.Serialize(_db.Mapper.ToDocument(doc)).Length;

    /// <summary>Buckets with from ≤ Start &lt; to. host null = only server-total buckets; otherwise that host.</summary>
    public List<TrafficBucketDoc> Query(BucketScale scale, DateTime from, DateTime to, string? host)
    {
        var h = host ?? TrafficBucketDoc.Total;
        if (!IsValidHostKey(h)) return [];
        return Col(scale).Find(b => b.Start >= from && b.Start < to && b.Host == h).ToList();
    }

    /// <summary>Blobs of <see cref="Query"/>'s buckets, by Id.</summary>
    public Dictionary<string, TrafficBlobDoc> QueryBlobs(BucketScale scale, DateTime from, DateTime to, string? host)
    {
        var h = host ?? TrafficBucketDoc.Total;
        if (!IsValidHostKey(h)) return [];
        return Blobs(scale).Find(b => b.Start >= from && b.Start < to && b.Host == h).ToDictionary(b => b.Id);
    }

    /// <summary>Per-host counters (not the server total) with from ≤ Start &lt; to. Small documents only.</summary>
    public List<TrafficBucketDoc> QueryHosts(BucketScale scale, DateTime from, DateTime to) =>
        Col(scale).Find(b => b.Start >= from && b.Start < to && b.Host != TrafficBucketDoc.Total).ToList();

    /// <summary>Deletes expired buckets; returns how many.</summary>
    public int Cleanup(DateTime now)
    {
        var minuteCut = now - MinuteRetention;
        var minuteHostCut = now - MinuteHostRetention;
        var hourCut = now - HourRetention;
        var dayCut = now - DayRetention;
        var removed = Col(BucketScale.Minute).DeleteMany(b => b.Start < minuteCut)
             + Col(BucketScale.Minute).DeleteMany(b => b.Host != TrafficBucketDoc.Total && b.Start < minuteHostCut)
             + Col(BucketScale.Hour).DeleteMany(b => b.Start < hourCut)
             + Col(BucketScale.Day).DeleteMany(b => b.Start < dayCut);
        Blobs(BucketScale.Minute).DeleteMany(b => b.Start < minuteCut);
        Blobs(BucketScale.Minute).DeleteMany(b => b.Host != TrafficBucketDoc.Total && b.Start < minuteHostCut);
        Blobs(BucketScale.Hour).DeleteMany(b => b.Start < hourCut);
        Blobs(BucketScale.Day).DeleteMany(b => b.Start < dayCut);
        return removed;
    }

    public void Dispose() => _db.Dispose();
}
