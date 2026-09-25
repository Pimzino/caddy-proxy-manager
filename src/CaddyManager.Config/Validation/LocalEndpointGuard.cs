using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using CaddyManager.Core.Models;

namespace CaddyManager.Config.Validation;

/// <summary>
/// Refuses upstreams / stream targets / advanced-route dials that point Caddy at the manager's own control surfaces on
/// this server: the Caddy admin API (full control over Caddy) and the manager web UI (its listener restrictions and the
/// "forwarded headers from loopback proxies" trust would be bypassed). "This server" means loopback, the unspecified
/// address, every address of a local network interface, "localhost" names and the machine's own host name.
/// Names are not resolved through DNS (saving a host must not depend on DNS latency).
/// </summary>
public sealed class LocalEndpointGuard
{
    private readonly Dictionary<int, string> _ports;
    private readonly HashSet<IPAddress> _addresses;
    private readonly HashSet<string> _names;

    public LocalEndpointGuard(IReadOnlyDictionary<int, string> protectedPorts, IEnumerable<IPAddress> localAddresses, IEnumerable<string> localNames)
    {
        _ports = new Dictionary<int, string>(protectedPorts);
        _addresses = localAddresses.Select(Canonical).ToHashSet();
        _names = localNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim().TrimEnd('.').ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Ports that must not be targeted on this server, with a description of what listens there.</summary>
    public IReadOnlyDictionary<int, string> ProtectedPorts => _ports;

    public const string AdminApiDescription = "the Caddy admin API";
    public const string UiDescription = "the Caddy Proxy Manager web UI";

    /// <summary>
    /// The guard for the current settings and this machine's addresses. The Caddy admin API is always protected;
    /// the manager UI ports only when <paramref name="includeUi"/> (non-admin callers). Administrators may publish
    /// the manager UI through Caddy (proxy host → 127.0.0.1:&lt;ui port&gt;) — the UI has its own authentication.
    /// </summary>
    public static LocalEndpointGuard Create(CaddySettings caddy, UiSettings ui, bool includeUi = true)
    {
        var (addresses, names) = LocalMachineIdentity.Get();
        return new LocalEndpointGuard(ProtectedPortsFor(caddy, ui, includeUi), addresses, names);
    }

    public static Dictionary<int, string> ProtectedPortsFor(CaddySettings caddy, UiSettings ui, bool includeUi = true)
    {
        var ports = new Dictionary<int, string>();
        if (TryParseListenPort(caddy.AdminListen, out var admin)) ports[admin] = AdminApiDescription;
        else ports[2019] = AdminApiDescription;
        if (!includeUi) return ports;
        if (NetUtil.IsValidPort(ui.Port)) ports.TryAdd(ui.Port, UiDescription + " (HTTP)");
        if (ui.HttpsEnabled && NetUtil.IsValidPort(ui.HttpsPort)) ports.TryAdd(ui.HttpsPort, UiDescription + " (HTTPS)");
        return ports;
    }

    /// <summary>Port of a "host:port" listen address (the admin API setting).</summary>
    public static bool TryParseListenPort(string? listen, out int port)
    {
        port = 0;
        if (string.IsNullOrWhiteSpace(listen)) return false;
        var v = listen.Trim();
        var slash = v.IndexOf('/');
        if (slash >= 0 && slash < v.Length - 1 && !v.StartsWith('[')) v = v[(slash + 1)..];
        var idx = v.LastIndexOf(':');
        if (idx < 0) return false;
        return int.TryParse(v[(idx + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out port) && NetUtil.IsValidPort(port);
    }

    /// <summary>True when the host name or address denotes this server.</summary>
    public bool IsLocalHost(string? host)
    {
        if (host is null) return false;
        var h = host.Trim().Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        if (h.Length == 0) return true; // ":2019" dials the local machine
        var zone = h.IndexOf('%');
        if (zone > 0) h = h[..zone];
        if (IPAddress.TryParse(h, out var ip))
        {
            ip = Canonical(ip);
            return IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || _addresses.Contains(ip);
        }
        if (h == "localhost" || h.EndsWith(".localhost", StringComparison.Ordinal)) return true;
        return _names.Contains(h);
    }

    /// <summary>A description of the problem when host:port is a protected endpoint on this server; otherwise null.</summary>
    public string? Check(string host, int port)
    {
        if (!_ports.TryGetValue(port, out var what) || !IsLocalHost(host)) return null;
        return Message(NetUtil.HostPort(host, port), what);
    }

    private static string Message(string target, string what) => what == AdminApiDescription
        ? $"'{target}' is {what} on this server. Proxying to it would let anyone who reaches the site reconfigure Caddy, so it is not allowed."
        : $"'{target}' is {what} on this server. Publishing the manager through a proxy host or stream would bypass its own listener restrictions, so it is not allowed (change the UI port or bind address under Settings > UI instead).";

    /// <summary>
    /// Checks a Caddy network address as used in "dial" ([network/]host:port[-endport]). Placeholders are treated as
    /// "could be anything": a placeholder host is assumed local and a placeholder port is assumed protected.
    /// </summary>
    public string? CheckDial(string? dial)
    {
        if (string.IsNullOrWhiteSpace(dial)) return null;
        var v = dial.Trim();
        var slash = v.IndexOf('/');
        if (slash >= 0 && !v.StartsWith('[') && !v.StartsWith('{'))
        {
            var network = v[..slash].ToLowerInvariant();
            if (network.StartsWith("unix", StringComparison.Ordinal) || network.StartsWith("fd", StringComparison.Ordinal)) return null;
            v = v[(slash + 1)..];
        }
        string host, portPart;
        if (v.StartsWith('['))
        {
            var close = v.IndexOf(']');
            if (close < 0) return null;
            host = v[1..close];
            portPart = close + 1 < v.Length && v[close + 1] == ':' ? v[(close + 2)..] : "";
        }
        else
        {
            var idx = LastColonOutsidePlaceholder(v);
            if (idx < 0) return null; // no port: Caddy rejects it anyway
            host = v[..idx];
            portPart = v[(idx + 1)..];
        }
        if (portPart.Length == 0) return null;

        var hostUnknown = host.Contains('{');
        if (!hostUnknown && !IsLocalHost(host)) return null;
        if (portPart.Contains('{'))
            return $"'{dial}' uses a placeholder port on {(hostUnknown ? "a placeholder host" : "this server")}, which could reach {AdminApiDescription} or {UiDescription}. Use a fixed port.";
        if (!TryParsePortRange(portPart, out var from, out var to)) return null;
        foreach (var (port, what) in _ports.OrderBy(p => p.Key))
        {
            if (port < from || port > to) continue;
            return hostUnknown
                ? $"'{dial}' uses a placeholder host with port {port}, which is {what} on this server. Use a fixed upstream host."
                : Message(dial, what);
        }
        return null;
    }

    private static int LastColonOutsidePlaceholder(string v)
    {
        var depth = 0;
        var last = -1;
        for (var i = 0; i < v.Length; i++)
        {
            if (v[i] == '{') depth++;
            else if (v[i] == '}') depth = Math.Max(0, depth - 1);
            else if (v[i] == ':' && depth == 0) last = i;
        }
        return last;
    }

    private static bool TryParsePortRange(string s, out int from, out int to)
    {
        from = to = 0;
        var dash = s.IndexOf('-');
        if (dash < 0)
        {
            if (!int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out from)) return false;
            to = from;
            return true;
        }
        return int.TryParse(s[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out from)
               && int.TryParse(s[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out to)
               && to >= from;
    }

    /// <summary>Every problem found in the reverse_proxy handlers of raw Caddy route JSON (nested subroutes included).</summary>
    public List<string> CheckRoutesJson(string? routesJson)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(routesJson)) return problems;
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(routesJson);
        }
        catch (JsonException)
        {
            return problems;
        }
        CheckNode(root, problems);
        return problems;
    }

    /// <summary>Walks any Caddy JSON fragment for reverse_proxy upstream dials.</summary>
    public void CheckNode(JsonNode? node, List<string> problems)
    {
        switch (node)
        {
            case JsonObject o:
                if (o["handler"] is JsonValue hv && hv.GetValueKind() == JsonValueKind.String && hv.GetValue<string>() == "reverse_proxy")
                    CheckReverseProxy(o, problems);
                foreach (var (_, child) in o) CheckNode(child, problems);
                break;
            case JsonArray a:
                foreach (var child in a) CheckNode(child, problems);
                break;
        }
    }

    private void CheckReverseProxy(JsonObject rp, List<string> problems)
    {
        if (rp["upstreams"] is JsonArray ups)
        {
            foreach (var u in ups)
            {
                if (u is JsonObject uo && uo["dial"] is JsonValue d && d.GetValueKind() == JsonValueKind.String)
                    Add(problems, CheckDial(d.GetValue<string>()));
            }
        }
        if (rp["dynamic_upstreams"] is JsonNode dyn) CheckDynamic(dyn, problems);
    }

    private void CheckDynamic(JsonNode node, List<string> problems)
    {
        switch (node)
        {
            case JsonObject o:
                if (o["source"] is JsonValue sv && sv.GetValueKind() == JsonValueKind.String && sv.GetValue<string>() == "a")
                {
                    var name = o["name"] is JsonValue nv && nv.GetValueKind() == JsonValueKind.String ? nv.GetValue<string>() : "";
                    var port = o["port"]?.ToString() ?? "";
                    if (port.Length > 0) Add(problems, CheckDial($"{(name.Contains(':') ? "[" + name + "]" : name)}:{port}"));
                }
                foreach (var (_, child) in o) if (child is not null) CheckDynamic(child, problems);
                break;
            case JsonArray a:
                foreach (var child in a) if (child is not null) CheckDynamic(child, problems);
                break;
        }
    }

    private static void Add(List<string> problems, string? p)
    {
        if (p is not null && !problems.Contains(p)) problems.Add(p);
    }

    private static IPAddress Canonical(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.ScopeId != 0) ip = new IPAddress(ip.GetAddressBytes());
        return ip;
    }
}

