using System.Net;
using System.Text.Json.Nodes;
using CaddyManager.Config.Admin;
using CaddyManager.Config.Certificates;
using CaddyManager.Config.Generation;
using CaddyManager.Config.Validation;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaddyManager.Config.Tests;

/// <summary>Routing order, NTLM transport, plugin/advanced settings and the endpoint guard in the generator.</summary>
public sealed class Round2GeneratorTests : IDisposable
{
    private readonly TempEnv _env = new();
    public void Dispose() => _env.Dispose();

    private ConfigGeneratorResult Gen(CaddySettings? s = null, IEnumerable<SiteHost>? hosts = null, IEnumerable<Certificate>? certs = null,
        IEnumerable<string>? modules = null, string? issuerJson = null, LocalEndpointGuard? guard = null, IEnumerable<StreamHost>? streams = null,
        string? mac = null)
    {
        var input = Build.Input(_env.Paths, s, hosts, certs: certs, streams: streams, modules: modules, eabMac: mac) with
        {
            AcmeIssuerJson = issuerJson,
            EndpointGuard = guard,
        };
        return CaddyConfigGenerator.Generate(input);
    }

    private static JsonArray Routes(JsonObject cfg, string server) => cfg["apps"]!["http"]!["servers"]![server]!["routes"]!.AsArray();

    private static string[] HostsOf(JsonArray routes) =>
        routes.Where(r => r!["match"] is not null).Select(r => string.Join(",", r!["match"]![0]!["host"]!.AsArray().Select(h => h!.GetValue<string>()))).ToArray();

    private static string[] Strings(JsonNode? n) => n!.AsArray().Select(x => x!.GetValue<string>()).ToArray();

    private static SiteHost Respond(string body, params string[] domains) => new()
    {
        Id = body,
        Kind = HostKind.Response,
        Domains = domains.ToList(),
        Tls = TlsMode.None,
        ResponseStatus = 200,
        ResponseBody = body,
        Compression = false,
    };

    // ------------------------------------------------------------------ routing order

    [Fact]
    public void Exact_names_of_every_host_come_before_any_wildcard()
    {
        var a = Respond("A", "a.test", "*.test");
        var b = Respond("B", "b.test");
        var routes = Routes(Gen(hosts: [a, b]).Config, "srv1");
        Assert.Equal(["a.test", "b.test", "*.test"], HostsOf(routes));
        // one handler, shared (cloned) by both routes of host A
        Assert.True(JsonNode.DeepEquals(routes[0]!["handle"], routes[2]!["handle"]));
        Assert.False(ReferenceEquals(routes[0]!["handle"], routes[2]!["handle"]));
    }

    [Fact]
    public void More_specific_wildcards_come_first()
    {
        var broad = Respond("broad", "*.b.test");
        var narrow = Respond("narrow", "*.a.b.test", "x.a.b.test");
        var mixed = Respond("mixed", "*.z.test", "*.deep.z.test");
        var routes = Routes(Gen(hosts: [broad, narrow, mixed]).Config, "srv1");
        Assert.Equal(["x.a.b.test", "*.a.b.test", "*.deep.z.test", "*.b.test", "*.z.test"], HostsOf(routes));
    }

    [Fact]
    public void Tls_connection_policies_exact_then_wildcard_then_catch_all()
    {
        var c1 = new Certificate { Id = "c1", Name = "wild", CertPath = "/s/c1/fullchain.pem", KeyPath = "/s/c1/privkey.pem" };
        var c2 = new Certificate { Id = "c2", Name = "b", CertPath = "/s/c2/fullchain.pem", KeyPath = "/s/c2/privkey.pem" };
        var wild = Build.Proxy("*.test", tls: TlsMode.Custom);
        wild.Domains.Insert(0, "a.test");
        wild.CertificateId = "c1";
        var deep = Build.Proxy("*.x.test", tls: TlsMode.Custom);
        deep.CertificateId = "c1";
        var b = Build.Proxy("b.test", tls: TlsMode.Custom);
        b.CertificateId = "c2";
        var cfg = Gen(hosts: [wild, deep, b], certs: [c1, c2]).Config;
        var policies = cfg["apps"]!["http"]!["servers"]!["srv0"]!["tls_connection_policies"]!.AsArray();
        var sni = policies.Select(p => p!["match"]?["sni"] is { } s ? string.Join(",", Strings(s)) : "{}").ToArray();
        Assert.Equal(["a.test", "b.test", "*.x.test", "*.test", "{}"], sni);
        Assert.Equal(["cpm-c1"], Strings(policies[0]!["certificate_selection"]!["any_tag"]));
        Assert.Equal(["cpm-c2"], Strings(policies[1]!["certificate_selection"]!["any_tag"]));
    }

