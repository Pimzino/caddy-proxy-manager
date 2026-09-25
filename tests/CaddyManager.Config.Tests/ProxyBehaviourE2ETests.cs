using System.Text.Json.Nodes;
using CaddyManager.Config.Generation;
using CaddyManager.Core;
using CaddyManager.Core.Models;

namespace CaddyManager.Config.Tests;

/// <summary>
/// End-to-end proof, with the real Caddy binary, of generator fixes that were kept from the original audit diff
/// without a test (review round 2): client IP from X-Forwarded-For, headers on error responses, header operation
/// folding, per-host access logs and the plain-HTTP server's protocols. Every test has a CONTROL that reloads a
/// hand-modified copy of the running config reproducing the original behaviour.
/// </summary>
public sealed class ProxyBehaviourE2ETests
{
    /// <summary>
    /// X-Forwarded-For spoofing through a trusted proxy (research #32, high). Without trusted_proxies_strict Caddy uses
    /// the LEFT-most X-Forwarded-For address, which the client writes itself; a real proxy appends the address it saw.
    /// https://caddyserver.com/docs/caddyfile/options#trusted-proxies-strict
    /// Ways it could fail: (1) a client that sends "X-Forwarded-For: 10.1.1.1" through the trusted proxy (which appends
    /// its real address 203.0.113.9) passes an allow-10.1.1.1 access list; (2) a real client 10.1.1.1 behind the proxy
    /// is refused (strict mode reads the wrong end); (3) a client not in the list gets through without spoofing;
    /// (4) the upstream does not see the forwarded chain; (5) the control (strict mode off) does not let the spoofed
    /// request through, i.e. the test would not notice the fix being removed.
    /// </summary>
    [CaddyFact]
    public async Task Spoofed_x_forwarded_for_cannot_pass_an_ip_access_list_behind_a_trusted_proxy()
    {
        var report = E2EArtifacts.Report(nameof(Spoofed_x_forwarded_for_cannot_pass_an_ip_access_list_behind_a_trusted_proxy));
        await using var upstream = await RecordingBackend.StartAsync(https: false);
        // The test client (127.0.0.1) plays the load balancer in front of Caddy.
        using var c = new LiveCaddy(s => s.TrustedProxies = ["127.0.0.1/32"]);
        c.S.Store.Col<AccessList>().Insert(new AccessList { Id = "office", Name = "Office", Rules = [new IpRule { Action = IpRuleAction.Allow, Cidr = "10.1.1.1/32" }] });
        var h = Build.Proxy("xff.test", upstream.Port);
        h.AccessListId = "office";
        c.Add(h);
        await c.StartAsync();
        await c.ApplyAsync();

        var rows = new JsonArray();
        report["observations"] = rows;
        async Task<int> Get(string label, string xff)
        {
            var r = await c.HttpAsync("xff.test", "/", ("X-Forwarded-For", xff));
            rows.Add(new JsonObject { ["case"] = label, ["xForwardedFor"] = xff, ["status"] = r.Status });
            return r.Status;
        }
        Assert.Equal(403, await Get("spoofed leftmost entry", "10.1.1.1, 203.0.113.9"));   // (1)
        Assert.Equal(200, await Get("real allowed client", "10.1.1.1"));                   // (2)
        Assert.Equal(403, await Get("real other client", "203.0.113.9"));                  // (3)
        var seen = upstream.Requests.Single().Headers["X-Forwarded-For"];
        report["upstreamXForwardedFor"] = seen;
        Assert.StartsWith("10.1.1.1", seen);                                               // (4)

        // (5) CONTROL: strict mode removed.
        var control = await c.RunningConfigAsync();
        foreach (var (_, srv) in control["apps"]!["http"]!["servers"]!.AsObject()) srv!.AsObject().Remove("trusted_proxies_strict");
        await c.LoadRawAsync(control);
        var spoofed = await Get("CONTROL without strict: spoofed leftmost entry", "10.1.1.1, 203.0.113.9");
        Assert.Equal(200, spoofed);
        report["controlReproducedSpoofing"] = spoofed == 200;

        E2EArtifacts.Write("xff-trusted-proxies-strict.json", report);
    }

