using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CaddyManager.Config.Generation;
using CaddyManager.Core.Models;
using Microsoft.AspNetCore.Http;

namespace CaddyManager.Config.Tests;

/// <summary>
/// End-to-end checks, against the real Caddy binary, of the console fields for what Caddy v2.11.6 / v2.11.7 added:
/// URL pattern locations (http.matchers.url_pattern), hashed cookies in access logs (the cookie and set_cookie log
/// filters), time-based access log rotation (roll_interval) and the Proxy-Status name (proxy_status_name).
/// https://github.com/caddyserver/caddy/releases/tag/v2.11.6 ; https://github.com/caddyserver/caddy/releases/tag/v2.11.7
/// </summary>
public sealed class Caddy2117FeaturesE2ETests
{
    /// <summary>
    /// Locations that match by URL pattern (ProxyLocation.UrlPattern).
    /// Ways it could fail:
    /// (1) a request that fits the pattern does not reach the location's upstream; (2) a request that does not fit it
    ///     does (a pattern "/books/:id" must not match "/books" or "/books/1/pages"); (3) a path-prefix location that
    ///     also matches wins, although the docs say pattern locations are checked first; (4) "ignore case" has no
    ///     effect, or patterns ignore case without it; (5) the request reaches the upstream with a changed path;
    /// (6) Caddy rejects the generated configuration; (7) a pattern Caddy cannot parse is stored, so every later apply
    ///     fails; (8) a pattern location with "strip path prefix", without a pattern text, or a duplicate is accepted.
    /// </summary>
    [CaddyFact]
    public async Task Url_pattern_locations_route_matching_requests_before_path_locations()
    {
        var report = E2EArtifacts.Report(nameof(Url_pattern_locations_route_matching_requests_before_path_locations));
        var observations = new JsonArray();
        report["observations"] = observations;
        await using var main = await RecordingBackend.StartAsync(https: false);
        await using var books = await RecordingBackend.StartAsync(https: false);
        await using var prefix = await RecordingBackend.StartAsync(https: false);
        await using var reports = await RecordingBackend.StartAsync(https: false);
        using var c = new LiveCaddy();
        var host = Build.Proxy("pat.test", main.Port);
        host.Locations =
        [
            new ProxyLocation { Path = "/books", Upstreams = [new Upstream { Host = "127.0.0.1", Port = prefix.Port }] },
            new ProxyLocation { UrlPattern = "/books/:id", Upstreams = [new Upstream { Host = "127.0.0.1", Port = books.Port }] },
            new ProxyLocation { UrlPattern = "/Reports/:year/*", UrlPatternIgnoreCase = true, Upstreams = [new Upstream { Host = "127.0.0.1", Port = reports.Port }] },
        ];
        c.Add(host);
        await c.StartAsync();
        await c.ApplyAsync(); // (6)

        var backends = new Dictionary<string, RecordingBackend> { ["main"] = main, ["books"] = books, ["prefix"] = prefix, ["reports"] = reports };
        async Task Expect(string target, string expected)
        {
            foreach (var b in backends.Values) b.Clear();
            var r = await c.HttpAsync("pat.test", target);
            var hit = backends.Where(b => b.Value.Requests.Count > 0).Select(b => b.Key).ToList();
            var path = hit.Count == 1 ? backends[hit[0]].Requests.Single().Path : null;
            observations.Add(new JsonObject { ["target"] = target, ["status"] = r.Status, ["reached"] = string.Join(",", hit), ["upstreamPath"] = path });
            Assert.Equal(200, r.Status);
            Assert.Equal([expected], hit);
            Assert.Equal(target, path); // (5)
        }

        await Expect("/books/42", "books");            // (1)(3): the prefix location /books matches too
        await Expect("/books/42?x=1", "books");
        await Expect("/books", "prefix");              // (2)
        await Expect("/books/42/pages", "prefix");     // (2)
        // (4) patterns are case-sensitive without the option; Caddy's path matcher (the prefix location) is not.
        await Expect("/Books/42", "prefix");
        await Expect("/reports/2026/q3.pdf", "reports"); // (4) ignore case
        await Expect("/other", "main");

        var running = await c.RunningConfigAsync();
        var matchers = JsonWalk.Descendants(running).OfType<JsonObject>().Where(o => o.ContainsKey("url_pattern")).Select(o => o["url_pattern"]!.ToJsonString()).ToList();
        report["matchers"] = new JsonArray(matchers.Select(m => (JsonNode)m).ToArray());
        Assert.Equal(2, matchers.Count);

        // (7)(8) through the API.
        await using var api = ApiHost.Start(installBinary: true);
        object Body(params object[] locations) => new
        {
            kind = "proxy", domains = new[] { "pat-api.test" }, tls = "none",
            upstreams = new[] { new { host = "127.0.0.1", port = main.Port } },
            locations,
        };
        object Up() => new[] { new { host = "127.0.0.1", port = books.Port } };
        var refused = new JsonArray();
        report["refusedByApi"] = refused;
        async Task Refused(string what, HttpStatusCode expected, params object[] locations)
        {
            var r = await api.SendAsync(HttpMethod.Post, "/api/hosts", Body(locations));
            var text = await r.Content.ReadAsStringAsync();
            refused.Add(new JsonObject { ["case"] = what, ["status"] = (int)r.StatusCode, ["response"] = text.Length > 300 ? text[..300] : text });
            Assert.Equal(expected, r.StatusCode);
            Assert.Empty(api.Store.Col<SiteHost>().FindAll());
        }
        await Refused("strip prefix with a pattern", HttpStatusCode.BadRequest, new { urlPattern = "/a/:b", stripPrefix = true, upstreams = Up() });
        await Refused("duplicate pattern", HttpStatusCode.BadRequest, new { urlPattern = "/a/:b", upstreams = Up() }, new { urlPattern = "/a/:b", upstreams = Up() });
        await Refused("pattern with a space", HttpStatusCode.BadRequest, new { urlPattern = "/a b", upstreams = Up() });
        await Refused("pattern without a leading / or scheme", HttpStatusCode.BadRequest, new { urlPattern = "books/:id", upstreams = Up() });
        // (7) syntactically plausible, but Caddy cannot compile it: refused by Caddy, rolled back.
        await Refused("pattern Caddy cannot parse", HttpStatusCode.UnprocessableEntity, new { urlPattern = "/a/(unclosed", upstreams = Up() });
        var ok = await api.SendAsync(HttpMethod.Post, "/api/hosts", Body(new { urlPattern = "/a/:b", urlPatternIgnoreCase = true, upstreams = Up() }));
        Assert.True(ok.StatusCode == HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());
        var stored = api.Store.Col<SiteHost>().FindAll().Single().Locations.Single();
        Assert.Equal("/a/:b", stored.UrlPattern);
        Assert.True(stored.UrlPatternIgnoreCase);

        E2EArtifacts.Write("url-pattern-locations.json", report);
    }

