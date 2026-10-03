using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using CaddyManager.Config.Generation;
using CaddyManager.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace CaddyManager.Config.Tests;

/// <summary>
/// End-to-end checks of Settings › Caddy › Request limits and headers against the real Caddy binary: the settings are
/// applied through CaddyConfigService, real requests are sent over raw sockets and every test writes a JSON artifact.
/// The behaviour they configure arrived in Caddy v2.11.6 (16 KiB header limit, idle read/write timeouts, request
/// headers with "." dropped, expected_underscore_headers / expected_dot_headers).
/// https://github.com/caddyserver/caddy/releases/tag/v2.11.6 ; https://caddyserver.com/docs/json/apps/http/servers/
/// </summary>
public sealed class RequestLimitsE2ETests
{
    /// <summary>
    /// "Request headers to keep" (CaddySettings.KeptRequestHeaders).
    /// Ways it could fail:
    /// (1) the documented default is wrong: a client header with "_" or "." in its name reaches the upstream;
    /// (2) a kept underscore header (SM_USER) is still dropped; (3) a kept dotted header (X.Trace) is still dropped;
    /// (4) a prefix entry (webhook_*) is not honoured; (5) the hyphenated twin of a kept header (SM-USER) still reaches
    ///     the upstream, although the docs say Caddy drops it; (6) an unlisted header with "_" or "." gets through once
    ///     a list exists; (7) a name with both characters (X_Tenant.Id) is not kept by its exact entry;
    /// (8) the list only applies to one of the two servers (plain HTTP or HTTPS);
    /// (9) the generator warns about an advanced route that matches a kept header, or does not warn about one that
    ///     matches a dotted header that is not kept; (10) Caddy rejects the generated configuration;
    /// (11) an entry without "_" or "." reaches Caddy, which then refuses the whole configuration (CONTROL; the settings
    ///      API test below shows such entries are refused before they are stored);
    /// (12) a Caddy older than v2.11.6 gets the new fields and rejects the whole configuration.
    /// </summary>
    [CaddyFact]
    public async Task Kept_request_headers_reach_the_upstream_and_everything_else_with_underscore_or_dot_is_dropped()
    {
        var report = E2EArtifacts.Report(nameof(Kept_request_headers_reach_the_upstream_and_everything_else_with_underscore_or_dot_is_dropped));
        var observations = new JsonArray();
        report["observations"] = observations;
        await using var upstream = await RecordingBackend.StartAsync(https: false);
        using var c = new LiveCaddy();
        var plain = Build.Proxy("plain.test", upstream.Port);
        plain.AdvancedRoutesJson = """
            [{"match":[{"header":{"SM_USER":["never-sent"]}}],"handle":[{"handler":"static_response","body":"kept matcher"}]},
             {"match":[{"header":{"X.Other":["never-sent"]}}],"handle":[{"handler":"static_response","body":"dot matcher"}]}]
            """;
        c.Add(plain);
        c.Add(Build.Proxy("tls.test", upstream.Port, TlsMode.Internal));
        await c.StartAsync();

        (string Name, string Value)[] sent =
        [
            ("SM_USER", "alice"), ("SM-USER", "mallory"), ("X.Trace", "t1"), ("webhook_id", "w1"), ("X_Tenant.Id", "42"),
            ("X_Other", "o1"), ("X.Other", "o2"), ("X-Normal", "n1"),
        ];
        async Task<Dictionary<string, string>> Seen(string label, bool https)
        {
            upstream.Clear();
            var status = https
                ? (await c.HttpsRequestAsync("tls.test", "/", sent)).Status
                : (await c.HttpAsync("plain.test", "/", sent)).Status;
            Assert.Equal(200, status);
            var headers = upstream.Requests.Single().Headers;
            var got = new JsonObject();
            foreach (var (name, _) in sent) got[name] = headers.TryGetValue(name, out var v) ? v : null;
            observations.Add(new JsonObject { ["case"] = label, ["https"] = https, ["upstreamSaw"] = got });
            return headers;
        }

        // (1) default: nothing with "_" or "." gets through; hyphenated names do.
        var first = await c.ApplyAsync(); // (10)
        Assert.Equal(200, (await c.HttpsUntilAsync("tls.test", r => r.Status == 200, TimeSpan.FromSeconds(15))).Status);
        Assert.Contains(first.Warnings, w => w.Contains("SM_USER") && w.Contains("never matches"));
        Assert.Contains(first.Warnings, w => w.Contains("X.Other") && w.Contains("never matches")); // (9)
        foreach (var https in new[] { false, true })
        {
            var h = await Seen("default", https);
            foreach (var dropped in new[] { "SM_USER", "X.Trace", "webhook_id", "X_Tenant.Id", "X_Other", "X.Other" }) Assert.False(h.ContainsKey(dropped), dropped);
            Assert.Equal("mallory", h["SM-USER"]);
            Assert.Equal("n1", h["X-Normal"]);
        }

        // (2)-(8) with a list.
        c.UpdateSettings(s => s.KeptRequestHeaders = ["SM_USER", "X.Trace", "webhook_*", "X_Tenant.Id"]);
        var second = await c.ApplyAsync(); // (10)
        Assert.DoesNotContain(second.Warnings, w => w.Contains("SM_USER")); // (9) kept: the matcher can match now
        Assert.Contains(second.Warnings, w => w.Contains("X.Other") && w.Contains("never matches"));
        foreach (var https in new[] { false, true }) // (8)
        {
            var h = await Seen("with list", https);
            Assert.Equal("alice", h["SM_USER"]);     // (2)
            Assert.Equal("t1", h["X.Trace"]);        // (3)
            Assert.Equal("w1", h["webhook_id"]);     // (4)
            Assert.Equal("42", h["X_Tenant.Id"]);    // (7)
            Assert.False(h.ContainsKey("SM-USER"));  // (5)
            Assert.False(h.ContainsKey("X_Other"));  // (6)
            Assert.False(h.ContainsKey("X.Other"));
            Assert.Equal("n1", h["X-Normal"]);
        }
        var running = await c.RunningConfigAsync();
        foreach (var name in new[] { CaddyConfigGenerator.HttpServerName, CaddyConfigGenerator.HttpsServerName })
        {
            var srv = running["apps"]!["http"]!["servers"]![name]!;
            Assert.Equal(["SM_USER", "webhook_*", "X_Tenant.Id"], srv["expected_underscore_headers"]!.AsArray().Select(x => x!.GetValue<string>()));
            Assert.Equal(["X.Trace"], srv["expected_dot_headers"]!.AsArray().Select(x => x!.GetValue<string>()));
        }

        // (11) CONTROL: an entry the settings API refuses (see the settings API test below), loaded straight into Caddy,
        // makes it refuse the whole configuration.
        var control = await c.RunningConfigAsync();
        control["apps"]!["http"]!["servers"]![CaddyConfigGenerator.HttpServerName]!["expected_underscore_headers"] = new JsonArray("X-Normal");
        var refused = await Assert.ThrowsAsync<Core.CaddyAdminException>(() => c.LoadRawAsync(control));
        report["controlCaddyRefuses"] = refused.Message;
        Assert.Contains("expected_underscore_headers", refused.Message);

        // (12) an older Caddy must not get the fields it does not know.
        var old = CaddyConfigGenerator.Generate(Build.Input(c.S.Paths, c.S.Store.GetSettings<CaddySettings>(), [plain]) with { InstalledVersion = "v2.11.4" });
        var oldServer = old.Config["apps"]!["http"]!["servers"]![CaddyConfigGenerator.HttpServerName]!.AsObject();
        Assert.False(oldServer.ContainsKey("expected_underscore_headers"));
        Assert.False(oldServer.ContainsKey("expected_dot_headers"));
        Assert.Contains(old.Warnings, w => w.Contains("v2.11.6") && w.Contains("v2.11.4"));
        report["olderCaddyWarning"] = old.Warnings.First(w => w.Contains("v2.11.6"));

        E2EArtifacts.Write("kept-request-headers.json", report);
    }

