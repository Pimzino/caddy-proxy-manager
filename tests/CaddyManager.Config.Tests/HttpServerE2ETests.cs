using System.Text.Json.Nodes;
using CaddyManager.Config.Generation;
using CaddyManager.Core.Models;

namespace CaddyManager.Config.Tests;

/// <summary>
/// End-to-end checks of what the real Caddy binary does with the generated HTTP-side config: every test starts
/// `caddy run`, applies the model through CaddyConfigService, sends real requests and writes a JSON artifact.
/// Where feasible a CONTROL reloads a hand-modified copy of the config that reproduces the original bug, proving the
/// assertions would catch it.
/// </summary>
public sealed class HttpServerE2ETests
{
    /// <summary>
    /// HTTP→HTTPS redirects (research #0/#2/#67/#68, review round 2: the port heuristic) with the "public HTTPS port"
    /// setting shown in Settings > Listeners and the host editor.
    /// Ways it could fail:
    /// (1) hosts that only use custom certificates get the default site (404) on http:// because Caddy only inserts
    ///     its redirects when it manages at least one certificate; (2) the Location drops the query or path, or decodes
    ///     percent-encoded characters in the path (%3F, %2F would change the URL);
    /// (3) public port unset and HttpsPort ≠ 443: the Location omits ":HttpsPort" (clients go to 443, where nothing
    ///     listens) — whether or not the client's Host carried a port;
    /// (4) public port 443 (NAT/port forwarding 443 → HttpsPort): the Location carries the internal HttpsPort, which is
    ///     not reachable from outside; (5) another public port (e.g. 8443) is not used;
    /// (6) an IPv6-literal Host ("[::1]:port" or "[::1]") loses its brackets ("https://::1:8443/" is not a URL);
    /// (7) the plain-HTTP copy of a ForceHttps=false host sends HSTS (ignored over HTTP, RFC 6797 §8.1) or loses its
    ///     content; (8) Caddy rejects the generated config;
    /// (9) controls: Caddy's own redirects with custom certificates only (no redirect), and the previous
    ///     "{http.request.host}" Location with an IPv6 Host (brackets lost).
    /// </summary>
    [CaddyFact]
    public async Task Force_https_redirects_use_the_public_https_port_and_keep_the_request_exactly()
    {
        var report = E2EArtifacts.Report(nameof(Force_https_redirects_use_the_public_https_port_and_keep_the_request_exactly));
        var observations = new JsonArray();
        report["observations"] = observations;
        using var c = new LiveCaddy();
        await using var upstream = await RecordingBackend.StartAsync(https: false);
        using var cert = TestCerts.SelfSigned(["custom.test", "both.test"]);
        var row = c.AddUploadedCertificate("cust1", cert);
        var custom = Build.Proxy("custom.test", upstream.Port, TlsMode.Custom);
        custom.CertificateId = row.Id;
        var both = Build.Proxy("both.test", upstream.Port, TlsMode.Custom);
        both.CertificateId = row.Id;
        both.ForceHttps = false;
        both.Hsts = true;
        c.Add(custom);
        c.Add(both);
        c.Add(Build.Proxy("::1", upstream.Port, TlsMode.Internal));
        await c.StartAsync();
        await c.ApplyAsync(); // (8)
        var p = c.HttpsPort;

        async Task<RawHttp.Response> Get(string host, string target, string label)
        {
            var r = await c.HttpAsync(host, target);
            observations.Add(new JsonObject { ["request"] = label, ["host"] = host, ["target"] = target, ["status"] = r.Status, ["location"] = r.Header("Location"), ["hsts"] = r.Header("Strict-Transport-Security") });
            return r;
        }
        async Task Expect(string host, string target, string location, string label)
        {
            var r = await Get(host, target, label);
            Assert.Equal(308, r.Status);
            Assert.Equal(location, r.Header("Location"));
        }

        // (1)(2)(3) public port unset: always ":HttpsPort".
        await Expect($"custom.test:{c.HttpPort}", "/x/y?q=1&r=2", $"https://custom.test:{p}/x/y?q=1&r=2", "public port unset, Host with port");
        await Expect("custom.test", "/x/y?q=1", $"https://custom.test:{p}/x/y?q=1", "public port unset, Host without port");
        await Expect("custom.test", "/a%3Fb%23c%2Fd%20e?z=%26", $"https://custom.test:{p}/a%3Fb%23c%2Fd%20e?z=%26", "encoded path characters");
        // (6) IPv6 literals keep their brackets.
        await Expect($"[::1]:{c.HttpPort}", "/v6?x=1", $"https://[::1]:{p}/v6?x=1", "IPv6 Host with port");
        await Expect("[::1]", "/v6", $"https://[::1]:{p}/v6", "IPv6 Host without port");
        // (7) ForceHttps=false: served over HTTP, without HSTS.
        var plain = await Get("both.test", "/p", "ForceHttps off over HTTP");
        Assert.Equal(200, plain.Status);
        Assert.Null(plain.Header("Strict-Transport-Security"));
        var tls = await c.HttpsUntilAsync("both.test", r => r.Status == 200, TimeSpan.FromSeconds(10), "/p");
        Assert.Equal(200, tls.Status);

        // (4) public port 443: NAT forwards 443 to HttpsPort, so no port in the Location.
        c.UpdateSettings(s => s.PublicHttpsPort = 443);
        await c.ApplyAsync();
        await Expect($"custom.test:{c.HttpPort}", "/x?q=1", "https://custom.test/x?q=1", "public port 443, Host with port");
        await Expect("custom.test", "/x", "https://custom.test/x", "public port 443, Host without port");
        await Expect($"[::1]:{c.HttpPort}", "/v6", "https://[::1]/v6", "public port 443, IPv6 Host");
        // (5) another public port.
        c.UpdateSettings(s => s.PublicHttpsPort = 8443);
        await c.ApplyAsync();
        await Expect("custom.test", "/x", "https://custom.test:8443/x", "public port 8443");

        // (9a) CONTROL: the previous Location expression with an IPv6 Host.
        var control = await c.RunningConfigAsync();
        foreach (var sr in JsonWalk.Handlers(control["apps"]!["http"]!["servers"]![CaddyConfigGenerator.HttpServerName], "static_response"))
            if (sr["status_code"]?.GetValue<int>() == 308)
                sr["headers"]!["Location"] = new JsonArray("https://{http.request.host}:8443{http.request.uri}");
        await c.LoadRawAsync(control);
        var v6Broken = await Get($"[::1]:{c.HttpPort}", "/v6", "CONTROL: {http.request.host} with an IPv6 Host");
        Assert.Equal("https://::1:8443/v6", v6Broken.Header("Location"));

        // (9b) CONTROL: Caddy's own redirects (our srv1 routes removed, disable_redirects off) with custom certificates only.
        control = await c.RunningConfigAsync();
        var srv1Routes = control["apps"]!["http"]!["servers"]![CaddyConfigGenerator.HttpServerName]!["routes"]!.AsArray();
        foreach (var r in srv1Routes.Where(r => r!.ToJsonString().Contains("custom.test") || r!.ToJsonString().Contains("::1")).ToList()) srv1Routes.Remove(r);
        control["apps"]!["http"]!["servers"]![CaddyConfigGenerator.HttpsServerName]!["automatic_https"]!.AsObject().Remove("disable_redirects");
        // Only custom-certificate hosts: drop the internal IPv6 host so Caddy manages no certificate.
        foreach (var r in control["apps"]!["http"]!["servers"]![CaddyConfigGenerator.HttpsServerName]!["routes"]!.AsArray().Where(r => r!.ToJsonString().Contains("::1")).ToList())
            control["apps"]!["http"]!["servers"]![CaddyConfigGenerator.HttpsServerName]!["routes"]!.AsArray().Remove(r);
        control["apps"]!.AsObject().Remove("pki");
        if (control["apps"]!["tls"] is JsonObject tlsApp) tlsApp.Remove("automation");
        await c.LoadRawAsync(control);
        var broken = await Get($"custom.test:{c.HttpPort}", "/x", "CONTROL: Caddy automatic redirects, custom certificates only");
        Assert.NotEqual(308, broken.Status);
        report["controlReproducedBug"] = broken.Status != 308;

        E2EArtifacts.Write("http-redirects.json", report);
    }