    /// <summary>
    /// Access log settings: "Cookies hashed in access logs" (CaddySettings.AccessLogHashedCookies) and "Rotate access
    /// logs every (days)" (CaddySettings.AccessLogRollDays).
    /// Ways it could fail:
    /// (1) with credentials logged, the value of a listed cookie appears in the host's access log, in the request's
    ///     Cookie header or in the response's Set-Cookie header; (2) the hash is not the one Caddy documents (first 4
    ///     bytes of the SHA-256), so lines cannot be correlated; (3) cookies that are not listed are changed, or the
    ///     Set-Cookie attributes are lost; (4) the control does not show the values in clear without the list (then the
    ///     test proves nothing); (5) the rotation interval is not in the running configuration, or Caddy rejects it;
    /// (6) the settings break the default behaviour: without credential logging Caddy still redacts both headers;
    /// (7) an older Caddy gets a filter or writer option it does not know and rejects the whole configuration.
    /// </summary>
    [CaddyFact]
    public async Task Access_logs_hash_listed_cookies_and_rotate_by_time()
    {
        var report = E2EArtifacts.Report(nameof(Access_logs_hash_listed_cookies_and_rotate_by_time));
        await using var upstream = await RecordingBackend.StartAsync(https: false, custom: ctx =>
        {
            ctx.Response.Headers.Append("Set-Cookie", "sid=server-secret; Path=/; HttpOnly");
            ctx.Response.Headers.Append("Set-Cookie", "theme=dark; Path=/");
            return Task.FromResult(false);
        });
        using var c = new LiveCaddy();
        var host = Build.Proxy("log.test", upstream.Port);
        host.AccessLog = true;
        c.Add(host);
        await c.StartAsync();
        var logFile = Path.Combine(c.S.Paths.AccessLogDir, "log.test.log");

        async Task<JsonObject> RequestAndReadLogAsync(string marker)
        {
            var r = await c.HttpAsync("log.test", "/" + marker, ("Cookie", "sid=client-secret; theme=light"));
            Assert.Equal(200, r.Status);
            JsonObject? line = null;
            await Wait.Until(() =>
            {
                if (!File.Exists(logFile)) return Task.FromResult(false);
                using var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                foreach (var l in new StreamReader(fs).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    if (l.Contains("/" + marker) && JsonNode.Parse(l) is JsonObject o) line = o;
                return Task.FromResult(line is not null);
            }, TimeSpan.FromSeconds(10), () => "No access log line for /" + marker);
            return line!;
        }
        static string Cookie(JsonObject line) => line["request"]!["headers"]!["Cookie"]!.ToJsonString();
        static string SetCookie(JsonObject line) => line["resp_headers"]!["Set-Cookie"]!.ToJsonString();
        static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8].ToLowerInvariant();

        // (6) default: Caddy redacts both headers.
        await c.ApplyAsync();
        var redacted = await RequestAndReadLogAsync("default");
        report["default"] = new JsonObject { ["cookie"] = Cookie(redacted), ["setCookie"] = SetCookie(redacted) };
        Assert.DoesNotContain("secret", Cookie(redacted));
        Assert.DoesNotContain("secret", SetCookie(redacted));

        // (4) CONTROL: credentials logged, no list: values in clear.
        c.UpdateSettings(s => s.ServerOptionsJson = """{"logs":{"should_log_credentials":true}}""");
        await c.ApplyAsync();
        var clear = await RequestAndReadLogAsync("control");
        report["controlCredentialsLogged"] = new JsonObject { ["cookie"] = Cookie(clear), ["setCookie"] = SetCookie(clear) };
        Assert.Contains("client-secret", Cookie(clear));
        Assert.Contains("server-secret", SetCookie(clear));

        // (1)(2)(3)(5) with the list and a rotation interval.
        c.UpdateSettings(s => { s.AccessLogHashedCookies = ["sid"]; s.AccessLogRollDays = 7; });
        await c.ApplyAsync();
        var hashed = await RequestAndReadLogAsync("hashed");
        report["hashed"] = new JsonObject { ["cookie"] = Cookie(hashed), ["setCookie"] = SetCookie(hashed) };
        Assert.DoesNotContain("client-secret", Cookie(hashed));                 // (1)
        Assert.DoesNotContain("server-secret", SetCookie(hashed));              // (1)
        Assert.Contains("sid=" + Hash("client-secret"), Cookie(hashed));        // (2)
        Assert.Contains("sid=" + Hash("server-secret") + "; Path=/; HttpOnly", SetCookie(hashed)); // (2)(3)
        Assert.Contains("theme=light", Cookie(hashed));                         // (3)
        Assert.Contains("theme=dark; Path=/", SetCookie(hashed));               // (3)
        var running = await c.RunningConfigAsync();
        var writer = running["logging"]!["logs"]![CaddyConfigGenerator.AccessLoggerPrefix + host.Id]!["writer"]!;
        report["writer"] = writer.DeepClone();
        Assert.Equal("168h", writer["roll_interval"]!.GetValue<string>()); // (5)
        Assert.Equal(20, writer["roll_size_mb"]!.GetValue<int>());

        // (7) older Caddy versions.
        JsonObject AccessLog(string version)
        {
            var gen = CaddyConfigGenerator.Generate(Build.Input(c.S.Paths, c.S.Store.GetSettings<CaddySettings>(), [host]) with { InstalledVersion = version });
            report["warnings " + version] = new JsonArray(gen.Warnings.Select(w => (JsonNode)w).ToArray());
            return gen.Config["logging"]!["logs"]![CaddyConfigGenerator.AccessLoggerPrefix + host.Id]!.AsObject();
        }
        var v2114 = AccessLog("v2.11.4");
        Assert.DoesNotContain("set_cookie", v2114.ToJsonString());
        Assert.Contains("\"filter\":\"cookie\"", v2114.ToJsonString());
        Assert.NotNull(v2114["writer"]!["roll_interval"]);
        var v2102 = AccessLog("v2.10.2");
        Assert.Null(v2102["writer"]!["roll_interval"]);

        E2EArtifacts.Write("access-log-cookies-and-rotation.json", report);
    }

