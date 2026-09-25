namespace CaddyManager.Telemetry.Traffic;

/// <summary>One monitored client of a <see cref="TopClients"/> summary (persisted in day buckets).</summary>
public sealed class TopClientCounter
{
    public string Ip { get; set; } = "";
    /// <summary>Estimated requests (an over-estimate by at most <see cref="Error"/>).</summary>
    public long Count { get; set; }
    /// <summary>Maximum over-estimation of <see cref="Count"/>.</summary>
    public long Error { get; set; }
    /// <summary>Response bytes counted while this client was monitored (a lower bound).</summary>
    public long BytesOut { get; set; }
    public DateTime LastSeen { get; set; }
}

/// <summary>
/// Space-Saving heavy hitters (Metwally, Agrawal, El Abbadi 2005): at most <see cref="Capacity"/> counters; an unmonitored
/// client replaces the counter with the smallest count and inherits it as its error. Every client with more than N/k
/// requests is guaranteed to be monitored. Merging (Agarwal et al., "Mergeable summaries", 2012): counts add; a client
/// missing from a full summary could have had up to that summary's minimum, which is added to count and error.
/// </summary>
internal sealed class TopClients
{
    public const int DefaultCapacity = 200;

    private readonly Dictionary<string, TopClientCounter> _counters = new(StringComparer.Ordinal);

    public TopClients(int capacity = DefaultCapacity) => Capacity = capacity;

    public int Capacity { get; }
    public int Count => _counters.Count;
    public IEnumerable<TopClientCounter> Counters => _counters.Values;

    public void Add(string ip, long bytesOut, DateTime at)
    {
        if (_counters.TryGetValue(ip, out var c))
        {
            c.Count++;
            c.BytesOut += bytesOut;
            if (at > c.LastSeen) c.LastSeen = at;
            return;
        }
        if (_counters.Count < Capacity)
        {
            _counters[ip] = new TopClientCounter { Ip = ip, Count = 1, BytesOut = bytesOut, LastSeen = at };
            return;
        }
        var min = MinCounter();
        _counters.Remove(min.Ip);
        _counters[ip] = new TopClientCounter { Ip = ip, Count = min.Count + 1, Error = min.Count, BytesOut = bytesOut, LastSeen = at };
    }

    /// <summary>Merges another summary into this one and trims back to <see cref="Capacity"/>.</summary>
    public void Merge(TopClients other) => Merge(other._counters.Values, other.Count >= other.Capacity);

    public void Merge(IEnumerable<TopClientCounter> otherCounters, bool otherIsFull)
    {
        var other = otherCounters.ToDictionary(c => c.Ip, StringComparer.Ordinal);
        long myMin = _counters.Count >= Capacity ? MinCounter().Count : 0;
        long otherMin = otherIsFull && other.Count > 0 ? other.Values.Min(c => c.Count) : 0;
        var merged = new Dictionary<string, TopClientCounter>(StringComparer.Ordinal);
        foreach (var c in _counters.Values)
        {
            var o = other.GetValueOrDefault(c.Ip);
            merged[c.Ip] = new TopClientCounter
            {
                Ip = c.Ip,
                Count = c.Count + (o?.Count ?? otherMin),
                Error = c.Error + (o?.Error ?? otherMin),
                BytesOut = c.BytesOut + (o?.BytesOut ?? 0),
                LastSeen = o is not null && o.LastSeen > c.LastSeen ? o.LastSeen : c.LastSeen,
            };
        }
        foreach (var o in other.Values)
        {
            if (merged.ContainsKey(o.Ip)) continue;
            merged[o.Ip] = new TopClientCounter
            {
                Ip = o.Ip,
                Count = o.Count + myMin,
                Error = o.Error + myMin,
                BytesOut = o.BytesOut,
                LastSeen = o.LastSeen,
            };
        }
        _counters.Clear();
        foreach (var c in merged.Values.OrderByDescending(c => c.Count).ThenBy(c => c.Ip, StringComparer.Ordinal).Take(Capacity))
            _counters[c.Ip] = c;
    }

    /// <summary>Counters by descending count.</summary>
    public List<TopClientCounter> Top(int n) =>
        _counters.Values.OrderByDescending(c => c.Count).ThenByDescending(c => c.BytesOut).ThenBy(c => c.Ip, StringComparer.Ordinal).Take(n).ToList();

    public List<TopClientCounter> Snapshot() => _counters.Values.Select(c => new TopClientCounter
    {
        Ip = c.Ip, Count = c.Count, Error = c.Error, BytesOut = c.BytesOut, LastSeen = c.LastSeen,
    }).ToList();

    public static TopClients From(IEnumerable<TopClientCounter>? counters, int capacity = DefaultCapacity)
    {
        var t = new TopClients(capacity);
        if (counters is null) return t;
        foreach (var c in counters.OrderByDescending(c => c.Count).Take(capacity))
            t._counters[c.Ip] = new TopClientCounter { Ip = c.Ip, Count = c.Count, Error = c.Error, BytesOut = c.BytesOut, LastSeen = c.LastSeen };
        return t;
    }

    private TopClientCounter MinCounter()
    {
        TopClientCounter? min = null;
        foreach (var c in _counters.Values)
            if (min is null || c.Count < min.Count) min = c;
        return min!;
    }
}
