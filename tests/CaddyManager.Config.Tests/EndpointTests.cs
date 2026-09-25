using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaddyManager.Config.Tests;

internal sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var role = Request.Headers["X-Test-Role"].FirstOrDefault() ?? "admin";
        if (role == "anonymous") return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "tester"), new Claim(ClaimTypes.Role, role)], "Test");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
    }
}

/// <summary>Config endpoints hosted in-memory against a real LiteStore and the real config service.</summary>
public sealed class ApiHost : IAsyncDisposable
{
    public TempEnv Env { get; } = new();
    public WebApplication App { get; }
    public HttpClient Client { get; }
    public RecordingAuditLog Audit { get; } = new();
    public RecordingEventSink Events { get; } = new();

    private ApiHost(bool installBinary, Action<IServiceCollection>? configure)
    {
        if (installBinary) ConfigServices.InstallBinary(Env.Paths);
        // Keep the admin API pointed at a port where nothing listens.
        Env.Store.SaveSettings(new CaddySettings { AdminListen = $"127.0.0.1:{Net.FreeTcpPort()}", HttpPort = 18090, HttpsPort = 18453 });

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddCore(Env.Paths, Env.Store).AddConfigModule();
        builder.Services.ConfigureHttpJsonOptions(o => JsonDefaults.Configure(o.SerializerOptions));
        builder.Services.AddProblemDetails();
        builder.Services.AddSingleton<IAuditLog>(Audit);
        builder.Services.AddSingleton<IEventSink>(Events);
        configure?.Invoke(builder.Services);
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", null);
        builder.Services.AddAuthorization(o =>
        {
            o.AddPolicy(Policies.Viewer, p => p.RequireAuthenticatedUser());
            o.AddPolicy(Policies.Operator, p => p.RequireRole("operator", "admin"));
            o.AddPolicy(Policies.Admin, p => p.RequireRole("admin"));
        });
        App = builder.Build();
        App.UseAuthentication();
        App.UseAuthorization();
        App.MapConfigEndpoints();
        App.StartAsync().GetAwaiter().GetResult();
        Client = App.GetTestClient();
    }

    public static ApiHost Start(bool installBinary = false, Action<IServiceCollection>? configure = null) => new(installBinary, configure);

    public IStore Store => Env.Store;

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
        Env.Dispose();
    }
}

internal sealed class DirCleanup(string dir) : IDisposable
{
    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public sealed class EndpointTests
{
    private static readonly JsonSerializerOptions Json = JsonDefaults.Api;

    private static async Task<JsonObject> Body(HttpResponseMessage r) =>
        JsonNode.Parse(await r.Content.ReadAsStringAsync())!.AsObject();

    private static object ProxyHost(string domain, int port = 8080, bool enabled = true) => new
    {
        kind = "proxy",
        enabled,
        domains = new[] { domain },
        tls = "none",
        upstreams = new[] { new { scheme = "http", host = "127.0.0.1", port } },
    };

    private static HttpRequestMessage As(string role, HttpMethod method, string url, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Test-Role", role);
        if (body is not null) req.Content = JsonContent.Create(body, options: Json);
        return req;
    }

    // ------------------------------------------------------------------ hosts

    [Fact]
    public async Task Host_crud_is_applied_written_only_and_audited()
    {
        await using var api = ApiHost.Start();
        var c = api.Client;

        var created = await c.PostAsJsonAsync("/api/hosts", ProxyHost("App.Example.com"), Json);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var body = await Body(created);
        var id = body["item"]!["id"]!.GetValue<string>();
        Assert.Equal("app.example.com", body["item"]!["domains"]![0]!.GetValue<string>());
        Assert.Equal("proxy", body["item"]!["kind"]!.GetValue<string>());
        Assert.True(body["apply"]!["success"]!.GetValue<bool>());
        Assert.True(body["apply"]!["writtenOnly"]!.GetValue<bool>());
        Assert.Contains("app.example.com", File.ReadAllText(api.Env.Paths.CaddyConfigFile));

        var list = await c.GetFromJsonAsync<JsonArray>("/api/hosts?kind=proxy", Json);
        Assert.Single(list!);
        Assert.Empty((await c.GetFromJsonAsync<JsonArray>("/api/hosts?kind=redirect", Json))!);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/hosts?kind=nope")).StatusCode);

        var put = await c.PutAsJsonAsync($"/api/hosts/{id}", ProxyHost("app.example.com", 9090), Json);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(9090, (await Body(put))["item"]!["upstreams"]![0]!["port"]!.GetValue<int>());

        var disabled = await c.PostAsync($"/api/hosts/{id}/disable", null);
        Assert.False((await Body(disabled))["item"]!["enabled"]!.GetValue<bool>());
        Assert.DoesNotContain("app.example.com", File.ReadAllText(api.Env.Paths.CaddyConfigFile));
        var enabled = await c.PostAsync($"/api/hosts/{id}/enable", null);
        Assert.True((await Body(enabled))["item"]!["enabled"]!.GetValue<bool>());

        var del = await c.DeleteAsync($"/api/hosts/{id}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        Assert.True((await Body(del))["apply"]!["success"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/hosts/{id}")).StatusCode);

