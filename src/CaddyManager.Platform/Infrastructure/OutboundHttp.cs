using System.Net;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Platform.Infrastructure;

/// <summary>
/// HttpClient for outbound Internet calls (GitHub, caddyserver.com, Let's Encrypt).
/// Uses the "default" named client unless BinarySettings.OutboundProxy is set, in which case a
/// dedicated client with that proxy (credentials may be given as http://user:pass@proxy:8080) is used.
/// </summary>
public sealed class OutboundHttp(IHttpClientFactory factory, IStore store, ILogger<OutboundHttp> logger) : IDisposable
{
    public const string UserAgent = "CaddyProxyManager/1.0";
    private readonly Lock _lock = new();
    private string? _proxyKey;
    private HttpClient? _proxyClient;

    /// <summary>Returns a client; do not dispose it. Per-call timeouts should be applied with a CancellationToken.</summary>
    public HttpClient Client
    {
        get
        {
            var proxy = store.GetSettings<BinarySettings>().OutboundProxy?.Trim();
            if (string.IsNullOrEmpty(proxy)) return factory.CreateClient("default");
            lock (_lock)
            {
                if (_proxyClient is not null && _proxyKey == proxy) return _proxyClient;
                var old = _proxyClient;
                _proxyClient = CreateProxyClient(proxy);
                _proxyKey = proxy;
                // Allow in-flight requests on the previous client to finish before disposing it.
                if (old is not null) _ = Task.Delay(TimeSpan.FromMinutes(15)).ContinueWith(_ => old.Dispose(), TaskScheduler.Default);
                logger.LogInformation("Outbound HTTP requests use proxy {Proxy}", RedactProxy(proxy));
                return _proxyClient;
            }
        }
    }

    public static HttpClient CreateProxyClient(string proxyUrl)
    {
        var handler = new SocketsHttpHandler
        {
            Proxy = CreateProxy(proxyUrl),
            UseProxy = true,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    public static WebProxy CreateProxy(string proxyUrl)
    {
        if (!Uri.TryCreate(proxyUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new ArgumentException($"Outbound proxy '{RedactProxy(proxyUrl)}' is not a valid http(s) URL (e.g. http://proxy.corp.local:8080).");
        var proxy = new WebProxy(new UriBuilder(uri) { UserName = "", Password = "" }.Uri) { BypassProxyOnLocal = true };
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            proxy.Credentials = new NetworkCredential(Uri.UnescapeDataString(parts[0]),
                parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
        }
        return proxy;
    }

    /// <summary>Hides the password part of a proxy URL for logs and API output.</summary>
    public static string RedactProxy(string? proxyUrl)
    {
        if (string.IsNullOrEmpty(proxyUrl)) return "";
        if (!Uri.TryCreate(proxyUrl, UriKind.Absolute, out var uri) || !uri.UserInfo.Contains(':')) return proxyUrl;
        var user = uri.UserInfo.Split(':', 2)[0];
        return new UriBuilder(uri) { UserName = user, Password = RedactedPassword }.Uri.ToString().TrimEnd('/');
    }

    public const string RedactedPassword = "********";

    public void Dispose()
    {
        lock (_lock) _proxyClient?.Dispose();
    }
}