    /// <summary>
    /// Redirect hosts with "preserve path" (reviewer finding: stray '&amp;').
    /// Ways it could fail: (1) a target with its own query gets the request path appended after the query;
    /// (2) a request without a query leaves a dangling '&amp;' (or '?'); (3) request and target queries are not both
    /// kept; (4) braces in the target are read as placeholders and silently removed; (5) a target without a query
    /// loses the request query; (7) percent-encoded '?', '#', '/' or spaces in the request path are decoded, which
    /// changes the URL (review round 2: "/x%3Fy%23z" became "/p/x?y#z?a=1"; {http.request.uri.path} is the decoded
    /// req.URL.Path, replacer.go v2.11.4), with or without a target query; (6) the control (the previous expression
    /// "{http.request.uri.path}?a=1&amp;{http.request.uri.query}") does not show the dangling '&amp;' and the decoding.
    /// https://caddyserver.com/docs/json/apps/http/#docs (placeholders) ;
    /// https://github.com/caddyserver/caddy/blob/v2.11.4/modules/caddyhttp/replacer.go
    /// </summary>
    [CaddyFact]
    public async Task Redirect_host_preserves_path_and_merges_queries_without_stray_separators()
    {
        var report = E2EArtifacts.Report(nameof(Redirect_host_preserves_path_and_merges_queries_without_stray_separators));
        var observations = new JsonArray();
        report["observations"] = observations;
        using var c = new LiveCaddy();
        c.Add(new SiteHost { Kind = HostKind.Redirect, Domains = ["rq.test"], Tls = TlsMode.None, RedirectTarget = "https://example.com/p?a=1", RedirectCode = 302, PreservePath = true });
        c.Add(new SiteHost { Kind = HostKind.Redirect, Domains = ["rp.test"], Tls = TlsMode.None, RedirectTarget = "https://example.com/base/", RedirectCode = 301, PreservePath = true });
        c.Add(new SiteHost { Kind = HostKind.Redirect, Domains = ["rb.test"], Tls = TlsMode.None, RedirectTarget = "https://example.com/{literal}?x={y}", RedirectCode = 302, PreservePath = false });
        await c.StartAsync();
        await c.ApplyAsync();

        async Task Expect(string host, string target, string location)
        {
            var r = await c.HttpAsync(host, target);
            observations.Add(new JsonObject { ["host"] = host, ["target"] = target, ["status"] = r.Status, ["location"] = r.Header("Location"), ["expected"] = location });
            Assert.Equal(location, r.Header("Location"));
        }
        await Expect("rq.test", "/req/path", "https://example.com/p/req/path?a=1");          // (1)(2)
        await Expect("rq.test", "/req/path?b=2", "https://example.com/p/req/path?a=1&b=2");  // (3)
        await Expect("rq.test", "/", "https://example.com/p/?a=1");
        await Expect("rp.test", "/x/y?z=1", "https://example.com/base/x/y?z=1");            // (5)
        await Expect("rb.test", "/ignored", "https://example.com/{literal}?x={y}");          // (4)
        await Expect("rq.test", "/x%3Fy%23z", "https://example.com/p/x%3Fy%23z?a=1");                 // (7)
        await Expect("rq.test", "/x%2Fy%20z?b=%26", "https://example.com/p/x%2Fy%20z?a=1&b=%26");
        await Expect("rp.test", "/x%3Fy%23z?q=1", "https://example.com/base/x%3Fy%23z?q=1");

        // (6) CONTROL: the previous expression "...?{target query}&{http.request.uri.query}".
        var control = await c.RunningConfigAsync();
        foreach (var sr in JsonWalk.Handlers(control, "static_response").Where(h => h["status_code"]?.GetValue<int>() == 302).ToList())
        {
            if (sr["headers"]?["Location"]?[0]?.GetValue<string>() is { } l && l.StartsWith("https://example.com/p", StringComparison.Ordinal))
                sr["headers"]!["Location"] = new JsonArray("https://example.com/p{http.request.uri.path}?a=1&{http.request.uri.query}");
        }
        foreach (var sub in JsonWalk.Handlers(control, "subroute").Where(s => s.ToJsonString().Contains("https://example.com/p{http.request.uri.path}?a=1&{http.request.uri.query}")).ToList())
            sub["routes"] = new JsonArray(new JsonObject { ["handle"] = new JsonArray(new JsonObject
            {
                ["handler"] = "static_response", ["status_code"] = 302,
                ["headers"] = new JsonObject { ["Location"] = new JsonArray("https://example.com/p{http.request.uri.path}?a=1&{http.request.uri.query}") },
            }) });
        await c.LoadRawAsync(control);
        var broken = await c.HttpAsync("rq.test", "/req/path");
        observations.Add(new JsonObject { ["control"] = true, ["target"] = "/req/path", ["location"] = broken.Header("Location") });
        Assert.EndsWith("&", broken.Header("Location"));
        var decoded = await c.HttpAsync("rq.test", "/x%3Fy%23z");
        observations.Add(new JsonObject { ["control"] = true, ["target"] = "/x%3Fy%23z", ["location"] = decoded.Header("Location") });
        Assert.Equal("https://example.com/p/x?y#z?a=1&", decoded.Header("Location"));

        E2EArtifacts.Write("redirect-host-query.json", report);
    }

