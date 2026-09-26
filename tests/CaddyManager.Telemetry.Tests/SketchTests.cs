using CaddyManager.Telemetry.Traffic;

namespace CaddyManager.Telemetry.Tests;

/// <summary>
/// Isolated tests of the unique-client sketch (exact set → HyperLogLog) and the Space-Saving top-k summary.
///
/// Ways the unique-client sketch could fail (written before the code):
///  1. Exact mode miscounts: duplicates counted twice, or the 1024th/1025th distinct client handled off by one.
///  2. The exact → HLL switch loses the clients already in the exact set (count drops) or double-inserts them.
///  3. Bias: the raw HLL estimate is badly biased in the small/intermediate range (a few thousand for p = 12) if the
///     small-range correction is missing or wrong, or at large cardinalities if the rank computation is off by one.
///  4. Poor hashing: similar inputs (sequential IPs) cluster into few registers → big underestimates (the keyed
///     HMAC-SHA256 hash must spread them like the old unkeyed hash did).
///  5. Merge is not a union: exact+exact above the limit, exact+HLL, HLL+exact and HLL+HLL must equal the sketch of all
///     inputs; merging must be idempotent (A∪A = A) and order independent.
///  6. Serialization drops state (register array truncated, exact hashes lost) or throws on an empty/corrupt blob.
///  7. Empty sketch reports non-zero.
///
/// Ways the top-k summary could fail:
///  1. A client with more than N/k requests is evicted (Space-Saving guarantee violated).
///  2. Counts under-estimate (Space-Saving counts must be ≥ true count; error must bound the over-estimate).
///  3. Capacity not enforced (unbounded memory with many distinct clients).
///  4. Merging two summaries loses a heavy hitter that is heavy only in the combination, or counts one twice.
///  5. Round trip through persistence (Snapshot/From) changes counts.
/// </summary>
public sealed class SketchTests
{
    /// <summary>
    /// A fixed key, so every run hashes the same way and the statistical assertions are repeatable (the product uses the
    /// installation's random key, see ClientHasher; any key spreads inputs uniformly).
    /// </summary>
    internal static readonly ClientHasher Hasher = ClientHasher.FromKey(TestKeys.Fixed);

    private static string Ip(int i) => $"10.{i >> 16 & 255}.{i >> 8 & 255}.{i & 255}";

    [Fact]
    public void ExactUpToLimit_ThenEstimates()
    {
        var s = new ClientSketch();
        Assert.Equal(0, s.Count);
        for (var i = 0; i < ClientSketch.ExactLimit; i++)
        {
            s.Add(Ip(i));
            s.Add(Ip(i)); // duplicates never count
        }
        Assert.True(s.IsExact);
        Assert.Equal(ClientSketch.ExactLimit, s.Count);
        s.Add(Ip(ClientSketch.ExactLimit));
        Assert.False(s.IsExact);
        Assert.InRange(s.Count, 1025 * 0.95, 1025 * 1.05);
    }

    [Theory]
    [InlineData(2_000)]
    [InlineData(5_000)]      // around the classic HLL small-range switch-over (2.5 m = 10,240) and below
    [InlineData(12_000)]
    [InlineData(50_000)]
    [InlineData(300_000)]
    public void EstimateWithinFourStandardErrors(int n)
    {
        var s = new ClientSketch();
        for (var i = 0; i < n; i++) s.Add(Ip(i));
        var error = Math.Abs(s.Count - n) / (double)n;
        Assert.True(error < 4 * 1.04 / 64, $"n={n} estimate={s.Count} error={error:P2}");
    }

    [Fact]
    public void AverageErrorOverManyIndependentSetsIsUnbiasedAndMatchesTheory()
    {
        // 40 disjoint sets of 20,000 clients: mean relative error ≈ 0 and RMS ≈ 1.04/√4096 = 1.6 %.
        var errors = new List<double>();
        for (var set = 0; set < 40; set++)
        {
            var s = new ClientSketch();
            for (var i = 0; i < 20_000; i++) s.Add($"198.{set}.{i >> 8 & 255}.{i & 255}|{i}");
            errors.Add((s.Count - 20_000) / 20_000.0);
        }
        var mean = errors.Average();
        var rms = Math.Sqrt(errors.Average(e => e * e));
        Assert.InRange(mean, -0.01, 0.01);
        Assert.InRange(rms, 0.005, 0.03);
    }

