using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace CaddyManager.Config.Tests;

/// <summary>Round 2 privilege boundaries of the Config endpoints.</summary>
public sealed class SecurityEndpointTests
{
    private static readonly JsonSerializerOptions Json = JsonDefaults.Api;

    private static async Task<JsonObject> Body(HttpResponseMessage r) => JsonNode.Parse(await r.Content.ReadAsStringAsync())!.AsObject();

    private static Task<HttpResponseMessage> Send(ApiHost api, string role, HttpMethod method, string url, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Test-Role", role);
        if (body is not null) req.Content = JsonContent.Create(body, options: Json);
        return api.Client.SendAsync(req);
    }

    private static object Proxy(string domain, string host = "10.0.0.10", int port = 8080, string? advanced = null) => new
    {
        kind = "proxy",
        domains = new[] { domain },
        tls = "none",
        upstreams = new[] { new { scheme = "http", host, port } },
        advancedRoutesJson = advanced,
    };

    private static object Static(string domain, string root) => new { kind = "static", domains = new[] { domain }, tls = "none", rootPath = root };

    private const string AdvancedRoute = """[{"match":[{"path":["/robots.txt"]}],"handle":[{"handler":"static_response","body":"no"}]}]""";

    private static int AdminPort(ApiHost api) => int.Parse(api.Store.GetSettings<CaddySettings>().AdminListen.Split(':')[1]);

    // ------------------------------------------------------------------ advanced routes

