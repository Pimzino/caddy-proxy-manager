using System.Text.Json.Nodes;
using CaddyManager.Config.Generation;
using CaddyManager.Core.Models;

namespace CaddyManager.Config.Tests;

public sealed class GeneratorTests : IDisposable
{
    private readonly TempEnv _env = new();
    public void Dispose() => _env.Dispose();

    private ConfigGeneratorResult Gen(CaddySettings? s = null, IEnumerable<SiteHost>? hosts = null, IEnumerable<AccessList>? lists = null,
        IEnumerable<Certificate>? certs = null, IEnumerable<StreamHost>? streams = null, IEnumerable<string>? modules = null, string? mac = null,
        IReadOnlySet<string>? unavailable = null)
    {
        var input = Build.Input(_env.Paths, s, hosts, lists, certs, streams, modules, mac);
        if (unavailable is not null) input = input with { UnavailableCertificateIds = unavailable };
        return CaddyConfigGenerator.Generate(input);
    }

    private static JsonObject Srv(JsonObject cfg, string name) => cfg["apps"]!["http"]!["servers"]![name]!.AsObject();
    private static JsonArray Routes(JsonObject cfg, string server) => Srv(cfg, server)["routes"]!.AsArray();

    private static JsonObject HostRoute(JsonObject cfg, string server, string domain) =>
        Routes(cfg, server).Select(r => r!.AsObject())
            .Single(r => r["match"]?[0]?["host"]?.AsArray().Any(h => h!.GetValue<string>() == domain) == true);

    private static JsonArray Sub(JsonObject route) => route["handle"]![0]!["routes"]!.AsArray();

    private static IEnumerable<JsonObject> Handlers(JsonObject route) =>
        Sub(route).SelectMany(r => r!["handle"]!.AsArray()).Select(h => h!.AsObject());

    private static JsonObject Handler(JsonObject route, string name) => Handlers(route).First(h => h["handler"]!.GetValue<string>() == name);

    private static string[] Strings(JsonNode? n) => n!.AsArray().Select(x => x!.GetValue<string>()).ToArray();

    // ------------------------------------------------------------------ globals

    [Fact]
    public void Admin_storage_and_logging_follow_settings()
    {
        var cfg = Gen(new CaddySettings { AdminListen = "127.0.0.1:2999", LogLevel = "warn" }).Config;
        Assert.Equal("127.0.0.1:2999", cfg["admin"]!["listen"]!.GetValue<string>());
        Assert.False(cfg["admin"]!["config"]!["persist"]!.GetValue<bool>());
        Assert.Equal("file_system", cfg["storage"]!["module"]!.GetValue<string>());
        Assert.Equal(_env.Paths.CaddyStorageDir, cfg["storage"]!["root"]!.GetValue<string>());
        var def = cfg["logging"]!["logs"]!["default"]!;
        Assert.Equal("WARN", def["level"]!.GetValue<string>());
        Assert.Equal("file", def["writer"]!["output"]!.GetValue<string>());
        Assert.Equal(_env.Paths.CaddyProcessLog, def["writer"]!["filename"]!.GetValue<string>());
        Assert.Equal(20, def["writer"]!["roll_size_mb"]!.GetValue<int>());
        Assert.Equal(10, def["writer"]!["roll_keep"]!.GetValue<int>());
        // Admin API request logging (the manager polls every few seconds) is split off at WARN into the same file; access
        // logs (traffic statistics are on by default) never go to caddy.log.
        Assert.Equal(["admin.api", "http.log.access"], Strings(def["exclude"]));
        var adminLog = cfg["logging"]!["logs"]!["cpm_admin_api"]!;
        Assert.Equal(["admin.api"], Strings(adminLog["include"]));
        Assert.Equal("WARN", adminLog["level"]!.GetValue<string>());
        Assert.Equal(_env.Paths.CaddyProcessLog, adminLog["writer"]!["filename"]!.GetValue<string>());
        Assert.Equal(80, cfg["apps"]!["http"]!["http_port"]!.GetValue<int>());
        Assert.Equal(443, cfg["apps"]!["http"]!["https_port"]!.GetValue<int>());
    }

    [Fact]
    public void Boot_config_is_minimal()
    {
        var cfg = CaddyConfigGenerator.BuildBootConfig(new CaddySettings(), _env.Paths);
        Assert.Equal(["admin", "logging", "storage"], cfg.Select(p => p.Key).ToArray());
        Assert.Equal(["admin.api"], Strings(cfg["logging"]!["logs"]!["default"]!["exclude"]));
        Assert.Equal("WARN", cfg["logging"]!["logs"]!["cpm_admin_api"]!["level"]!.GetValue<string>());
    }

