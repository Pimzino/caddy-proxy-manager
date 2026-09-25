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

    [GeneratedRegex("^[A-Za-z0-9!#$%&'*+.^_`|~-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderNameRegex();

    /// <summary>Lower-cases, trims, removes a trailing dot and converts IDN names to punycode. Null when empty.</summary>
    public static string? NormalizeDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return null;
        var d = domain.Trim().TrimEnd('.').ToLowerInvariant();
        if (d.Length == 0) return null;
        if (IPAddress.TryParse(d.Trim('[', ']'), out var ip)) return ip.ToString();
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
        if (IPAddress.TryParse(domain, out _)) return true;
        var d = domain.StartsWith("*.", StringComparison.Ordinal) ? domain[2..] : domain;
        if (d.Length == 0) return false;
        var labels = d.Split('.');
        return labels.All(l => LabelRegex().IsMatch(l));
    }

    /// <summary>Valid upstream host: DNS name or IP address (no scheme, no port, no wildcard).</summary>
    public static bool IsValidHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var h = host.Trim();
        if (IPAddress.TryParse(h.Trim('[', ']'), out _)) return true;
        if (h.Contains('*')) return false;
        return IsValidDomain(h.ToLowerInvariant().TrimEnd('.'));
    }

    public static bool IsValidPort(int port) => port is >= 1 and <= 65535;

    /// <summary>host:port with IPv6 addresses bracketed.</summary>
    public static string HostPort(string host, int port)
    {
        var h = host.Trim().Trim('[', ']');
        return IPAddress.TryParse(h, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{h}]:{port}"
            : $"{h}:{port}";
    }

    /// <summary>IP address or CIDR (v4/v6), or the keyword "all".</summary>
    public static bool IsValidCidr(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var v = value.Trim();
        if (v.Equals("all", StringComparison.OrdinalIgnoreCase)) return true;
        if (IPAddress.TryParse(v, out _) && !v.Contains('/')) return true;
        return IPNetwork.TryParse(v, out _) || TryParseLooseCidr(v);
    }

    /// <summary>Accepts CIDRs with host bits set (e.g. 10.1.2.3/8) which IPNetwork rejects but Caddy accepts.</summary>
    private static bool TryParseLooseCidr(string v)
    {
        var slash = v.IndexOf('/');
        if (slash <= 0) return false;
        if (!IPAddress.TryParse(v[..slash], out var ip)) return false;
        if (!int.TryParse(v[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var bits)) return false;
        var max = ip.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        return bits >= 0 && bits <= max;
    }

    /// <summary>Caddy remote_ip/client_ip ranges for a rule value ("all" → both address families).</summary>
    public static IEnumerable<string> ExpandCidr(string value)
    {
        var v = value.Trim();
        if (v.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            yield return "0.0.0.0/0";
            yield return "::/0";
            yield break;
        }
        yield return v;
    }

    /// <summary>An address a listener can bind to (IP only).</summary>
    public static bool IsValidBindAddress(string? value) =>
        !string.IsNullOrWhiteSpace(value) && IPAddress.TryParse(value.Trim().Trim('[', ']'), out _);

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