    /// <summary>
    /// "Request header limit" (CaddySettings.MaxRequestHeaderKb).
    /// Ways it could fail:
    /// (1) the documented default is wrong: Caddy accepts headers far above 16 KiB without the setting (then the docs
    ///     and the troubleshooting entry about 431 mislead); (2) the setting is not applied: a 24 KiB header still gets
    ///     431 with a 64 KiB limit; (3) it is applied to one server only (HTTP or HTTPS); (4) the limit is not enforced:
    ///     a header above it passes; (5) Caddy rejects the configuration.
    /// </summary>
    [CaddyFact]
    public async Task Request_headers_above_the_limit_get_431_and_the_limit_can_be_raised()
    {
        var report = E2EArtifacts.Report(nameof(Request_headers_above_the_limit_get_431_and_the_limit_can_be_raised));
        var observations = new JsonArray();
        report["observations"] = observations;
        // Kestrel's own limit is 32 KiB for all request headers together; raised so that only Caddy's limit decides.
        await using var upstream = await RecordingBackend.StartAsync(https: false, maxRequestHeadersBytes: 256 * 1024);
        using var c = new LiveCaddy();
        c.Add(Build.Proxy("plain.test", upstream.Port));
        c.Add(Build.Proxy("tls.test", upstream.Port, TlsMode.Internal));
        await c.StartAsync();

        async Task<int> Send(string label, bool https, int headerKb)
        {
            var status = await StatusWithBigHeaderAsync(https ? c.HttpsPort : c.HttpPort, https ? "tls.test" : "plain.test", headerKb * 1024, https);
            observations.Add(new JsonObject { ["case"] = label, ["https"] = https, ["headerKiB"] = headerKb, ["status"] = status });
            return status;
        }

        await c.ApplyAsync(); // (5)
        Assert.Equal(200, (await c.HttpsUntilAsync("tls.test", r => r.Status == 200, TimeSpan.FromSeconds(15))).Status);
        foreach (var https in new[] { false, true })
        {
            Assert.Equal(200, await Send("default", https, 8));
            Assert.Equal(431, await Send("default", https, 24)); // (1)
        }

        c.UpdateSettings(s => s.MaxRequestHeaderKb = 64);
        await c.ApplyAsync(); // (5)
        foreach (var https in new[] { false, true }) // (3)
        {
            Assert.Equal(200, await Send("64 KiB limit", https, 24)); // (2)
            Assert.Equal(431, await Send("64 KiB limit", https, 96)); // (4)
        }
        var running = await c.RunningConfigAsync();
        Assert.Equal(64 * 1024, running["apps"]!["http"]!["servers"]![CaddyConfigGenerator.HttpsServerName]!["max_header_bytes"]!.GetValue<int>());

        E2EArtifacts.Write("request-header-limit.json", report);
    }

