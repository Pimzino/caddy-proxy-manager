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
public sealed partial class CaddyAdminClient : ICaddyAdminClient
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

    /// <summary>
    /// Upstreams whose health a health check actually measures (see <see cref="GetUpstreamStatusAsync"/>), for the
    /// dashboard and the upstream-down alert. An upstream without any check is left out: Caddy reports it healthy
    /// whatever its state (Upstream.Healthy() is true without active or passive checks), so listing it would claim a
    /// health nobody measured.
    /// </summary>
    public async Task<List<UpstreamHealth>> GetUpstreamsAsync(CancellationToken ct = default) =>
        (await GetUpstreamStatusAsync(ct)).Where(u => u.Monitored)
            .Select(u => new UpstreamHealth { Address = u.Address, NumRequests = u.NumRequests, Fails = u.Fails, Healthy = u.Healthy })
            .ToList();

    /// <summary>
    /// Every upstream Caddy knows, with whether a health check monitors it: an active check on a reverse_proxy that
    /// uses the address, or passive checks (fail_duration set) there. The manager itself generates no passive checks
    /// (one request could mark every upstream down), so an upstream without an active check is not monitored unless an
    /// administrator added passive checks in advanced routes.
    /// https://github.com/caddyserver/caddy/blob/v2.11.4/modules/caddyhttp/reverseproxy/hosts.go (Upstream.Healthy)
    /// </summary>
    public async Task<List<UpstreamStatus>> GetUpstreamStatusAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = Linked(ct, TimeSpan.FromSeconds(15));
            using var resp = await Client().GetAsync(Url("/reverse_proxy/upstreams"), cts.Token);
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            if (!resp.IsSuccessStatusCode)
                throw new CaddyAdminException($"Caddy admin API returned {(int)resp.StatusCode}: {ExtractError(body)}");
            var metric = await GetUpstreamHealthMetricAsync(cts.Token);
            var monitored = MonitoredUpstreams(await GetConfigAsync(cts.Token));
            return ParseUpstreams(body, metric)
                .Select(u => new UpstreamStatus(u.Address, u.NumRequests, u.Fails, u.Healthy, monitored.Contains(u.Address)))
                .ToList();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Caddy admin API not reachable at {Url}", BaseUrl);
            return new List<UpstreamStatus>();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new List<UpstreamStatus>();
        }
    }

    /// <summary>
    /// Dial addresses of every reverse_proxy (generated, advanced routes or Caddyfile) that has an active health check
    /// or passive checks with a fail_duration (Caddy ignores passive settings without it, healthchecks.go countFailure).
    /// </summary>
    internal static HashSet<string> MonitoredUpstreams(string? configJson)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(configJson)) return set;
        JsonNode? root;
        try { root = JsonNode.Parse(configJson); }
        catch (JsonException) { return set; }
        Walk(root);
        return set;

        void Walk(JsonNode? n)
        {
            switch (n)
            {
                case JsonObject o:
                    if (o["handler"] is JsonValue hv && hv.GetValueKind() == JsonValueKind.String && hv.GetValue<string>() == "reverse_proxy"
                        && o["health_checks"] is JsonObject hc
                        && (hc["active"] is JsonObject || (hc["passive"] is JsonObject p && p["fail_duration"] is JsonNode fd && fd.ToJsonString() is not ("0" or "\"0s\"" or "\"\"")))
                        && o["upstreams"] is JsonArray ups)
                    {
                        foreach (var u in ups)
                            if (u?["dial"] is JsonValue d && d.GetValueKind() == JsonValueKind.String) set.Add(d.GetValue<string>());
                    }
                    foreach (var (_, v) in o) Walk(v);
                    break;
                case JsonArray a:
                    foreach (var v in a) Walk(v);
                    break;
            }
        }
    }

    /// <summary>
    /// caddy_reverse_proxy_upstreams_healthy per upstream address from the admin /metrics endpoint (Prometheus text),
    /// or an empty map when it cannot be read. Each reverse_proxy handler sets it every 10 s from Upstream.Healthy(),
    /// which combines ACTIVE health checks with the passive failure count (fails &lt; max_fails) - the only place Caddy
    /// exposes active health. /reverse_proxy/upstreams only has address, num_requests and fails (passive).
    /// https://github.com/caddyserver/caddy/blob/v2.11.4/modules/caddyhttp/reverseproxy/metrics.go ;
    /// https://caddyserver.com/docs/api#get-reverse_proxyupstreams
    /// </summary>
    private async Task<Dictionary<string, bool>> GetUpstreamHealthMetricAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await Client().GetAsync(Url("/metrics"), ct);
            if (!resp.IsSuccessStatusCode) return new();
            return ParseUpstreamHealthMetric(await resp.Content.ReadAsStringAsync(ct));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Caddy metrics not readable at {Url}", BaseUrl);
            return new();
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^caddy_reverse_proxy_upstreams_healthy\{[^}]*upstream=""((?:[^""\\]|\\.)*)""[^}]*\}\s+(\S+)", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex UpstreamHealthyMetric();

    internal static Dictionary<string, bool> ParseUpstreamHealthMetric(string metrics)
    {
        var map = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (System.Text.RegularExpressions.Match m in UpstreamHealthyMetric().Matches(metrics))
        {
            var address = m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\");
            if (double.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
                map[address] = v != 0;
        }
        return map;
    }

    /// <summary>
    /// Upstreams with their health: unhealthy when Caddy's healthy metric says so (active checks, or passive failures
    /// reaching max_fails), or - when no metric is known for the address - when requests to it failed within the passive
    /// window (fails &gt; 0; Caddy only counts fails for proxies with passive checks).
    /// </summary>
    internal static List<UpstreamHealth> ParseUpstreams(string body, IReadOnlyDictionary<string, bool>? healthyMetric = null)
    {
        var list = new List<UpstreamHealth>();
        if (string.IsNullOrWhiteSpace(body) || JsonNode.Parse(body) is not JsonArray arr) return list;
        foreach (var n in arr)
        {
            if (n is not JsonObject o) continue;
            var fails = Int(o["fails"]);
            var address = o["address"]?.GetValue<string>() ?? "";
            var healthy = healthyMetric is not null && healthyMetric.TryGetValue(address, out var metric) ? metric : fails == 0;
            list.Add(new UpstreamHealth
            {
                Address = address,
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

/// <summary>An upstream with its health and whether a health check monitors it (false = health unknown).</summary>
public sealed record UpstreamStatus(string Address, int NumRequests, int Fails, bool Healthy, bool Monitored);
