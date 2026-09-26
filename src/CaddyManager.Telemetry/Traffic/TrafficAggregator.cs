using System.Globalization;

namespace CaddyManager.Telemetry.Traffic;

/// <summary>How a log line is applied: normally to everything; during a replay after a restart only to the sketches and
/// top clients (the counters already contain the line — see TrafficIngestion).</summary>
internal enum ApplyMode { Full, BlobsOnly }

/// <summary>
/// In-memory state of the buckets being filled. Every request updates six buckets: minute / hour / day × (server total,
/// host key). A bucket is loaded from the store the first time it is touched and then STAYS in memory while it is in use:
/// a flush writes only what changed since the previous one — the small counter documents every flush, the sketches and top
/// clients (blobs) only when the ingester asks for them (at most once a minute) — and nothing is read back. Buckets are
/// evicted once everything about them is saved and their period has been over for a while; a late request for an evicted
/// bucket simply reloads it. Space-Saving top clients live in day buckets (a running summary, not deltas: merging per-flush
/// deltas would inflate the error bound with every flush). Not thread-safe; the ingester serialises access.
/// </summary>
internal sealed class TrafficAggregator(TrafficStore store, ClientHasher hasher)
{
    private sealed class Live
    {
        public required BucketScale Scale;
        public required TrafficBucketDoc Doc;
        public required ClientSketch Clients;
        public required DateTime End;
        public Dictionary<int, long> Codes = new();
        public TopClients? Top;
        public bool CountersDirty;
        public bool BlobDirty;
    }

    /// <summary>Clean buckets stay cached this long after their period ended (late log entries, reports).</summary>
    private static readonly TimeSpan EvictAfter = TimeSpan.FromMinutes(2);
    /// <summary>Above this many cached buckets every clean one is evicted after a save (bounded memory for backlogs).</summary>
    private const int MaxCached = 5000;

    private readonly Dictionary<(BucketScale, string, DateTime), Live> _live = new();
    private readonly Live[] _batch = new Live[6];
    private DateTime _newest;

    public int CachedBuckets => _live.Count;
    public bool HasCounterChanges { get; private set; }
    public bool HasBlobChanges { get; private set; }

    /// <summary>
    /// Applies one request under <paramref name="host"/> (a <see cref="HostMatcher"/> key). All six buckets are loaded
    /// first, so a store error leaves nothing half-applied (the caller can retry the line).
    /// </summary>
    public void Add(in AccessEntry e, string host, ApplyMode mode)
    {
        var minute = new DateTime(e.At.Ticks - e.At.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        var hour = new DateTime(e.At.Ticks - e.At.Ticks % TimeSpan.TicksPerHour, DateTimeKind.Utc);
        var day = new DateTime(e.At.Ticks - e.At.Ticks % TimeSpan.TicksPerDay, DateTimeKind.Utc);
        _batch[0] = Get(BucketScale.Minute, TrafficBucketDoc.Total, minute);
        _batch[1] = Get(BucketScale.Minute, host, minute);
        _batch[2] = Get(BucketScale.Hour, TrafficBucketDoc.Total, hour);
        _batch[3] = Get(BucketScale.Hour, host, hour);
        _batch[4] = Get(BucketScale.Day, TrafficBucketDoc.Total, day);
        _batch[5] = Get(BucketScale.Day, host, day);
        var clientHash = hasher.Hash(e.ClientIp);
        foreach (var b in _batch)
        {
            if (mode == ApplyMode.Full) ApplyCounters(b, e);
            if (b.Clients.AddHash(clientHash)) b.BlobDirty = true;
            if (b.Top is not null)
            {
                b.Top.Add(e.ClientIp.Length == 0 ? "(unknown)" : e.ClientIp, e.BytesOut, e.At);
                b.BlobDirty = true;
            }
            HasBlobChanges |= b.BlobDirty;
        }
        if (mode == ApplyMode.Full) HasCounterChanges = true;
        if (e.At > _newest) _newest = e.At;
    }

    private static void ApplyCounters(Live b, in AccessEntry e)
    {
        var d = b.Doc;
        d.Requests++;
        d.BytesIn += e.BytesIn;
        d.BytesOut += e.BytesOut;
        d.DurationSeconds += e.DurationSeconds;
        switch (e.Status)
        {
            case >= 200 and < 300: d.Status2xx++; break;
            case >= 300 and < 400: d.Status3xx++; break;
            case >= 400 and < 500: d.Status4xx++; break;
            case >= 500 and < 600: d.Status5xx++; break;
            default: d.StatusOther++; break; // 0 (aborted), 1xx, anything odd
        }
        b.Codes[e.Status] = b.Codes.GetValueOrDefault(e.Status) + 1;
        b.CountersDirty = true;
    }

    private Live Get(BucketScale scale, string host, DateTime start)
    {
        var key = (scale, host, start);
        if (_live.TryGetValue(key, out var live)) return live;
        var doc = store.Find(scale, host, start) ?? new TrafficBucketDoc
        {
            Id = TrafficBucketDoc.MakeId(host, start),
            Start = start,
            Host = host,
        };
        var blob = store.FindBlob(scale, host, start);
        live = new Live
        {
            Scale = scale,
            Doc = doc,
            Clients = ClientSketch.Deserialize(blob?.Clients),
            Top = scale == BucketScale.Day ? TopClients.From(blob?.TopClients) : null,
            End = scale switch
            {
                BucketScale.Minute => start.AddMinutes(1),
                BucketScale.Hour => start.AddHours(1),
                _ => start.AddDays(1),
            },
        };
        foreach (var (code, count) in doc.StatusCodes)
            if (int.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c)) live.Codes[c] = count;
        _live[key] = live;
        return live;
    }

