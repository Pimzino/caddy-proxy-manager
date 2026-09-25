using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using CaddyManager.Config.Admin;
using CaddyManager.Config.Certificates;
using CaddyManager.Config.Generation;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaddyManager.Config.Tests;

/// <summary>Runs the real Caddy binary: `caddy validate` on a feature-complete config and `caddy run` behaviour checks.</summary>
public sealed class CaddyIntegrationTests
{
    // ------------------------------------------------------------------ fixture model

    private static void SeedEverything(ConfigServices s, int upstreamPort, int httpPort, int httpsPort, int adminPort, out string customCertId, out X509Certificate2 customCert)
    {
        var store = s.Store;
        store.SaveSettings(new CaddySettings
        {
            HttpPort = httpPort,
            HttpsPort = httpsPort,
            AdminListen = $"127.0.0.1:{adminPort}",
            EnableHttp3 = false,
            AcmeEmail = "ops@example.com",
            AcmeCa = AcmeCa.LetsEncryptStaging,
            TrustedProxies = [],
            LogLevel = "info",
            DefaultSite = DefaultSiteBehavior.NotFound,
        });

        var basic = new AccessList { Id = "basic", Name = "Basic", Users = [new AccessUser { Username = "bob", PasswordHash = Passwords.Hash("hunter2") }] };
        var deny = new AccessList { Id = "deny", Name = "Deny local", Rules = [new IpRule { Action = IpRuleAction.Deny, Cidr = "127.0.0.1" }] };
        var anyLocal = new AccessList
        {
            Id = "anylocal", Name = "Local or auth", SatisfyAny = true,
            Rules = [new IpRule { Action = IpRuleAction.Allow, Cidr = "127.0.0.0/8" }],
            Users = [new AccessUser { Username = "bob", PasswordHash = Passwords.Hash("hunter2") }],
        };
        var anyRemote = new AccessList
        {
            Id = "anyremote", Name = "LAN or auth", SatisfyAny = true,
            Rules = [new IpRule { Action = IpRuleAction.Allow, Cidr = "10.0.0.0/8" }],
            Users = [new AccessUser { Username = "bob", PasswordHash = Passwords.Hash("hunter2") }],
        };
        var both = new AccessList
        {
            Id = "both", Name = "Local and auth", SatisfyAny = false,
            Rules = [new IpRule { Action = IpRuleAction.Allow, Cidr = "127.0.0.0/8" }],
            Users = [new AccessUser { Username = "bob", PasswordHash = Passwords.Hash("hunter2") }],
        };
        foreach (var al in new[] { basic, deny, anyLocal, anyRemote, both }) store.Col<AccessList>().Insert(al);

        customCert = TestCerts.SelfSigned(["custom.test"]);
        var parsed = CertificateParser.FromPem(customCert.ExportCertificatePem(), TestCerts.KeyPem(customCert));
        var files = new CertificateFileStore(store, s.Paths, NullLogger<CertificateFileStore>.Instance);
        customCertId = "cust1";
        var (cp, kp) = files.Write(customCertId, parsed);
        store.Col<Certificate>().Insert(new Certificate
        {
            Id = customCertId, Name = "custom.test", Source = CertificateSource.Uploaded, CertPath = cp, KeyPath = kp,
            Subjects = parsed.Metadata.Subjects, NotAfter = parsed.Metadata.NotAfter, Thumbprint = parsed.Metadata.Thumbprint,
        });

        var staticRoot = Path.Combine(s.Paths.DataDir, "www");
        Directory.CreateDirectory(staticRoot);
        File.WriteAllText(Path.Combine(staticRoot, "index.html"), "<h1>spa index</h1>");
        File.WriteAllText(Path.Combine(staticRoot, "hello.txt"), "hello static");

        SiteHost P(string domain, TlsMode tls = TlsMode.None) => new()
        {
            Kind = HostKind.Proxy, Domains = [domain], Tls = tls, Compression = false,
            Upstreams = [new Upstream { Host = "127.0.0.1", Port = upstreamPort }],
        };

        var proxy = P("proxy.test");
        proxy.Compression = true;
        proxy.AccessLog = true;
        proxy.RequestHeaders = [new HeaderOp { Name = "X-Test", Value = "from-caddy" }];
        proxy.ResponseHeaders = [new HeaderOp { Name = "X-Served-By", Value = "cpm" }, new HeaderOp { Action = HeaderAction.Delete, Name = "Server" }];
        proxy.BlockExploits = true;
        proxy.Locations = [new ProxyLocation { Path = "/api", StripPrefix = true, Upstreams = [new Upstream { Host = "127.0.0.1", Port = upstreamPort }] }];
        proxy.UpstreamHostHeader = "{upstream}";
        proxy.HealthCheck = new HealthCheck { Enabled = true, Path = "/health", IntervalSeconds = 30, TimeoutSeconds = 5 };
        proxy.LoadBalancing = LoadBalancingPolicy.First;

        var auth = P("auth.test"); auth.AccessListId = "basic";
        var denied = P("deny.test"); denied.AccessListId = "deny";
        var anyL = P("anylocal.test"); anyL.AccessListId = "anylocal";
        var anyR = P("anyremote.test"); anyR.AccessListId = "anyremote";
        var bothH = P("both.test"); bothH.AccessListId = "both";

        var redirect = new SiteHost { Kind = HostKind.Redirect, Domains = ["redirect.test"], Tls = TlsMode.None, RedirectTarget = "https://example.com/", RedirectCode = 302, PreservePath = true };
        var respond = new SiteHost { Kind = HostKind.Response, Domains = ["respond.test"], Tls = TlsMode.None, ResponseStatus = 418, ResponseBody = "short and stout", ResponseContentType = "text/plain; charset=utf-8", Compression = false };
        var spa = new SiteHost { Kind = HostKind.Static, Domains = ["static.test"], Tls = TlsMode.None, RootPath = staticRoot, SpaFallback = true, Browse = false };

        var internalHost = P("internal.test", TlsMode.Internal);
        internalHost.Hsts = true;
        var internalBoth = P("internal-both.test", TlsMode.Internal);
        internalBoth.ForceHttps = false;
        var custom = P("custom.test", TlsMode.Custom);
        custom.CertificateId = customCertId;

        foreach (var h in new[] { proxy, auth, denied, anyL, anyR, bothH, redirect, respond, spa, internalHost, internalBoth, custom })
            store.Col<SiteHost>().Insert(h);
    }