    [Fact]
    public void Adapted_caddyfile_config_gets_admin_storage_and_logging_when_missing()
    {
        var settings = new CaddySettings { AdminListen = "127.0.0.1:2999" };
        var warnings = new List<string>();
        var json = CaddyConfigGenerator.CompleteAdaptedConfig("""{"apps":{"http":{"servers":{"srv0":{"listen":[":80"]}}}}}""", settings, _env.Paths, warnings);
        var cfg = JsonNode.Parse(json)!;
        Assert.Equal("127.0.0.1:2999", cfg["admin"]!["listen"]!.GetValue<string>());
        Assert.Equal(_env.Paths.CaddyStorageDir, cfg["storage"]!["root"]!.GetValue<string>());
        Assert.Equal(["admin.api"], Strings(cfg["logging"]!["logs"]!["default"]!["exclude"]));
        Assert.NotNull(cfg["apps"]!["http"]);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Adapted_caddyfile_config_keeps_user_globals_and_warns_about_a_foreign_admin_endpoint()
    {
        var settings = new CaddySettings { AdminListen = "127.0.0.1:2019" };
        var warnings = new List<string>();
        var json = CaddyConfigGenerator.CompleteAdaptedConfig(
            """{"admin":{"listen":"localhost:3000"},"storage":{"module":"file_system","root":"D:/certs"},"logging":{"logs":{"default":{"level":"ERROR"}}}}""",
            settings, _env.Paths, warnings);
        var cfg = JsonNode.Parse(json)!;
        Assert.Equal("localhost:3000", cfg["admin"]!["listen"]!.GetValue<string>());
        Assert.Equal("D:/certs", cfg["storage"]!["root"]!.GetValue<string>());
        Assert.Equal("ERROR", cfg["logging"]!["logs"]!["default"]!["level"]!.GetValue<string>());
        Assert.Contains(warnings, w => w.Contains("localhost:3000") && w.Contains("127.0.0.1:2019"));

        // admin block without listen (e.g. only "admin { origins ... }") gets the manager's address
        var partial = JsonNode.Parse(CaddyConfigGenerator.CompleteAdaptedConfig("""{"admin":{"origins":["x"]}}""", settings, _env.Paths, []))!;
        Assert.Equal("127.0.0.1:2019", partial["admin"]!["listen"]!.GetValue<string>());
    }

    [Fact]
    public void Admin_api_log_level_never_drops_below_warn_but_follows_error()
    {
        var debug = Gen(new CaddySettings { LogLevel = "debug" }).Config["logging"]!["logs"]!;
        Assert.Equal("DEBUG", debug["default"]!["level"]!.GetValue<string>());
        Assert.Equal("WARN", debug["cpm_admin_api"]!["level"]!.GetValue<string>());
        var error = Gen(new CaddySettings { LogLevel = "error" }).Config["logging"]!["logs"]!;
        Assert.Equal("ERROR", error["cpm_admin_api"]!["level"]!.GetValue<string>());
    }

    [Fact]
    public void Empty_model_only_has_http_server_with_default_site()
    {
        var cfg = Gen().Config;
        var servers = cfg["apps"]!["http"]!["servers"]!.AsObject();
        Assert.Single(servers);
        Assert.Equal([":80"], Strings(Srv(cfg, "srv1")["listen"]));
        var routes = Routes(cfg, "srv1");
        Assert.Single(routes);
        Assert.Equal(404, routes[0]!["handle"]![0]!["status_code"]!.GetValue<int>());
        Assert.Null(cfg["apps"]!["tls"]);
    }

    [Fact]
    public void Output_is_deterministic_regardless_of_input_order()
    {
        var a = Build.Proxy("b.example.com", tls: TlsMode.Acme);
        var b = Build.Proxy("a.example.com", tls: TlsMode.Internal);
        var c = Build.Proxy("*.wild.example.com", tls: TlsMode.Internal);
        var r1 = Gen(hosts: [a, b, c]).ToJson();
        var r2 = Gen(hosts: [c, b, a]).ToJson();
        Assert.Equal(r1, r2);
        Assert.Equal(CaddyJson.Hash(r1), CaddyJson.Hash(r2));
        // exact names before wildcards
        var hosts = Routes(JsonNode.Parse(r1)!.AsObject(), "srv0").Take(3)
            .Select(r => r!["match"]![0]!["host"]![0]!.GetValue<string>()).ToArray();
        Assert.Equal(["a.example.com", "b.example.com", "*.wild.example.com"], hosts);
    }

    [Fact]
    public void Disabled_hosts_are_excluded()
    {
        var h = Build.Proxy("off.example.com", tls: TlsMode.None);
        h.Enabled = false;
        var cfg = Gen(hosts: [h]).Config;
        Assert.DoesNotContain("off.example.com", cfg.ToJsonString());
    }

    [Fact]
    public void Duplicate_domain_is_served_once_with_warning()
    {
        var a = Build.Proxy("dup.example.com", 1000, TlsMode.None);
        var b = Build.Proxy("dup.example.com", 2000, TlsMode.None);
        b.Domains.Add("other.example.com");
        var r = Gen(hosts: [a, b]);
        Assert.Contains(r.Warnings, w => w.Contains("dup.example.com"));
        var count = Routes(r.Config, "srv1").Count(x => x!["match"]?[0]?["host"]?.AsArray().Any(h => h!.GetValue<string>() == "dup.example.com") == true);
        Assert.Equal(1, count);
    }

    // ------------------------------------------------------------------ TLS modes / servers

    [Fact]
    public void Tls_none_is_http_only_and_skipped_from_automatic_https()
    {
        var plain = Build.Proxy("plain.example.com", tls: TlsMode.None);
        var secure = Build.Proxy("secure.example.com", tls: TlsMode.Acme);
        var cfg = Gen(hosts: [plain, secure]).Config;
        HostRoute(cfg, "srv1", "plain.example.com");
        Assert.DoesNotContain(Routes(cfg, "srv0"), r => r!.ToJsonString().Contains("plain.example.com"));
        Assert.Equal(["plain.example.com"], Strings(Srv(cfg, "srv0")["automatic_https"]!["skip"]));
    }

    [Fact]
    public void Force_https_false_serves_on_both_servers()
    {
        var h = Build.Proxy("both.example.com", tls: TlsMode.Acme);
        h.ForceHttps = false;
        var cfg = Gen(hosts: [h]).Config;
        HostRoute(cfg, "srv0", "both.example.com");
        HostRoute(cfg, "srv1", "both.example.com");
        // catch-all is last on both
        Assert.Null(Routes(cfg, "srv1").Last()!["match"]);
        Assert.Null(Routes(cfg, "srv0").Last()!["match"]);
    }

    [Fact]
    public void Acme_policy_uses_settings()
    {
        var s = new CaddySettings { AcmeEmail = "ops@example.com", AcmeCa = AcmeCa.LetsEncryptStaging, HttpPort = 8080, HttpsPort = 8443, DisableTlsAlpnChallenge = true };
        var cfg = Gen(s, [Build.Proxy("a.example.com", tls: TlsMode.Acme)]).Config;
        var policy = cfg["apps"]!["tls"]!["automation"]!["policies"]![0]!;
        Assert.Equal(["a.example.com"], Strings(policy["subjects"]));
        var iss = policy["issuers"]![0]!;
        Assert.Equal("acme", iss["module"]!.GetValue<string>());
        Assert.Equal(CaddyConfigGenerator.LetsEncryptStagingDirectory, iss["ca"]!.GetValue<string>());
        Assert.Equal("ops@example.com", iss["email"]!.GetValue<string>());
        Assert.Equal(8080, iss["challenges"]!["http"]!["alternate_port"]!.GetValue<int>());
        Assert.True(iss["challenges"]!["tls-alpn"]!["disabled"]!.GetValue<bool>());
        Assert.Equal([":8443"], Strings(Srv(cfg, "srv0")["listen"]));
        Assert.Equal([":8080"], Strings(Srv(cfg, "srv1")["listen"]));
        Assert.Equal(8080, cfg["apps"]!["http"]!["http_port"]!.GetValue<int>());
    }

    [Fact]
    public void Acme_variants_zerossl_custom_and_fallback()
    {
        var zs = Gen(new CaddySettings { AcmeCa = AcmeCa.ZeroSsl, EabKeyId = "kid" }, [Build.Proxy("a.example.com", tls: TlsMode.Acme)], mac: "mac").Config;
        var iss = zs["apps"]!["tls"]!["automation"]!["policies"]![0]!["issuers"]![0]!;
        Assert.Equal(CaddyConfigGenerator.ZeroSslDirectory, iss["ca"]!.GetValue<string>());
        Assert.Equal("kid", iss["external_account"]!["key_id"]!.GetValue<string>());
        Assert.Equal("mac", iss["external_account"]!["mac_key"]!.GetValue<string>());

        var custom = Gen(new CaddySettings { AcmeCa = AcmeCa.Custom, CustomAcmeDirectory = "https://ca.corp.local/acme/directory", CustomAcmeRootPath = "/certs/root.pem" },
            [Build.Proxy("a.example.com", tls: TlsMode.Acme)]).Config;
        var ci = custom["apps"]!["tls"]!["automation"]!["policies"]![0]!["issuers"]![0]!;
        Assert.Equal("https://ca.corp.local/acme/directory", ci["ca"]!.GetValue<string>());
        Assert.Equal(["/certs/root.pem"], Strings(ci["trusted_roots_pem_files"]));

        var le = Gen(new CaddySettings { AcmeCa = AcmeCa.LetsEncrypt, EabKeyId = "kid" }, [Build.Proxy("a.example.com", tls: TlsMode.Acme)], mac: "mac").Config;
        var issuers = le["apps"]!["tls"]!["automation"]!["policies"]![0]!["issuers"]!.AsArray();
        Assert.Equal(2, issuers.Count);
        Assert.Equal(CaddyConfigGenerator.LetsEncryptDirectory, issuers[0]!["ca"]!.GetValue<string>());
        Assert.Null(issuers[0]!["external_account"]);
        Assert.Equal(CaddyConfigGenerator.ZeroSslDirectory, issuers[1]!["ca"]!.GetValue<string>());

        var plainLe = Gen(new CaddySettings(), [Build.Proxy("a.example.com", tls: TlsMode.Acme)]).Config;
        Assert.Single(plainLe["apps"]!["tls"]!["automation"]!["policies"]![0]!["issuers"]!.AsArray());
    }

    [Fact]
    public void Wildcard_acme_domain_warns()
    {
        var r = Gen(hosts: [Build.Proxy("*.example.com", tls: TlsMode.Acme)]);
        Assert.Contains(r.Warnings, w => w.Contains("*.example.com") && w.Contains("DNS"));
    }

    [Fact]
    public void Internal_tls_policy_and_no_trust_install()
    {
        var cfg = Gen(hosts: [Build.Proxy("app.corp.local", tls: TlsMode.Internal)]).Config;
        var policy = cfg["apps"]!["tls"]!["automation"]!["policies"]![0]!;
        Assert.Equal(["app.corp.local"], Strings(policy["subjects"]));
        Assert.Equal("internal", policy["issuers"]![0]!["module"]!.GetValue<string>());
        Assert.False(cfg["apps"]!["pki"]!["certificate_authorities"]!["local"]!["install_trust"]!.GetValue<bool>());
    }

    [Fact]
    public void Custom_certificate_is_loaded_tagged_and_selected_by_sni()
    {
        var cert = new Certificate { Id = "cert1", Name = "corp", CertPath = "/store/cert1/fullchain.pem", KeyPath = "/store/cert1/privkey.pem" };
        var h = Build.Proxy("shop.example.com", tls: TlsMode.Custom);
        h.CertificateId = "cert1";
        h.Domains.Add("www.shop.example.com");
        var acme = Build.Proxy("acme.example.com", tls: TlsMode.Acme);
        var cfg = Gen(hosts: [h, acme], certs: [cert]).Config;

        var lf = cfg["apps"]!["tls"]!["certificates"]!["load_files"]![0]!;
        Assert.Equal("/store/cert1/fullchain.pem", lf["certificate"]!.GetValue<string>());
        Assert.Equal("/store/cert1/privkey.pem", lf["key"]!.GetValue<string>());
        Assert.Equal(["cpm-cert1"], Strings(lf["tags"]));

        var policies = Srv(cfg, "srv0")["tls_connection_policies"]!.AsArray();
        Assert.Equal(2, policies.Count);
        Assert.Equal(["shop.example.com", "www.shop.example.com"], Strings(policies[0]!["match"]!["sni"]));
        Assert.Equal(["cpm-cert1"], Strings(policies[0]!["certificate_selection"]!["any_tag"]));
        Assert.Empty(policies[1]!.AsObject());
        Assert.Equal(["shop.example.com", "www.shop.example.com"], Strings(Srv(cfg, "srv0")["automatic_https"]!["skip_certificates"]));
    }

    [Fact]
    public void Custom_certificate_with_missing_files_skips_host()
    {
        var cert = new Certificate { Id = "gone", Name = "gone", CertPath = "/nope.pem", KeyPath = "/nope.key" };
        var h = Build.Proxy("x.example.com", tls: TlsMode.Custom);
        h.CertificateId = "gone";
        var r = Gen(hosts: [h], certs: [cert], unavailable: new HashSet<string> { "gone" });
        Assert.DoesNotContain("x.example.com", r.Config.ToJsonString());
        Assert.Contains(r.Warnings, w => w.Contains("x.example.com"));
    }

    [Fact]
    public void Http3_bind_addresses_trusted_proxies_and_server_options()
    {
        var s = new CaddySettings
        {
            EnableHttp3 = false,
            BindAddresses = ["10.0.0.5", "::1"],
            TrustedProxies = ["10.0.0.0/8"],
            ServerOptionsJson = """{"max_header_bytes": 65536, "routes": []}""",
        };
        var r = Gen(s, [Build.Proxy("a.example.com", tls: TlsMode.Acme)]);
        var srv0 = Srv(r.Config, "srv0");
        Assert.Equal(["h1", "h2"], Strings(srv0["protocols"]));
        Assert.Equal(["10.0.0.5:443", "[::1]:443"], Strings(srv0["listen"]));
        Assert.Equal(["10.0.0.5:80", "[::1]:80"], Strings(Srv(r.Config, "srv1")["listen"]));
        Assert.Equal("static", srv0["trusted_proxies"]!["source"]!.GetValue<string>());
        Assert.Equal(["10.0.0.0/8"], Strings(srv0["trusted_proxies"]!["ranges"]));
        Assert.Equal(65536, srv0["max_header_bytes"]!.GetValue<int>());
        Assert.Contains(r.Warnings, w => w.Contains("routes"));

        Assert.Equal(["h1", "h2"], Strings(Srv(Gen(new CaddySettings(), [Build.Proxy("a.example.com", tls: TlsMode.Acme)]).Config, "srv0")["protocols"])); // off by default
        var h3 = Gen(new CaddySettings { EnableHttp3 = true }, [Build.Proxy("a.example.com", tls: TlsMode.Acme)]).Config;
        Assert.Equal(["h1", "h2", "h3"], Strings(Srv(h3, "srv0")["protocols"]));
    }

    // ------------------------------------------------------------------ host kinds

    [Fact]
    public void Proxy_with_everything()
    {
        var h = new SiteHost
        {
            Kind = HostKind.Proxy,
            Domains = ["App.Example.com"],
            Tls = TlsMode.Acme,
            Upstreams = [new Upstream { Scheme = UpstreamScheme.Https, Host = "10.0.0.1", Port = 8443 }, new Upstream { Scheme = UpstreamScheme.Https, Host = "fe80::1", Port = 8443 }],
            UpstreamTlsInsecure = true,
            LoadBalancing = LoadBalancingPolicy.LeastConn,
            HealthCheck = new HealthCheck { Enabled = true, Path = "/health", IntervalSeconds = 15, TimeoutSeconds = 3, ExpectStatus = 204 },
            UpstreamHostHeader = "{upstream}",
            RequestHeaders = [new HeaderOp { Action = HeaderAction.Set, Name = "X-Env", Value = "prod" }, new HeaderOp { Action = HeaderAction.Add, Name = "X-Tag", Value = "a" }, new HeaderOp { Action = HeaderAction.Delete, Name = "X-Secret" }],
            Compression = true,
        };
        var cfg = Gen(hosts: [h]).Config;
        var route = HostRoute(cfg, "srv0", "app.example.com");
        Assert.True(route["terminal"]!.GetValue<bool>());
        var rp = Handler(route, "reverse_proxy");
        Assert.Equal(["10.0.0.1:8443", "[fe80::1]:8443"], rp["upstreams"]!.AsArray().Select(u => u!["dial"]!.GetValue<string>()).ToArray());
        Assert.True(rp["transport"]!["tls"]!["insecure_skip_verify"]!.GetValue<bool>());
        Assert.Equal("http", rp["transport"]!["protocol"]!.GetValue<string>());
        Assert.Equal("least_conn", rp["load_balancing"]!["selection_policy"]!["policy"]!.GetValue<string>());
        var active = rp["health_checks"]!["active"]!;
        Assert.Equal("/health", active["uri"]!.GetValue<string>());
        Assert.Equal("15s", active["interval"]!.GetValue<string>());
        Assert.Equal("3s", active["timeout"]!.GetValue<string>());
        Assert.Equal(204, active["expect_status"]!.GetValue<int>());
        var req = rp["headers"]!["request"]!;
        Assert.Equal(["{http.reverse_proxy.upstream.hostport}"], Strings(req["set"]!["Host"]));
        Assert.Equal(["prod"], Strings(req["set"]!["X-Env"]));
        Assert.Equal(["a"], Strings(req["add"]!["X-Tag"]));
        Assert.Equal(["X-Secret"], Strings(req["delete"]));
        var enc = Handler(route, "encode");
        Assert.NotNull(enc["encodings"]!["gzip"]);
        Assert.NotNull(enc["encodings"]!["zstd"]);
    }

    [Theory]
    [InlineData(LoadBalancingPolicy.RoundRobin, "round_robin")]
    [InlineData(LoadBalancingPolicy.Random, "random")]
    [InlineData(LoadBalancingPolicy.LeastConn, "least_conn")]
    [InlineData(LoadBalancingPolicy.First, "first")]
    [InlineData(LoadBalancingPolicy.Cookie, "cookie")]
    [InlineData(LoadBalancingPolicy.UriHash, "uri_hash")]
    public void Load_balancing_policy_names(LoadBalancingPolicy policy, string expected)
    {
        var h = Build.Proxy("lb.example.com");
        h.Upstreams.Add(new Upstream { Host = "127.0.0.2", Port = 80 });
        h.LoadBalancing = policy;
        var rp = Handler(HostRoute(Gen(hosts: [h]).Config, "srv1", "lb.example.com"), "reverse_proxy");
        Assert.Equal(expected, rp["load_balancing"]!["selection_policy"]!["policy"]!.GetValue<string>());
        Assert.Equal("5s", rp["load_balancing"]!["try_duration"]!.GetValue<string>());
    }

    [Fact]
    public void Literal_host_header_and_single_upstream_has_no_lb()
    {
        var h = Build.Proxy("hh.example.com");
        h.UpstreamHostHeader = "internal.local";
        var rp = Handler(HostRoute(Gen(hosts: [h]).Config, "srv1", "hh.example.com"), "reverse_proxy");
        Assert.Equal(["internal.local"], Strings(rp["headers"]!["request"]!["set"]!["Host"]));
        Assert.Null(rp["load_balancing"]);
        Assert.Null(rp["transport"]);
    }

    [Fact]
    public void Keep_client_host_is_explicit_for_https_upstreams_only()
    {
        // Caddy sends the upstream address as Host to HTTPS upstreams by default; "keep client Host" must override it.
        var https = Build.Proxy("pc.example.com", 9440);
        https.Upstreams[0].Scheme = UpstreamScheme.Https;
        https.UpstreamTlsInsecure = true;
        var rp = Handler(HostRoute(Gen(hosts: [https]).Config, "srv1", "pc.example.com"), "reverse_proxy");
        Assert.Equal(["{http.request.hostport}"], Strings(rp["headers"]!["request"]!["set"]!["Host"]));

        var plain = Build.Proxy("plain.example.com", 8080);
        var rp2 = Handler(HostRoute(Gen(hosts: [plain]).Config, "srv1", "plain.example.com"), "reverse_proxy");
        Assert.Null(rp2["headers"]);
    }

    [Fact]
    public void Locations_come_first_longest_path_first_with_strip_prefix()
    {
        var h = Build.Proxy("loc.example.com", 9000);
        h.Locations =
        [
            new ProxyLocation { Path = "/api", Upstreams = [new Upstream { Host = "127.0.0.1", Port = 9001 }], StripPrefix = true },
            new ProxyLocation { Path = "/api/v2/", Upstreams = [new Upstream { Scheme = UpstreamScheme.Https, Host = "api2.internal", Port = 443 }], UpstreamTlsInsecure = true },
        ];
        var sub = Sub(HostRoute(Gen(hosts: [h]).Config, "srv1", "loc.example.com"));
        var first = sub[0]!;
        Assert.Equal(["/api/v2", "/api/v2/*"], Strings(first["match"]![0]!["path"]));
        Assert.Equal("reverse_proxy", first["handle"]![0]!["handler"]!.GetValue<string>());
        Assert.True(first["handle"]![0]!["transport"]!["tls"]!["insecure_skip_verify"]!.GetValue<bool>());
        var second = sub[1]!;
        Assert.Equal(["/api", "/api/*"], Strings(second["match"]![0]!["path"]));
        Assert.Equal("rewrite", second["handle"]![0]!["handler"]!.GetValue<string>());
        Assert.Equal("/api", second["handle"]![0]!["strip_path_prefix"]!.GetValue<string>());
        Assert.Equal("127.0.0.1:9001", second["handle"]![1]!["upstreams"]![0]!["dial"]!.GetValue<string>());
        // default proxy last
        Assert.Equal("127.0.0.1:9000", sub[^1]!["handle"]![0]!["upstreams"]![0]!["dial"]!.GetValue<string>());
    }

    [Fact]
    public void Redirect_preserves_path_and_code()
    {
        var h = new SiteHost { Kind = HostKind.Redirect, Domains = ["old.example.com"], Tls = TlsMode.None, RedirectTarget = "https://new.example.com/", RedirectCode = 308, PreservePath = true, Compression = false };
        var sr = Handler(HostRoute(Gen(hosts: [h]).Config, "srv1", "old.example.com"), "static_response");
        Assert.Equal(308, sr["status_code"]!.GetValue<int>());
        Assert.Equal(["https://new.example.com{http.request.uri}"], Strings(sr["headers"]!["Location"]));

        h.PreservePath = false;
        h.RedirectCode = 302;
        sr = Handler(HostRoute(Gen(hosts: [h]).Config, "srv1", "old.example.com"), "static_response");
        Assert.Equal(302, sr["status_code"]!.GetValue<int>());
        Assert.Equal(["https://new.example.com/"], Strings(sr["headers"]!["Location"]));
    }

    [Fact]
    public void Response_host()
    {
        var h = new SiteHost { Kind = HostKind.Response, Domains = ["maint.example.com"], Tls = TlsMode.None, ResponseStatus = 503, ResponseBody = "<h1>Maintenance</h1>", ResponseContentType = "text/html; charset=utf-8", Compression = false };
        var sr = Handler(HostRoute(Gen(hosts: [h]).Config, "srv1", "maint.example.com"), "static_response");
        Assert.Equal(503, sr["status_code"]!.GetValue<int>());
        Assert.Equal("<h1>Maintenance</h1>", sr["body"]!.GetValue<string>());
        Assert.Equal(["text/html; charset=utf-8"], Strings(sr["headers"]!["Content-Type"]));
    }

    [Fact]
    public void Hsts_response_headers_block_exploits_and_advanced_routes_order()
    {
        var h = Build.Proxy("sec.example.com", tls: TlsMode.Acme);
        h.Hsts = true;
        h.HstsSubdomains = true;
        h.HstsMaxAgeSeconds = 600;
        h.BlockExploits = true;
        h.Compression = true;
        h.ResponseHeaders = [new HeaderOp { Name = "X-Frame-Options", Value = "DENY" }, new HeaderOp { Action = HeaderAction.Delete, Name = "Server" }, new HeaderOp { Action = HeaderAction.Add, Name = "X-A", Value = "1" }];
        h.AdvancedRoutesJson = """[{"match":[{"path":["/custom"]}],"handle":[{"handler":"static_response","body":"custom"}]}]""";
        var sub = Sub(HostRoute(Gen(hosts: [h]).Config, "srv0", "sec.example.com"));

        // order: block exploits, headers, encode, advanced, proxy
        Assert.NotNull(sub[0]!["match"]![0]!["path_regexp"]);
        Assert.Equal(403, sub[0]!["handle"]![0]!["status_code"]!.GetValue<int>());
        var headers = sub[1]!["handle"]![0]!;
        Assert.Equal("headers", headers["handler"]!.GetValue<string>());
        Assert.Equal(["max-age=600; includeSubDomains"], Strings(headers["response"]!["set"]!["Strict-Transport-Security"]));
        Assert.Equal(["DENY"], Strings(headers["response"]!["set"]!["X-Frame-Options"]));
        Assert.Equal(["1"], Strings(headers["response"]!["add"]!["X-A"]));
        Assert.Equal(["Server"], Strings(headers["response"]!["delete"]));
        Assert.True(headers["response"]!["deferred"]!.GetValue<bool>());
        Assert.Equal("encode", sub[2]!["handle"]![0]!["handler"]!.GetValue<string>());
        Assert.Equal("custom", sub[3]!["handle"]![0]!["body"]!.GetValue<string>());
        Assert.Equal("reverse_proxy", sub[4]!["handle"]![0]!["handler"]!.GetValue<string>());
    }

    [Fact]
    public void Hsts_not_sent_for_plain_http_hosts()
    {
        var h = Build.Proxy("plain.example.com");
        h.Hsts = true;
        Assert.DoesNotContain("Strict-Transport-Security", Gen(hosts: [h]).Config.ToJsonString());
    }

    [Fact]
    public void Invalid_advanced_routes_are_ignored_with_warning()
    {
        var h = Build.Proxy("adv.example.com");
        h.AdvancedRoutesJson = "{not json";
        var r = Gen(hosts: [h]);
        Assert.Contains(r.Warnings, w => w.Contains("adv.example.com"));
    }

    // ------------------------------------------------------------------ access lists

    private static AccessList List(bool satisfyAny, bool withUsers, params (IpRuleAction, string)[] rules) => new()
    {
        Id = "al1",
        Name = "Staff",
        SatisfyAny = satisfyAny,
        Users = withUsers ? [new AccessUser { Username = "bob", PasswordHash = "$2a$12$abcdefghijklmnopqrstuuAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" }] : [],
        Rules = rules.Select(r => new IpRule { Action = r.Item1, Cidr = r.Item2 }).ToList(),
    };

    private JsonArray AccessSub(AccessList al, CaddySettings? s = null)
    {
        var h = Build.Proxy("al.example.com");
        h.AccessListId = al.Id;
        return Sub(HostRoute(Gen(s, [h], [al]).Config, "srv1", "al.example.com"));
    }

    [Fact]
    public void Ip_allow_rules_with_implicit_deny()
    {
        var sub = AccessSub(List(false, false, (IpRuleAction.Allow, "10.0.0.0/8"), (IpRuleAction.Deny, "all")));
        var decision = sub[0]!["handle"]![0]!["routes"]!.AsArray();
        Assert.Equal(3, decision.Count);
        // implicit default first, then rules in reverse order (last assignment = first matching rule)
        Assert.Null(decision[0]!["match"]);
        Assert.Equal("deny", decision[0]!["handle"]![0]!["cpm_ip"]!.GetValue<string>());
        Assert.Equal(["0.0.0.0/0", "::/0"], Strings(decision[1]!["match"]![0]!["remote_ip"]!["ranges"]));
        Assert.Equal("deny", decision[1]!["handle"]![0]!["cpm_ip"]!.GetValue<string>());
        Assert.Equal(["10.0.0.0/8"], Strings(decision[2]!["match"]![0]!["remote_ip"]!["ranges"]));
        Assert.Equal("allow", decision[2]!["handle"]![0]!["cpm_ip"]!.GetValue<string>());
        Assert.All(decision, d => Assert.Null(d!["terminal"]));
        Assert.Equal(["deny"], Strings(sub[1]!["match"]![0]!["vars"]!["cpm_ip"]));
        Assert.Equal(403, sub[1]!["handle"]![0]!["status_code"]!.GetValue<int>());
        Assert.Equal("reverse_proxy", sub[2]!["handle"]![0]!["handler"]!.GetValue<string>());
    }

    [Fact]
    public void Only_deny_rules_have_implicit_allow_and_client_ip_with_trusted_proxies()
    {
        var sub = AccessSub(List(false, false, (IpRuleAction.Deny, "192.168.1.10")), new CaddySettings { TrustedProxies = ["10.0.0.0/8"] });
        var decision = sub[0]!["handle"]![0]!["routes"]!.AsArray();
        Assert.Equal("allow", decision[0]!["handle"]![0]!["cpm_ip"]!.GetValue<string>());
        Assert.NotNull(decision[1]!["match"]![0]!["client_ip"]);
    }

    [Fact]
    public void Basic_auth_only_strips_authorization()
    {
        var sub = AccessSub(List(false, true));
        var auth = sub[0]!["handle"]![0]!;
        Assert.Equal("authentication", auth["handler"]!.GetValue<string>());
        var basic = auth["providers"]!["http_basic"]!;
        Assert.Equal("bcrypt", basic["hash"]!["algorithm"]!.GetValue<string>());
        Assert.Equal("bob", basic["accounts"]![0]!["username"]!.GetValue<string>());
        Assert.StartsWith("$2a$", basic["accounts"]![0]!["password"]!.GetValue<string>());
        var rp = sub[1]!["handle"]![0]!;
        Assert.Equal(["Authorization"], Strings(rp["headers"]!["request"]!["delete"]));
    }

    [Fact]
    public void Pass_auth_to_upstream_keeps_authorization()
    {
        var al = List(false, true);
        al.PassAuthToUpstream = true;
        var sub = AccessSub(al);
        Assert.Null(sub[1]!["handle"]![0]!["headers"]);
    }

    [Fact]
    public void Satisfy_all_requires_ip_and_auth()
    {
        var sub = AccessSub(List(false, true, (IpRuleAction.Allow, "10.0.0.0/8")));
        Assert.Equal(["deny"], Strings(sub[1]!["match"]![0]!["vars"]!["cpm_ip"]));
        Assert.Null(sub[2]!["match"]);
        Assert.Equal("authentication", sub[2]!["handle"]![0]!["handler"]!.GetValue<string>());
    }

    [Fact]
    public void Satisfy_any_skips_auth_for_allowed_ips()
    {
        var sub = AccessSub(List(true, true, (IpRuleAction.Allow, "10.0.0.0/8")));
        Assert.Equal(["allow"], Strings(sub[1]!["match"]![0]!["not"]![0]!["vars"]!["cpm_ip"]));
        Assert.Equal("authentication", sub[1]!["handle"]![0]!["handler"]!.GetValue<string>());
        Assert.Equal("reverse_proxy", sub[2]!["handle"]![0]!["handler"]!.GetValue<string>());
    }

    [Fact]
    public void Missing_access_list_fails_closed()
    {
        var h = Build.Proxy("gone.example.com");
        h.AccessListId = "missing";
        var r = Gen(hosts: [h]);
        var sub = Sub(HostRoute(r.Config, "srv1", "gone.example.com"));
        Assert.Equal(403, sub[0]!["handle"]![0]!["status_code"]!.GetValue<int>());
        Assert.Contains(r.Warnings, w => w.Contains("gone.example.com"));
    }

    // ------------------------------------------------------------------ default site / logs / streams

    [Theory]
    [InlineData(DefaultSiteBehavior.NotFound)]
    [InlineData(DefaultSiteBehavior.CloseConnection)]
    [InlineData(DefaultSiteBehavior.Redirect)]
    [InlineData(DefaultSiteBehavior.CaddyWelcome)]
    public void Default_site_behaviour(DefaultSiteBehavior mode)
    {
        var cfg = Gen(new CaddySettings { DefaultSite = mode, DefaultRedirectUrl = "https://www.example.com/" }).Config;
        var last = Routes(cfg, "srv1").Last()!;
        Assert.Null(last["match"]);
        var h = last["handle"]![0]!;
        switch (mode)
        {
            case DefaultSiteBehavior.NotFound: Assert.Equal(404, h["status_code"]!.GetValue<int>()); break;
            case DefaultSiteBehavior.CloseConnection: Assert.True(h["abort"]!.GetValue<bool>()); break;
            case DefaultSiteBehavior.Redirect:
                Assert.Equal(302, h["status_code"]!.GetValue<int>());
                Assert.Equal(["https://www.example.com/"], Strings(h["headers"]!["Location"]));
                break;
            case DefaultSiteBehavior.CaddyWelcome:
                Assert.Equal(200, h["status_code"]!.GetValue<int>());
                Assert.Contains("Caddy works", h["body"]!.GetValue<string>());
                break;
        }
    }

    [Fact]
    public void Access_logs_use_named_loggers()
    {
        var h = Build.Proxy("*.logs.example.com", tls: TlsMode.Internal);
        h.Id = "h1";
        h.AccessLog = true;
        var cfg = Gen(hosts: [h]).Config;
        var logs = cfg["logging"]!["logs"]!;
        var logger = logs["cpm_access_h1"]!;
        Assert.Equal(Path.Combine(_env.Paths.AccessLogDir, "wildcard.logs.example.com.log"), logger["writer"]!["filename"]!.GetValue<string>());
        Assert.Equal("json", logger["encoder"]!["format"]!.GetValue<string>());
        Assert.Equal(["http.log.access.cpm_access_h1"], Strings(logger["include"]));
        var srvLogs = Srv(cfg, "srv0")["logs"]!;
        Assert.Equal(["cpm_access_h1"], Strings(srvLogs["logger_names"]!["*.logs.example.com"]));
        // Traffic statistics (on by default) need every request logged: unmapped hosts are not skipped.
        Assert.Null(srvLogs["skip_unmapped_hosts"]);

        var noStats = Gen(new CaddySettings { TrafficStatsEnabled = false }, hosts: [h]).Config;
        Assert.True(Srv(noStats, "srv0")["logs"]!["skip_unmapped_hosts"]!.GetValue<bool>());
        Assert.Null(noStats["logging"]!["logs"]!["cpm_stats"]);
    }

    [Fact]
    public void Streams_require_layer4_module()
    {
        var st = new StreamHost { Id = "s1", Protocol = StreamProtocol.Tcp, ListenPort = 3389, UpstreamHost = "10.0.0.9", UpstreamPort = 3389 };
        var udp = new StreamHost { Id = "s2", Protocol = StreamProtocol.Udp, ListenPort = 53, UpstreamHost = "10.0.0.53", UpstreamPort = 53 };

        var without = Gen(streams: [st], modules: ["http", "tls"]);
        Assert.Null(without.Config["apps"]!["layer4"]);
        Assert.Contains(without.Warnings, w => w.Contains("layer4"));

        var unknown = Gen(streams: [st]);
        Assert.Null(unknown.Config["apps"]!["layer4"]);

        var with = Gen(streams: [st, udp], modules: ["http", "layer4"]).Config;
        var servers = with["apps"]!["layer4"]!["servers"]!;
        Assert.Equal(["tcp/:3389"], Strings(servers["s1"]!["listen"]));
        var proxy = servers["s1"]!["routes"]![0]!["handle"]![0]!;
        Assert.Equal("proxy", proxy["handler"]!.GetValue<string>());
        Assert.Equal(["tcp/10.0.0.9:3389"], Strings(proxy["upstreams"]![0]!["dial"]));
        Assert.Equal(["udp/:53"], Strings(servers["s2"]!["listen"]));
        Assert.Equal(["udp/10.0.0.53:53"], Strings(servers["s2"]!["routes"]![0]!["handle"]![0]!["upstreams"]![0]!["dial"]));
    }
}