    /// <summary>Counter documents changed since the last save (their current, complete state).</summary>
    public List<(BucketScale Scale, TrafficBucketDoc Doc)> CollectCounters()
    {
        var result = new List<(BucketScale, TrafficBucketDoc)>();
        foreach (var b in _live.Values)
        {
            if (!b.CountersDirty) continue;
            b.Doc.StatusCodes = b.Codes.ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => kv.Value);
            result.Add((b.Scale, b.Doc));
        }
        return result;
    }

    /// <summary>Blobs changed since the last blob save.</summary>
    public List<(BucketScale Scale, TrafficBlobDoc Doc)> CollectBlobs()
    {
        var result = new List<(BucketScale, TrafficBlobDoc)>();
        foreach (var b in _live.Values)
            if (b.BlobDirty) result.Add((b.Scale, ToBlob(b)));
        return result;
    }

    /// <summary>Unsaved blobs of buckets with from ≤ Start &lt; to, by Id (reports overlay them on the stored ones).</summary>
    public Dictionary<string, TrafficBlobDoc> PendingBlobs(BucketScale scale, DateTime from, DateTime to)
    {
        var result = new Dictionary<string, TrafficBlobDoc>(StringComparer.Ordinal);
        foreach (var b in _live.Values)
            if (b.BlobDirty && b.Scale == scale && b.Doc.Start >= from && b.Doc.Start < to) result[b.Doc.Id] = ToBlob(b);
        return result;
    }

    private static TrafficBlobDoc ToBlob(Live b) => new()
    {
        Id = b.Doc.Id,
        Start = b.Doc.Start,
        Host = b.Doc.Host,
        Clients = b.Clients.Serialize(),
        TopClients = b.Top?.Snapshot(),
    };

    /// <summary>After a successful save of everything collected: marks it clean and evicts buckets no longer needed.</summary>
    public void MarkSaved(bool blobsSaved)
    {
        foreach (var b in _live.Values)
        {
            b.CountersDirty = false;
            if (blobsSaved) b.BlobDirty = false;
        }
        HasCounterChanges = false;
        if (blobsSaved) HasBlobChanges = false;
        // Nothing ingested yet (a fresh install, or Caddy not serving): _newest is MinValue, which cannot go further back.
        var cutoff = _newest == default ? DateTime.MinValue : _newest - EvictAfter;
        var all = _live.Count > MaxCached;
        foreach (var (key, b) in _live.ToList())
            if (!b.CountersDirty && !b.BlobDirty && (all || b.End <= cutoff)) _live.Remove(key);
    }

    /// <summary>Forgets everything (after a failed save the ingester re-reads the log from the stored cursors).</summary>
    public void Clear()
    {
        _live.Clear();
        _newest = default;
        HasCounterChanges = HasBlobChanges = false;
    }
}