    // ------------------------------------------------------------------ validate

    [CaddyFact]
    public async Task Caddy_validate_accepts_feature_complete_config()
    {
        using var s = new ConfigServices(installBinary: true);
        SeedEverything(s, 9, 18080, 18443, 12019, out _, out var cert);
        cert.Dispose();

        // Extra features that are not exercised live.
        var settings = s.Store.GetSettings<CaddySettings>();
        settings.TrustedProxies = ["10.0.0.0/8", "fd00::/8"];
        settings.BindAddresses = ["127.0.0.1"];
        settings.EnableHttp3 = true;
        settings.AcmeCa = AcmeCa.Custom;
        settings.CustomAcmeDirectory = "https://ca.corp.example/acme/directory";
        settings.EabKeyId = "kid";
        settings.EabMacKeyProtected = s.Provider.GetRequiredService<ISecretProtector>().Protect("bWFjLWtleS1mb3ItdGVzdHM");
        settings.DisableTlsAlpnChallenge = true;
        settings.ServerOptionsJson = """{"max_header_bytes": 1048576}""";
        settings.DefaultSite = DefaultSiteBehavior.CloseConnection;
        s.Store.SaveSettings(settings);

        var lb = Build.Proxy("lb.test", 81, TlsMode.Acme);
        lb.Upstreams = [new Upstream { Scheme = UpstreamScheme.Https, Host = "10.1.1.1", Port = 443 }, new Upstream { Scheme = UpstreamScheme.Https, Host = "backend.internal", Port = 8443 }];
        lb.UpstreamTlsInsecure = true;
        lb.LoadBalancing = LoadBalancingPolicy.Cookie;
        lb.HealthCheck = new HealthCheck { Enabled = true, Path = "/healthz", IntervalSeconds = 10, TimeoutSeconds = 2, ExpectStatus = 200 };
        lb.Hsts = true; lb.HstsSubdomains = true;
        lb.AccessListId = "anyremote";
        lb.AdvancedRoutesJson = """[{"match":[{"path":["/robots.txt"]}],"handle":[{"handler":"static_response","body":"User-agent: *\nDisallow: /"}]}]""";
        lb.Locations = [new ProxyLocation { Path = "/legacy/", StripPrefix = true, UpstreamTlsInsecure = true, Upstreams = [new Upstream { Scheme = UpstreamScheme.Https, Host = "old.internal", Port = 443 }] }];
        var wild = Build.Proxy("*.apps.test", 82, TlsMode.Internal);
        wild.AccessLog = true;
        foreach (var p in Enum.GetValues<LoadBalancingPolicy>())
        {
            var h = Build.Proxy($"lb-{p.ToString().ToLowerInvariant()}.test", 83, TlsMode.None);
            h.Upstreams.Add(new Upstream { Host = "127.0.0.2", Port = 83 });
            h.LoadBalancing = p;
            s.Store.Col<SiteHost>().Insert(h);
        }
        s.Store.Col<SiteHost>().Insert(lb);
        s.Store.Col<SiteHost>().Insert(wild);
        s.Store.Col<StreamHost>().Insert(new StreamHost { ListenPort = 3389, UpstreamHost = "10.0.0.5", UpstreamPort = 3389 });

        var result = s.Config.Generate();
        Assert.Contains(result.Warnings, w => w.Contains("layer4"));
        var json = result.ToJson();
        var v = await s.Config.ValidateAsync(json);
        Assert.True(v.Valid, v.Error + "\n" + v.Output + "\n" + json);
    }