    // ------------------------------------------------------------------ NTLM

    [Fact]
    public void Ntlm_uses_the_http_ntlm_transport_when_installed()
    {
        var h = Build.Proxy("sp.corp.test", tls: TlsMode.Internal);
        h.UpstreamNtlm = true;
        h.Upstreams = [new Upstream { Scheme = UpstreamScheme.Https, Host = "sharepoint01", Port = 443 }];
        h.UpstreamTlsInsecure = true;
        h.Locations = [new ProxyLocation { Path = "/owa", Upstreams = [new Upstream { Host = "exch01", Port = 80 }] }];
        var r = Gen(hosts: [h], modules: ["http", CaddyConfigGenerator.NtlmModule]);
        Assert.DoesNotContain(r.Warnings, w => w.Contains("NTLM"));
        var json = r.Config.ToJsonString();
        var proxies = JsonNode.Parse(json)!["apps"]!["http"]!["servers"]!["srv0"]!["routes"]![0]!["handle"]![0]!["routes"]!.AsArray()
            .SelectMany(x => x!["handle"]!.AsArray()).Where(x => x!["handler"]!.GetValue<string>() == "reverse_proxy").ToList();
        Assert.Equal(2, proxies.Count);
        Assert.All(proxies, p => Assert.Equal("http_ntlm", p!["transport"]!["protocol"]!.GetValue<string>()));
        var main = proxies.Single(p => p!["upstreams"]![0]!["dial"]!.GetValue<string>() == "sharepoint01:443");
        Assert.True(main!["transport"]!["tls"]!["insecure_skip_verify"]!.GetValue<bool>());
        Assert.Null(proxies.Single(p => p != main)!["transport"]!["tls"]);
    }

    [Fact]
    public void Ntlm_without_the_plugin_falls_back_with_a_warning()
    {
        var h = Build.Proxy("sp.corp.test", tls: TlsMode.None);
        h.UpstreamNtlm = true;
        var r = Gen(hosts: [h], modules: ["http"]);
        Assert.Contains(r.Warnings, w => w.Contains("NTLM") && w.Contains(CaddyConfigGenerator.NtlmPlugin));
        Assert.DoesNotContain("http_ntlm", r.Config.ToJsonString());
        Assert.Contains(Gen(hosts: [h]).Warnings, w => w.Contains(CaddyConfigGenerator.NtlmPlugin)); // modules unknown
    }

    // ------------------------------------------------------------------ plugin / advanced settings

    [Fact]
    public void Extra_apps_are_merged_but_generated_apps_are_protected()
    {
        var s = new CaddySettings { ExtraAppsJson = """{"dynamic_dns":{"domains":{"example.com":["@"]}},"tls":{"x":1},"bad":5}""" };
        var r = Gen(s, [Build.Proxy("a.test", tls: TlsMode.Internal)]);
        var apps = r.Config["apps"]!.AsObject();
        Assert.Equal("@", apps["dynamic_dns"]!["domains"]!["example.com"]![0]!.GetValue<string>());
        Assert.Null(apps["tls"]!["x"]);
        Assert.Null(apps["bad"]);
        Assert.Contains(r.Warnings, w => w.Contains("'tls'"));
        Assert.Contains(r.Warnings, w => w.Contains("'bad'"));
        Assert.Contains(Gen(new CaddySettings { ExtraAppsJson = "[1]" }).Warnings, w => w.Contains("extra apps"));
    }

