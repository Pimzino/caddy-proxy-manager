using CaddyManager.Core;
using LiteDB;

namespace CaddyManager.Telemetry.Traffic;

public enum BucketScale { Minute, Hour, Day }

/// <summary>One aggregated bucket: server total (Host = <see cref="Total"/>) or one host, for one minute/hour/day (UTC).</summary>
public sealed class TrafficBucketDoc
{
    /// <summary>"&lt;host&gt;|&lt;yyyyMMddHHmm start&gt;" (unique within the scale's collection).</summary>
    public string Id { get; set; } = "";
    public DateTime Start { get; set; }
    /// <summary>Host name, or <see cref="Total"/> for the server total.</summary>
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
    /// <summary>Serialized <see cref="ClientSketch"/>.</summary>
    public byte[]? Clients { get; set; }
    /// <summary>Status code (as string, LiteDB document keys are strings) → count.</summary>
    public Dictionary<string, long> StatusCodes { get; set; } = new();
    /// <summary>Space-Saving summary; day buckets only.</summary>
    public List<TopClientCounter>? TopClients { get; set; }

    /// <summary>
    /// Host key of server-total buckets. Not "": LiteDB's BsonMapper stores empty strings as null by default
    /// (EmptyStringToNull), which would break equality queries. "*" is never a normalised host name (the aggregator files a
    /// literal "*" Host header under "(none)").
    /// </summary>
    public const string Total = "*";

    public static string MakeId(string host, DateTime start) => host + "|" + start.ToString("yyyyMMddHHmm");
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

    internal const string CursorId = "stats";
}

/// <summary>
/// LiteDB persistence of traffic statistics (collections owned by the Telemetry module: traffic_minute, traffic_hour,
/// traffic_day, traffic_cursor). Aggregated buckets and the tail cursor are written in ONE transaction so a restart
/// resumes exactly where the stored counters end (no loss, no double counting).
/// </summary>
public sealed class TrafficStore
{
    private readonly ILiteDatabase _db;

    /// <summary>SPEC retention: minute 48 h (server total), hour 35 d, day 400 d. Per-host minute buckets only feed the 1-hour view.</summary>
    public static readonly TimeSpan MinuteRetention = TimeSpan.FromHours(48);
    public static readonly TimeSpan MinuteHostRetention = TimeSpan.FromHours(2);
    public static readonly TimeSpan HourRetention = TimeSpan.FromDays(35);
    public static readonly TimeSpan DayRetention = TimeSpan.FromDays(400);

    public TrafficStore(IStore store)
    {
        _db = store.Database;
        foreach (var scale in Enum.GetValues<BucketScale>())
        {
            var col = Col(scale);
            col.EnsureIndex(b => b.Start);
            col.EnsureIndex(b => b.Host);
        }
    }

    public ILiteCollection<TrafficBucketDoc> Col(BucketScale scale) => _db.GetCollection<TrafficBucketDoc>(scale switch
    {
        BucketScale.Minute => "traffic_minute",
        BucketScale.Hour => "traffic_hour",
        _ => "traffic_day",
    });

    private ILiteCollection<TrafficCursorDoc> Cursors => _db.GetCollection<TrafficCursorDoc>("traffic_cursor");

    public TrafficCursorDoc? LoadCursor() => Cursors.FindById(TrafficCursorDoc.CursorId);

    public TrafficBucketDoc? Find(BucketScale scale, string host, DateTime start) => Col(scale).FindById(TrafficBucketDoc.MakeId(host, start));

    /// <summary>Upserts the buckets and the cursor atomically.</summary>
    public void Save(IEnumerable<(BucketScale Scale, TrafficBucketDoc Doc)> buckets, TrafficCursorDoc cursor)
    {
        // LiteDB 5 transactions are per thread; this method is synchronous so begin/commit happen on the same thread.
        var own = _db.BeginTrans();
        try
        {
            foreach (var group in buckets.GroupBy(b => b.Scale))
                Col(group.Key).Upsert(group.Select(b => b.Doc));
            Cursors.Upsert(cursor);
            if (own) _db.Commit();
        }
        catch
        {
            if (own) _db.Rollback();
            throw;
        }
    }

    /// <summary>Buckets with from ≤ Start &lt; to. host null = only server-total buckets; otherwise that host.</summary>
    public List<TrafficBucketDoc> Query(BucketScale scale, DateTime from, DateTime to, string? host)
    {
        var h = host ?? TrafficBucketDoc.Total;
        return Col(scale).Find(b => b.Start >= from && b.Start < to && b.Host == h).ToList();
    }

    /// <summary>Per-host buckets (not the server total) with from ≤ Start &lt; to.</summary>
    public List<TrafficBucketDoc> QueryHosts(BucketScale scale, DateTime from, DateTime to) =>
        Col(scale).Find(b => b.Start >= from && b.Start < to && b.Host != TrafficBucketDoc.Total).ToList();

    /// <summary>Deletes expired buckets; returns how many.</summary>
    public int Cleanup(DateTime now)
    {
        var minuteCut = now - MinuteRetention;
        var minuteHostCut = now - MinuteHostRetention;
        var hourCut = now - HourRetention;
        var dayCut = now - DayRetention;
        return Col(BucketScale.Minute).DeleteMany(b => b.Start < minuteCut)
             + Col(BucketScale.Minute).DeleteMany(b => b.Host != TrafficBucketDoc.Total && b.Start < minuteHostCut)
             + Col(BucketScale.Hour).DeleteMany(b => b.Start < hourCut)
             + Col(BucketScale.Day).DeleteMany(b => b.Start < dayCut);
    }
}