    /// <summary>
    /// "Upload idle timeout" and "Download idle timeout" (CaddySettings.ReadIdleTimeoutSeconds / WriteIdleTimeoutSeconds).
    /// Ways it could fail:
    /// (1) the timeout is not applied: an upload that stops sending is still waited for after the configured time;
    /// (2) without the setting a stalled upload is cut after a few seconds (the default is 1 minute), so the UI's
    ///     "empty = 1 minute" would be wrong; (3) a quiet but healthy stream is cut by the timeouts, which is what
    ///     Caddy v2.11.6 did: (a) server-sent events with a pause between events, (b) the same stream opened with a
    ///     POST that has a body, (c) an upgraded connection (WebSocket) that is idle in both directions;
    /// (4) Caddy rejects the configuration; (6) a Caddy older than v2.11.6 gets the new fields and rejects the whole configuration.
    /// </summary>
    [CaddyFact]
    public async Task Idle_timeouts_abort_stalled_uploads_but_not_quiet_streams()
    {
        var report = E2EArtifacts.Report(nameof(Idle_timeouts_abort_stalled_uploads_but_not_quiet_streams));
        var pause = TimeSpan.FromSeconds(4);
        await using var upstream = await RecordingBackend.StartAsync(https: false, custom: async ctx =>
        {
            switch (ctx.Request.Path.Value)
            {
                case "/upload":
                    // Kestrel's own minimum request body rate (240 bytes/s after 5 s) must not answer for Caddy.
                    ctx.Features.Get<Microsoft.AspNetCore.Server.Kestrel.Core.Features.IHttpMinRequestBodyDataRateFeature>()!.MinDataRate = null;
                    using (var body = new MemoryStream())
                    {
                        await ctx.Request.Body.CopyToAsync(body, ctx.RequestAborted);
                        await ctx.Response.WriteAsync($"received {body.Length}");
                    }
                    return true;
                case "/sse":
                    await ctx.Request.Body.CopyToAsync(Stream.Null, ctx.RequestAborted);
                    ctx.Response.ContentType = "text/event-stream";
                    await ctx.Response.WriteAsync("data: first\n\n");
                    await ctx.Response.Body.FlushAsync();
                    await Task.Delay(pause, ctx.RequestAborted);
                    await ctx.Response.WriteAsync("data: second\n\n");
                    return true;
                case "/upgrade":
                    var up = ctx.Features.Get<IHttpUpgradeFeature>()!;
                    ctx.Response.Headers.Connection = "Upgrade";
                    ctx.Response.Headers.Upgrade = "cpm-echo";
                    await using (var stream = await up.UpgradeAsync())
                    {
                        var buffer = new byte[256];
                        int n;
                        while ((n = await stream.ReadAsync(buffer, ctx.RequestAborted)) > 0) await stream.WriteAsync(buffer.AsMemory(0, n), ctx.RequestAborted);
                    }
                    return true;
                default:
                    return false;
            }
        });
        using var c = new LiveCaddy();
        c.Add(Build.Proxy("idle.test", upstream.Port));
        await c.StartAsync();
        await c.ApplyAsync(); // (4)

        // (2) default (1 minute): an upload that pauses longer than the timeout configured below is still completed.
        var patient = await StalledUploadAsync(c.HttpPort, pause, finish: true);
        report["defaultStalledUpload"] = patient.ToJson();
        Assert.False(patient.ClosedWhileStalled, patient.Response);
        Assert.Contains("200", patient.StatusLine);
        Assert.Contains("received 1000", patient.Response);

        c.UpdateSettings(s => { s.ReadIdleTimeoutSeconds = 1; s.WriteIdleTimeoutSeconds = 1; });
        await c.ApplyAsync(); // (4)
        var running = await c.RunningConfigAsync();
        var srv = running["apps"]!["http"]!["servers"]![CaddyConfigGenerator.HttpServerName]!;
        Assert.Equal("1s", srv["read_idle_timeout"]!.GetValue<string>());
        Assert.Equal("1s", srv["write_idle_timeout"]!.GetValue<string>());

        // (1) the stalled upload is cut.
        var cut = await StalledUploadAsync(c.HttpPort, pause, finish: false);
        report["stalledUploadWith1s"] = cut.ToJson();
        Assert.True(cut.ClosedWhileStalled, "The stalled upload was still open after " + pause);
        Assert.DoesNotContain("received", cut.Response);

        // (3a) SSE with a pause four times the timeout.
        var sse = await c.HttpAsync("idle.test", "/sse");
        report["sseGet"] = new JsonObject { ["status"] = sse.Status, ["body"] = sse.Body };
        Assert.Equal(200, sse.Status);
        Assert.Contains("data: first", sse.Body);
        Assert.Contains("data: second", sse.Body);
        // (3b) the same stream opened with a POST body.
        var ssePost = await RawExchangeAsync(c.HttpPort, "POST /sse HTTP/1.1\r\nHost: idle.test\r\nConnection: close\r\nContent-Type: text/plain\r\nContent-Length: 5\r\n\r\nhello", TimeSpan.FromSeconds(20));
        report["ssePost"] = ssePost;
        Assert.StartsWith("HTTP/1.1 200", ssePost);
        Assert.Contains("data: second", ssePost);
        // (3c) an upgraded connection that is silent for four times the timeout, then echoes.
        var echo = await UpgradeEchoAsync(c.HttpPort, pause);
        report["upgradeAfterIdle"] = echo;
        Assert.Equal("ping-after-idle", echo);

        // (6) an older Caddy must not get the fields it does not know.
        var old = CaddyConfigGenerator.Generate(Build.Input(c.S.Paths, c.S.Store.GetSettings<CaddySettings>(), [Build.Proxy("idle.test", upstream.Port)]) with { InstalledVersion = "v2.10.2" });
        var oldServer = old.Config["apps"]!["http"]!["servers"]![CaddyConfigGenerator.HttpServerName]!.AsObject();
        Assert.False(oldServer.ContainsKey("read_idle_timeout"));
        Assert.False(oldServer.ContainsKey("write_idle_timeout"));
        Assert.Contains(old.Warnings, w => w.Contains("idle timeout") && w.Contains("v2.11.6"));

        E2EArtifacts.Write("idle-timeouts.json", report);
    }