    /// <summary>
    /// "Block common exploits" path traversal (research #88; reviewer false positive /ok/v1.../x).
    /// Ways it could fail: (1) encoded traversal (..%2f, %2e%2e/, double-encoded %252e, backslash, "..;") reaches the
    /// upstream; (2) legitimate names that merely contain dots (v1..., file..txt, a..b, release-1.2..3) are blocked;
    /// (3) the query-string rules stop working; (4) a blocked request still reaches the upstream; (5) the control
    /// (previous pattern) does not reproduce the false positive.
    /// </summary>
    [CaddyFact]
    public async Task Block_exploits_rejects_encoded_traversal_without_blocking_dotted_names()
    {
        var report = E2EArtifacts.Report(nameof(Block_exploits_rejects_encoded_traversal_without_blocking_dotted_names));
        var observations = new JsonArray();
        report["observations"] = observations;
        await using var upstream = await RecordingBackend.StartAsync(https: false);
        using var c = new LiveCaddy();
        var h = Build.Proxy("bx.test", upstream.Port);
        h.BlockExploits = true;
        c.Add(h);
        await c.StartAsync();
        await c.ApplyAsync();

        async Task<int> Status(string target)
        {
            upstream.Clear();
            var r = await c.HttpAsync("bx.test", target);
            var reached = upstream.Requests.Count > 0;
            observations.Add(new JsonObject { ["target"] = target, ["status"] = r.Status, ["reachedUpstream"] = reached });
            if (r.Status == 403) Assert.False(reached, $"{target} was blocked but still reached the upstream"); // (4)
            return r.Status;
        }
        foreach (var t in new[] { "/a/..%2f..%2fwindows/win.ini", "/a/%2e%2e/%2e%2e/secret", "/a/%2E%2E%2Fsecret", "/a/%252e%252e%252fsecret",
                     "/a/..%5cwin.ini", "/a/..\\win.ini", "/..;/admin", "/a/.%2e/x", "/static/..%2f" })
            Assert.True(await Status(t) == 403, $"{t} was not blocked"); // (1)
        foreach (var t in new[] { "/ok/v1.../x", "/file..txt", "/a..b/c", "/release-1.2..3/", "/dots.../", "/x?next=/path" })
            Assert.True(await Status(t) == 200, $"{t} was blocked"); // (2)
        Assert.Equal(403, await Status("/?id=1%20union%20select%20(1)")); // (3)
        Assert.Equal(403, await Status("/.git/config"));

        // (5) CONTROL: the previous raw pattern blocks the dotted name.
        var control = await c.RunningConfigAsync();
        foreach (var o in JsonWalk.Descendants(control).OfType<JsonObject>().Where(o => o["name"]?.GetValue<string>() == "cpm_exploit_raw").ToList())
            o["pattern"] = @"(?i)^[^?]*(\.|%2e|%252e){2}(/|\\|%2f|%5c|%252f|%255c|\?|$)";
        await c.LoadRawAsync(control);
        var falsePositive = await Status("/ok/v1.../x");
        Assert.Equal(403, falsePositive);
        report["controlReproducedFalsePositive"] = falsePositive == 403;

        E2EArtifacts.Write("block-exploits-traversal.json", report);
    }