    [Fact]
    public void Acme_issuer_json_is_deep_merged_into_every_acme_issuer()
    {
        const string dns = """{"challenges":{"dns":{"provider":{"name":"cloudflare","api_token":"tok"}}},"module":"evil"}""";
        var s = new CaddySettings { AcmeCa = AcmeCa.LetsEncrypt, EabKeyId = "kid", HttpPort = 8080 };
        var r = Gen(s, [Build.Proxy("*.example.com", tls: TlsMode.Acme)], issuerJson: dns, modules: ["http", "tls"], mac: "mac");
        var issuers = r.Config["apps"]!["tls"]!["automation"]!["policies"]![0]!["issuers"]!.AsArray();
        Assert.Equal(2, issuers.Count); // LE + ZeroSSL fallback
        foreach (var iss in issuers)
        {
            Assert.Equal("acme", iss!["module"]!.GetValue<string>());
            Assert.Equal("cloudflare", iss["challenges"]!["dns"]!["provider"]!["name"]!.GetValue<string>());
            Assert.Equal(8080, iss["challenges"]!["http"]!["alternate_port"]!.GetValue<int>()); // merged, not replaced
        }
        Assert.DoesNotContain(r.Warnings, w => w.Contains("wildcard certificates"));
        Assert.Contains(r.Warnings, w => w.Contains("dns.providers.cloudflare"));
        Assert.Contains(r.Warnings, w => w.Contains("'module'"));

        var plain = Gen(s, [Build.Proxy("*.example.com", tls: TlsMode.Acme)]);
        Assert.Contains(plain.Warnings, w => w.Contains("wildcard certificates"));
        var withModule = Gen(s, [Build.Proxy("*.example.com", tls: TlsMode.Acme)], issuerJson: dns, modules: ["dns.providers.cloudflare"]);
        Assert.DoesNotContain(withModule.Warnings, w => w.Contains("dns.providers.cloudflare"));
    }

    [Fact]
    public void Tls_connection_policy_json_is_merged_into_every_policy()
    {
        var s = new CaddySettings { TlsConnectionPolicyJson = """{"protocol_min":"tls1.3","match":{"sni":["x"]}}""" };
        var onlyAcme = Gen(s, [Build.Proxy("a.test", tls: TlsMode.Acme)]);
        var single = onlyAcme.Config["apps"]!["http"]!["servers"]!["srv0"]!["tls_connection_policies"]!.AsArray();
        Assert.Single(single);
        Assert.Equal("tls1.3", single[0]!["protocol_min"]!.GetValue<string>());
        Assert.Null(single[0]!["match"]);
        Assert.Contains(onlyAcme.Warnings, w => w.Contains("'match'"));

        var cert = new Certificate { Id = "c1", Name = "c", CertPath = "/c.pem", KeyPath = "/k.pem" };
        var custom = Build.Proxy("c.test", tls: TlsMode.Custom);
        custom.CertificateId = "c1";
        var both = Gen(s, [custom], [cert]).Config["apps"]!["http"]!["servers"]!["srv0"]!["tls_connection_policies"]!.AsArray();
        Assert.Equal(2, both.Count);
        Assert.All(both, p => Assert.Equal("tls1.3", p!["protocol_min"]!.GetValue<string>()));
        Assert.Equal(["c.test"], Strings(both[0]!["match"]!["sni"]));
    }

    // ------------------------------------------------------------------ endpoint guard safety net

    [Fact]
    public void Generator_drops_targets_of_protected_endpoints()
    {
        var guard = new LocalEndpointGuard(new Dictionary<int, string> { [2019] = LocalEndpointGuard.AdminApiDescription }, [], ["localhost"]);
        var h = Build.Proxy("a.test", 2019, host: "127.0.0.1");
        h.Upstreams.Add(new Upstream { Host = "10.0.0.1", Port = 80 });
        var adv = Build.Proxy("adv.test", 8080);
        adv.AdvancedRoutesJson = """[{"handle":[{"handler":"reverse_proxy","upstreams":[{"dial":"localhost:2019"}]}]}]""";
        var stream = new StreamHost { Id = "s1", ListenPort = 3389, UpstreamHost = "localhost", UpstreamPort = 2019 };
        var r = Gen(hosts: [h, adv], guard: guard, streams: [stream], modules: ["layer4"]);
        var json = r.Config["apps"]!.ToJsonString();
        Assert.DoesNotContain("127.0.0.1:2019", json);
        Assert.DoesNotContain("localhost:2019", json);
        Assert.Contains("10.0.0.1:80", json);
        Assert.Contains(r.Warnings, w => w.Contains("Caddy admin API") && w.Contains("adv.test"));
        Assert.Contains(r.Warnings, w => w.Contains("Stream tcp/3389 was skipped"));
    }