    /// <summary>
    /// With HTTP/3 on, every reverse proxy buffers the start of the request body (request_buffers), and since
    /// v2.11.7 Caddy refuses a request that asks for incremental forwarding (RFC 10036 "Incremental: ?1") when it
    /// has to buffer: 501. The docs state this.
    /// Ways it could fail: (1) the docs are wrong: such a request is answered 200 with HTTP/3 on, or 501 with HTTP/3
    /// off; (2) requests without the header, or with it but without a body, are refused too.
    /// </summary>
    [CaddyFact]
    public async Task Incremental_uploads_get_501_only_while_http3_is_on()
    {
        var report = E2EArtifacts.Report(nameof(Incremental_uploads_get_501_only_while_http3_is_on));
        await using var upstream = await RecordingBackend.StartAsync(https: false);
        using var c = new LiveCaddy();
        c.Add(Build.Proxy("inc.test", upstream.Port));
        // HTTP/3 needs an HTTPS host to exist; the requests below use the plain-HTTP host, whose reverse proxy is
        // generated with the same request_buffers.
        c.Add(Build.Proxy("inc-tls.test", upstream.Port, TlsMode.Internal));
        await c.StartAsync();

        async Task<JsonObject> Probe(bool http3)
        {
            c.UpdateSettings(s => s.EnableHttp3 = http3);
            await c.ApplyAsync();
            string Post(string extra) => $"POST /in HTTP/1.1\r\nHost: inc.test\r\nConnection: close\r\n{extra}Content-Length: 5\r\n\r\nhello";
            var incremental = await RawExchangeAsync(c.HttpPort, Post("Incremental: ?1\r\n"), TimeSpan.FromSeconds(10));
            var normal = await RawExchangeAsync(c.HttpPort, Post(""), TimeSpan.FromSeconds(10));
            var bodiless = await c.HttpAsync("inc.test", "/in", ("Incremental", "?1"));
            return new JsonObject
            {
                ["http3"] = http3,
                ["incrementalPost"] = incremental.Split("\r\n")[0],
                ["normalPost"] = normal.Split("\r\n")[0],
                ["incrementalGetWithoutBody"] = bodiless.Status,
            };
        }

        var off = await Probe(http3: false);
        var on = await Probe(http3: true);
        report["http3Off"] = off;
        report["http3On"] = on;
        Assert.StartsWith("HTTP/1.1 200", off["incrementalPost"]!.GetValue<string>()); // (1)
        Assert.StartsWith("HTTP/1.1 501", on["incrementalPost"]!.GetValue<string>());  // (1)
        foreach (var probe in new[] { off, on })                                        // (2)
        {
            Assert.StartsWith("HTTP/1.1 200", probe["normalPost"]!.GetValue<string>());
            Assert.Equal(200, probe["incrementalGetWithoutBody"]!.GetValue<int>());
        }

        E2EArtifacts.Write("http3-incremental-uploads.json", report);
    }