    /// <summary>
    /// Static hosts (research #92/#93; reviewer: /.well-known/ blocked).
    /// Ways it could fail: (1) dotfiles and dot-folders (.env, .git/config) are downloadable; (2) upper-case variants
    /// (/.ENV, /WEB.CONFIG) get through on case-insensitive file systems; (3) IIS web.config is downloadable, also as
    /// "web.config." or "web.config " (Windows ignores trailing dots and spaces);
    /// (4) /.well-known/ files (security.txt, apple-app-site-association) return 404; (5) a dotfile inside
    /// /.well-known/ is served; (6) directory listings show dotfiles; (7) "/sub/" with its own index.html gets the
    /// SPA index, or deep links stop falling back to the SPA index; (8) a static host without a root folder serves
    /// the service's working directory; (9) the control shows `hide: [".*"]` alone blocks /.well-known/.
    /// </summary>
    [CaddyFact]
    public async Task Static_hosts_hide_dotfiles_and_web_config_but_serve_well_known()
    {
        var report = E2EArtifacts.Report(nameof(Static_hosts_hide_dotfiles_and_web_config_but_serve_well_known));
        var observations = new JsonArray();
        report["observations"] = observations;
        using var c = new LiveCaddy();
        var root = c.S.Env.WebRoot();
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        Directory.CreateDirectory(Path.Combine(root, ".well-known"));
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        Directory.CreateDirectory(Path.Combine(root, "list", ".svn"));
        File.WriteAllText(Path.Combine(root, "list", "visible.txt"), "v");
        File.WriteAllText(Path.Combine(root, "list", ".htpasswd"), "admin:x");
        File.WriteAllText(Path.Combine(root, "index.html"), "spa index");
        File.WriteAllText(Path.Combine(root, "hello.txt"), "hello static");
        File.WriteAllText(Path.Combine(root, ".env"), "SECRET=1");
        File.WriteAllText(Path.Combine(root, ".git", "config"), "[core]");
        File.WriteAllText(Path.Combine(root, "web.config"), "<configuration/>");
        File.WriteAllText(Path.Combine(root, ".well-known", "security.txt"), "Contact: mailto:security@example.com");
        File.WriteAllText(Path.Combine(root, ".well-known", ".hidden"), "nope");
        File.WriteAllText(Path.Combine(root, "sub", "index.html"), "sub index");
        c.Add(new SiteHost { Kind = HostKind.Static, Domains = ["spa.test"], Tls = TlsMode.None, RootPath = root, SpaFallback = true, Compression = false });
        c.Add(new SiteHost { Kind = HostKind.Static, Domains = ["browse.test"], Tls = TlsMode.None, RootPath = root, Browse = true, Compression = false });
        c.Add(new SiteHost { Kind = HostKind.Static, Domains = ["noroot.test"], Tls = TlsMode.None, RootPath = "", Compression = false });
        await c.StartAsync();
        var apply = await c.ApplyAsync();
        Assert.Contains(apply.Warnings, w => w.Contains("noroot.test") && w.Contains("no root folder"));

        async Task<RawHttp.Response> Get(string host, string target)
        {
            var r = await c.HttpAsync(host, target);
            observations.Add(new JsonObject { ["host"] = host, ["target"] = target, ["status"] = r.Status, ["body"] = r.Body.Length > 80 ? r.Body[..80] : r.Body });
            return r;
        }
        foreach (var host in new[] { "spa.test", "browse.test" })
        {
            // "/web.config." and "/web.config%20": Windows ignores trailing dots and spaces when opening files.
            foreach (var t in new[] { "/.env", "/.ENV", "/.git/config", "/web.config", "/WEB.CONFIG", "/web.config.", "/web.config%20", "/sub/../.env", "/.well-known/.hidden" })
            {
                var r = await Get(host, t);
                Assert.True(r.Status == 404, $"{host}{t} returned {r.Status}: {r.Body}"); // (1)(2)(3)(5)
                Assert.DoesNotContain("SECRET", r.Body);
                Assert.DoesNotContain("[core]", r.Body);
                Assert.DoesNotContain("<configuration", r.Body);
            }
            var wk = await Get(host, "/.well-known/security.txt"); // (4)
            Assert.Equal(200, wk.Status);
            Assert.Contains("Contact:", wk.Body);
            Assert.Equal("hello static", (await Get(host, "/hello.txt")).Body);
        }
        // (6) listing (a folder without index.html) without dotfiles
        var listing = await Get("browse.test", "/list/");
        Assert.Equal(200, listing.Status);
        Assert.Contains("visible.txt", listing.Body);
        Assert.DoesNotContain(".htpasswd", listing.Body);
        Assert.DoesNotContain(".svn", listing.Body);
        // (7) SPA
        Assert.Equal("sub index", (await Get("spa.test", "/sub/")).Body);
        Assert.Equal("spa index", (await Get("spa.test", "/deep/client/route?tab=2")).Body);
        // (8) no root → 503, never the working directory
        var noRoot = await Get("noroot.test", "/");
        Assert.Equal(503, noRoot.Status);

        // (9) CONTROL: hide ".*" on the .well-known file server too.
        var control = await c.RunningConfigAsync();
        foreach (var fs in JsonWalk.Handlers(control, "file_server")) fs["hide"] = new JsonArray(".*");
        await c.LoadRawAsync(control);
        var blocked = await Get("spa.test", "/.well-known/security.txt");
        Assert.Equal(404, blocked.Status);
        report["controlReproducedWellKnown404"] = blocked.Status == 404;

        E2EArtifacts.Write("static-dotfiles-well-known.json", report);
    }