    [CaddyFact]
    public async Task Caddy_validate_reports_errors()
    {
        using var s = new ConfigServices(installBinary: true);
        var v = await s.Config.ValidateAsync("""{"apps":{"http":{"servers":{"x":{"listen":[":1"],"routes":[{"handle":[{"handler":"no_such_handler"}]}]}}}}}""");
        Assert.False(v.Valid);
        Assert.Contains("no_such_handler", v.Error);
    }

    [CaddyFact]
    public async Task Apply_without_running_caddy_validates_and_writes_only()
    {
        using var s = new ConfigServices(installBinary: true);
        s.Store.SaveSettings(new CaddySettings { AdminListen = $"127.0.0.1:{Net.FreeTcpPort()}", HttpPort = 18081, HttpsPort = 18444 });
        s.Store.Col<SiteHost>().Insert(Build.Proxy("offline.test"));
        var r = await s.Config.ApplyAsync("test");
        Assert.True(r.Success, r.Error);
        Assert.True(r.WrittenOnly);
        Assert.Contains(r.Warnings, w => w.Contains("not running"));
        Assert.Contains("offline.test", File.ReadAllText(s.Paths.CaddyConfigFile));
        Assert.Single(s.Store.Col<ConfigRevision>().FindAll());

        // an invalid config is rejected by `caddy validate` and not written
        var h = s.Store.Col<SiteHost>().FindAll().Single();
        h.AdvancedRoutesJson = """[{"handle":[{"handler":"bogus_handler"}]}]""";
        s.Store.Col<SiteHost>().Update(h);
        var bad = await s.Config.ApplyAsync("bad");
        Assert.False(bad.Success);
        Assert.Contains("bogus_handler", bad.Error);
        Assert.DoesNotContain("bogus_handler", File.ReadAllText(s.Paths.CaddyConfigFile));
        Assert.Contains(s.Events.Events, e => e.Severity == EventSeverity.Error && e.Key == "config-apply" && e.AlertRule == "configFailure");
    }

