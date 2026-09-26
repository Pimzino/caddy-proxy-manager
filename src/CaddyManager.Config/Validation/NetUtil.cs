using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace CaddyManager.Config.Validation;

/// <summary>Host name / address helpers shared by validation and config generation.</summary>
public static partial class NetUtil
{
    private static readonly IdnMapping Idn = new();

    [GeneratedRegex("^(?!-)[a-z0-9-]{1,63}(?<!-)$", RegexOptions.CultureInvariant)]
    private static partial Regex LabelRegex();

    // RFC 7230 token characters minus '*': Caddy's header "delete" treats '*' as a wildcard ("*" deletes every header).
    [GeneratedRegex("^[A-Za-z0-9!#$%&'+.^_`|~-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderNameRegex();

    // Canonical dotted-quad IPv4 (no leading zeros, no shorthand, no octal/hex parts).
    [GeneratedRegex(@"^(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])(\.(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])){3}$", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv4Regex();

    /// <summary>
    /// Parses an IP address the way Caddy (Go's netip.ParseAddr) does: canonical dotted-quad IPv4 or IPv6 text only.
    /// .NET's IPAddress.TryParse also accepts "10" (0.0.0.10), "10.1", "010.1.1.1" (octal: 8.1.1.1) and "0x0a.0.0.1",
    /// which Caddy rejects (the whole config load fails) or which silently mean a different address.
    /// https://github.com/caddyserver/caddy/blob/v2.11.4/modules/caddyhttp/ip_range.go ; https://pkg.go.dev/net/netip#ParseAddr
    /// </summary>
    public static bool TryParseIp(string? value, out IPAddress ip)
    {
        ip = IPAddress.None;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var v = value.Trim();
        if (v.Contains(':'))
        {
            if (!IPAddress.TryParse(v, out var v6) || v6.AddressFamily != AddressFamily.InterNetworkV6) return false;
            // An embedded IPv4 part (::ffff:10.0.0.1) must be canonical too.
            var lastColon = v.LastIndexOf(':');
            var tail = v[(lastColon + 1)..];
            var pct = tail.IndexOf('%');
            if (pct >= 0) tail = tail[..pct];
            if (tail.Contains('.') && !Ipv4Regex().IsMatch(tail)) return false;
            ip = v6;
            return true;
        }
        if (!Ipv4Regex().IsMatch(v) || !IPAddress.TryParse(v, out var v4)) return false;
        ip = v4;
        return true;
    }

    /// <summary>True for any text .NET would read as an IPv4 address although Caddy would not (e.g. "10", "010.1.1.1").</summary>
    private static bool IsNonCanonicalIpv4(string v) =>
        !v.Contains(':') && IPAddress.TryParse(v, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork && !Ipv4Regex().IsMatch(v);

    /// <summary>Lower-cases, trims, removes a trailing dot and converts IDN names to punycode. Null when empty.</summary>
    public static string? NormalizeDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return null;
        var d = domain.Trim().TrimEnd('.').ToLowerInvariant();
        if (d.Length == 0) return null;
        if (TryParseIp(d.Trim('[', ']'), out var ip)) return ip.ToString();
        try
        {
            var wildcard = d.StartsWith("*.", StringComparison.Ordinal);
            var rest = wildcard ? d[2..] : d;
            if (rest.Any(c => c > 127)) rest = Idn.GetAscii(rest);
            return wildcard ? "*." + rest : rest;
        }
        catch (ArgumentException)
        {
            return d;
        }
    }

    /// <summary>Valid DNS host name (optionally with a leading "*." wildcard label) or IP address.</summary>
    public static bool IsValidDomain(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain) || domain.Length > 253) return false;
        if (TryParseIp(domain, out _)) return true;
        var d = domain.StartsWith("*.", StringComparison.Ordinal) ? domain[2..] : domain;
        if (d.Length == 0) return false;
        var labels = d.Split('.');
        // An all-numeric last label is not a host name (RFC 3696 §2); it is a mistyped IP such as "10.1" or "010.1.1.1".
        if (labels[^1].All(char.IsAsciiDigit) || IsNonCanonicalIpv4(d)) return false;
        return labels.All(l => LabelRegex().IsMatch(l));
    }

    /// <summary>Valid upstream host: DNS name or IP address (no scheme, no port, no wildcard).</summary>
    public static bool IsValidHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var h = host.Trim();
        if (TryParseIp(h.Trim('[', ']'), out _)) return true;
        if (h.Contains('*')) return false;
        return IsValidDomain(h.ToLowerInvariant().TrimEnd('.'));
    }

    public static bool IsValidPort(int port) => port is >= 1 and <= 65535;

    /// <summary>host:port with IPv6 addresses bracketed.</summary>
    public static string HostPort(string host, int port)
    {
        var h = host.Trim().Trim('[', ']');
        return TryParseIp(h, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{h}]:{port}"
            : $"{h}:{port}";
    }

    /// <summary>IP address or CIDR (v4/v6), or the keyword "all".</summary>
    public static bool IsValidCidr(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var v = value.Trim();
        if (v.Equals("all", StringComparison.OrdinalIgnoreCase)) return true;
        return NormalizeCidr(v) is not null;
    }

    /// <summary>
    /// The canonical text of an IP or CIDR as Caddy parses it (netip.ParseAddr / netip.ParsePrefix), or null when Caddy
    /// would reject it. Host bits may be set (10.1.2.3/8), as Caddy accepts them; zones are not allowed in a prefix.
    /// </summary>
    public static string? NormalizeCidr(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        if (v.Any(char.IsWhiteSpace)) return null; // "1.2.3.4 /8": Go's ParsePrefix rejects inner spaces
        var slash = v.IndexOf('/');
        if (slash < 0) return TryParseIp(v, out var single) ? single.ToString() : null;
        if (slash == 0 || !TryParseIp(v[..slash], out var ip) || v[..slash].Contains('%')) return null;
        var bitsText = v[(slash + 1)..];
        if (bitsText.Length is 0 or > 3 || (bitsText.Length > 1 && bitsText[0] == '0')) return null;
        if (!int.TryParse(bitsText, NumberStyles.None, CultureInfo.InvariantCulture, out var bits)) return null;
        var max = ip.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        return bits <= max ? $"{ip}/{bits}" : null;
    }

    /// <summary>Caddy remote_ip/client_ip ranges for a rule value ("all" → both address families), in canonical form.</summary>
    public static IEnumerable<string> ExpandCidr(string value)
    {
        var v = value.Trim();
        if (v.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            yield return "0.0.0.0/0";
            yield return "::/0";
            yield break;
        }
        yield return NormalizeCidr(v) ?? v;
    }

    /// <summary>An address a listener can bind to (IP only).</summary>
    public static bool IsValidBindAddress(string? value) =>
        !string.IsNullOrWhiteSpace(value) && TryParseIp(value.Trim().Trim('[', ']'), out _);

    /// <summary>host:port for listen addresses (IPv6 bracketed); empty host = all interfaces.</summary>
    public static string ListenAddress(string? bindAddress, int port) =>
        string.IsNullOrWhiteSpace(bindAddress) ? $":{port}" : HostPort(bindAddress, port);

    public static bool IsValidHeaderName(string? name) => !string.IsNullOrWhiteSpace(name) && HeaderNameRegex().IsMatch(name);

    /// <summary>Absolute http(s) URL.</summary>
    public static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && !string.IsNullOrEmpty(u.Host);

    /// <summary>Loopback admin listen address "host:port" (127.0.0.1, [::1] or localhost).</summary>
    public static bool IsLoopbackListen(string? listen, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(listen)) { error = "Admin listen address is required."; return false; }
        var v = listen.Trim();
        var idx = v.LastIndexOf(':');
        if (idx <= 0 || idx == v.Length - 1) { error = "Use host:port, e.g. 127.0.0.1:2019."; return false; }
        var host = v[..idx].Trim('[', ']');
        if (!int.TryParse(v[(idx + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port) || !IsValidPort(port))
        {
            error = "Port must be 1-65535."; return false;
        }
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(host, out var ip)) { error = "Host must be an IP address or localhost."; return false; }
        if (!IPAddress.IsLoopback(ip)) { error = "The Caddy admin API must stay on a loopback address (127.0.0.1 or ::1)."; return false; }
        return true;
    }

    /// <summary>File-name safe version of a domain (wildcards become "wildcard").</summary>
    public static string SafeFileName(string domain)
    {
        var name = domain.Replace("*", "wildcard", StringComparison.Ordinal);
        foreach (var c in Path.GetInvalidFileNameChars().Concat([':', '\\', '/', '?', '"', '<', '>', '|']))
            name = name.Replace(c, '_');
        return name;
    }
}
