using System.Globalization;
using CaddyManager.Core.Contracts;

namespace CaddyManager.Telemetry.Traffic;

/// <summary>
/// Builds <see cref="TrafficReport"/>s from the stored buckets (SPEC "Reports"): hour → 60 minute points, day → 24 hourly,
/// week → 168 hourly, month → 30 daily; oldest first, zero-filled. The window ends with the bucket that contains "now"
/// (so the newest point is still filling). Unique clients over the window = cardinality of the union of the bucket sketches.
/// </summary>
internal sealed class TrafficReports(TrafficStore store)
{
    public const int MaxRows = 20;

    public static (BucketScale Scale, TimeSpan Step, int Points, string Name) Shape(TrafficRange range) => range switch
    {
        TrafficRange.Hour => (BucketScale.Minute, TimeSpan.FromMinutes(1), 60, "minute"),
        TrafficRange.Week => (BucketScale.Hour, TimeSpan.FromHours(1), 168, "hour"),
        TrafficRange.Month => (BucketScale.Day, TimeSpan.FromDays(1), 30, "day"),
        _ => (BucketScale.Hour, TimeSpan.FromHours(1), 24, "hour"),
    };

    public TrafficReport Build(TrafficQuery query, DateTime now, bool enabled, DateTime? lastIngestAt, long malformedLines, long filesMissed)
    {
        var (scale, step, points, name) = Shape(query.Range);
        var host = string.IsNullOrWhiteSpace(query.Host) ? null : AccessLogParser.NormalizeHost(query.Host);
        var end = new DateTime(now.Ticks - now.Ticks % step.Ticks, DateTimeKind.Utc) + step;
        var from = end - step * points;

        if (!enabled)
        {
            return new TrafficReport
            {
                Range = query.Range, From = from, To = now, BucketSize = name, Host = host, Enabled = false, LastIngestAt = lastIngestAt,
                Notes = ["Traffic statistics are turned off (Settings > Caddy). Caddy does not write the statistics log while they are off."],
            };
        }

        var buckets = store.Query(scale, from, end, host);
        var byStart = buckets.ToDictionary(b => b.Start);
        var series = new List<TrafficPoint>(points);
        for (var i = 0; i < points; i++)
        {
            var at = from + step * i;
            series.Add(byStart.TryGetValue(at, out var b)
                ? new TrafficPoint
                {
                    At = at, Requests = b.Requests, BytesIn = b.BytesIn, BytesOut = b.BytesOut,
                    UniqueClients = ClientSketch.Deserialize(b.Clients).Count, Status4xx = b.Status4xx, Status5xx = b.Status5xx,
                }
                : new TrafficPoint { At = at });
        }

        var totals = Sum(buckets);
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

    private static TrafficTotals Sum(IReadOnlyCollection<TrafficBucketDoc> buckets)
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
            sketch.Merge(ClientSketch.Deserialize(b.Clients));
        }
        return new TrafficTotals
        {
            Requests = requests, BytesIn = bytesIn, BytesOut = bytesOut, UniqueClients = sketch.Count,
            Status2xx = s2, Status3xx = s3, Status4xx = s4, Status5xx = s5, StatusOther = other,
            AvgDurationMs = requests == 0 ? 0 : Math.Round(duration * 1000 / requests, 3),
        };
    }

    private List<TrafficHostRow> TopHosts(BucketScale scale, DateTime from, DateTime end, string? host)
    {
        var rows = store.QueryHosts(scale, from, end)
            .Where(b => host is null || b.Host == host)
            .GroupBy(b => b.Host)
            .Select(g =>
            {
                var t = Sum(g.ToList());
                return new TrafficHostRow
                {
                    Host = g.Key, Requests = t.Requests, BytesIn = t.BytesIn, BytesOut = t.BytesOut,
                    UniqueClients = t.UniqueClients, Status4xx = t.Status4xx, Status5xx = t.Status5xx,
                };
            });
        return rows.OrderByDescending(r => r.Requests).ThenBy(r => r.Host, StringComparer.Ordinal).Take(MaxRows).ToList();
    }

    /// <summary>Merges the day buckets' Space-Saving summaries of every UTC day overlapping the window.</summary>
    private List<TrafficClientRow> TopClientRows(DateTime from, DateTime end, string? host)
    {
        var dayFrom = new DateTime(from.Ticks - from.Ticks % TimeSpan.TicksPerDay, DateTimeKind.Utc);
        var merged = new TopClients();
        foreach (var day in store.Query(BucketScale.Day, dayFrom, end, host))
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