    /// <summary>
    /// "Proxy-Status name" (CaddySettings.ProxyStatusName): the name Caddy puts into the Proxy-Status response header
    /// when it refuses to forward a request incrementally (501, see RequestLimitsE2ETests).
    /// Ways it could fail: (1) the 501 carries no Proxy-Status header although a name is set, or the name is not in it;
    /// (2) a header is sent without a name; (3) normal responses get the header; (4) Caddy rejects the configuration;
    /// (5) a Caddy older than v2.11.7 gets the field and rejects the whole configuration.
    /// </summary>
    [CaddyFact]
    public async Task Proxy_status_name_is_sent_when_an_incremental_upload_is_refused()
    {
        var report = E2EArtifacts.Report(nameof(Proxy_status_name_is_sent_when_an_incremental_upload_is_refused));
        await using var upstream = await RecordingBackend.StartAsync(https: false);
        using var c = new LiveCaddy(s => s.EnableHttp3 = true);
        var host = Build.Proxy("ps.test", upstream.Port);
        c.Add(host);
        c.Add(Build.Proxy("ps-tls.test", upstream.Port, TlsMode.Internal));
        await c.StartAsync();

        async Task<string> IncrementalPostAsync()
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, c.HttpPort);
            await using var stream = tcp.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("POST /in HTTP/1.1\r\nHost: ps.test\r\nConnection: close\r\nIncremental: ?1\r\nContent-Length: 5\r\n\r\nhello"));
            using var ms = new MemoryStream();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await stream.CopyToAsync(ms, cts.Token); } catch (IOException) { }
            return Encoding.Latin1.GetString(ms.ToArray());
        }

        await c.ApplyAsync(); // (4)
        var unnamed = await IncrementalPostAsync();
        report["withoutName"] = unnamed;
        Assert.StartsWith("HTTP/1.1 501", unnamed);
        Assert.DoesNotContain("Proxy-Status", unnamed, StringComparison.OrdinalIgnoreCase); // (2)

        c.UpdateSettings(s => s.ProxyStatusName = "edge01.example.com");
        await c.ApplyAsync(); // (4)
        var named = await IncrementalPostAsync();
        report["withName"] = named;
        Assert.StartsWith("HTTP/1.1 501", named);
        var header = named.Split("\r\n").FirstOrDefault(l => l.StartsWith("Proxy-Status:", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(header); // (1)
        Assert.Contains("edge01.example.com", header);
        Assert.Contains("error=", header);
        var normal = await c.HttpAsync("ps.test", "/");
        Assert.Equal(200, normal.Status);
        Assert.Null(normal.Header("Proxy-Status")); // (3)

        // (5)
        var old = CaddyConfigGenerator.Generate(Build.Input(c.S.Paths, c.S.Store.GetSettings<CaddySettings>(), [host]) with { InstalledVersion = "v2.11.6" });
        Assert.DoesNotContain("proxy_status_name", old.Config.ToJsonString());
        Assert.Contains(old.Warnings, w => w.Contains("Proxy-Status") && w.Contains("v2.11.7"));

        E2EArtifacts.Write("proxy-status-name.json", report);
    }

    /// <summary>
    /// The three settings through PUT /api/settings/caddy.
    /// Ways it could fail: (1) values Caddy would refuse or that make no sense are stored (a rotation of 0 or more than
    /// a year of days, cookie names with characters a cookie name cannot have, a Proxy-Status name with quotes, control
    /// characters or more than 255 characters); (2) valid values are not stored as sent, or names keep surrounding
    /// spaces or duplicates; (3) the fields cannot be cleared again.
    /// </summary>
    [CaddyFact]
    public async Task Log_and_proxy_status_settings_are_validated_stored_and_clearable_through_the_settings_api()
    {
        var report = E2EArtifacts.Report(nameof(Log_and_proxy_status_settings_are_validated_stored_and_clearable_through_the_settings_api));
        var rows = new JsonArray();
        report["requests"] = rows;
        await using var api = ApiHost.Start(installBinary: true);
        async Task<(HttpStatusCode Status, string Body)> Put(JsonObject body)
        {
            var r = await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", body);
            var text = await r.Content.ReadAsStringAsync();
            rows.Add(new JsonObject { ["body"] = body.DeepClone(), ["status"] = (int)r.StatusCode, ["response"] = text.Length > 300 ? text[..300] : text });
            return (r.StatusCode, text);
        }
        async Task Refused(string field, JsonNode? value, string errorKey)
        {
            var (status, body) = await Put(new JsonObject { [field] = value });
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Contains(errorKey, body);
        }

        foreach (var bad in new[] { 0, -1, 366 }) await Refused("accessLogRollDays", bad, "accessLogRollDays"); // (1)
        foreach (var bad in new[] { "has space", "a=b", "semi;colon", "ünicode", "quo\"te" })
            await Refused("accessLogHashedCookies", new JsonArray(bad), "accessLogHashedCookies[0]");
        foreach (var bad in new[] { "quo\"te", "back\\slash", "line\nbreak", new string('a', 256) })
            await Refused("proxyStatusName", bad, "proxyStatusName");

        var ok = await Put(new JsonObject // (2)
        {
            ["accessLogRollDays"] = 30, ["accessLogHashedCookies"] = new JsonArray(" sid ", "ASP.NET_SessionId", "sid"), ["proxyStatusName"] = " edge01.example.com ",
        });
        Assert.Equal(HttpStatusCode.OK, ok.Status);
        var stored = api.Env.Store.GetSettings<CaddySettings>();
        Assert.Equal(30, stored.AccessLogRollDays);
        Assert.Equal(["sid", "ASP.NET_SessionId"], stored.AccessLogHashedCookies);
        Assert.Equal("edge01.example.com", stored.ProxyStatusName);

        var cleared = await Put(new JsonObject { ["accessLogRollDays"] = null, ["accessLogHashedCookies"] = new JsonArray(), ["proxyStatusName"] = "" }); // (3)
        Assert.Equal(HttpStatusCode.OK, cleared.Status);
        stored = api.Env.Store.GetSettings<CaddySettings>();
        Assert.Null(stored.AccessLogRollDays);
        Assert.Empty(stored.AccessLogHashedCookies);
        Assert.Null(stored.ProxyStatusName);

        E2EArtifacts.Write("log-and-proxy-status-settings-api.json", report);
    }

    /// <summary>
    /// The minimum transfer rates (CaddySettings.ReadMinRateBytes / WriteMinRateBytes).
    /// Ways it could fail:
    /// (1) the minimum upload rate is not enforced: an upload that trickles a few bytes often enough never to be idle
    ///     is never cut; (2) the CONTROL is missing: the same trickle is cut without a minimum rate too, so (1) would
    ///     prove nothing; (3) Caddy rejects the configuration; (4) a Caddy older than v2.11.6 gets the rate fields and
    ///     rejects the whole configuration; (5) a rate of 0 or below is stored through the API.
    /// Also recorded (CONTROL (6)): Caddy's per-route "timeouts" handler does not shorten the idle timeout while the
    /// server-wide timeout is active. That is why per-host timeouts switch the server-wide one off (next test); if
    /// this control starts to cut the upload, a Caddy release changed the precedence and that design can be simplified.
    /// </summary>
    [CaddyFact]
    public async Task Minimum_transfer_rates_cut_trickling_uploads()
    {
        var report = E2EArtifacts.Report(nameof(Minimum_transfer_rates_cut_trickling_uploads));
        await using var upstream = await RecordingBackend.StartAsync(https: false, custom: async ctx =>
        {
            // Kestrel answers 408 itself when a request body arrives slower than 240 bytes/s after 5 s; these tests
            // send slower than that on purpose, and only Caddy may cut them.
            ctx.Features.Get<Microsoft.AspNetCore.Server.Kestrel.Core.Features.IHttpMinRequestBodyDataRateFeature>()!.MinDataRate = null;
            using var body = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(body, ctx.RequestAborted);
            await ctx.Response.WriteAsync($"received {body.Length}");
            return true;
        });
        using var c = new LiveCaddy();
        var normal = Build.Proxy("normal.test", upstream.Port);
        c.Add(normal);
        await c.StartAsync();
        await c.ApplyAsync(); // (3)

        // Sends `total` bytes in pieces of `piece` every `gap`; reports whether the server ended the exchange early.
        async Task<JsonObject> UploadAsync(string hostName, int total, int piece, TimeSpan gap, TimeSpan firstPause)
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, c.HttpPort);
            await using var stream = tcp.GetStream();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"POST /up HTTP/1.1\r\nHost: {hostName}\r\nConnection: close\r\nContent-Length: {total}\r\n\r\n"));
            var received = new MemoryStream();
            var reader = Task.Run(async () =>
            {
                var buffer = new byte[4096];
                try
                {
                    int n;
                    while ((n = await stream.ReadAsync(buffer)) > 0) received.Write(buffer, 0, n);
                }
                catch (IOException) { }
            });
            var sent = 0;
            var cutAt = -1.0;
            try
            {
                while (sent < total)
                {
                    var n = Math.Min(piece, total - sent);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(new string('x', n)));
                    sent += n;
                    if (sent >= total) break;
                    if (await Task.WhenAny(reader, Task.Delay(sent == n ? firstPause : gap)) == reader) { cutAt = clock.Elapsed.TotalSeconds; break; }
                }
            }
            catch (IOException) { cutAt = clock.Elapsed.TotalSeconds; }
            await Task.WhenAny(reader, Task.Delay(TimeSpan.FromSeconds(10)));
            var text = Encoding.Latin1.GetString(received.ToArray());
            return new JsonObject
            {
                ["host"] = hostName, ["sent"] = sent, ["of"] = total, ["cutAfterSeconds"] = cutAt < 0 ? null : Math.Round(cutAt, 2),
                ["statusLine"] = text.Split("\r\n")[0], ["completed"] = text.Contains($"received {total}"),
            };
        }

        // (6) CONTROL: a "timeouts" handler of 1 s in front of the host, server-wide timeout at its default: an upload
        // that pauses for 4 s is NOT cut.
        var withHandler = await c.RunningConfigAsync();
        var hostRoutes = JsonWalk.Handlers(withHandler["apps"]!["http"]!["servers"]![CaddyConfigGenerator.HttpServerName], "subroute").First()["routes"]!.AsArray();
        hostRoutes.Insert(0, new JsonObject { ["handle"] = new JsonArray(new JsonObject { ["handler"] = "timeouts", ["read_timeout"] = 1_000_000_000L }) });
        await c.LoadRawAsync(withHandler);
        var perRoute = await UploadAsync("normal.test", 1000, 500, TimeSpan.Zero, TimeSpan.FromSeconds(4));
        report["perRouteTimeoutsHandler1sStalled4s"] = perRoute;
        Assert.True(perRoute["completed"]!.GetValue<bool>(), "Caddy's per-route timeouts handler now takes effect: " + perRoute.ToJsonString());

        // (2) CONTROL: 20 bytes every 300 ms for 6 s is never idle for the 2 s timeout, so it completes.
        c.UpdateSettings(s => s.ReadIdleTimeoutSeconds = 2);
        await c.ApplyAsync();
        var trickleControl = await UploadAsync("normal.test", 400, 20, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(300));
        report["trickleWithoutMinimumRate"] = trickleControl;
        Assert.True(trickleControl["completed"]!.GetValue<bool>(), trickleControl.ToJsonString());

        // (1) with a minimum of 1000 bytes/s the same trickle (about 67 bytes/s) is cut.
        c.UpdateSettings(s => { s.ReadMinRateBytes = 1000; s.WriteMinRateBytes = 1000; });
        await c.ApplyAsync(); // (3)
        var trickle = await UploadAsync("normal.test", 400, 20, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(300));
        report["trickleWithMinimumRate"] = trickle;
        Assert.False(trickle["completed"]!.GetValue<bool>(), trickle.ToJsonString());
        var running = await c.RunningConfigAsync();
        var srv = running["apps"]!["http"]!["servers"]![CaddyConfigGenerator.HttpServerName]!;
        Assert.Equal(1000, srv["read_min_rate"]!.GetValue<int>());
        Assert.Equal(1000, srv["write_min_rate"]!.GetValue<int>());

        // (4)
        var old = CaddyConfigGenerator.Generate(Build.Input(c.S.Paths, c.S.Store.GetSettings<CaddySettings>(), [normal]) with { InstalledVersion = "v2.11.4" });
        Assert.DoesNotContain("min_rate", old.Config.ToJsonString());
        Assert.Contains(old.Warnings, w => w.Contains("minimum rate") && w.Contains("v2.11.6"));

        // (5)
        await using var api = ApiHost.Start(installBinary: true);
        foreach (var bad in new[] { 0, -1 })
        {
            var r = await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new JsonObject { ["readMinRateBytes"] = bad, ["writeMinRateBytes"] = bad });
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Contains("readMinRateBytes", await r.Content.ReadAsStringAsync());
        }
        var okSettings = await api.SendAsync(HttpMethod.Put, "/api/settings/caddy", new JsonObject { ["readMinRateBytes"] = 500, ["writeMinRateBytes"] = null });
        Assert.Equal(HttpStatusCode.OK, okSettings.StatusCode);
        Assert.Equal(500, api.Env.Store.GetSettings<CaddySettings>().ReadMinRateBytes);
        Assert.Null(api.Env.Store.GetSettings<CaddySettings>().WriteMinRateBytes);

        E2EArtifacts.Write("minimum-transfer-rates.json", report);
    }

    /// <summary>
    /// Per-host idle timeouts (SiteHost.ReadIdleTimeoutSeconds / WriteIdleTimeoutSeconds), which replace the global
    /// ones for that host.
    /// Ways it could fail:
    /// (1) a host's shorter upload idle timeout is not applied: its stalled upload is still waited for;
    /// (2) a host's LONGER timeout is not applied: its upload is cut at the global timeout;
    /// (3) hosts without their own value lose the global timeout (it is switched off on the server): their stalled
    ///     upload is never cut; (4) or they get another host's value; (5) the global minimum rate is lost for all hosts
    ///     once one host has its own timeout; (6) the plain-HTTP and the HTTPS server differ; (7) the default site or
    ///     the HTTPS redirect of a host has no timeout at all; (8) Caddy rejects the configuration ("-1s", durations as
    ///     numbers); (9) without any per-host value the configuration changes (handlers everywhere for nothing);
    /// (10) a Caddy older than v2.11.6 gets the handler and rejects the whole configuration; (11) 0, negatives or more
    ///      than an hour are stored through the API.
    /// </summary>
    [CaddyFact]
    public async Task Host_idle_timeouts_replace_the_global_ones_for_that_host()
    {
        var report = E2EArtifacts.Report(nameof(Host_idle_timeouts_replace_the_global_ones_for_that_host));
        await using var upstream = await RecordingBackend.StartAsync(https: false, custom: async ctx =>
        {
            // Kestrel answers 408 itself when a request body arrives slower than 240 bytes/s after 5 s; these tests
            // send slower than that on purpose, and only Caddy may cut them.
            ctx.Features.Get<Microsoft.AspNetCore.Server.Kestrel.Core.Features.IHttpMinRequestBodyDataRateFeature>()!.MinDataRate = null;
            using var body = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(body, ctx.RequestAborted);
            await ctx.Response.WriteAsync($"received {body.Length}");
            return true;
        });
        using var c = new LiveCaddy(s => s.ReadIdleTimeoutSeconds = 3);
        var quick = Build.Proxy("quick.test", upstream.Port);
        var patient = Build.Proxy("patient.test", upstream.Port);
        var plain = Build.Proxy("plain.test", upstream.Port);
        var secure = Build.Proxy("secure.test", upstream.Port, TlsMode.Internal);
        foreach (var h in new[] { quick, patient, plain, secure }) c.Add(h);
        await c.StartAsync();

        // Sends half of a 1000-byte upload, pauses, sends the rest; reports whether the server gave up during the pause.
        async Task<JsonObject> StalledAsync(string hostName, double pauseSeconds)
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, c.HttpPort);
            await using var stream = tcp.GetStream();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"POST /up HTTP/1.1\r\nHost: {hostName}\r\nConnection: close\r\nContent-Length: 1000\r\n\r\n" + new string('x', 500)));
            var received = new MemoryStream();
            var reader = Task.Run(async () =>
            {
                var buffer = new byte[4096];
                try
                {
                    int n;
                    while ((n = await stream.ReadAsync(buffer)) > 0) received.Write(buffer, 0, n);
                }
                catch (IOException) { }
            });
            double? cutAt = null;
            if (await Task.WhenAny(reader, Task.Delay(TimeSpan.FromSeconds(pauseSeconds))) == reader) cutAt = clock.Elapsed.TotalSeconds;
            else
            {
                try { await stream.WriteAsync(Encoding.ASCII.GetBytes(new string('x', 500))); } catch (IOException) { }
                await Task.WhenAny(reader, Task.Delay(TimeSpan.FromSeconds(10)));
            }
            var text = Encoding.Latin1.GetString(received.ToArray());
            return new JsonObject
            {
                ["host"] = hostName, ["pauseSeconds"] = pauseSeconds, ["cutAfterSeconds"] = cutAt is null ? null : Math.Round(cutAt.Value, 2),
                ["statusLine"] = text.Split("\r\n")[0], ["completed"] = text.Contains("received 1000"),
            };
        }
        var cases = new JsonArray();
        report["uploads"] = cases;
        async Task Expect(string hostName, double pause, bool completes, string why)
        {
            var r = await StalledAsync(hostName, pause);
            r["expectation"] = why;
            cases.Add(r);
            Assert.True(r["completed"]!.GetValue<bool>() == completes, why + ": " + r.ToJsonString());
        }

        // (9) no per-host value: server-wide timeout, no handler.
        await c.ApplyAsync(); // (8)
        var before = await c.RunningConfigAsync();
        Assert.Empty(JsonWalk.Handlers(before, "timeouts"));
        Assert.Equal("3s", before["apps"]!["http"]!["servers"]![CaddyConfigGenerator.HttpServerName]!["read_idle_timeout"]!.GetValue<string>());
        await Expect("quick.test", 1.5, completes: true, "global 3 s, pause 1.5 s: completes");
        await Expect("plain.test", 5, completes: false, "global 3 s, pause 5 s: cut");

        // Per-host values: quick 1 s, patient 8 s, the others keep the global 3 s.
        quick.ReadIdleTimeoutSeconds = 1;
        patient.ReadIdleTimeoutSeconds = 8;
        patient.WriteIdleTimeoutSeconds = 120;
        c.S.Store.Col<SiteHost>().Update(quick);
        c.S.Store.Col<SiteHost>().Update(patient);
        await c.ApplyAsync(); // (8)

        await Expect("quick.test", 2.5, completes: false, "(1) host 1 s, pause 2.5 s (below the global 3 s): cut");
        await Expect("patient.test", 5, completes: true, "(2) host 8 s, pause 5 s (above the global 3 s): completes");
        await Expect("plain.test", 1.5, completes: true, "(4) no own value, pause 1.5 s (above quick's 1 s): completes");
        await Expect("plain.test", 5, completes: false, "(3) no own value, pause 5 s (above the global 3 s): cut");

        // A minimum rate is set only now: it gives credit for the bytes already sent, which would lengthen the pauses above.
        c.UpdateSettings(s => s.ReadMinRateBytes = 50);
        await c.ApplyAsync(); // (8)
        var running = await c.RunningConfigAsync();
        var servers = running["apps"]!["http"]!["servers"]!;
        var summary = new JsonObject();
        report["servers"] = summary;
        foreach (var name in new[] { CaddyConfigGenerator.HttpServerName, CaddyConfigGenerator.HttpsServerName }) // (6)
        {
            var srv = servers[name]!.AsObject();
            Assert.Equal("-1s", srv["read_idle_timeout"]!.GetValue<string>());
            Assert.Equal("-1s", srv["write_idle_timeout"]!.GetValue<string>());
            Assert.False(srv.ContainsKey("read_min_rate"));
            // (7) every route (hosts, HTTPS redirects, default site) starts with exactly one timeouts handler.
            var routes = srv["routes"]!.AsArray();
            var firsts = routes.Select(r => r!["handle"]![0]!).ToList();
            summary[name] = new JsonArray(firsts.Select(f => f.DeepClone()).ToArray());
            Assert.All(firsts, f => Assert.Equal("timeouts", f["handler"]!.GetValue<string>()));
            Assert.All(routes, r => Assert.Single(JsonWalk.Handlers(r, "timeouts")));
            Assert.All(firsts, f => Assert.Equal(50, f["read_min_rate"]!.GetValue<int>())); // (5)
        }
        long ReadTimeoutOf(string domain) => servers[CaddyConfigGenerator.HttpServerName]!["routes"]!.AsArray()
            .First(r => r!["match"]?[0]?["host"]?.AsArray().Any(h => h!.GetValue<string>() == domain) == true)!["handle"]![0]!["read_timeout"]!.GetValue<long>();
        Assert.Equal(1_000_000_000L, ReadTimeoutOf("quick.test"));
        Assert.Equal(8_000_000_000L, ReadTimeoutOf("patient.test"));
        Assert.Equal(3_000_000_000L, ReadTimeoutOf("plain.test"));
        Assert.Equal(3_000_000_000L, ReadTimeoutOf("secure.test"));
        var defaultSite = servers[CaddyConfigGenerator.HttpServerName]!["routes"]!.AsArray().Last()!["handle"]![0]!;
        Assert.Equal(3_000_000_000L, defaultSite["read_timeout"]!.GetValue<long>());
        Assert.Equal(60_000_000_000L, defaultSite["write_timeout"]!.GetValue<long>());

        // (10)
        var old = CaddyConfigGenerator.Generate(Build.Input(c.S.Paths, c.S.Store.GetSettings<CaddySettings>(), [quick, patient, plain]) with { InstalledVersion = "v2.11.4" });
        Assert.Empty(JsonWalk.Handlers(old.Config, "timeouts"));
        Assert.DoesNotContain("-1s", old.Config.ToJsonString());
        Assert.Contains(old.Warnings, w => w.Contains("quick.test") && w.Contains("v2.11.6"));

        // (11)
        await using var api = ApiHost.Start(installBinary: true);
        foreach (var bad in new[] { 0, -1, 3601 })
        {
            var r = await api.SendAsync(HttpMethod.Post, "/api/hosts", new
            {
                kind = "proxy", domains = new[] { "t.test" }, tls = "none", upstreams = new[] { new { host = "127.0.0.1", port = upstream.Port } },
                readIdleTimeoutSeconds = bad, writeIdleTimeoutSeconds = bad,
            });
            var text = await r.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Contains("readIdleTimeoutSeconds", text);
            Assert.Contains("writeIdleTimeoutSeconds", text);
        }
        var ok = await api.SendAsync(HttpMethod.Post, "/api/hosts", new
        {
            kind = "proxy", domains = new[] { "t.test" }, tls = "none", upstreams = new[] { new { host = "127.0.0.1", port = upstream.Port } },
            readIdleTimeoutSeconds = 300,
        });
        Assert.True(ok.StatusCode == HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());
        var saved = api.Store.Col<SiteHost>().FindAll().Single();
        Assert.Equal(300, saved.ReadIdleTimeoutSeconds);
        Assert.Null(saved.WriteIdleTimeoutSeconds);

        E2EArtifacts.Write("host-idle-timeouts.json", report);
    }
}