    /// <summary>
    /// The host's response headers and HSTS on error responses (research #48). Deferred header operations "do not take
    /// effect if an error occurs later in the middleware chain", so 401 (basic auth) and 502 (dead upstream) went out
    /// without HSTS or the host's security headers.
    /// https://caddyserver.com/docs/json/apps/http/servers/routes/handle/headers/response/deferred/
    /// Ways it could fail: (1) a 401 over HTTPS lacks HSTS or the custom header; (2) a 502 over HTTPS lacks them;
    /// (3) the plain-HTTP copy of a host (ForceHttps off) sends HSTS on errors (ignored over HTTP, RFC 6797 §8.1) or
    /// loses the custom header; (4) a host's error headers leak onto another host's errors; (5) a normal 200 loses
    /// them; (6) the control (the server's error routes removed) still shows the headers.
    /// </summary>
    [CaddyFact]
    public async Task Host_headers_and_hsts_are_sent_on_401_and_502_error_responses()
    {
        var report = E2EArtifacts.Report(nameof(Host_headers_and_hsts_are_sent_on_401_and_502_error_responses));
        await using var upstream = await RecordingBackend.StartAsync(https: false);
        var dead = Net.FreeTcpPort();
        using var c = new LiveCaddy();
        c.S.Store.Col<AccessList>().Insert(new AccessList
        {
            Id = "auth", Name = "Login",
            Users = [new AccessUser { Username = "bob", PasswordHash = Passwords.Hash("hunter2") }],
        });
        SiteHost Secure(string domain, int port)
        {
            var h = Build.Proxy(domain, port, TlsMode.Internal);
            h.Hsts = true;
            h.HstsMaxAgeSeconds = 31536000;
            h.ForceHttps = false; // also served over HTTP, to check the plain-HTTP copy
            h.ResponseHeaders = [new HeaderOp { Action = HeaderAction.Set, Name = "X-Frame-Options", Value = "DENY" }];
            return h;
        }
        var auth = Secure("auth.test", upstream.Port);
        auth.AccessListId = "auth";
        c.Add(auth);
        c.Add(Secure("bad.test", dead));
        c.Add(Build.Proxy("other.test", dead, TlsMode.Internal)); // no headers configured
        await c.StartAsync();
        await c.ApplyAsync();
        await c.HttpsUntilAsync("auth.test", r => r.Handshake, TimeSpan.FromSeconds(20));
        await c.HttpsUntilAsync("bad.test", r => r.Handshake, TimeSpan.FromSeconds(20));
        await c.HttpsUntilAsync("other.test", r => r.Handshake, TimeSpan.FromSeconds(20));

        var rows = new JsonArray();
        report["observations"] = rows;
        async Task<RawHttp.Response> Get(string scheme, string host, params (string, string)[] headers)
        {
            var r = scheme == "https" ? await c.HttpsRequestAsync(host, "/", headers) : await c.HttpAsync(host, "/", headers);
            rows.Add(new JsonObject { ["request"] = $"{scheme}://{host}/", ["status"] = r.Status, ["hsts"] = r.Header("Strict-Transport-Security"), ["xFrameOptions"] = r.Header("X-Frame-Options") });
            return r;
        }
        var basic = ("Authorization", "Basic " + Convert.ToBase64String("bob:hunter2"u8.ToArray()));

        var r401 = await Get("https", "auth.test");
        Assert.Equal(401, r401.Status);
        Assert.Equal("max-age=31536000", r401.Header("Strict-Transport-Security"));   // (1)
        Assert.Equal("DENY", r401.Header("X-Frame-Options"));
        var r502 = await Get("https", "bad.test");
        Assert.Equal(502, r502.Status);
        Assert.Equal("max-age=31536000", r502.Header("Strict-Transport-Security"));   // (2)
        Assert.Equal("DENY", r502.Header("X-Frame-Options"));
        var h401 = await Get("http", "auth.test");
        Assert.Equal(401, h401.Status);
        Assert.Null(h401.Header("Strict-Transport-Security"));                        // (3)
        Assert.Equal("DENY", h401.Header("X-Frame-Options"));
        var other = await Get("https", "other.test");
        Assert.Equal(502, other.Status);
        Assert.Null(other.Header("Strict-Transport-Security"));                       // (4)
        Assert.Null(other.Header("X-Frame-Options"));
        var ok = await Get("https", "auth.test", basic);
        Assert.Equal(200, ok.Status);
        Assert.Equal("max-age=31536000", ok.Header("Strict-Transport-Security"));     // (5)
        Assert.Equal("DENY", ok.Header("X-Frame-Options"));

        // (6) CONTROL: without the error routes the deferred headers are lost on errors.
        var control = await c.RunningConfigAsync();
        foreach (var (_, srv) in control["apps"]!["http"]!["servers"]!.AsObject()) srv!.AsObject().Remove("errors");
        await c.LoadRawAsync(control);
        var c401 = await Get("https", "auth.test");
        var c502 = await Get("https", "bad.test");
        Assert.Equal(401, c401.Status);
        Assert.Null(c401.Header("Strict-Transport-Security"));
        Assert.Null(c502.Header("Strict-Transport-Security"));
        report["controlReproducedMissingHeaders"] = true;

        E2EArtifacts.Write("error-response-headers.json", report);
    }