    [Fact]
    public void MergeIsUnionInEveryModeCombination()
    {
        ClientSketch Of(int from, int to)
        {
            var s = new ClientSketch();
            for (var i = from; i < to; i++) s.Add(Ip(i));
            return s;
        }

        // exact + exact staying exact
        var a = Of(0, 300);
        a.Merge(Of(200, 500));
        Assert.True(a.IsExact);
        Assert.Equal(500, a.Count);

        // exact + exact crossing the limit, compared with the sketch of all inputs
        var b = Of(0, 800);
        b.Merge(Of(600, 1500));
        Assert.Equal(Of(0, 1500).Count, b.Count);

        // HLL + exact, exact + HLL, HLL + HLL — all equal to the sketch of the union (register max is exact)
        var all = Of(0, 30_000).Count;
        var h1 = Of(0, 20_000);
        h1.Merge(Of(19_000, 19_500));
        h1.Merge(Of(10_000, 30_000));
        Assert.Equal(all, h1.Count);
        var e1 = Of(29_500, 30_000);
        e1.Merge(Of(0, 29_600));
        Assert.Equal(all, e1.Count);

        // idempotent
        var twice = Of(0, 5000);
        twice.Merge(Of(0, 5000));
        Assert.Equal(Of(0, 5000).Count, twice.Count);
    }

    [Fact]
    public void SerializationRoundTrips()
    {
        foreach (var n in new[] { 0, 1, 1024, 1025, 40_000 })
        {
            var s = new ClientSketch();
            for (var i = 0; i < n; i++) s.Add(Ip(i));
            var back = ClientSketch.Deserialize(s.Serialize());
            Assert.Equal(s.Count, back.Count);
            Assert.Equal(s.IsExact, back.IsExact);
            // still usable: adding existing members changes nothing
            for (var i = 0; i < Math.Min(n, 100); i++) back.Add(Ip(i));
            Assert.Equal(s.Count, back.Count);
        }
        Assert.Equal(0, ClientSketch.Deserialize(null).Count);
        Assert.Equal(0, ClientSketch.Deserialize([]).Count);
        Assert.Equal(0, ClientSketch.Deserialize([1, 5, 0, 0, 0, 9]).Count); // truncated exact blob
        Assert.Equal(0, ClientSketch.Deserialize([2, 1, 2, 3]).Count);       // truncated registers
    }

    [Fact]
    public void TopClientsKeepsEveryHeavyHitterAndOverestimatesWithinError()
    {
        var truth = new Dictionary<string, long>();
        var top = new TopClients(capacity: 50);
        var rnd = new Random(1234);
        var at = new DateTime(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc);
        long total = 0;
        for (var i = 0; i < 100_000; i++)
        {
            // 10 heavy clients (~40 % of the traffic) in a long tail of 20,000.
            var ip = rnd.NextDouble() < 0.4 ? $"heavy-{rnd.Next(10)}" : $"tail-{rnd.Next(20_000)}";
            truth[ip] = truth.GetValueOrDefault(ip) + 1;
            top.Add(ip, 10, at.AddSeconds(i));
            total++;
        }
        Assert.True(top.Count <= 50);
        foreach (var (ip, count) in truth.Where(kv => kv.Value > total / 50))
        {
            var c = Assert.Single(top.Counters, x => x.Ip == ip);
            Assert.True(c.Count >= count, $"{ip}: {c.Count} < true {count}");
            Assert.True(c.Count - c.Error <= count, $"{ip}: lower bound {c.Count - c.Error} > true {count}");
        }
        Assert.All(top.Top(10), c => Assert.StartsWith("heavy-", c.Ip));

        var back = TopClients.From(top.Snapshot(), 50);
        Assert.Equal(top.Top(50).Select(c => (c.Ip, c.Count, c.Error)), back.Top(50).Select(c => (c.Ip, c.Count, c.Error)));
    }

    [Fact]
    public void MergedSummariesFindClientsHeavyOnlyInCombination()
    {
        var at = new DateTime(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc);
        var day1 = new TopClients(capacity: 20);
        var day2 = new TopClients(capacity: 20);
        // "both" is 3rd on each day but 1st overall; "one-a"/"one-b" dominate a single day each.
        for (var i = 0; i < 300; i++) day1.Add("one-a", 1, at);
        for (var i = 0; i < 300; i++) day2.Add("one-b", 1, at);
        for (var i = 0; i < 200; i++) { day1.Add("both", 1, at); day2.Add("both", 1, at); }
        for (var i = 0; i < 1000; i++) { day1.Add($"x{i}", 1, at); day2.Add($"y{i}", 1, at); }
        var merged = new TopClients(capacity: 20);
        merged.Merge(day1);
        merged.Merge(day2);
        var first = merged.Top(1)[0];
        Assert.Equal("both", first.Ip);
        Assert.True(first.Count >= 400 && first.Count - first.Error <= 400);
        Assert.True(merged.Count <= 20);
        var oneA = Assert.Single(merged.Counters, c => c.Ip == "one-a");
        Assert.True(oneA.Count >= 300);
    }
}

internal static class SketchTestExtensions
{
    /// <summary>Adds a client the way the aggregator does: its keyed hash.</summary>
    public static void Add(this ClientSketch sketch, string client) => sketch.AddHash(SketchTests.Hasher.Hash(client));
}
