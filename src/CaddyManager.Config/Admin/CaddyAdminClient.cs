using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CaddyManager.Config.Generation;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Config.Admin;

/// <summary>Client for Caddy's admin API (http://&lt;AdminListen&gt;).</summary>
public sealed class CaddyAdminClient : ICaddyAdminClient
{
    public const string HttpClientName = "caddy-admin";
    private static readonly TimeSpan ReachabilityTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(90);

    private readonly IHttpClientFactory _factory;
    private readonly Func<string> _address;
    private readonly ILogger _logger;

    /// <summary>DI constructor: the admin address is read from CaddySettings on every call.</summary>
    public CaddyAdminClient(IHttpClientFactory factory, IStore store, ILogger<CaddyAdminClient> logger)
        : this(factory, () => store.GetSettings<CaddySettings>().AdminListen, logger)
    {
    }

    public CaddyAdminClient(IHttpClientFactory factory, Func<string> address, ILogger logger)
    {
        _factory = factory;
        _address = address;
        _logger = logger;
    }

    /// <summary>A client bound to a fixed admin address (e.g. the address of the currently running config).</summary>
    public CaddyAdminClient ForAddress(string listen) => new(_factory, () => listen, _logger);

    public string BaseUrl => BaseUrlFor(_address());

    /// <summary>Converts a Caddy admin "listen" value to an http base URL.</summary>
    public static string BaseUrlFor(string? listen)
    {
        var v = string.IsNullOrWhiteSpace(listen) ? "127.0.0.1:2019" : listen.Trim();
        if (v.StartsWith("unix/", StringComparison.OrdinalIgnoreCase) || v.StartsWith("unixgram/", StringComparison.OrdinalIgnoreCase))
            throw new CaddyAdminException($"Admin listen address '{v}' is a unix socket, which is not supported. Use 127.0.0.1:<port>.");
        if (v.StartsWith("tcp/", StringComparison.OrdinalIgnoreCase)) v = v[4..];
        if (v.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) v = v[7..];
        v = v.TrimEnd('/');
        var idx = v.LastIndexOf(':');
        string host, port;
        if (idx < 0) { host = v; port = "2019"; }
        else { host = v[..idx]; port = v[(idx + 1)..]; }
        host = host.Trim('[', ']');
        if (host.Length == 0 || host == "0.0.0.0") host = "127.0.0.1";
        else if (host == "::") host = "::1";
        if (IPAddress.TryParse(host, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            host = $"[{host}]";
        return $"http://{host}:{port}";
    }

    private HttpClient Client()
    {
        var c = _factory.CreateClient(HttpClientName);
        c.Timeout = Timeout.InfiniteTimeSpan; // per-call timeouts via CancellationTokenSource
        return c;
    }

    private Uri Url(string path) => new(BaseUrl + path);

    private static CancellationTokenSource Linked(CancellationToken ct, TimeSpan timeout)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        return cts;
    }

    public async Task<bool> IsReachableAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = Linked(ct, ReachabilityTimeout);
            using var resp = await Client().GetAsync(Url("/config/"), HttpCompletionOption.ResponseHeadersRead, cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (CaddyAdminException)
        {
            return false;
        }
    }

    public async Task<string?> GetConfigAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = Linked(ct, RequestTimeout);
            using var resp = await Client().GetAsync(Url("/config/"), cts.Token);
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            if (!resp.IsSuccessStatusCode)
                throw new CaddyAdminException($"Caddy admin API returned {(int)resp.StatusCode}: {ExtractError(body)}");
            return body;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Caddy admin API not reachable at {Url}", BaseUrl);
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task LoadAsync(string json, CancellationToken ct = default)
    {
        using var cts = Linked(ct, RequestTimeout);
        using var req = new HttpRequestMessage(HttpMethod.Post, Url("/load"))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        // Force a reload even when the JSON is unchanged, so certificate files are re-read from disk.
        req.Headers.CacheControl = new CacheControlHeaderValue { MustRevalidate = true };
        HttpResponseMessage resp;
        try
        {
            resp = await Client().SendAsync(req, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new HttpRequestException($"Timed out after {RequestTimeout.TotalSeconds:0}s waiting for Caddy to load the configuration.");
        }
        using (resp)
        {
            if (resp.IsSuccessStatusCode) return;
            var body = await resp.Content.ReadAsStringAsync(CancellationToken.None);
            var msg = ExtractError(body);
            _logger.LogWarning("Caddy rejected the configuration ({Status}): {Error}", (int)resp.StatusCode, msg);
            throw new CaddyAdminException(msg);
        }
    }

    public async Task<(string Json, List<string> Warnings)> AdaptCaddyfileAsync(string caddyfile, CancellationToken ct = default)
    {
        using var cts = Linked(ct, RequestTimeout);
        using var content = new StringContent(caddyfile ?? "", Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/caddyfile");
        using var resp = await Client().PostAsync(Url("/adapt"), content, cts.Token);
        var body = await resp.Content.ReadAsStringAsync(cts.Token);
        if (!resp.IsSuccessStatusCode) throw new CaddyAdminException(ExtractError(body));

        var warnings = new List<string>();
        JsonNode? result = null;
        try
        {
            var doc = JsonNode.Parse(body) as JsonObject;
            result = doc?["result"];
            if (doc?["warnings"] is JsonArray ws)
                foreach (var w in ws)
                    if (w is JsonObject wo) warnings.Add(FormatAdaptWarning(wo));
        }
        catch (JsonException ex)
        {
            throw new CaddyAdminException("Caddy returned an unreadable adapt response: " + ex.Message);
        }
        if (result is null) throw new CaddyAdminException("Caddy returned no adapted configuration.");
        return (CaddyJson.Serialize(result), warnings);
    }

    internal static string FormatAdaptWarning(JsonObject w)
    {
        var file = w["file"]?.GetValue<string>();
        var line = w["line"]?.GetValueKind() == JsonValueKind.Number ? w["line"]!.GetValue<int>() : 0;
        var directive = w["directive"]?.GetValue<string>();
        var message = w["message"]?.GetValue<string>() ?? w.ToJsonString();
        var where = file is null ? "" : line > 0 ? $"{file}:{line}: " : $"{file}: ";
        return where + (string.IsNullOrEmpty(directive) ? "" : $"{directive}: ") + message;
    }

    public async Task<List<UpstreamHealth>> GetUpstreamsAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = Linked(ct, TimeSpan.FromSeconds(15));
            using var resp = await Client().GetAsync(Url("/reverse_proxy/upstreams"), cts.Token);
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            if (!resp.IsSuccessStatusCode)
                throw new CaddyAdminException($"Caddy admin API returned {(int)resp.StatusCode}: {ExtractError(body)}");
            return ParseUpstreams(body);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Caddy admin API not reachable at {Url}", BaseUrl);
            return new List<UpstreamHealth>();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new List<UpstreamHealth>();
        }
    }

    internal static List<UpstreamHealth> ParseUpstreams(string body)
    {
        var list = new List<UpstreamHealth>();
        if (string.IsNullOrWhiteSpace(body) || JsonNode.Parse(body) is not JsonArray arr) return list;
        foreach (var n in arr)
        {
            if (n is not JsonObject o) continue;
            var fails = Int(o["fails"]);
            var healthy = o["healthy"] is JsonValue hv && hv.GetValueKind() is JsonValueKind.True or JsonValueKind.False
                ? hv.GetValue<bool>()
                : fails == 0;
            list.Add(new UpstreamHealth
            {
                Address = o["address"]?.GetValue<string>() ?? "",
                NumRequests = Int(o["num_requests"]),
                Fails = fails,
                Healthy = healthy,
            });
        }
        return list.OrderBy(u => u.Address, StringComparer.Ordinal).ToList();

        static int Int(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<int>() : 0;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        using var cts = Linked(ct, TimeSpan.FromSeconds(30));
        try
        {
            using var resp = await Client().PostAsync(Url("/stop"), content: null, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(cts.Token);
                throw new CaddyAdminException($"Caddy refused to stop ({(int)resp.StatusCode}): {ExtractError(body)}");
            }
        }
        catch (HttpRequestException ex) when (ex.InnerException is IOException)
        {
            // Caddy may close the connection while shutting down.
        }
    }

    /// <summary>Caddy errors are JSON: {"error":"..."}. Falls back to the raw body.</summary>
    internal static string ExtractError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "Caddy returned an error without details.";
        try
        {
            if (JsonNode.Parse(body) is JsonObject o && o["error"] is JsonValue e && e.GetValueKind() == JsonValueKind.String)
                return e.GetValue<string>().Trim();
        }
        catch (JsonException)
        {
        }
        return body.Trim();
    }
}
