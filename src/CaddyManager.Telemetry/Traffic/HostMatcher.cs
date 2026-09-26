using System.Globalization;
using System.Net;
using CaddyManager.Core.Models;

namespace CaddyManager.Telemetry.Traffic;

/// <summary>
/// Maps a request's Host (normalised by <see cref="AccessLogParser.NormalizeHost"/>) to the host key its statistics are
/// kept under. Only names configured on enabled hosts get their own buckets — the Host header is chosen by the client, so
/// keying on it directly lets anyone create unlimited buckets (and one over-long Host used to break saving altogether):
/// <list type="bullet">
/// <item>an exact configured name → that name (IPv6 literals in brackets, e.g. "[::1]");</item>
/// <item>a name matched by a configured wildcard → the wildcard ("*.example.com"), so random sub-domains stay one bucket;</item>
/// <item>everything else — unknown names, IP addresses nobody configured, requests without a Host — → <see cref="Other"/>.</item>
/// </list>
/// Matching follows Caddy v2.11.4's host matcher (modules/caddyhttp/matchers.go MatchHost.MatchWithError): case-insensitive
/// comparison of the Host without port and brackets; "*" matches exactly one label, so "*.example.com" matches
/// "a.example.com" but neither "example.com" nor "a.b.example.com". The configured domains are normalised the way the
/// Config module stores them (lower case, no trailing dot, IDN → punycode, canonical IP text). Keys are at most
/// <see cref="MaxHostLength"/> characters, far below LiteDB's 1,023-byte index key limit.
/// </summary>
internal sealed class HostMatcher
{
    /// <summary>Host key of every request whose Host is not a configured name.</summary>
    public const string Other = "(other)";
    /// <summary>A DNS name has at most 253 characters (+2 for the brackets of an IPv6 literal).</summary>
    public const int MaxHostLength = 255;

    private readonly HashSet<string> _exact = new(StringComparer.Ordinal);
    /// <summary>"example.com" for the configured wildcard "*.example.com".</summary>
    private readonly HashSet<string> _wildcardParents = new(StringComparer.Ordinal);

    public static readonly HostMatcher Empty = new([]);

    public HostMatcher(IEnumerable<string> configuredDomains)
    {
        foreach (var raw in configuredDomains)
        {
            if (Normalize(raw) is not { } d) continue;
            if (d.StartsWith("*.", StringComparison.Ordinal)) _wildcardParents.Add(d[2..]);
            else _exact.Add(d);
        }
    }

    /// <summary>Domains of the enabled hosts (what the generated Caddy configuration serves).</summary>
    public static HostMatcher FromHosts(IEnumerable<SiteHost> hosts) =>
        new(hosts.Where(h => h.Enabled).SelectMany(h => h.Domains ?? []));

    public int ConfiguredNames => _exact.Count + _wildcardParents.Count;

    /// <summary>The bucket key for a normalised request host ("" = the request had no Host).</summary>
    public string Resolve(string host)
    {
        if (host.Length == 0 || host.Length > MaxHostLength) return Other;
        var bare = host.Length > 2 && host[0] == '[' && host[^1] == ']' ? host[1..^1] : host;
        if (_exact.Contains(bare)) return host;
        var dot = bare.IndexOf('.');
        if (dot > 0 && dot < bare.Length - 1 && _wildcardParents.Contains(bare[(dot + 1)..])) return "*." + bare[(dot + 1)..];
        return Other;
    }

    /// <summary>
    /// For report filters: the key a host name is kept under — a configured name or wildcard itself, the wildcard a name
    /// falls under, or <see cref="Other"/>. Unknown names are returned unchanged (they may have old statistics).
    /// </summary>
    public string ResolveFilter(string host)
    {
        if (host == Other || host.Length > MaxHostLength) return host;
        if (host.StartsWith("*.", StringComparison.Ordinal)) return host;
        var key = Resolve(host);
        return key == Other ? host : key;
    }

    /// <summary>Configured domain → normalised form, or null when it is not a usable host name.</summary>
    internal static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var d = raw.Trim().TrimEnd('.').ToLowerInvariant();
        if (d.StartsWith('[') && d.EndsWith(']')) d = d[1..^1];
        if (d.Length == 0 || d.Length > 253) return null;
        if (IPAddress.TryParse(d, out var ip) && (d.Contains(':') || d.Count(c => c == '.') == 3)) return ip.ToString();
        var wildcard = d.StartsWith("*.", StringComparison.Ordinal);
        var rest = wildcard ? d[2..] : d;
        if (rest.Length == 0) return null;
        if (rest.Any(c => c > 127))
        {
            try { rest = new IdnMapping().GetAscii(rest).ToLowerInvariant(); }
            catch (ArgumentException) { return null; }
        }
        // Letters, digits, '-', '_' and dots between non-empty labels; no further '*' (Caddy only matches whole labels).
        if (rest.Split('.').Any(l => l.Length == 0 || l.Length > 63 || !l.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')))
            return null;
        var result = wildcard ? "*." + rest : rest;
        return result.Length <= 253 ? result : null;
    }
}