    [Fact]
    public async Task Advanced_routes_are_administrator_only()
    {
        await using var api = ApiHost.Start();
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(api, "operator", HttpMethod.Post, "/api/hosts", Proxy("op.example.com", advanced: AdvancedRoute))).StatusCode);
        Assert.Empty(api.Store.Col<SiteHost>().FindAll());

        var created = await Send(api, "admin", HttpMethod.Post, "/api/hosts", Proxy("adv.example.com", advanced: AdvancedRoute));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var id = (await Body(created))["item"]!["id"]!.GetValue<string>();

        // Operators may edit everything else and send the routes back unchanged (formatting does not matter)...
        var reformatted = JsonNode.Parse(AdvancedRoute)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var same = await Send(api, "operator", HttpMethod.Put, $"/api/hosts/{id}", Proxy("adv.example.com", port: 9000, advanced: reformatted));
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        Assert.Equal(9000, api.Store.Col<SiteHost>().FindById(id).Upstreams[0].Port);
        // ...but not change or remove them.
        var changed = await Send(api, "operator", HttpMethod.Put, $"/api/hosts/{id}", Proxy("adv.example.com", advanced: "[]"));
        Assert.Equal(HttpStatusCode.Forbidden, changed.StatusCode);
        Assert.Contains("administrators", (await Body(changed))["detail"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(api, "operator", HttpMethod.Put, $"/api/hosts/{id}", Proxy("adv.example.com"))).StatusCode);
        Assert.NotNull(api.Store.Col<SiteHost>().FindById(id).AdvancedRoutesJson);
    }

    [Fact]
    public async Task Secrets_in_advanced_routes_are_masked_for_non_admins_and_survive_round_trips()
    {
        await using var api = ApiHost.Start();
        const string auth = """[{"handle":[{"handler":"authentication","providers":{"http_basic":{"accounts":[{"username":"bob","password":"$2a$14$SECRETHASH"}]}}}]}]""";
        var created = await Body(await Send(api, "admin", HttpMethod.Post, "/api/hosts", Proxy("auth.example.com", advanced: auth)));
        var id = created["item"]!["id"]!.GetValue<string>();
        Assert.Contains("SECRETHASH", created["item"]!["advancedRoutesJson"]!.GetValue<string>());

        var viewer = await Body(await Send(api, "viewer", HttpMethod.Get, $"/api/hosts/{id}"));
        var masked = viewer["advancedRoutesJson"]!.GetValue<string>();
        Assert.DoesNotContain("SECRETHASH", masked);
        Assert.Contains("***", masked);
        var list = await (await Send(api, "operator", HttpMethod.Get, "/api/hosts")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("SECRETHASH", list);

        // An operator saving the host with the masked routes keeps the stored routes.
        var put = await Send(api, "operator", HttpMethod.Put, $"/api/hosts/{id}", Proxy("auth.example.com", port: 8181, advanced: masked));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.DoesNotContain("SECRETHASH", await put.Content.ReadAsStringAsync());
        Assert.Contains("SECRETHASH", api.Store.Col<SiteHost>().FindById(id).AdvancedRoutesJson);
    }

    // ------------------------------------------------------------------ static roots

    [Fact]
    public async Task Static_roots_cannot_expose_protected_folders()
    {
        await using var api = ApiHost.Start();
        async Task<HttpResponseMessage> Post(string role, string root, string domain = "static.example.com") =>
            await Send(api, role, HttpMethod.Post, "/api/hosts", Static(domain, root));

        foreach (var root in new[] { api.Env.Paths.DataDir, Path.Combine(api.Env.Paths.DataDir, "db"), @"C:\Windows\System32", @"C:\", @"C:\Program Files\App", @"C:\www\..\Windows" })
        {
            var r = await Post("admin", root);
            Assert.True(r.StatusCode == HttpStatusCode.BadRequest, root + ": " + r.StatusCode);
            Assert.NotNull((await Body(r))["errors"]!["rootPath"]);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await Post("operator", @"\\fileserver\web\site")).StatusCode);
        var unc = await Post("admin", @"\\fileserver\web\site", "unc.example.com");
        Assert.Equal(HttpStatusCode.OK, unc.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post("operator", @"D:\www\site", "ok.example.com")).StatusCode);

        // Operators may edit an administrator's UNC-rooted host as long as the root stays the same.
        var uncId = (await Body(unc))["item"]!["id"]!.GetValue<string>();
        var edit = new { kind = "static", domains = new[] { "unc.example.com" }, tls = "none", rootPath = @"\\fileserver\web\site", browse = true };
        Assert.Equal(HttpStatusCode.OK, (await Send(api, "operator", HttpMethod.Put, $"/api/hosts/{uncId}", edit)).StatusCode);
        var moved = new { kind = "static", domains = new[] { "unc.example.com" }, tls = "none", rootPath = @"\\fileserver\web\other" };
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(api, "operator", HttpMethod.Put, $"/api/hosts/{uncId}", moved)).StatusCode);

        // A disabled legacy host whose root is inside the data folder cannot be enabled.
        var legacy = new SiteHost { Kind = HostKind.Static, Enabled = false, Domains = ["legacy.example.com"], Tls = TlsMode.None, RootPath = Path.Combine(api.Env.Paths.DataDir, "www") };
        api.Store.Col<SiteHost>().Insert(legacy);
        var enable = await Send(api, "admin", HttpMethod.Post, $"/api/hosts/{legacy.Id}/enable");
        Assert.Equal(HttpStatusCode.BadRequest, enable.StatusCode);
        Assert.Contains("data folder", (await Body(enable))["detail"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------ protected endpoints

    [Fact]
    public async Task Upstreams_and_streams_cannot_target_the_admin_api_or_the_ui()
    {
        await using var api = ApiHost.Start();
        var adminPort = AdminPort(api);

        async Task<JsonObject> Bad(string role, string url, object body)
        {
            var r = await Send(api, role, HttpMethod.Post, url, body);
            Assert.True(r.StatusCode == HttpStatusCode.BadRequest, url + ": " + r.StatusCode + " " + await r.Content.ReadAsStringAsync());
            return (await Body(r))["errors"]!.AsObject();
        }

        Assert.Contains("admin API", (await Bad("operator", "/api/hosts", Proxy("a.example.com", "127.0.0.1", adminPort)))["upstreams[0].host"]![0]!.GetValue<string>());
        Assert.Contains("web UI", (await Bad("operator", "/api/hosts", Proxy("a.example.com", "localhost", 81)))["upstreams[0].host"]![0]!.GetValue<string>());
        // Administrators may publish the manager UI through Caddy (it has its own authentication).
        Assert.Equal(HttpStatusCode.OK, (await Send(api, "admin", HttpMethod.Post, "/api/hosts", Proxy("manager.example.com", "127.0.0.1", 81))).StatusCode);
        Assert.Contains("admin API", (await Bad("admin", "/api/hosts", Proxy("b.example.com", "127.0.0.1", adminPort)))["upstreams[0].host"]![0]!.GetValue<string>());
        var loc = new
        {
            kind = "proxy", domains = new[] { "loc.example.com" }, tls = "none",
            upstreams = new[] { new { host = "10.0.0.1", port = 80 } },
            locations = new[] { new { path = "/admin", upstreams = new[] { new { host = "::1", port = adminPort } } } },
        };
        Assert.NotNull((await Bad("operator", "/api/hosts", loc))["locations[0].upstreams[0].host"]);
        var adv = $$"""[{"handle":[{"handler":"reverse_proxy","upstreams":[{"dial":"127.0.0.1:{{adminPort}}"}]}]}]""";
        Assert.NotNull((await Bad("admin", "/api/hosts", Proxy("adv.example.com", advanced: adv)))["advancedRoutesJson"]);
        Assert.Equal(HttpStatusCode.OK, (await Send(api, "operator", HttpMethod.Post, "/api/hosts", Proxy("remote.example.com", "10.0.0.1", 81))).StatusCode);

        // streams: target (400) and listen port (409)
        Assert.NotNull((await Bad("operator", "/api/streams", new { protocol = "tcp", listenPort = 4000, upstreamHost = "localhost", upstreamPort = 81 }))["upstreamHost"]);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(api, "operator", HttpMethod.Post, "/api/streams", new { protocol = "tcp", listenPort = 81, upstreamHost = "10.0.0.2", upstreamPort = 22 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(api, "operator", HttpMethod.Post, "/api/streams", new { protocol = "tcp", listenPort = adminPort, upstreamHost = "10.0.0.2", upstreamPort = 22 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(api, "operator", HttpMethod.Post, "/api/streams", new { protocol = "udp", listenPort = 81, upstreamHost = "10.0.0.2", upstreamPort = 53 })).StatusCode);

        // The UI's HTTPS port only counts while UI HTTPS is enabled.
        Assert.Equal(HttpStatusCode.OK, (await Send(api, "operator", HttpMethod.Post, "/api/hosts", Proxy("h1.example.com", "127.0.0.1", 9443))).StatusCode);
        api.Store.SaveSettings(new UiSettings { HttpsEnabled = true, HttpsPort = 9444 });
        Assert.NotNull((await Bad("operator", "/api/hosts", Proxy("h2.example.com", "127.0.0.1", 9444)))["upstreams[0].host"]);
    }

    [Fact]
    public async Task Enabling_or_re_pointing_the_admin_api_cannot_create_a_path_to_it()
    {
        await using var api = ApiHost.Start();
        // A disabled host from before the guard existed (e.g. restored from a backup) that targets the admin API.
        var legacy = Build.Proxy("legacy.example.com", AdminPort(api), TlsMode.None, "localhost");
        legacy.Enabled = false;
        api.Store.Col<SiteHost>().Insert(legacy);
        var enable = await Send(api, "operator", HttpMethod.Post, $"/api/hosts/{legacy.Id}/enable");
        Assert.Equal(HttpStatusCode.BadRequest, enable.StatusCode);
        Assert.Contains("admin API", (await Body(enable))["detail"]!.GetValue<string>());

        // An administrator-created host publishing the manager UI can be re-enabled by operators.
        var ui = Build.Proxy("manager.example.com", 81, TlsMode.None, "127.0.0.1");
        ui.Enabled = false;
        api.Store.Col<SiteHost>().Insert(ui);
        Assert.Equal(HttpStatusCode.OK, (await Send(api, "operator", HttpMethod.Post, $"/api/hosts/{ui.Id}/enable")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Send(api, "admin", HttpMethod.Post, "/api/hosts", Proxy("app.example.com", "127.0.0.1", 8123))).StatusCode);
        var move = await Send(api, "admin", HttpMethod.Put, "/api/settings/caddy", new { adminListen = "127.0.0.1:8123" });
        Assert.Equal(HttpStatusCode.BadRequest, move.StatusCode);
        Assert.Contains("app.example.com", (await Body(move))["detail"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await Send(api, "admin", HttpMethod.Put, "/api/settings/caddy", new { adminListen = "127.0.0.1:8124" })).StatusCode);
    }

    // ------------------------------------------------------------------ certificates

    [Fact]
    public async Task Path_based_certificate_sources_are_administrator_only()
    {
        await using var api = ApiHost.Start(configure: s => s.AddSingleton<CaddyManager.Config.Certificates.IWindowsCertificateSource>(new FakeWindowsStore()));
        var path = new { name = "x", certPath = @"C:\certs\a.pem", keyPath = @"C:\certs\a.key" };
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(api, "operator", HttpMethod.Post, "/api/certificates/path", path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(api, "operator", HttpMethod.Post, "/api/certificates/pfx-path", new { pfxPath = @"C:\certs\a.pfx" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(api, "operator", HttpMethod.Get, "/api/certificates/windows-store")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(api, "operator", HttpMethod.Post, "/api/certificates/windows-store", new { subject = "a" })).StatusCode);

        using var cert = TestCerts.SelfSigned(["up.example.com"]);
        var up = await Send(api, "operator", HttpMethod.Post, "/api/certificates/pem", new { name = "Up", certPem = cert.ExportCertificatePem(), keyPem = TestCerts.KeyPem(cert) });
        Assert.Equal(HttpStatusCode.OK, up.StatusCode);
        var id = (await Body(up))["item"]!["id"]!.GetValue<string>();
        var repoint = await Send(api, "operator", HttpMethod.Post, $"/api/certificates/{id}/replace", path);
        Assert.Equal(HttpStatusCode.Forbidden, repoint.StatusCode);
    }

    [Fact]
    public async Task Certificate_paths_are_restricted_and_errors_are_generic()
    {
        await using var api = ApiHost.Start();
        var outside = Path.Combine(Path.GetTempPath(), "cpm-certpath-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outside);
        using var cleanup = new DirCleanup(outside);
        File.WriteAllText(Path.Combine(outside, "notes.txt"), "x");

        async Task<JsonObject> Post(string url, object body, HttpStatusCode expected = HttpStatusCode.BadRequest)
        {
            var r = await Send(api, "admin", HttpMethod.Post, url, body);
            Assert.Equal(expected, r.StatusCode);
            return await Body(r);
        }

        var ext = await Post("/api/certificates/path", new { certPath = Path.Combine(outside, "notes.txt"), keyPath = Path.Combine(outside, "k.pem") });
        Assert.Contains("extensions", ext["errors"]!["certPath"]![0]!.GetValue<string>());
        var db = await Post("/api/certificates/path", new { certPath = Path.Combine(api.Env.Paths.DataDir, "db", "x.pem"), keyPath = Path.Combine(api.Env.Paths.DataDir, "db", "secret.key") });
        Assert.Contains("data folder", db["errors"]!["keyPath"]![0]!.GetValue<string>());
        var missing = await Post("/api/certificates/path", new { certPath = Path.Combine(outside, "missing.pem"), keyPath = Path.Combine(outside, "k.pem") });
        Assert.Contains("cannot be read", missing["detail"]!.GetValue<string>());
        var folder = await Post("/api/certificates/pfx-path", new { pfxPath = Path.Combine(outside, "dir.pfx") });
        Directory.CreateDirectory(Path.Combine(outside, "dir2.pfx"));
        var isDir = await Post("/api/certificates/pfx-path", new { pfxPath = Path.Combine(outside, "dir2.pfx") });
        Assert.Equal(folder["detail"]!.GetValue<string>().Replace("dir.pfx", "X"), isDir["detail"]!.GetValue<string>().Replace("dir2.pfx", "X"));
    }

    // ------------------------------------------------------------------ redaction

    [Fact]
    public async Task Viewers_get_redacted_configs_and_settings()
    {
        await using var api = ApiHost.Start();
        var put = await Send(api, "admin", HttpMethod.Put, "/api/settings/caddy", new
        {
            acmeCa = "letsEncrypt",
            eabKeyId = "kid",
            eabMacKey = "SECRETMAC",
            acmeIssuerJson = """{"challenges":{"dns":{"provider":{"name":"cloudflare","api_token":"CFTOKEN"}}}}""",
            serverOptionsJson = """{"max_header_bytes":65536}""",
            extraAppsJson = """{"events":{}}""",
            rawCaddyfile = "secret.example.com { respond hi }",
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var putBody = await put.Content.ReadAsStringAsync();
        Assert.DoesNotContain("CFTOKEN", putBody);
        Assert.True(JsonNode.Parse(putBody)!["item"]!["hasAcmeIssuerJson"]!.GetValue<bool>());

        var list = await Body(await Send(api, "admin", HttpMethod.Post, "/api/access-lists", new { name = "Staff", users = new[] { new { username = "bob", password = "hunter2hunter2" } } }));
        var listId = list["item"]!["id"]!.GetValue<string>();
        var host = new { kind = "proxy", domains = new[] { "app.example.com" }, tls = "acme", accessListId = listId, upstreams = new[] { new { host = "10.0.0.1", port = 80 } } };
        Assert.Equal(HttpStatusCode.OK, (await Send(api, "admin", HttpMethod.Post, "/api/hosts", host)).StatusCode);
        var hash = api.Store.Col<AccessList>().FindById(listId).Users[0].PasswordHash;

        var viewerPreview = (await Body(await Send(api, "viewer", HttpMethod.Get, "/api/config/preview")))["json"]!.GetValue<string>();
        var adminPreview = (await Body(await Send(api, "admin", HttpMethod.Get, "/api/config/preview")))["json"]!.GetValue<string>();
        foreach (var secret in new[] { "SECRETMAC", "CFTOKEN", hash })
        {
            Assert.DoesNotContain(secret, viewerPreview);
            Assert.Contains(secret, adminPreview);
        }
        Assert.Contains("cloudflare", viewerPreview);

        var revs = await (await Send(api, "viewer", HttpMethod.Get, "/api/config/revisions?take=1")).Content.ReadFromJsonAsync<JsonArray>(Json);
        var revId = revs![0]!["id"]!.GetValue<string>();
        var viewerRev = (await Body(await Send(api, "viewer", HttpMethod.Get, $"/api/config/revisions/{revId}")))["json"]!.GetValue<string>();
        var adminRev = (await Body(await Send(api, "admin", HttpMethod.Get, $"/api/config/revisions/{revId}")))["json"]!.GetValue<string>();
        foreach (var secret in new[] { "SECRETMAC", "CFTOKEN", hash })
        {
            Assert.DoesNotContain(secret, viewerRev);
            Assert.Contains(secret, adminRev);
        }

        var viewerSettings = await Body(await Send(api, "viewer", HttpMethod.Get, "/api/settings/caddy"));
        foreach (var hidden in new[] { "rawCaddyfile", "serverOptionsJson", "extraAppsJson" })
            Assert.False(viewerSettings.ContainsKey(hidden), hidden);
        Assert.True(viewerSettings["hasAcmeIssuerJson"]!.GetValue<bool>());
        Assert.False(viewerSettings.ContainsKey("acmeIssuerJsonProtected"));
        var adminSettings = await Body(await Send(api, "admin", HttpMethod.Get, "/api/settings/caddy"));
        Assert.Equal("""{"events":{}}""", adminSettings["extraAppsJson"]!.GetValue<string>());
        Assert.Contains("secret.example.com", adminSettings["rawCaddyfile"]!.GetValue<string>());
        Assert.DoesNotContain("CFTOKEN", adminSettings.ToJsonString());
    }

    // ------------------------------------------------------------------ plugin settings validation

    [Fact]
    public async Task Plugin_settings_are_validated_and_the_issuer_json_is_a_write_only_secret()
    {
        await using var api = ApiHost.Start();
        async Task<JsonObject> Bad(object body)
        {
            var r = await Send(api, "admin", HttpMethod.Put, "/api/settings/caddy", body);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            return await Body(r);
        }
        Assert.NotNull((await Bad(new { extraAppsJson = """{"http":{}}""" }))["errors"]!["extraAppsJson"]);
        Assert.NotNull((await Bad(new { extraAppsJson = "[1,2]" }))["errors"]!["extraAppsJson"]);
        Assert.NotNull((await Bad(new { extraAppsJson = """{"dynamic_dns":5}""" }))["errors"]!["extraAppsJson"]);
        Assert.NotNull((await Bad(new { tlsConnectionPolicyJson = """{"match":{"sni":["a"]}}""" }))["errors"]!["tlsConnectionPolicyJson"]);
        Assert.NotNull((await Bad(new { tlsConnectionPolicyJson = "nope" }))["errors"]!["tlsConnectionPolicyJson"]);
        Assert.NotNull((await Bad(new { acmeIssuerJson = "{broken" }))["errors"]!["acmeIssuerJson"]);
        Assert.NotNull((await Bad(new { acmeIssuerJson = """{"module":"internal"}""" }))["errors"]!["acmeIssuerJson"]);
        Assert.NotNull((await Bad(new { disableHttpChallenge = true, disableTlsAlpnChallenge = true }))["errors"]!["disableTlsAlpnChallenge"]);
        Assert.NotNull((await Bad(new { httpPort = 81 }))["errors"]!["httpPort"]); // the UI port

        // object form, and with a DNS challenge both other challenges may be disabled
        var ok = await Send(api, "admin", HttpMethod.Put, "/api/settings/caddy", new
        {
            acmeIssuerJson = new { challenges = new { dns = new { provider = new { name = "rfc2136", key = "k" } } } },
            disableHttpChallenge = true,
            disableTlsAlpnChallenge = true,
            tlsConnectionPolicyJson = """{"protocol_min":"tls1.3"}""",
        });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var stored = api.Store.GetSettings<CaddySettings>();
        Assert.Contains("rfc2136", api.App.Services.GetRequiredService<ISecretProtector>().Unprotect(stored.AcmeIssuerJsonProtected!));
        Assert.DoesNotContain("rfc2136", stored.AcmeIssuerJsonProtected);
        // absent = unchanged (and still satisfies the challenge rule), "" = clear
        Assert.Equal(HttpStatusCode.OK, (await Send(api, "admin", HttpMethod.Put, "/api/settings/caddy", new { logLevel = "warn" })).StatusCode);
        Assert.Equal(stored.AcmeIssuerJsonProtected, api.Store.GetSettings<CaddySettings>().AcmeIssuerJsonProtected);
        Assert.Equal(HttpStatusCode.OK, (await Send(api, "admin", HttpMethod.Put, "/api/settings/caddy", new { acmeIssuerJson = "", disableHttpChallenge = false })).StatusCode);
        Assert.Null(api.Store.GetSettings<CaddySettings>().AcmeIssuerJsonProtected);
        Assert.Contains(api.Audit.Entries, e => e.ObjectType == "settings");
    }
}