    /// <summary>
    /// Request headers with underscores (research #81, Caddy v2.11.4 GHSA-f59h-q822-g45g).
    /// Ways it could fail: (1) the documented behaviour is wrong — client headers such as SM_USER still reach the
    /// upstream (then the UI note is misleading) or hyphenated headers are dropped too; (2) a header the host itself
    /// sets with an underscore (request header operation) is dropped as well (then the UI must forbid it);
    /// (3) advanced routes that match an underscore header are accepted silently although they can never match
    /// (the generator must warn); (4) Caddy rejects the config.
    /// </summary>
    [CaddyFact]
    public async Task Client_headers_with_underscores_are_dropped_but_headers_set_by_the_host_are_sent()
    {
        var report = E2EArtifacts.Report(nameof(Client_headers_with_underscores_are_dropped_but_headers_set_by_the_host_are_sent));
        await using var upstream = await RecordingBackend.StartAsync(https: false);
        using var c = new LiveCaddy();
        var h = Build.Proxy("us.test", upstream.Port);
        h.RequestHeaders = [new HeaderOp { Name = "X_Set_By_Host", Value = "yes" }];
        h.AdvancedRoutesJson = """[{"match":[{"header":{"SM_USER":["*"]}}],"handle":[{"handler":"static_response","body":"never"}]}]""";
        c.Add(h);
        await c.StartAsync();
        var apply = await c.ApplyAsync(); // (4)
        var warning = apply.Warnings.FirstOrDefault(w => w.Contains("SM_USER") && w.Contains("underscore"));
        Assert.NotNull(warning); // (3)

        var r = await c.HttpAsync("us.test", "/", ("SM_USER", "alice"), ("X_Api_Key", "k"), ("X-Normal", "1"));
        Assert.Equal(200, r.Status);
        Assert.NotEqual("never", r.Body);
        var seen = upstream.Requests.Single().Headers;
        report["upstreamHeaders"] = new JsonObject(seen.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));
        report["generatorWarning"] = warning;
        Assert.False(seen.ContainsKey("SM_USER"));      // (1)
        Assert.False(seen.ContainsKey("X_Api_Key"));
        Assert.Equal("1", seen["X-Normal"]);
        Assert.Equal("yes", seen["X_Set_By_Host"]);     // (2)