    [CaddyFact]
    public async Task Caddyfile_mode_adapts_with_binary()
    {
        using var s = new ConfigServices(installBinary: true);
        var admin = Net.FreeTcpPort();
        s.Store.SaveSettings(new CaddySettings
        {
            Mode = ConfigMode.Caddyfile,
            AdminListen = $"127.0.0.1:{admin}",
            RawCaddyfile = $"{{\n admin 127.0.0.1:{admin}\n}}\n:18082 {{\n respond \"hi\"\n}}\n",
        });
        var adapted = await s.Config.AdaptCaddyfileAsync("http://a.test {\n respond hi\n}\n");
        Assert.NotNull(adapted);
        Assert.Contains("static_response", adapted!.Value.Json);

        var bad = await Assert.ThrowsAsync<CaddyAdminException>(() => s.Config.AdaptCaddyfileAsync("a.test {\n not_a_directive\n}\n"));
        Assert.Contains("not_a_directive", bad.Message);

        var r = await s.Config.ApplyAsync("caddyfile");
        Assert.True(r.Success, r.Error);
        Assert.Contains("18082", File.ReadAllText(s.Paths.CaddyConfigFile));
    }

    // ------------------------------------------------------------------ live

    [CaddyFact]
    public async Task Live_caddy_serves_generated_config_and_hot_reloads()
    {
        await using var upstream = await EchoUpstream.StartAsync();
        using var s = new ConfigServices(installBinary: true);
        var httpPort = Net.FreeTcpPort();
        var httpsPort = Net.FreeTcpPort();
        var adminPort = Net.FreeTcpPort();
        SeedEverything(s, upstream.Port, httpPort, httpsPort, adminPort, out var _, out var customCert);
        using var certScope = customCert;

        s.Config.EnsureBootConfig();
        using var caddy = new CaddyProcess(s.Paths);
        var admin = s.Provider.GetRequiredService<CaddyAdminClient>();
        await WaitUntil(() => admin.IsReachableAsync(), TimeSpan.FromSeconds(20), () => "Caddy did not start:\n" + caddy.Output);

        var apply = await s.Config.ApplyAsync("integration test");
        Assert.True(apply.Success, apply.Error + "\n" + caddy.Output);
        Assert.False(apply.WrittenOnly);

        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(15) };
        Task<HttpResponseMessage> Get(string host, string path = "/", string? user = null, string? pass = null, string? acceptEncoding = null)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{httpPort}{path}");
            req.Headers.Host = host;
            // Config reloads close idle keep-alive connections; use a fresh connection per request.
            req.Headers.ConnectionClose = true;
            if (user is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}")));
            if (acceptEncoding is not null) req.Headers.AcceptEncoding.ParseAdd(acceptEncoding);
            return http.SendAsync(req);
        }

        // reverse proxy + request/response headers + host override
        using (var r = await Get("proxy.test", "/hello?x=1"))
        {
            var body = await r.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Contains("path=/hello?x=1", body);
            Assert.Contains("x-test=from-caddy", body);
            Assert.Contains($"host=127.0.0.1:{upstream.Port}", body);
            Assert.Equal("cpm", r.Headers.GetValues("X-Served-By").Single());
            Assert.False(r.Headers.Contains("Server"));
        }
        // location with strip prefix
        using (var r = await Get("proxy.test", "/api/items"))
            Assert.Contains("path=/items", await r.Content.ReadAsStringAsync());
        // compression
        using (var r = await Get("proxy.test", "/big", acceptEncoding: "gzip"))
            Assert.Contains("gzip", r.Content.Headers.ContentEncoding);
        // block exploits
        using (var r = await Get("proxy.test", "/?id=1%20union%20select%20(1)"))
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        using (var r = await Get("proxy.test", "/.git/config"))
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);

        // redirect
        using (var r = await Get("redirect.test", "/foo?bar=1"))
        {
            Assert.Equal(HttpStatusCode.Found, r.StatusCode);
            Assert.Equal("https://example.com/foo?bar=1", r.Headers.Location!.ToString());
        }
        // custom response
        using (var r = await Get("respond.test"))
        {
            Assert.Equal((HttpStatusCode)418, r.StatusCode);
            Assert.Equal("short and stout", await r.Content.ReadAsStringAsync());
            Assert.Equal("text/plain", r.Content.Headers.ContentType!.MediaType);
        }
        // static + SPA fallback
        using (var r = await Get("static.test", "/hello.txt")) Assert.Equal("hello static", await r.Content.ReadAsStringAsync());
        using (var r = await Get("static.test", "/some/client/route")) Assert.Contains("spa index", await r.Content.ReadAsStringAsync());

        // basic auth (Authorization is stripped before the upstream)
        using (var r = await Get("auth.test")) Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        using (var r = await Get("auth.test", user: "bob", pass: "wrong")) Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        using (var r = await Get("auth.test", user: "bob", pass: "hunter2"))
        {
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Contains("auth=no", await r.Content.ReadAsStringAsync());
        }
        // IP deny
        using (var r = await Get("deny.test")) Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        // satisfy any: allowed IP skips auth; other IPs must authenticate
        using (var r = await Get("anylocal.test")) Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using (var r = await Get("anyremote.test")) Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        using (var r = await Get("anyremote.test", user: "bob", pass: "hunter2")) Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        // satisfy all: allowed IP still needs auth
        using (var r = await Get("both.test")) Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        using (var r = await Get("both.test", user: "bob", pass: "hunter2")) Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        // default site
        using (var r = await Get("unknown.test")) Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);

        // TLS host with ForceHttps → Caddy's automatic redirect on the HTTP port
        using (var r = await Get("internal.test", "/x"))
        {
            Assert.Equal(HttpStatusCode.PermanentRedirect, r.StatusCode);
            // Caddy omits the port when it equals the configured https_port (it is assumed to be the public port).
            Assert.Equal("https://internal.test/x", r.Headers.Location!.ToString());
        }
        // ForceHttps=false → served over plain HTTP too
        using (var r = await Get("internal-both.test"))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        // HTTPS: internal CA and custom certificate
        var (status, served) = await HttpsGet("internal.test", httpsPort);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("internal.test", served!.GetNameInfo(X509NameType.DnsName, false));
        var (cstatus, cserved) = await HttpsGet("custom.test", httpsPort);
        Assert.Equal(HttpStatusCode.OK, cstatus);
        Assert.Equal(customCert.Thumbprint, cserved!.Thumbprint);

        // Renewed files on disk + re-apply of an unchanged config → Caddy serves the new certificate
        // (the admin client forces the reload with Cache-Control: must-revalidate).
        using (var renewed = TestCerts.SelfSigned(["custom.test"]))
        {
            var certRow = s.Store.Col<Certificate>().FindById("cust1");
            File.WriteAllText(certRow.KeyPath, TestCerts.KeyPem(renewed));
            File.WriteAllText(certRow.CertPath, renewed.ExportCertificatePem());
            var reapply = await s.Config.ApplyAsync("certificate renewed");
            Assert.True(reapply.Success, reapply.Error);
            var (_, rserved) = await HttpsGet("custom.test", httpsPort);
            Assert.Equal(renewed.Thumbprint, rserved!.Thumbprint);
        }

        // access log file
        await WaitUntil(() => Task.FromResult(File.Exists(Path.Combine(s.Paths.AccessLogDir, "proxy.test.log"))), TimeSpan.FromSeconds(5), () => "no access log");

        // upstreams API
        var ups = await admin.GetUpstreamsAsync();
        Assert.Contains(ups, u => u.Address == $"127.0.0.1:{upstream.Port}");

        // running config round-trips
        var running = await admin.GetConfigAsync();
        Assert.Contains("proxy.test", running);

        // hot reload through the admin client
        var cfg = JsonNode.Parse(File.ReadAllText(s.Paths.CaddyConfigFile))!.AsObject();
        var respondRoute = cfg["apps"]!["http"]!["servers"]!["srv1"]!["routes"]!.AsArray()
            .First(x => x!["match"]?[0]?["host"]?[0]?.GetValue<string>() == "respond.test")!;
        respondRoute["handle"]![0]!["routes"]![0]!["handle"]![0]!["body"] = "reloaded";
        await admin.LoadAsync(CaddyJson.Serialize(cfg));
        using (var r = await Get("respond.test")) Assert.Equal("reloaded", await r.Content.ReadAsStringAsync());

        // service apply picks up model changes
        var host = s.Store.Col<SiteHost>().FindAll().Single(h => h.Domains[0] == "respond.test");
        host.ResponseBody = "from the model";
        s.Store.Col<SiteHost>().Update(host);
        var r2 = await s.Config.ApplyAsync("model change");
        Assert.True(r2.Success, r2.Error);
        using (var r = await Get("respond.test")) Assert.Equal("from the model", await r.Content.ReadAsStringAsync());

        // Caddy rejects an invalid config: error surfaced, old config keeps running, file untouched, event raised
        var before = File.ReadAllText(s.Paths.CaddyConfigFile);
        host.AdvancedRoutesJson = """[{"handle":[{"handler":"definitely_not_a_handler"}]}]""";
        s.Store.Col<SiteHost>().Update(host);
        var bad = await s.Config.ApplyAsync("bad change");
        Assert.False(bad.Success);
        Assert.Contains("definitely_not_a_handler", bad.Error);
        Assert.Equal(before, File.ReadAllText(s.Paths.CaddyConfigFile));
        using (var r = await Get("respond.test")) Assert.Equal("from the model", await r.Content.ReadAsStringAsync());
        Assert.Contains(s.Events.Events, e => e.Severity == EventSeverity.Error && e.Key == "config-apply");

        host.AdvancedRoutesJson = null;
        s.Store.Col<SiteHost>().Update(host);
        Assert.True((await s.Config.ApplyAsync("fixed")).Success);
        Assert.Contains(s.Events.Events, e => e.Severity == EventSeverity.Recovered && e.Key == "config-apply");

        var revisions = s.Store.Col<ConfigRevision>().FindAll().ToList();
        Assert.Equal(5, revisions.Count);
        Assert.Single(revisions, x => !x.Success);

        // stop via admin API
        await admin.StopAsync();
        await caddy.WaitForExitAsync(TimeSpan.FromSeconds(10));
        Assert.True(caddy.HasExited);
        Assert.False(await admin.IsReachableAsync());
    }

    private static async Task<(HttpStatusCode, X509Certificate2?)> HttpsGet(string host, int port)
    {
        X509Certificate2? served = null;
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectCallback = async (ctx, ct) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                await socket.ConnectAsync(IPAddress.Loopback, port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            },
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                {
                    if (cert is not null) served = X509CertificateLoader.LoadCertificate(cert.GetRawCertData());
                    return true;
                },
            },
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        Exception? last = null;
        // Caddy obtains internal certificates in the background right after the load.
        for (var i = 0; i < 40; i++)
        {
            try
            {
                using var resp = await client.GetAsync($"https://{host}:{port}/");
                return (resp.StatusCode, served);
            }
            catch (HttpRequestException ex)
            {
                last = ex;
                await Task.Delay(250);
            }
        }
        throw new Exception($"HTTPS request to {host} failed", last);
    }

    private static async Task WaitUntil(Func<Task<bool>> condition, TimeSpan timeout, Func<string> message)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (await condition()) return;
            await Task.Delay(200);
        }
        Assert.Fail(message());
    }
}