    /// <summary>
    /// "Delete X, then add X: v" header operations (research #48). Caddy applies one handler's operations in a fixed
    /// order — add, set, delete — not in the listed order, so the delete removed the value just added.
    /// https://github.com/caddyserver/caddy/blob/v2.11.4/modules/caddyhttp/headers/headers.go (ApplyTo)
    /// Ways it could fail: (1) the response lacks the header the host added after deleting the upstream's value;
    /// (2) the upstream's value survives the delete; (3) the request header towards the upstream has the same
    /// problem (client value kept, or host value missing); (4) "set a, add b" does not send both values;
    /// (5) the control (unfolded add + delete in one handler) does not lose the header.
    /// </summary>
    [CaddyFact]
    public async Task Delete_then_add_of_a_header_sends_only_the_added_value()
    {
        var report = E2EArtifacts.Report(nameof(Delete_then_add_of_a_header_sends_only_the_added_value));
        await using var upstream = await RecordingBackend.StartAsync(https: false, custom: ctx =>
        {
            ctx.Response.Headers["X-Powered-By"] = "upstream";
            ctx.Response.Headers["X-Multi"] = "upstream";
            return Task.FromResult(false);
        });
        using var c = new LiveCaddy();
        var h = Build.Proxy("fold.test", upstream.Port);
        h.ResponseHeaders =
        [
            new HeaderOp { Action = HeaderAction.Delete, Name = "X-Powered-By" },
            new HeaderOp { Action = HeaderAction.Add, Name = "X-Powered-By", Value = "proxy" },
            new HeaderOp { Action = HeaderAction.Set, Name = "X-Multi", Value = "a" },
            new HeaderOp { Action = HeaderAction.Add, Name = "X-Multi", Value = "b" },
        ];
        h.RequestHeaders =
        [
            new HeaderOp { Action = HeaderAction.Delete, Name = "X-Test" },
            new HeaderOp { Action = HeaderAction.Add, Name = "X-Test", Value = "from-host" },
        ];
        c.Add(h);
        await c.StartAsync();
        await c.ApplyAsync();

        var r = await c.HttpAsync("fold.test", "/", ("X-Test", "from-client"));
        var sentUp = upstream.Requests.Single().Headers.GetValueOrDefault("X-Test");
        report["fixed"] = new JsonObject
        {
            ["status"] = r.Status,
            ["xPoweredBy"] = new JsonArray((r.Headers.GetValueOrDefault("X-Powered-By") ?? []).Select(v => (JsonNode)v).ToArray()),
            ["xMulti"] = new JsonArray((r.Headers.GetValueOrDefault("X-Multi") ?? []).Select(v => (JsonNode)v).ToArray()),
            ["upstreamXTest"] = sentUp,
        };
        Assert.Equal(200, r.Status);
        Assert.Equal(["proxy"], r.Headers["X-Powered-By"]);                                               // (1)(2)
        Assert.Equal("from-host", sentUp);                                                                // (3)
        Assert.Equal(["a", "b"], string.Join(",", r.Headers["X-Multi"]).Split(',').Select(v => v.Trim())); // (4)

        // (5) CONTROL: the literal operations in one handler (add + delete).
        var control = await c.RunningConfigAsync();
        foreach (var hh in JsonWalk.Handlers(control, "headers"))
            if (hh["response"] is JsonObject resp && resp["set"]?["X-Powered-By"] is not null)
            {
                resp["set"]!.AsObject().Remove("X-Powered-By");
                resp["add"] = new JsonObject { ["X-Powered-By"] = new JsonArray("proxy") };
                resp["delete"] = new JsonArray("X-Powered-By");
            }
        await c.LoadRawAsync(control);
        var broken = await c.HttpAsync("fold.test", "/");
        report["controlXPoweredBy"] = broken.Header("X-Powered-By");
        Assert.Null(broken.Header("X-Powered-By"));

        E2EArtifacts.Write("header-op-folding.json", report);
    }