        Assert.Equal(["created", "updated", "disabled", "enabled", "deleted"], api.Audit.Entries.Select(e => e.Action).ToArray());
        Assert.All(api.Audit.Entries, e => Assert.Equal("host", e.ObjectType));
    }

    [Fact]
    public async Task Host_validation_errors()
    {
        await using var api = ApiHost.Start();
        var c = api.Client;

        async Task<JsonObject> Bad(object host)
        {
            var r = await c.PostAsJsonAsync("/api/hosts", host, Json);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            return (await Body(r))["errors"]!.AsObject();
        }

        Assert.NotNull((await Bad(new { kind = "proxy", domains = Array.Empty<string>(), upstreams = new[] { new { host = "a", port = 1 } } }))["domains"]);
        Assert.NotNull((await Bad(new { kind = "proxy", domains = new[] { "bad_domain!.com" }, upstreams = new[] { new { host = "a", port = 1 } } }))["domains"]);
        var up = await Bad(new { kind = "proxy", domains = new[] { "a.example.com" }, upstreams = new[] { new { host = "http://x", port = 0 } } });
        Assert.NotNull(up["upstreams[0].host"]);
        Assert.NotNull(up["upstreams[0].port"]);
        Assert.NotNull((await Bad(new { kind = "proxy", domains = new[] { "a.example.com" }, upstreams = Array.Empty<object>() }))["upstreams"]);
        var red = await Bad(new { kind = "redirect", domains = new[] { "a.example.com" }, redirectTarget = "ftp://x", redirectCode = 305 });
        Assert.NotNull(red["redirectTarget"]);
        Assert.NotNull(red["redirectCode"]);
        Assert.NotNull((await Bad(new { kind = "static", domains = new[] { "a.example.com" } }))["rootPath"]);
        Assert.NotNull((await Bad(new { kind = "response", domains = new[] { "a.example.com" }, tls = "custom", certificateId = "nope" }))["certificateId"]);
        Assert.NotNull((await Bad(new { kind = "response", domains = new[] { "a.example.com" }, advancedRoutesJson = "{\"a\":1}" }))["advancedRoutesJson"]);
        Assert.NotNull((await Bad(new { kind = "response", domains = new[] { "a.example.com" }, accessListId = "missing" }))["accessListId"]);
        Assert.Empty(api.Store.Col<SiteHost>().FindAll());
    }

    [Fact]
    public async Task Wildcard_domains_are_accepted()
    {
        await using var api = ApiHost.Start();
        var r = await api.Client.PostAsJsonAsync("/api/hosts", ProxyHost("*.apps.example.com"), Json);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Duplicate_domain_conflicts_only_between_enabled_hosts()
    {
        await using var api = ApiHost.Start();
        var c = api.Client;
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/hosts", ProxyHost("dup.example.com"), Json)).StatusCode);

        var conflict = await c.PostAsJsonAsync("/api/hosts", ProxyHost("DUP.example.com"), Json);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Contains("dup.example.com", (await Body(conflict))["detail"]!.GetValue<string>());

        var disabled = await c.PostAsJsonAsync("/api/hosts", ProxyHost("dup.example.com", enabled: false), Json);
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        var id = (await Body(disabled))["item"]!["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync($"/api/hosts/{id}/enable", null)).StatusCode);
    }

    [Fact]
    public async Task Roles_are_enforced()
    {
        await using var api = ApiHost.Start();
        var c = api.Client;
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(As("viewer", HttpMethod.Get, "/api/hosts"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(As("viewer", HttpMethod.Post, "/api/hosts", ProxyHost("a.example.com")))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(As("operator", HttpMethod.Post, "/api/hosts", ProxyHost("a.example.com")))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(As("operator", HttpMethod.Put, "/api/settings/caddy", new { logLevel = "debug" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(As("anonymous", HttpMethod.Get, "/api/hosts"))).StatusCode);
    }

    [CaddyFact]
    public async Task Caddy_rejection_rolls_back_and_returns_422()
    {
        await using var api = ApiHost.Start(installBinary: true);
        var c = api.Client;
        var ok = await c.PostAsJsonAsync("/api/hosts", ProxyHost("keep.example.com"), Json);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var okBody = await Body(ok);
        Assert.True(okBody["apply"]!["writtenOnly"]!.GetValue<bool>());
        var id = okBody["item"]!["id"]!.GetValue<string>();

        var badHost = new
        {
            kind = "proxy", domains = new[] { "bad.example.com" }, tls = "none",
            upstreams = new[] { new { host = "127.0.0.1", port = 80 } },
            advancedRoutesJson = """[{"handle":[{"handler":"not_a_real_handler"}]}]""",
        };
        var rejected = await c.PostAsJsonAsync("/api/hosts", badHost, Json);
        Assert.True(rejected.StatusCode == HttpStatusCode.UnprocessableEntity, await rejected.Content.ReadAsStringAsync());
        Assert.Contains("not_a_real_handler", (await Body(rejected))["detail"]!.GetValue<string>());
        Assert.Single(api.Store.Col<SiteHost>().FindAll());

        // PUT rejected → previous version restored
        var badPut = await c.PutAsJsonAsync($"/api/hosts/{id}", new
        {
            kind = "proxy", domains = new[] { "keep.example.com" }, tls = "none",
            upstreams = new[] { new { host = "127.0.0.1", port = 8081 } }, // not 81: that is the manager UI port (refused)
            advancedRoutesJson = """[{"handle":[{"handler":"not_a_real_handler"}]}]""",
        }, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badPut.StatusCode);
        var stored = api.Store.Col<SiteHost>().FindById(id);
        Assert.Equal(8080, stored.Upstreams[0].Port);
        Assert.Null(stored.AdvancedRoutesJson);
        Assert.DoesNotContain("not_a_real_handler", File.ReadAllText(api.Env.Paths.CaddyConfigFile));
        Assert.Contains(api.Events.Events, e => e.AlertRule == "configFailure");
        Assert.DoesNotContain(api.Audit.Entries, e => e.Action == "updated");
    }

    // ------------------------------------------------------------------ streams

    [Fact]
    public async Task Streams_crud_support_and_conflicts()
    {
        await using var api = ApiHost.Start();
        var c = api.Client;
        var support = await Body(await c.GetAsync("/api/streams/support"));
        Assert.False(support["supported"]!.GetValue<bool>());
        Assert.Equal("github.com/mholt/caddy-l4", support["plugin"]!.GetValue<string>());

        var r = await c.PostAsJsonAsync("/api/streams", new { protocol = "tcp", listenPort = 3389, upstreamHost = "10.0.0.5", upstreamPort = 3389 }, Json);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await Body(r);
        Assert.Contains(body["apply"]!["warnings"]!.AsArray(), w => w!.GetValue<string>().Contains("layer4"));
        var id = body["item"]!["id"]!.GetValue<string>();

        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/streams", new { protocol = "tcp", listenPort = 3389, upstreamHost = "10.0.0.6", upstreamPort = 1 }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/streams", new { protocol = "udp", listenPort = 3389, upstreamHost = "10.0.0.6", upstreamPort = 1 }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/streams", new { protocol = "tcp", listenPort = 18090, upstreamHost = "10.0.0.6", upstreamPort = 1 }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/streams", new { protocol = "tcp", listenPort = 0, upstreamHost = "", upstreamPort = 1 }, Json)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync($"/api/streams/{id}", new { protocol = "tcp", listenPort = 3390, upstreamHost = "10.0.0.5", upstreamPort = 3389 }, Json)).StatusCode);
        Assert.Equal(2, (await c.GetFromJsonAsync<JsonArray>("/api/streams", Json))!.Count);

        // The standing "streams skipped" warning belongs to stream changes, the preview and manual applies — not to every host save.
        var host = await Body(await c.PostAsJsonAsync("/api/hosts", ProxyHost("nostreamwarning.example.com"), Json));
        Assert.DoesNotContain(host["apply"]!["warnings"]!.AsArray(), w => w!.GetValue<string>().Contains("layer4"));
        var manual = await Body(await c.PostAsync("/api/config/apply", null));
        Assert.Contains(manual["warnings"]!.AsArray(), w => w!.GetValue<string>().Contains("layer4"));
        var preview = await c.GetFromJsonAsync<JsonObject>("/api/config/preview", Json);
        Assert.Contains(preview!["warnings"]!.AsArray(), w => w!.GetValue<string>().Contains("layer4"));

        var deleted = await c.DeleteAsync($"/api/streams/{id}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Contains((await Body(deleted))["apply"]!["warnings"]!.AsArray(), w => w!.GetValue<string>().Contains("layer4")); // one stream left
    }

    // ------------------------------------------------------------------ access lists

    [Fact]
    public async Task Access_list_passwords_are_write_only()
    {
        await using var api = ApiHost.Start();
        var c = api.Client;
        var r = await c.PostAsJsonAsync("/api/access-lists", new
        {
            name = "Staff",
            satisfyAny = true,
            rules = new[] { new { action = "allow", cidr = "10.0.0.0/8" }, new { action = "deny", cidr = "all" } },
            users = new[] { new { username = "alice", password = "correct horse" } },
        }, Json);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var raw = await r.Content.ReadAsStringAsync();
        Assert.DoesNotContain("correct horse", raw);
        Assert.DoesNotContain("passwordHash", raw);
        Assert.DoesNotContain("$2", raw);
        var item = JsonNode.Parse(raw)!["item"]!;
        Assert.Equal("alice", item["users"]![0]!["username"]!.GetValue<string>());
        Assert.True(item["users"]![0]!["hasPassword"]!.GetValue<bool>());
        Assert.Equal(0, item["usedBy"]!.GetValue<int>());
        var id = item["id"]!.GetValue<string>();
        var hash = api.Store.Col<AccessList>().FindById(id).Users[0].PasswordHash;
        Assert.True(Passwords.Verify("correct horse", hash));

        // PUT without password keeps the hash; a new user needs one
        var bad = await c.PutAsJsonAsync($"/api/access-lists/{id}", new { name = "Staff", users = new object[] { new { username = "alice" }, new { username = "bob" } } }, Json);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var put = await c.PutAsJsonAsync($"/api/access-lists/{id}", new { name = "Staff 2", users = new object[] { new { username = "alice" }, new { username = "bob", password = "pw" } } }, Json);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var saved = api.Store.Col<AccessList>().FindById(id);
        Assert.Equal(hash, saved.Users.Single(u => u.Username == "alice").PasswordHash);
        Assert.Equal(2, saved.Users.Count);

        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/access-lists", new { name = "x", rules = new[] { new { action = "allow", cidr = "999.1.1.1" } } }, Json)).StatusCode);

        // in use → 409
        var host = new { kind = "response", domains = new[] { "al.example.com" }, tls = "none", accessListId = id };
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/hosts", host, Json)).StatusCode);
        var got = await c.GetFromJsonAsync<JsonObject>($"/api/access-lists/{id}", Json);
        Assert.Equal(1, got!["usedBy"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.Conflict, (await c.DeleteAsync($"/api/access-lists/{id}")).StatusCode);
    }

    // ------------------------------------------------------------------ settings

    [Fact]
    public async Task Settings_wire_shape_and_secret_semantics()
    {
        await using var api = ApiHost.Start();
        var c = api.Client;
        var get = await c.GetFromJsonAsync<JsonObject>("/api/settings/caddy", Json);
        Assert.False(get!["hasEabMacKey"]!.GetValue<bool>());
        Assert.Null(get["eabMacKeyProtected"]);
        Assert.Equal("managed", get["mode"]!.GetValue<string>());

        var put = await c.PutAsJsonAsync("/api/settings/caddy", new { acmeCa = "zeroSsl", acmeEmail = "ops@example.com", eabKeyId = "kid", eabMacKey = "secret-mac" }, Json);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var body = await Body(put);
        Assert.True(body["item"]!["hasEabMacKey"]!.GetValue<bool>());
        Assert.Equal("zeroSsl", body["item"]!["acmeCa"]!.GetValue<string>());
        Assert.DoesNotContain("secret-mac", body.ToJsonString());
        var stored = api.Store.GetSettings<CaddySettings>();
        Assert.NotEqual("secret-mac", stored.EabMacKeyProtected);
        Assert.Equal("secret-mac", api.App.Services.GetRequiredService<ISecretProtector>().Unprotect(stored.EabMacKeyProtected!));
        Assert.Equal(18090, stored.HttpPort); // untouched fields kept

        // absent = unchanged
        await c.PutAsJsonAsync("/api/settings/caddy", new { logLevel = "debug" }, Json);
        Assert.Equal(stored.EabMacKeyProtected, api.Store.GetSettings<CaddySettings>().EabMacKeyProtected);
        // "" = clear (key id cleared too, otherwise validation fails)
        var cleared = await c.PutAsJsonAsync("/api/settings/caddy", new { eabKeyId = "", eabMacKey = "" }, Json);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Null(api.Store.GetSettings<CaddySettings>().EabMacKeyProtected);

        var invalid = await c.PutAsJsonAsync("/api/settings/caddy", new { httpPort = 0, adminListen = "0.0.0.0:2019", trustedProxies = new[] { "nope" } }, Json);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var errors = (await Body(invalid))["errors"]!.AsObject();
        Assert.NotNull(errors["httpPort"]);
        Assert.NotNull(errors["adminListen"]);
        Assert.NotNull(errors["trustedProxies[0]"]);
        Assert.Contains(api.Audit.Entries, e => e.ObjectType == "settings");
    }

    // ------------------------------------------------------------------ certificates

    [Fact]
    public async Task Certificate_upload_pem_pfx_path_update_and_delete()
    {
        await using var api = ApiHost.Start();
        var c = api.Client;
        using var cert = TestCerts.SelfSigned(["tls.example.com"]);

        // paste PEM
        var pem = await c.PostAsJsonAsync("/api/certificates/pem", new { name = "Pasted", certPem = cert.ExportCertificatePem(), keyPem = TestCerts.KeyPem(cert) }, Json);
        Assert.Equal(HttpStatusCode.OK, pem.StatusCode);
        var pemItem = (await Body(pem))["item"]!;
        var pemId = pemItem["id"]!.GetValue<string>();
        Assert.Equal("uploaded", pemItem["source"]!.GetValue<string>());
        Assert.Equal(["tls.example.com"], pemItem["subjects"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
        Assert.True(File.Exists(Path.Combine(api.Env.Paths.DefaultCertificateStore, pemId, "fullchain.pem")));
        Assert.True(File.Exists(Path.Combine(api.Env.Paths.DefaultCertificateStore, pemId, "privkey.pem")));

        // multipart PEM files
        using (var form = new MultipartFormDataContent())
        {
            form.Add(new StringContent("Files"), "name");
            form.Add(new ByteArrayContent(System.Text.Encoding.ASCII.GetBytes(cert.ExportCertificatePem())), "certFile", "cert.pem");
            form.Add(new ByteArrayContent(System.Text.Encoding.ASCII.GetBytes(TestCerts.KeyPem(cert))), "keyFile", "key.pem");
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/api/certificates/upload", form)).StatusCode);
        }

        // multipart PFX, wrong then right password
        var pfx = cert.Export(X509ContentType.Pfx, "pfx-pass")!;
        using (var form = new MultipartFormDataContent())
        {
            form.Add(new StringContent("Pfx"), "name");
            form.Add(new ByteArrayContent(pfx), "pfxFile", "site.pfx");
            form.Add(new StringContent("wrong"), "pfxPassword");
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/certificates/upload", form)).StatusCode);
        }
        string pfxId;
        using (var form = new MultipartFormDataContent())
        {
            form.Add(new StringContent("Pfx"), "name");
            form.Add(new ByteArrayContent(pfx), "pfxFile", "site.pfx");
            form.Add(new StringContent("pfx-pass"), "pfxPassword");
            var r = await c.PostAsync("/api/certificates/upload", form);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            pfxId = (await Body(r))["item"]!["id"]!.GetValue<string>();
        }

        // mismatched key
        using var other = TestCerts.SelfSigned(["other.example.com"]);
        var mismatch = await c.PostAsJsonAsync("/api/certificates/pem", new { name = "x", certPem = cert.ExportCertificatePem(), keyPem = TestCerts.KeyPem(other) }, Json);
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        Assert.Contains("does not match", (await Body(mismatch))["detail"]!.GetValue<string>());

        // file path (outside the data folder: certificate files may only be referenced there inside the certificate store)
        var dir = Path.Combine(Path.GetTempPath(), "cpm-config-tests-external", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        using var cleanup = new DirCleanup(dir);
        File.WriteAllText(Path.Combine(dir, "c.pem"), cert.ExportCertificatePem());
        File.WriteAllText(Path.Combine(dir, "k.pem"), TestCerts.KeyPem(cert));
        var byPath = await c.PostAsJsonAsync("/api/certificates/path", new { name = "OnDisk", certPath = Path.Combine(dir, "c.pem"), keyPath = Path.Combine(dir, "k.pem") }, Json);
        Assert.Equal(HttpStatusCode.OK, byPath.StatusCode);
        Assert.Equal("filePath", (await Body(byPath))["item"]!["source"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/certificates/path", new { name = "x", certPath = Path.Combine(dir, "missing.pem"), keyPath = Path.Combine(dir, "k.pem") }, Json)).StatusCode);

        // inventory
        var inv = await c.GetFromJsonAsync<JsonArray>("/api/certificates", Json);
        Assert.Equal(4, inv!.Count(x => x!["kind"]!.GetValue<string>() == "custom"));

        // rename
        var renamed = await c.PutAsJsonAsync($"/api/certificates/{pemId}", new { name = "Renamed", notes = "n" }, Json);
        Assert.Equal("Renamed", (await Body(renamed))["item"]!["name"]!.GetValue<string>());

        // replace with a new cert
        using var newer = TestCerts.SelfSigned(["tls.example.com", "www.tls.example.com"]);
        using (var form = new MultipartFormDataContent())
        {
            form.Add(new ByteArrayContent(System.Text.Encoding.ASCII.GetBytes(newer.ExportCertificatePem())), "certFile", "cert.pem");
            form.Add(new ByteArrayContent(System.Text.Encoding.ASCII.GetBytes(TestCerts.KeyPem(newer))), "keyFile", "key.pem");
            var rep = await c.PostAsync($"/api/certificates/{pemId}/replace", form);
            Assert.Equal(HttpStatusCode.OK, rep.StatusCode);
            Assert.Equal(newer.Thumbprint, (await Body(rep))["item"]!["thumbprint"]!.GetValue<string>());
        }

        // in use → 409; unused → deleted with files
        var host = new { kind = "response", domains = new[] { "tls.example.com" }, tls = "custom", certificateId = pemId };
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/hosts", host, Json)).StatusCode);
        Assert.Contains("load_files", File.ReadAllText(api.Env.Paths.CaddyConfigFile));
        Assert.Equal(HttpStatusCode.Conflict, (await c.DeleteAsync($"/api/certificates/{pemId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.DeleteAsync($"/api/certificates/{pfxId}")).StatusCode);
        Assert.False(Directory.Exists(Path.Combine(api.Env.Paths.DefaultCertificateStore, pfxId)));

        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/certificates/internal-root")).StatusCode);
    }

    [Fact]
    public async Task Expired_certificate_is_accepted_with_warning()
    {
        await using var api = ApiHost.Start();
        using var cert = TestCerts.SelfSigned(["old.example.com"], DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(-1));
        var r = await api.Client.PostAsJsonAsync("/api/certificates/pem", new { name = "Old", certPem = cert.ExportCertificatePem(), keyPem = TestCerts.KeyPem(cert) }, Json);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains((await Body(r))["apply"]!["warnings"]!.AsArray(), w => w!.GetValue<string>().Contains("expired"));
    }

    // ------------------------------------------------------------------ config

    [Fact]
    public async Task Config_preview_apply_revisions_and_running()
    {
        await using var api = ApiHost.Start();
        var c = api.Client;
        await c.PostAsJsonAsync("/api/hosts", ProxyHost("preview.example.com"), Json);

        var preview = await c.GetFromJsonAsync<JsonObject>("/api/config/preview", Json);
        Assert.Contains("preview.example.com", preview!["json"]!.GetValue<string>());

        var apply = await c.PostAsync("/api/config/apply", null);
        Assert.Equal(HttpStatusCode.OK, apply.StatusCode);
        Assert.True((await Body(apply))["success"]!.GetValue<bool>());

        var revs = await c.GetFromJsonAsync<JsonArray>("/api/config/revisions?take=1", Json);
        Assert.Single(revs!);
        var rev = revs![0]!;
        Assert.Null(rev["json"]);
        Assert.Equal(64, rev["hash"]!.GetValue<string>().Length);
        Assert.Equal("Manual apply", rev["reason"]!.GetValue<string>());
        Assert.Equal("tester", rev["appliedBy"]!.GetValue<string>());
        var full = await c.GetFromJsonAsync<JsonObject>($"/api/config/revisions/{rev["id"]!.GetValue<string>()}", Json);
        Assert.Contains("preview.example.com", full!["json"]!.GetValue<string>());
        Assert.Equal(2, (await c.GetFromJsonAsync<JsonArray>("/api/config/revisions", Json))!.Count);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await c.GetAsync("/api/config/running")).StatusCode);
        Assert.Empty((await c.GetFromJsonAsync<JsonArray>("/api/caddy/upstreams", Json))!);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await c.PostAsJsonAsync("/api/config/caddyfile/adapt", new { caddyfile = "a.test {\n respond hi\n}" }, Json)).StatusCode);
        Assert.Contains(api.Audit.Entries, e => e.Action == "applied");
    }

    [CaddyFact]
    public async Task Caddyfile_adapt_endpoint_with_binary()
    {
        await using var api = ApiHost.Start(installBinary: true);
        var ok = await api.Client.PostAsJsonAsync("/api/config/caddyfile/adapt", new { caddyfile = "http://a.test {\n respond hi\n}\n" }, Json);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var body = await Body(ok);
        Assert.Contains("static_response", body["json"]!.GetValue<string>());
        Assert.NotNull(body["warnings"]);
        var bad = await api.Client.PostAsJsonAsync("/api/config/caddyfile/adapt", new { caddyfile = "a.test {\n nonsense_directive\n}\n" }, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
    }

    [CaddyFact]
    public async Task Config_preview_follows_the_active_mode()
    {
        await using var api = ApiHost.Start(installBinary: true);
        var c = api.Client;
        await c.PostAsJsonAsync("/api/hosts", ProxyHost("managed.example.com"), Json);

        var managed = await c.GetFromJsonAsync<JsonObject>("/api/config/preview", Json);
        Assert.Equal("managed", managed!["mode"]!.GetValue<string>());
        Assert.Contains("managed.example.com", managed["json"]!.GetValue<string>());

        // Caddyfile mode: the preview is what an apply would load (the adapted Caddyfile), not the managed hosts.
        var s = api.Store.GetSettings<CaddySettings>();
        s.Mode = ConfigMode.Caddyfile;
        s.RawCaddyfile = "http://cf.example.com {\n respond \"from caddyfile\"\n}\n";
        api.Store.SaveSettings(s);
        var adapted = await c.GetFromJsonAsync<JsonObject>("/api/config/preview", Json);
        Assert.Equal("caddyfile", adapted!["mode"]!.GetValue<string>());
        Assert.Contains("cf.example.com", adapted["json"]!.GetValue<string>());
        Assert.DoesNotContain("managed.example.com", adapted["json"]!.GetValue<string>());
        // completed with the manager's admin endpoint, as an apply would load it
        Assert.Contains(s.AdminListen, adapted["json"]!.GetValue<string>());

        s.RawCaddyfile = "a.test {\n nonsense_directive\n}\n";
        api.Store.SaveSettings(s);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await c.GetAsync("/api/config/preview")).StatusCode);
    }
}
