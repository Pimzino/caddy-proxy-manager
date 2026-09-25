using System.Net;
using CaddyManager.Core;
using CaddyManager.Core.Models;

namespace CaddyManager.Ops.Events;

/// <summary>
/// HttpClient for webhook notifications and Microsoft Entra ID token requests. When an outbound proxy is configured in
/// Settings → Updates (BinarySettings.OutboundProxy) these requests use it, like the manager's other outbound calls
/// (Platform's OutboundHttp); hosts listed in the NO_PROXY setting (BinarySettings.NoProxy, e.g. internal webhook
/// receivers) and loopback addresses bypass it.
/// Without this, the "default" client uses HttpClient.DefaultProxy, which on Windows reads the proxy environment variables
/// or the WinINet settings of the service account (LocalSystem), not the proxy configured in the UI:
/// https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclient.defaultproxy
/// SMTP (MailKit) is not sent through the HTTP proxy: mail servers are reached directly (see docs/notifications.md).
/// </summary>
internal sealed class NotificationHttp(IHttpClientFactory factory, IStore? store) : IDisposable
{
    private readonly Lock _lock = new();
    private string? _key;
    private HttpClient? _proxyClient;

    /// <summary>Returns a client for the current proxy settings; do not dispose it.</summary>
    public HttpClient Client
    {
        get
        {
            BinarySettings binary;
            try { binary = store?.GetSettings<BinarySettings>() ?? new BinarySettings(); }
            catch { return factory.CreateClient("default"); }
            var proxy = binary.OutboundProxy?.Trim();
            if (string.IsNullOrEmpty(proxy)) return factory.CreateClient("default");
            var key = proxy + "\n" + binary.NoProxy;
            lock (_lock)
            {
                if (_proxyClient is not null && _key == key) return _proxyClient;
                var old = _proxyClient;
                _proxyClient = new HttpClient(new SocketsHttpHandler
                {
                    Proxy = new SettingsWebProxy(proxy, binary.NoProxy),
                    UseProxy = true,
                    AutomaticDecompression = DecompressionMethods.All,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                })
                { Timeout = TimeSpan.FromMinutes(2) };
                _proxyClient.DefaultRequestHeaders.UserAgent.ParseAdd("CaddyProxyManager/1.0");
                _key = key;
                // Let in-flight requests on the previous client finish before disposing it.
                if (old is not null) _ = Task.Delay(TimeSpan.FromMinutes(5)).ContinueWith(_ => old.Dispose(), TaskScheduler.Default);
                return _proxyClient;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock) _proxyClient?.Dispose();
    }
}

/// <summary>
/// The configured outbound proxy (http://[user:pass@]host:port) with a NO_PROXY style bypass list: "*", host names
/// (exact, or a suffix with a leading "." / "*."), IP addresses and CIDR ranges; an optional ":port" is ignored.
/// Loopback targets always bypass the proxy.
/// </summary>
internal sealed class SettingsWebProxy : IWebProxy
{
    private readonly Uri _proxy;
    private readonly List<string> _hosts = [];
    private readonly List<IPNetwork> _networks = [];
    private readonly bool _bypassAll;

    public SettingsWebProxy(string proxyUrl, string? noProxy)
    {
        if (!Uri.TryCreate(proxyUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new ArgumentException("The outbound proxy is not a valid http(s) URL (Settings → Updates).");
        _proxy = new UriBuilder(uri) { UserName = "", Password = "" }.Uri;
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            Credentials = new NetworkCredential(Uri.UnescapeDataString(parts[0]), parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
        }
        foreach (var raw in (noProxy ?? "").Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw == "*") { _bypassAll = true; continue; }
            var e = raw.StartsWith("*.", StringComparison.Ordinal) ? raw[1..] : raw;
            if (IPNetwork.TryParse(e, out var net)) { _networks.Add(net); continue; }
            if (IPAddress.TryParse(e.Trim('[', ']'), out var ip)) { _networks.Add(new IPNetwork(ip, ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128)); continue; }
            var colon = e.LastIndexOf(':');
            if (colon > 0 && e.IndexOf(':') == colon) e = e[..colon];
            _hosts.Add(e.ToLowerInvariant());
        }
    }

    public ICredentials? Credentials { get; set; }

    public Uri? GetProxy(Uri destination) => IsBypassed(destination) ? destination : _proxy;

    public bool IsBypassed(Uri host)
    {
        if (_bypassAll || host.IsLoopback) return true;
        var name = host.IdnHost.Trim('[', ']').ToLowerInvariant();
        if (IPAddress.TryParse(name, out var ip))
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            return _networks.Any(n => n.Contains(ip));
        }
        foreach (var h in _hosts)
        {
            if (h.StartsWith('.'))
            {
                if (name.EndsWith(h, StringComparison.Ordinal) || name == h[1..]) return true;
            }
            else if (name == h || name.EndsWith("." + h, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}