    /// <summary>
    /// Per-host access logs for a Host header in another letter case (research #55). Caddy routes "LOG.TEST" to the
    /// host (host matching ignores case) but picks the access logger with an exact, case-sensitive logger_names lookup
    /// (logging.go v2.11.4), so the line went to the default log. The host now names its logger in the
    /// access_logger_names variable. https://github.com/caddyserver/caddy/blob/v2.11.4/modules/caddyhttp/logging.go
    /// Ways it could fail: (1) the upper-case request is missing from the host's access log; (2) it is written to
    /// caddy.log (the process log) instead; (3) the lower-case request stops being logged; (4) another host's requests
    /// end up in this host's file; (5) the control (variable removed) still logs the upper-case request to the file.
    /// </summary>
    [CaddyFact]
    public async Task Upper_case_host_names_are_logged_to_the_hosts_access_log()
    {
        var report = E2EArtifacts.Report(nameof(Upper_case_host_names_are_logged_to_the_hosts_access_log));
        await using var upstream = await RecordingBackend.StartAsync(https: false);
        using var c = new LiveCaddy();
        var h = Build.Proxy("log.test", upstream.Port);
        h.Id = "logged";
        h.AccessLog = true;
        c.Add(h);
        c.Add(Build.Proxy("quiet.test", upstream.Port));
        await c.StartAsync();
        await c.ApplyAsync();
        var file = Path.Combine(c.S.Paths.AccessLogDir, "log.test.log");

        string Read(string path)
        {
            if (!File.Exists(path)) return "";
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new StreamReader(fs).ReadToEnd();
        }
        async Task<bool> Logged(string marker) => await Wait.For(() => Task.FromResult(Read(file).Contains(marker)), TimeSpan.FromSeconds(5));

        Assert.Equal(200, (await c.HttpAsync("LOG.TEST", "/upper-marker")).Status);
        Assert.Equal(200, (await c.HttpAsync("log.test", "/lower-marker")).Status);
        Assert.Equal(200, (await c.HttpAsync("quiet.test", "/quiet-marker")).Status);
        var upper = await Logged("/upper-marker");
        var lower = await Logged("/lower-marker");
        var processLog = Read(c.S.Paths.CaddyProcessLog);
        report["fixed"] = new JsonObject
        {
            ["upperCaseInHostLog"] = upper, ["lowerCaseInHostLog"] = lower,
            ["upperCaseInProcessLog"] = processLog.Contains("/upper-marker"), ["otherHostInHostLog"] = Read(file).Contains("/quiet-marker"),
        };
        Assert.True(upper, "upper-case request missing from the host's access log");  // (1)
        Assert.DoesNotContain("/upper-marker", processLog);                           // (2)
        Assert.True(lower);                                                            // (3)
        Assert.DoesNotContain("/quiet-marker", Read(file));                            // (4)

        // (5) CONTROL: without the access_logger_names variable.
        var control = await c.RunningConfigAsync();
        foreach (var sub in JsonWalk.Handlers(control, "subroute"))
            if (sub["routes"] is JsonArray routes)
                foreach (var route in routes.OfType<JsonObject>().Where(r => r.ToJsonString().Contains("access_logger_names")).ToList())
                    routes.Remove(route);
        await c.LoadRawAsync(control);
        Assert.Equal(200, (await c.HttpAsync("LOG.TEST", "/control-marker")).Status);
        Assert.Equal(200, (await c.HttpAsync("log.test", "/control-lower")).Status);
        Assert.True(await Logged("/control-lower")); // the file is written, so a missing line is not a timing issue
        var controlLogged = Read(file).Contains("/control-marker");
        report["controlUpperCaseInHostLog"] = controlLogged;
        Assert.False(controlLogged);

        E2EArtifacts.Write("access-log-upper-case-host.json", report);
    }

    /// <summary>
    /// The plain-HTTP server lists only HTTP/1 (research #29). HTTP/2 needs TLS; Caddy skips "h2" on a cleartext
    /// listener and logs a warning on every config load (server.go v2.11.4), noise that feeds the log-based alerts.
    /// Ways it could fail: (1) the warning "HTTP/2 skipped because it requires TLS" appears after a load; (2) plain
    /// HTTP stops being served; (3) the control (srv1 with h1+h2) does not log the warning.
    /// </summary>
    [CaddyFact]
    public async Task Plain_http_server_is_http1_only_and_loads_without_the_h2_warning()
    {
        var report = E2EArtifacts.Report(nameof(Plain_http_server_is_http1_only_and_loads_without_the_h2_warning));
        await using var upstream = await RecordingBackend.StartAsync(https: false);
        using var c = new LiveCaddy();
        c.Add(Build.Proxy("plain.test", upstream.Port));
        await c.StartAsync();
        await c.ApplyAsync();
        Assert.Equal(200, (await c.HttpAsync("plain.test", "/")).Status); // (2)
        const string warning = "HTTP/2 skipped because it requires TLS";
        var cfg = await c.RunningConfigAsync();
        report["srv1Protocols"] = cfg["apps"]!["http"]!["servers"]![CaddyConfigGenerator.HttpServerName]!["protocols"]!.DeepClone();
        var fixedLog = c.ProcessLog();
        report["warningAfterLoad"] = fixedLog.Contains(warning);
        Assert.DoesNotContain(warning, fixedLog); // (1)

        // (3) CONTROL
        cfg["apps"]!["http"]!["servers"]![CaddyConfigGenerator.HttpServerName]!["protocols"] = new JsonArray("h1", "h2");
        await c.LoadRawAsync(cfg);
        var warned = await Wait.For(() => Task.FromResult(c.ProcessLog().Contains(warning)), TimeSpan.FromSeconds(5));
        report["controlWarned"] = warned;
        Assert.True(warned);

        E2EArtifacts.Write("srv1-h1-only.json", report);
    }
}
