using System.Globalization;

namespace CaddyManager.Telemetry.Traffic;

/// <summary>
/// In-memory accumulation between flushes. Every request updates six buckets: minute / hour / day × (server total, host).
/// A bucket is loaded from the store the first time it is touched after a flush, updated in memory, and written back in
/// full by <see cref="Collect"/> — so the stored state plus the cursor always describe exactly the lines consumed.
/// Space-Saving top clients live in day buckets (a running summary, not deltas: merging per-flush deltas would inflate the
/// error bound with every flush). Not thread-safe; the ingester serialises access.
/// </summary>
internal sealed class TrafficAggregator(TrafficStore store)
{
    private sealed class Live
    {
        public required BucketScale Scale;
        public required TrafficBucketDoc Doc;
        public required ClientSketch Clients;
        public Dictionary<int, long> Codes = new();
        public TopClients? Top;
    }

    private readonly Dictionary<(BucketScale, string, DateTime), Live> _live = new();

    public int PendingBuckets => _live.Count;

    public void Add(in AccessEntry e)
    {
        var minute = new DateTime(e.At.Ticks - e.At.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        var hour = new DateTime(e.At.Ticks - e.At.Ticks % TimeSpan.TicksPerHour, DateTimeKind.Utc);
        var day = new DateTime(e.At.Ticks - e.At.Ticks % TimeSpan.TicksPerDay, DateTimeKind.Utc);
        var host = e.Host.Length == 0 || e.Host == TrafficBucketDoc.Total ? NoHost : e.Host;
        var clientHash = ClientSketch.Hash(e.ClientIp);
        Apply(Get(BucketScale.Minute, TrafficBucketDoc.Total, minute), e, clientHash);
        Apply(Get(BucketScale.Minute, host, minute), e, clientHash);
        Apply(Get(BucketScale.Hour, TrafficBucketDoc.Total, hour), e, clientHash);
        Apply(Get(BucketScale.Hour, host, hour), e, clientHash);
        Apply(Get(BucketScale.Day, TrafficBucketDoc.Total, day), e, clientHash);
        Apply(Get(BucketScale.Day, host, day), e, clientHash);
    }

    /// <summary>Host label for requests without a Host header (HTTP/1.0).</summary>
    public const string NoHost = "(none)";

    private static void Apply(Live b, in AccessEntry e, ulong clientHash)
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
        b.Clients.AddHash(clientHash);
        b.Top?.Add(e.ClientIp.Length == 0 ? "(unknown)" : e.ClientIp, e.BytesOut, e.At);
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
        live = new Live
        {
            Scale = scale,
            Doc = doc,
            Clients = ClientSketch.Deserialize(doc.Clients),
            Top = scale == BucketScale.Day ? TopClients.From(doc.TopClients) : null,
        };
        foreach (var (code, count) in doc.StatusCodes)
            if (int.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c)) live.Codes[c] = count;
        _live[key] = live;
        return live;
    }

    /// <summary>Every touched bucket in its complete, updated form. Call <see cref="Clear"/> once they are saved.</summary>
    public List<(BucketScale Scale, TrafficBucketDoc Doc)> Collect()
    {
        var result = new List<(BucketScale, TrafficBucketDoc)>(_live.Count);
        foreach (var b in _live.Values)
        {
            b.Doc.Clients = b.Clients.Serialize();
            b.Doc.StatusCodes = b.Codes.ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => kv.Value);
            if (b.Top is not null) b.Doc.TopClients = b.Top.Snapshot();
            result.Add((b.Scale, b.Doc));
        }
        return result;
    }

    /// <summary>After a successful save: the next request reloads each bucket from the store. After a failed save the
    /// state is kept (the tailer has already moved past those lines) and the next flush retries.</summary>
    public void Clear() => _live.Clear();
}
