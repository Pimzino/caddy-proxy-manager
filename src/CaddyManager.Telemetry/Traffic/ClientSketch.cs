using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace CaddyManager.Telemetry.Traffic;

/// <summary>
/// Distinct-client counter of one bucket: an exact set of 64-bit client hashes up to <see cref="ExactLimit"/> entries,
/// then a HyperLogLog sketch (p = 12, 4096 registers, standard error 1.04/√4096 ≈ 1.6 %). Sketches are mergeable (union),
/// so unique clients over a range = cardinality of the merged bucket sketches. Only hashes are kept — never IPs.
/// </summary>
internal sealed class ClientSketch
{
    public const int ExactLimit = 1024;
    public const int Precision = 12;
    public const int Registers = 1 << Precision;
    /// <summary>Largest register value: q + 1 with q = 64 − p hash bits left after the index.</summary>
    private const int MaxRank = 64 - Precision + 1;

    private HashSet<ulong>? _exact = new();
    private byte[]? _registers;

    public bool IsExact => _exact is not null;

    public void Add(string client) => AddHash(Hash(client));

    public void AddHash(ulong hash)
    {
        if (_exact is not null)
        {
            if (!_exact.Add(hash) || _exact.Count <= ExactLimit) return;
            ToHll();
            return;
        }
        AddToRegisters(_registers!, hash);
    }

    /// <summary>Union with another sketch (this instance changes).</summary>
    public void Merge(ClientSketch other)
    {
        if (other._exact is not null)
        {
            foreach (var h in other._exact) AddHash(h);
            return;
        }
        if (_exact is not null) ToHll();
        var mine = _registers!;
        var theirs = other._registers!;
        for (var i = 0; i < Registers; i++)
            if (theirs[i] > mine[i]) mine[i] = theirs[i];
    }

    public long Count => _exact?.Count ?? Estimate(_registers!);

    private void ToHll()
    {
        var regs = new byte[Registers];
        foreach (var h in _exact!) AddToRegisters(regs, h);
        _registers = regs;
        _exact = null;
    }

    private static void AddToRegisters(byte[] regs, ulong hash)
    {
        var index = (int)(hash >> (64 - Precision));
        // Rank = position of the first 1-bit in the remaining 52 bits (1-based); all zero → q + 1.
        var rest = hash << Precision;
        var rank = rest == 0 ? MaxRank : BitOperations.LeadingZeroCount(rest) + 1;
        if (rank > regs[index]) regs[index] = (byte)rank;
    }

    /// <summary>
    /// Ertl's improved raw estimator ("New cardinality estimation algorithms for HyperLogLog sketches", 2017,
    /// https://arxiv.org/abs/1702.01284, Algorithm 6 — also what Redis uses): unbiased over the whole range without the
    /// empirical bias tables of HLL++ and without a switch-over to linear counting. It contains the small-range
    /// correction (σ term over empty registers) and the large-range correction (τ term over saturated registers).
    /// </summary>
    internal static long Estimate(byte[] regs)
    {
        Span<int> histogram = stackalloc int[MaxRank + 1];
        foreach (var r in regs) histogram[r]++;
        const double m = Registers;
        var z = m * Tau(1 - histogram[MaxRank] / m);
        for (var k = MaxRank - 1; k >= 1; k--) z = 0.5 * (z + histogram[k]);
        z += m * Sigma(histogram[0] / m);
        if (double.IsInfinity(z)) return 0; // every register empty
        const double alphaInf = 0.721347520444481703680; // 1 / (2 ln 2)
        return (long)Math.Round(alphaInf * m * m / z);
    }

    private static double Sigma(double x)
    {
        if (x == 1) return double.PositiveInfinity;
        double y = 1, z = x, previous;
        do
        {
            x *= x;
            previous = z;
            z += x * y;
            y += y;
        } while (previous != z);
        return z;
    }

    private static double Tau(double x)
    {
        if (x == 0 || x == 1) return 0;
        double y = 1, z = 1 - x, previous;
        do
        {
            x = Math.Sqrt(x);
            previous = z;
            y *= 0.5;
            z -= (1 - x) * (1 - x) * y;
        } while (previous != z);
        return z / 3;
    }

    // ------------------------------------------------------------------ hashing

    /// <summary>
    /// 64-bit hash of a client address: FNV-1a over the UTF-8 bytes, then the MurmurHash3 fmix64 finaliser so every
    /// input bit affects every output bit (HyperLogLog needs uniformly distributed high bits).
    /// </summary>
    public static ulong Hash(string client)
    {
        Span<byte> buffer = stackalloc byte[128];
        var bytes = Encoding.UTF8.GetByteCount(client) <= buffer.Length
            ? buffer[..Encoding.UTF8.GetBytes(client, buffer)]
            : Encoding.UTF8.GetBytes(client);
        var h = 14695981039346656037UL;
        foreach (var b in bytes)
        {
            h ^= b;
            h *= 1099511628211UL;
        }
        h ^= h >> 33;
        h *= 0xff51afd7ed558ccdUL;
        h ^= h >> 33;
        h *= 0xc4ceb9fe1a85ec53UL;
        h ^= h >> 33;
        return h;
    }

    // ------------------------------------------------------------------ persistence
    // Format: byte 1 = exact, then int32 count and count × uint64 hashes; byte 2 = HyperLogLog, then 4096 registers.

    public byte[] Serialize()
    {
        if (_exact is not null)
        {
            var buf = new byte[5 + _exact.Count * 8];
            buf[0] = 1;
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(1), _exact.Count);
            var o = 5;
            foreach (var h in _exact)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(o), h);
                o += 8;
            }
            return buf;
        }
        var hll = new byte[1 + Registers];
        hll[0] = 2;
        _registers!.CopyTo(hll, 1);
        return hll;
    }

    /// <summary>Restores a sketch; an empty or unreadable blob yields an empty sketch.</summary>
    public static ClientSketch Deserialize(byte[]? data)
    {
        var s = new ClientSketch();
        if (data is null || data.Length == 0) return s;
        if (data[0] == 2 && data.Length == 1 + Registers)
        {
            s._exact = null;
            s._registers = data.AsSpan(1).ToArray();
            return s;
        }
        if (data[0] == 1 && data.Length >= 5)
        {
            var count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(1));
            if (count < 0 || data.Length != 5 + (long)count * 8) return s;
            for (var i = 0; i < count; i++) s.AddHash(BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(5 + i * 8)));
        }
        return s;
    }
}