    // ------------------------------------------------------------------ real binary

    [CaddyFact]
    public async Task Caddy_validates_plugin_settings_ntlm_fallback_and_wildcard_ordering()
    {
        using var s = new ConfigServices(installBinary: true);
        using var cert = TestCerts.SelfSigned(["c.test", "*.c.test"]);
        var files = new CertificateFileStore(s.Store, s.Paths, NullLogger<CertificateFileStore>.Instance);
        var parsed = CertificateParser.FromPem(cert.ExportCertificatePem(), TestCerts.KeyPem(cert));
        var (cp, kp) = files.Write("c1", parsed);
        s.Store.Col<Certificate>().Insert(new Certificate { Id = "c1", Name = "c", CertPath = cp, KeyPath = kp });

        var settings = new CaddySettings
        {
            AdminListen = $"127.0.0.1:{Net.FreeTcpPort()}",
            HttpPort = 18095,
            HttpsPort = 18495,
            TlsConnectionPolicyJson = """{"protocol_min":"tls1.3"}""",
            ExtraAppsJson = """{"events":{}}""",
            AcmeIssuerJsonProtected = s.Provider.GetRequiredService<ISecretProtector>().Protect("""{"preferred_chains":{"smallest":true}}"""),
        };
        s.Store.SaveSettings(settings);
        var custom = Build.Proxy("c.test", 8081, TlsMode.Custom);
        custom.Domains.Add("*.c.test");
        custom.CertificateId = "c1";
        var ntlm = Build.Proxy("sp.test", 8082, TlsMode.Acme);
        ntlm.UpstreamNtlm = true;
        ntlm.Upstreams = [new Upstream { Scheme = UpstreamScheme.Https, Host = "10.9.9.9", Port = 443 }];
        s.Store.Col<SiteHost>().Insert(custom);
        s.Store.Col<SiteHost>().Insert(ntlm);
        s.Store.Col<SiteHost>().Insert(Build.Proxy("x.c.test", 8083, TlsMode.Internal));

        var result = s.Config.Generate();
        Assert.Contains(result.Warnings, w => w.Contains(CaddyConfigGenerator.NtlmPlugin)); // vanilla binary: fallback
        var json = result.ToJson();
        Assert.Contains("\"events\"", json);
        Assert.Contains("\"smallest\"", json);
        var v = await s.Config.ValidateAsync(json);
        Assert.True(v.Valid, v.Error + "\n" + json);
    }

    [CaddyFact]
    public async Task Live_caddy_routes_exact_names_to_their_own_host_before_wildcards()
    {
        using var s = new ConfigServices(installBinary: true);
        var httpPort = Net.FreeTcpPort();
        s.Store.SaveSettings(new CaddySettings { AdminListen = $"127.0.0.1:{Net.FreeTcpPort()}", HttpPort = httpPort, HttpsPort = Net.FreeTcpPort(), EnableHttp3 = false });
        // Host A has an exact and a wildcard name; host B's exact name is covered by A's wildcard.
        s.Store.Col<SiteHost>().Insert(Respond("A", "a.test", "*.test"));
        s.Store.Col<SiteHost>().Insert(Respond("B", "b.test"));
        s.Store.Col<SiteHost>().Insert(Respond("DEEP", "*.deep.test"));

        s.Config.EnsureBootConfig();
        using var caddy = new CaddyProcess(s.Paths);
        var admin = s.Provider.GetRequiredService<CaddyAdminClient>();
        for (var i = 0; i < 100 && !await admin.IsReachableAsync(); i++) await Task.Delay(200);
        var apply = await s.Config.ApplyAsync("routing test");
        Assert.True(apply.Success, apply.Error + "\n" + caddy.Output);

        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
        async Task<string> Get(string host)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{httpPort}/");
            req.Headers.Host = host;
            req.Headers.ConnectionClose = true;
            using var r = await http.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            return await r.Content.ReadAsStringAsync();
        }
        Assert.Equal("B", await Get("b.test"));
        Assert.Equal("A", await Get("a.test"));
        Assert.Equal("A", await Get("c.test"));
        Assert.Equal("DEEP", await Get("x.deep.test"));
        await admin.StopAsync();
        await caddy.WaitForExitAsync(TimeSpan.FromSeconds(10));
    }
}