    /// <summary>
    /// The four settings through PUT /api/settings/caddy, as the console sends them.
    /// Ways it could fail: (1) values Caddy would refuse or that make no sense are stored (a header size of 0 or above
    /// 1 MiB, idle timeouts of 0 or above an hour, header names without "_" or ".", with "*" anywhere but the end, with
    /// characters that are not allowed in a header name) and every later apply fails; (2) valid values are
    /// not stored or come back changed; (3) the fields cannot be cleared again (null / empty list = Caddy defaults);
    /// (4) names are stored with surrounding spaces, or twice when they only differ in case.
    /// </summary>
    [CaddyFact]
    public async Task Request_limit_settings_are_validated_stored_and_clearable_through_the_settings_api()
    {
        var report = E2EArtifacts.Report(nameof(Request_limit_settings_are_validated_stored_and_clearable_through_the_settings_api));
        var rows = new JsonArray();
        report["requests"] = rows;
        await using var api = ApiHost.Start(installBinary: true);
        var json = Core.JsonDefaults.Api;
        async Task<(HttpStatusCode Status, string Body)> Put(JsonObject body)
        {
            var r = await System.Net.Http.Json.HttpClientJsonExtensions.PutAsJsonAsync(api.Client, "/api/settings/caddy", body, json);
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

        // (1)
        foreach (var bad in new[] { 0, -1, 3, 1025 }) await Refused("maxRequestHeaderKb", bad, "maxRequestHeaderKb");
        foreach (var bad in new[] { 0, -5, 3601 })
        {
            await Refused("readIdleTimeoutSeconds", bad, "readIdleTimeoutSeconds");
            await Refused("writeIdleTimeoutSeconds", bad, "writeIdleTimeoutSeconds");
        }
        foreach (var bad in new[] { "X-Normal", "SM*USER", "web_*hook", "Ünder_score", "has space_x", "*", "_*x" })
            await Refused("keptRequestHeaders", new JsonArray(bad), "keptRequestHeaders[0]");

        // (2)(4)
        var ok = await Put(new JsonObject
        {
            ["maxRequestHeaderKb"] = 64, ["readIdleTimeoutSeconds"] = 120, ["writeIdleTimeoutSeconds"] = 30,
            ["keptRequestHeaders"] = new JsonArray(" SM_USER ", "X.Trace", "webhook_*", "sm_user"),
        });
        Assert.Equal(HttpStatusCode.OK, ok.Status);
        var stored = api.Env.Store.GetSettings<CaddySettings>();
        Assert.Equal(64, stored.MaxRequestHeaderKb);
        Assert.Equal(120, stored.ReadIdleTimeoutSeconds);
        Assert.Equal(30, stored.WriteIdleTimeoutSeconds);
        Assert.Equal(["SM_USER", "X.Trace", "webhook_*"], stored.KeptRequestHeaders);
        var got = await System.Net.Http.Json.HttpClientJsonExtensions.GetFromJsonAsync<JsonObject>(api.Client, "/api/settings/caddy", json);
        Assert.Equal(64, got!["maxRequestHeaderKb"]!.GetValue<int>());
        Assert.Equal(3, got["keptRequestHeaders"]!.AsArray().Count);

        // (3)
        var cleared = await Put(new JsonObject
        {
            ["maxRequestHeaderKb"] = null, ["readIdleTimeoutSeconds"] = null, ["writeIdleTimeoutSeconds"] = null, ["keptRequestHeaders"] = new JsonArray(),
        });
        Assert.Equal(HttpStatusCode.OK, cleared.Status);
        stored = api.Env.Store.GetSettings<CaddySettings>();
        Assert.Null(stored.MaxRequestHeaderKb);
        Assert.Null(stored.ReadIdleTimeoutSeconds);
        Assert.Null(stored.WriteIdleTimeoutSeconds);
        Assert.Empty(stored.KeptRequestHeaders);

        E2EArtifacts.Write("request-limits-settings-api.json", report);
    }

    // ------------------------------------------------------------------ raw socket helpers

    private sealed record StalledUpload(bool ClosedWhileStalled, double ClosedAfterSeconds, string Response)
    {
        public string StatusLine => Response.Split("\r\n")[0];
        public JsonObject ToJson() => new() { ["closedWhileStalled"] = ClosedWhileStalled, ["closedAfterSeconds"] = Math.Round(ClosedAfterSeconds, 2), ["response"] = Response };
    }

    /// <summary>
    /// Announces a 1000-byte upload, sends 10 bytes and then nothing for <paramref name="stall"/>. Reports whether the
    /// server ended the connection during the stall; with <paramref name="finish"/> the rest is sent afterwards.
    /// </summary>
    private static async Task<StalledUpload> StalledUploadAsync(int port, TimeSpan stall, bool finish)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        await using var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("POST /upload HTTP/1.1\r\nHost: idle.test\r\nConnection: close\r\nContent-Length: 1000\r\n\r\n" + new string('x', 10)));
        var clock = Stopwatch.StartNew();
        var received = new MemoryStream();
        var buffer = new byte[4096];
        var closed = false;
        using (var stallOver = new CancellationTokenSource(stall))
        {
            try
            {
                while (true)
                {
                    var n = await stream.ReadAsync(buffer, stallOver.Token);
                    if (n == 0) { closed = true; break; }
                    received.Write(buffer, 0, n);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { closed = true; }
        }
        var closedAfter = clock.Elapsed.TotalSeconds;
        if (!closed && finish)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(new string('x', 990)));
            using var done = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                int n;
                while ((n = await stream.ReadAsync(buffer, done.Token)) > 0) received.Write(buffer, 0, n);
            }
            catch (IOException) { }
        }
        return new StalledUpload(closed, closedAfter, Encoding.Latin1.GetString(received.ToArray()));
    }

    /// <summary>
    /// Status of a GET that carries one header of <paramref name="headerBytes"/> bytes. Only the status line is read: a
    /// server that refuses the request stops reading it and closes, which can reset the connection while the rest of
    /// the answer is still on its way.
    /// </summary>
    private static async Task<int> StatusWithBigHeaderAsync(int port, string host, int headerBytes, bool tls)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(IPAddress.Loopback, port);
                Stream stream = tcp.GetStream();
                if (tls)
                {
                    var ssl = new System.Net.Security.SslStream(stream, leaveInnerStreamOpen: false, (_, _, _, _) => true);
                    await ssl.AuthenticateAsClientAsync(new System.Net.Security.SslClientAuthenticationOptions
                    {
                        TargetHost = host,
                        ApplicationProtocols = [System.Net.Security.SslApplicationProtocol.Http11],
                    });
                    stream = ssl;
                }
                await using (stream)
                {
                    var request = Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\nX-Big: {new string('a', headerBytes)}\r\n\r\n");
                    var line = new StringBuilder();
                    var buffer = new byte[256];
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var write = stream.WriteAsync(request, cts.Token).AsTask();
                    while (!line.ToString().Contains("\r\n"))
                    {
                        var n = await stream.ReadAsync(buffer, cts.Token);
                        if (n == 0) break;
                        line.Append(Encoding.Latin1.GetString(buffer, 0, n));
                    }
                    try { await write; } catch (IOException) { }
                    var parts = line.ToString().Split(' ');
                    if (parts.Length > 1 && int.TryParse(parts[1], out var status)) return status;
                }
            }
            catch (IOException) when (attempt < 5)
            {
            }
            if (attempt == 5) throw new IOException($"No HTTP status for a {headerBytes}-byte header on port {port} after 5 attempts.");
            await Task.Delay(200);
        }
    }

    /// <summary>Sends the request bytes as written and returns everything the server answers until it closes.</summary>
    private static async Task<string> RawExchangeAsync(int port, string request, TimeSpan timeout)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        await using var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        using var ms = new MemoryStream();
        using var cts = new CancellationTokenSource(timeout);
        try { await stream.CopyToAsync(ms, cts.Token); }
        catch (IOException) { }
        return Encoding.Latin1.GetString(ms.ToArray());
    }

    /// <summary>Upgrades the connection, stays silent for <paramref name="idle"/>, then sends a line and returns the echo.</summary>
    private static async Task<string> UpgradeEchoAsync(int port, TimeSpan idle)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        await using var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("GET /upgrade HTTP/1.1\r\nHost: idle.test\r\nConnection: Upgrade\r\nUpgrade: cpm-echo\r\n\r\n"));
        using var cts = new CancellationTokenSource(idle + TimeSpan.FromSeconds(20));
        var buffer = new byte[4096];
        var head = new StringBuilder();
        while (!head.ToString().Contains("\r\n\r\n"))
        {
            var n = await stream.ReadAsync(buffer, cts.Token);
            if (n == 0) return "closed before the upgrade: " + head;
            head.Append(Encoding.Latin1.GetString(buffer, 0, n));
        }
        if (!head.ToString().StartsWith("HTTP/1.1 101", StringComparison.Ordinal)) return "no upgrade: " + head;
        await Task.Delay(idle, cts.Token);
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes("ping-after-idle"), cts.Token);
            var n = await stream.ReadAsync(buffer, cts.Token);
            return n == 0 ? "closed after the idle period" : Encoding.Latin1.GetString(buffer, 0, n);
        }
        catch (IOException ex)
        {
            return "connection lost after the idle period: " + ex.Message;
        }
    }
}