        E2EArtifacts.Write("underscore-headers.json", report);
    }

    /// <summary>
    /// Default site over HTTPS (research #69). The documented behaviour (Settings > Unknown hosts note) is that HTTPS
    /// requests for names without a certificate, and requests by IP address (no SNI), fail the TLS handshake, and
    /// that setting fallback_sni/default_sni in the TLS connection policy JSON makes them reach the default site.
    /// Ways it could fail: (1) the handshake succeeds with some certificate (then the note is wrong); (2) the advice
    /// does not work: with fallback_sni/default_sni the handshake still fails or a configured host's content is
    /// served for the unknown name instead of the default site; (3) known hosts break; (4) Caddy rejects the policy.
    /// </summary>
    [CaddyFact]
    public async Task Default_site_over_https_needs_a_fallback_certificate()
    {
        var report = E2EArtifacts.Report(nameof(Default_site_over_https_needs_a_fallback_certificate));
        using var c = new LiveCaddy(s => s.DefaultSite = DefaultSiteBehavior.CaddyWelcome);
        c.Add(new SiteHost { Kind = HostKind.Response, Domains = ["known.test"], Tls = TlsMode.Internal, ResponseStatus = 200, ResponseBody = "known", ForceHttps = true });
        await c.StartAsync();
        await c.ApplyAsync();
        var known = await c.HttpsUntilAsync("known.test", r => r.Status == 200, TimeSpan.FromSeconds(15));
        Assert.Equal("known", known.Body); // (3)
        var unknown = await c.HttpsAsync("nope.test");
        var byIp = await c.HttpsAsync(null);
        report["withoutFallback"] = new JsonObject { ["unknownSni"] = unknown.ToJson(), ["noSni"] = byIp.ToJson() };
        Assert.False(unknown.Handshake); // (1)
        Assert.False(byIp.Handshake);

        c.UpdateSettings(s => s.TlsConnectionPolicyJson = """{"fallback_sni":"known.test","default_sni":"known.test"}""");
        await c.ApplyAsync(); // (4)
        var unknown2 = await c.HttpsUntilAsync("nope.test", r => r.Handshake, TimeSpan.FromSeconds(10));
        var byIp2 = await c.HttpsUntilAsync(null, r => r.Handshake, TimeSpan.FromSeconds(10));
        report["withFallback"] = new JsonObject { ["unknownSni"] = unknown2.ToJson(), ["noSni"] = byIp2.ToJson() };
        Assert.Equal(200, unknown2.Status); // (2) default site (Caddy welcome text), not the known host
        Assert.Contains("no site is configured", unknown2.Body);
        Assert.Equal(200, byIp2.Status);
        Assert.Contains("no site is configured", byIp2.Body);
        Assert.Equal("known", (await c.HttpsAsync("known.test")).Body);

        E2EArtifacts.Write("default-site-https.json", report);
    }

    /// <summary>
    /// The "public HTTPS port" setting through the settings API (Settings > Listeners).
    /// Ways it could fail: (1) an out-of-range port (0, -1, 65536) is stored and then emitted into every redirect;
    /// (2) a valid port is not stored or not returned to the UI; (3) the setting cannot be cleared again (null = use the
    /// HTTPS port); (4) the stored value is not what the generated redirect uses.
    /// </summary>
    [CaddyFact]
    public async Task Public_https_port_is_validated_stored_and_clearable_through_the_settings_api()
    {
        var report = E2EArtifacts.Report(nameof(Public_https_port_is_validated_stored_and_clearable_through_the_settings_api));
        var rows = new JsonArray();
        report["requests"] = rows;
        await using var api = ApiHost.Start(installBinary: true);
        var json = Core.JsonDefaults.Api;
        async Task<System.Net.HttpStatusCode> Put(JsonObject body)
        {
            var r = await System.Net.Http.Json.HttpClientJsonExtensions.PutAsJsonAsync(api.Client, "/api/settings/caddy", body, json);
            rows.Add(new JsonObject { ["body"] = body.DeepClone(), ["status"] = (int)r.StatusCode, ["response"] = await r.Content.ReadAsStringAsync() is var t && t.Length > 300 ? t[..300] : t });
            return r.StatusCode;
        }
        foreach (var bad in new[] { 0, -1, 65536 })
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, await Put(new JsonObject { ["publicHttpsPort"] = bad })); // (1)
        Assert.Equal(System.Net.HttpStatusCode.OK, await Put(new JsonObject { ["publicHttpsPort"] = 443 }));
        var stored = await System.Net.Http.Json.HttpClientJsonExtensions.GetFromJsonAsync<JsonObject>(api.Client, "/api/settings/caddy", json);
        Assert.Equal(443, stored!["publicHttpsPort"]!.GetValue<int>());                                                 // (2)
        var settings = api.Env.Store.GetSettings<CaddySettings>();
        var gen = CaddyConfigGenerator.Generate(Build.Input(api.Env.Paths, settings, hosts: [Build.Proxy("p.test", 1234, TlsMode.Internal)]));
        var locations = JsonWalk.Handlers(gen.Config["apps"]!["http"]!["servers"]![CaddyConfigGenerator.HttpServerName], "static_response")
            .Select(h => h["headers"]?["Location"]?[0]?.GetValue<string>()).Where(l => l is not null).ToList();
        report["locationsWith443"] = new JsonArray(locations.Select(l => (JsonNode)l!).ToArray());
        Assert.All(locations, l => Assert.DoesNotContain(":" + settings.HttpsPort, l!));                                // (4)
        Assert.Equal(System.Net.HttpStatusCode.OK, await Put(new JsonObject { ["publicHttpsPort"] = null }));
        Assert.Null(api.Env.Store.GetSettings<CaddySettings>().PublicHttpsPort);                                        // (3)

        E2EArtifacts.Write("public-https-port-settings-api.json", report);
    }
}
