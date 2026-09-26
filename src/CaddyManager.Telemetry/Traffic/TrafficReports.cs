using System.Globalization;
using CaddyManager.Core.Contracts;

namespace CaddyManager.Telemetry.Traffic;

/// <summary>
/// Builds <see cref="TrafficReport"/>s from the stored buckets (SPEC "Reports"): hour → 60 minute points, day → 24 hourly,
/// week → 168 hourly, month → 30 daily; oldest first, zero-filled. The window ends with the bucket that contains "now"
/// (so the newest point is still filling). Unique clients over the window = cardinality of the union of the bucket sketches.
/// Sketches and top clients come from the store, overlaid with the ingester's unsaved ones (<paramref name="pending"/>),
/// so a report is current although those are saved only once a minute.
/// </summary>
internal sealed class TrafficReports(TrafficStore store,
    Func<BucketScale, DateTime, DateTime, IReadOnlyDictionary<string, TrafficBlobDoc>>? pending = null)
{
    public const int MaxRows = 20;

    public static (BucketScale Scale, TimeSpan Step, int Points, string Name) Shape(TrafficRange range) => range switch
    {
        TrafficRange.Hour => (BucketScale.Minute, TimeSpan.FromMinutes(1), 60, "minute"),
        TrafficRange.Week => (BucketScale.Hour, TimeSpan.FromHours(1), 168, "hour"),
        TrafficRange.Month => (BucketScale.Day, TimeSpan.FromDays(1), 30, "day"),
        _ => (BucketScale.Hour, TimeSpan.FromHours(1), 24, "hour"),
    };

    public const string DisabledNote =
        "Traffic statistics are turned off (Settings > Caddy). Caddy does not write the statistics log while they are off.";
    public const string CaddyfileNote =
        "Traffic statistics are not collected in Caddyfile mode: the statistics log is part of the managed configuration. " +
        "Switch back to managed mode to collect them.";

    /// <param name="host">Host key to report on (already resolved by the caller), null = all hosts.</param>
    /// <param name="disabledNote">Non-null = statistics are not being collected; the report is empty with this note.</param>
    public TrafficReport Build(TrafficQuery query, string? host, DateTime now, string? disabledNote, DateTime? lastIngestAt,
        long malformedLines, long filesMissed)
    {
        var (scale, step, points, name) = Shape(query.Range);
        var end = new DateTime(now.Ticks - now.Ticks % step.Ticks, DateTimeKind.Utc) + step;
        var from = end - step * points;

        if (disabledNote is not null)
        {
            return new TrafficReport
            {
                Range = query.Range, From = from, To = now, BucketSize = name, Host = host, Enabled = false, LastIngestAt = lastIngestAt,
                Notes = [disabledNote],
            };
        }

        var blobs = Blobs(scale, from, end, host);
        // One point per bucket start. There is one document per start; grouping (instead of a dictionary that throws on a
        // duplicate) keeps a report working should an older build have written two.
        var byStart = store.Query(scale, from, end, host).GroupBy(b => b.Start).ToDictionary(g => g.Key, g => g.ToList());
        var buckets = byStart.Values.SelectMany(g => g).ToList();
        var series = new List<TrafficPoint>(points);
        for (var i = 0; i < points; i++)
        {
            var at = from + step * i;
            if (!byStart.TryGetValue(at, out var group))
            {
                series.Add(new TrafficPoint { At = at });
                continue;
            }
            var t = Sum(group, blobs);
            series.Add(new TrafficPoint
            {
                At = at, Requests = t.Requests, BytesIn = t.BytesIn, BytesOut = t.BytesOut, UniqueClients = t.UniqueClients,
                Status4xx = t.Status4xx, Status5xx = t.Status5xx,
            });
        }

        var totals = Sum(buckets, blobs);
        var codes = new Dictionary<int, long>();
        foreach (var b in buckets)
            foreach (var (code, count) in b.StatusCodes)
                if (int.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c)) codes[c] = codes.GetValueOrDefault(c) + count;

        return new TrafficReport
        {
            Range = query.Range,
            From = from,
            To = now,
            BucketSize = name,
            Host = host,
            Enabled = true,
            LastIngestAt = lastIngestAt,
            Totals = totals,
            Series = series,
            TopHosts = TopHosts(scale, from, end, host),
            TopClients = TopClientRows(from, end, host),
            StatusCodes = codes.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Select(kv => new StatusCount(kv.Key, kv.Value)).ToList(),
            Notes = Notes(query.Range, malformedLines, filesMissed),
        };
    }

    private readonly Dictionary<(BucketScale, DateTime, DateTime), IReadOnlyDictionary<string, TrafficBlobDoc>> _pending = new();

    /// <summary>Stored blobs of one host (null = server total) in the window, overlaid with the unsaved ones.</summary>
    private Dictionary<string, TrafficBlobDoc> Blobs(BucketScale scale, DateTime from, DateTime end, string? host)
    {
        var result = store.QueryBlobs(scale, from, end, host);
        if (pending is null) return result;
        if (!_pending.TryGetValue((scale, from, end), out var unsaved))
            _pending[(scale, from, end)] = unsaved = pending(scale, from, end);
        var h = host ?? TrafficBucketDoc.Total;
        foreach (var (id, blob) in unsaved)
            if (blob.Host == h) result[id] = blob;
        return result;
    }

    private static TrafficTotals Sum(IReadOnlyCollection<TrafficBucketDoc> buckets, IReadOnlyDictionary<string, TrafficBlobDoc> blobs)
    {
        var sketch = new ClientSketch();
        long requests = 0, bytesIn = 0, bytesOut = 0, s2 = 0, s3 = 0, s4 = 0, s5 = 0, other = 0;
        double duration = 0;
        foreach (var b in buckets)
        {
            requests += b.Requests;
            bytesIn += b.BytesIn;
            bytesOut += b.BytesOut;
            s2 += b.Status2xx;
            s3 += b.Status3xx;
            s4 += b.Status4xx;
            s5 += b.Status5xx;
            other += b.StatusOther;
            duration += b.DurationSeconds;
            if (blobs.TryGetValue(b.Id, out var blob)) sketch.Merge(ClientSketch.Deserialize(blob.Clients));
        }
        return new TrafficTotals
        {
            Requests = requests, BytesIn = bytesIn, BytesOut = bytesOut, UniqueClients = sketch.Count,
            Status2xx = s2, Status3xx = s3, Status4xx = s4, Status5xx = s5, StatusOther = other,
            AvgDurationMs = requests == 0 ? 0 : Math.Round(duration * 1000 / requests, 3),
        };
    }

    /// <summary>
    /// Ranks hosts by the small counter documents only, then reads the sketches of the (at most 20) hosts shown. Host keys
    /// are the configured names, their wildcards and "(other)", so the number of hosts is bounded by the configuration.
    /// </summary>
    private List<TrafficHostRow> TopHosts(BucketScale scale, DateTime from, DateTime end, string? host)
    {
        var top = store.QueryHosts(scale, from, end)
            .Where(b => host is null || b.Host == host)
            .GroupBy(b => b.Host)
            .Select(g => (Host: g.Key, Buckets: g.ToList(), Requests: g.Sum(b => b.Requests)))
            .OrderByDescending(x => x.Requests).ThenBy(x => x.Host, StringComparer.Ordinal)
            .Take(MaxRows)
            .ToList();
        return top.Select(x =>
        {
            var t = Sum(x.Buckets, Blobs(scale, from, end, x.Host));
            return new TrafficHostRow
            {
                Host = x.Host, Requests = t.Requests, BytesIn = t.BytesIn, BytesOut = t.BytesOut,
                UniqueClients = t.UniqueClients, Status4xx = t.Status4xx, Status5xx = t.Status5xx,
            };
        }).ToList();
    }

    /// <summary>Merges the day buckets' Space-Saving summaries of every UTC day overlapping the window.</summary>
    private List<TrafficClientRow> TopClientRows(DateTime from, DateTime end, string? host)
    {
        var dayFrom = new DateTime(from.Ticks - from.Ticks % TimeSpan.TicksPerDay, DateTimeKind.Utc);
        var merged = new TopClients();
        foreach (var day in Blobs(BucketScale.Day, dayFrom, end, host).Values.OrderBy(b => b.Start))
        {
            var counters = day.TopClients ?? [];
            merged.Merge(counters, counters.Count >= TopClients.DefaultCapacity);
        }
        return merged.Top(MaxRows).Select(c => new TrafficClientRow
        {
            Ip = c.Ip, Requests = c.Count, BytesOut = c.BytesOut, LastSeen = DateTime.SpecifyKind(c.LastSeen, DateTimeKind.Utc),
        }).ToList();
    }

    private static List<string> Notes(TrafficRange range, long malformed, long missed)
    {
        var notes = new List<string>
        {
            "Counts every HTTP request Caddy handled and logged, including redirects to HTTPS and ACME HTTP-01 challenges. " +
            "Layer-4 streams and connections rejected before an HTTP request was parsed (TLS handshake failures, malformed " +
            "requests, HTTP/2 or HTTP/3 protocol errors) are not counted.",
            "Hosts are the names configured on enabled hosts (a wildcard host is one row). Requests for any other name, " +
            "and requests without a Host, are counted under \"(other)\".",
            "Data in/out are request and response body bytes (after compression); headers and TLS overhead are not included.",
            "Unique clients are distinct client IP addresses (after trusted proxies): exact up to 1,024 per bucket, " +
            "estimated above that (typical error ±1.6 %).",
            range == TrafficRange.Month || range == TrafficRange.Week || range == TrafficRange.Day
                ? "Top clients are approximate (heavy-hitter summary of 200 clients per UTC day) and cover whole UTC days."
                : "Top clients are approximate (heavy-hitter summary of 200 clients per UTC day) and cover the whole UTC day(s) of this hour.",
            "Times are UTC buckets; the newest bucket is still filling.",
        };
        if (malformed > 0) notes.Add($"{malformed:N0} unreadable log line(s) were skipped.");
        if (missed > 0) notes.Add($"{missed:N0} rotated log file(s) were deleted before they were read; their requests are missing.");
        return notes;
    }
}