/// <summary>This machine's interface addresses and host names (cached for a minute).</summary>
public static class LocalMachineIdentity
{
    private static readonly object Gate = new();
    private static (DateTime At, List<IPAddress> Addresses, List<string> Names)? _cache;

    public static (IReadOnlyList<IPAddress> Addresses, IReadOnlyList<string> Names) Get()
    {
        lock (Gate)
        {
            if (_cache is { } c && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(1)) return (c.Addresses, c.Names);
            var addresses = new List<IPAddress>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    try
                    {
                        addresses.AddRange(nic.GetIPProperties().UnicastAddresses.Select(a => a.Address));
                    }
                    catch (NetworkInformationException)
                    {
                    }
                }
            }
            catch (NetworkInformationException)
            {
            }
            catch (PlatformNotSupportedException)
            {
            }

            var names = new List<string> { "localhost" };
            try
            {
                var host = Dns.GetHostName();
                if (!string.IsNullOrWhiteSpace(host)) names.Add(host);
                names.Add(Environment.MachineName);
                var domain = IPGlobalProperties.GetIPGlobalProperties().DomainName;
                if (!string.IsNullOrWhiteSpace(domain) && !string.IsNullOrWhiteSpace(host) && !host.Contains('.'))
                    names.Add($"{host}.{domain}");
            }
            catch (Exception ex) when (ex is SocketException or NetworkInformationException or PlatformNotSupportedException or InvalidOperationException)
            {
            }
            _cache = (DateTime.UtcNow, addresses, names);
            return (addresses, names);
        }
    }
}
