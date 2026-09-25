using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace CaddyManager.Platform.Binary;

/// <summary>
/// Semantic version as used by Caddy release tags ("v2.11.4", "v2.10.0-beta.3").
/// Comparison follows SemVer 2.0 precedence (build metadata ignored, a pre-release sorts before its release).
/// </summary>
public sealed partial record CaddyVersion(int Major, int Minor, int Patch, string? PreRelease) : IComparable<CaddyVersion>
{
    [GeneratedRegex(@"^\s*v?(?<maj>\d+)\.(?<min>\d+)(?:\.(?<pat>\d+))?(?:-(?<pre>[0-9A-Za-z.\-]+))?(?:\+[0-9A-Za-z.\-]+)?\s*$")]
    private static partial Regex Pattern();

    /// <summary>
    /// The Caddy release the generated configuration and the integration tests are verified against. CI downloads exactly
    /// this release (.github/workflows/build.yml reads it from this line) and fails when `caddy version` differs. Bump it
    /// only after reading the release notes of every version in between (https://github.com/caddyserver/caddy/releases):
    /// v2.11.0, for example, changed the Host header sent to HTTPS upstreams.
    /// </summary>
    public const string Tested = "v2.11.4";

    public bool IsPreRelease => PreRelease is not null;

    /// <summary>Canonical tag form, e.g. "v2.11.4".</summary>
    public override string ToString() => $"v{Major}.{Minor}.{Patch}" + (PreRelease is null ? "" : "-" + PreRelease);

    /// <summary>Version without the "v" prefix, as used in release asset names ("2.11.4").</summary>
    public string Bare => ToString()[1..];

    public static bool TryParse(string? text, [NotNullWhen(true)] out CaddyVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = Pattern().Match(text);
        if (!m.Success) return false;
        if (!int.TryParse(m.Groups["maj"].Value, out var maj) || !int.TryParse(m.Groups["min"].Value, out var min)) return false;
        var pat = 0;
        if (m.Groups["pat"].Success && !int.TryParse(m.Groups["pat"].Value, out pat)) return false;
        version = new CaddyVersion(maj, min, pat, m.Groups["pre"].Success ? m.Groups["pre"].Value : null);
        return true;
    }

    public static CaddyVersion Parse(string text) =>
        TryParse(text, out var v) ? v : throw new FormatException($"'{text}' is not a valid Caddy version (expected e.g. v2.11.4).");

    /// <summary>Compares two version strings; unparseable versions sort lowest. Returns &lt;0, 0, &gt;0.</summary>
    public static int Compare(string? a, string? b)
    {
        var okA = TryParse(a, out var va);
        var okB = TryParse(b, out var vb);
        if (!okA && !okB) return 0;
        if (!okA) return -1;
        if (!okB) return 1;
        return va!.CompareTo(vb);
    }

    /// <summary>True when <paramref name="candidate"/> is strictly newer than <paramref name="current"/>.</summary>
    public static bool IsNewer(string? candidate, string? current) =>
        TryParse(candidate, out _) && Compare(candidate, current) > 0;

    public int CompareTo(CaddyVersion? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        if (PreRelease is null && other.PreRelease is null) return 0;
        if (PreRelease is null) return 1;           // release > pre-release
        if (other.PreRelease is null) return -1;
        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    private static int ComparePreRelease(string a, string b)
    {
        var pa = a.Split('.');
        var pb = b.Split('.');
        for (var i = 0; i < Math.Min(pa.Length, pb.Length); i++)
        {
            var na = long.TryParse(pa[i], out var ia);
            var nb = long.TryParse(pb[i], out var ib);
            int c;
            if (na && nb) c = ia.CompareTo(ib);
            else if (na) c = -1;                    // numeric identifiers have lower precedence
            else if (nb) c = 1;
            else c = string.CompareOrdinal(pa[i], pb[i]);
            if (c != 0) return c;
        }
        return pa.Length.CompareTo(pb.Length);
    }
}
