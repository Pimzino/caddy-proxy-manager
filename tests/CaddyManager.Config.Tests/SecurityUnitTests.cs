using System.Net;
using System.Text.Json.Nodes;
using CaddyManager.Config.Validation;
using CaddyManager.Core.Models;

namespace CaddyManager.Config.Tests;

public sealed class SecurityUnitTests : IDisposable
{
    private readonly TempEnv _env = new();
    public void Dispose() => _env.Dispose();

    private static LocalEndpointGuard Guard() => new(
        new Dictionary<int, string> { [2019] = LocalEndpointGuard.AdminApiDescription, [81] = LocalEndpointGuard.UiDescription + " (HTTP)" },
        [IPAddress.Parse("192.168.10.5"), IPAddress.Parse("fe80::1")],
        ["localhost", "web01", "web01.corp.example"]);

    // ------------------------------------------------------------------ local endpoints

    [Theory]
    [InlineData("127.0.0.1", 2019, true)]
    [InlineData("127.9.9.9", 2019, true)]
    [InlineData("localhost", 2019, true)]
    [InlineData("LOCALHOST.", 81, true)]
    [InlineData("app.localhost", 81, true)]
    [InlineData("::1", 2019, true)]
    [InlineData("[::1]", 81, true)]
    [InlineData("::ffff:127.0.0.1", 81, true)]
    [InlineData("0.0.0.0", 2019, true)]
    [InlineData("192.168.10.5", 81, true)]
    [InlineData("fe80::1", 81, true)]
    [InlineData("WEB01", 81, true)]
    [InlineData("web01.corp.example", 2019, true)]
    [InlineData("127.0.0.1", 8080, false)]
    [InlineData("10.0.0.5", 2019, false)]
    [InlineData("backend.corp.example", 81, false)]
    [InlineData("web02", 81, false)]
    public void Protected_local_endpoints(string host, int port, bool blocked) =>
        Assert.Equal(blocked, Guard().Check(host, port) is not null);

    [Fact]
    public void Messages_name_the_protected_service()
    {
        Assert.Contains("Caddy admin API", Guard().Check("127.0.0.1", 2019));
        Assert.Contains("web UI", Guard().Check("localhost", 81));
    }

    [Theory]
    [InlineData("127.0.0.1:2019", true)]
    [InlineData("tcp/localhost:2019", true)]
    [InlineData("tcp4/127.0.0.1:81", true)]
    [InlineData("[::1]:2019", true)]
    [InlineData(":2019", true)]
    [InlineData("127.0.0.1:2000-2100", true)]
    [InlineData("localhost:{http.request.header.X-Port}", true)]
    [InlineData("{http.request.host}:2019", true)]
    [InlineData("{http.request.host}:8080", false)]
    [InlineData("backend:{env.PORT}", false)]
    [InlineData("127.0.0.1:8080", false)]
    [InlineData("10.1.1.1:2019", false)]
    [InlineData("unix//run/app.sock", false)]
    [InlineData("127.0.0.1", false)]
    public void Dial_addresses(string dial, bool blocked) =>
        Assert.Equal(blocked, Guard().CheckDial(dial) is not null);

    [Fact]
    public void Advanced_routes_are_walked_for_reverse_proxy_dials()
    {
        var nested = """
            [{"handle":[{"handler":"subroute","routes":[{"match":[{"path":["/x/*"]}],
              "handle":[{"handler":"reverse_proxy","upstreams":[{"dial":"10.0.0.1:80"},{"dial":"localhost:2019"}]}]}]}]}]
            """;
        Assert.Single(Guard().CheckRoutesJson(nested));
        var dynamicA = """[{"handle":[{"handler":"reverse_proxy","dynamic_upstreams":{"source":"a","name":"localhost","port":"81"}}]}]""";
        Assert.Single(Guard().CheckRoutesJson(dynamicA));
        var fine = """[{"handle":[{"handler":"reverse_proxy","upstreams":[{"dial":"10.0.0.1:2019"}]},{"handler":"static_response","body":"127.0.0.1:2019"}]}]""";
        Assert.Empty(Guard().CheckRoutesJson(fine));
        Assert.Empty(Guard().CheckRoutesJson("not json"));
    }

    [Fact]
    public void Protected_ports_follow_settings()
    {
        var ports = LocalEndpointGuard.ProtectedPortsFor(new CaddySettings { AdminListen = "127.0.0.1:2999" }, new UiSettings { Port = 8181, HttpsPort = 8443 });
        Assert.Equal([2999, 8181], ports.Keys.Order().ToArray());
        var https = LocalEndpointGuard.ProtectedPortsFor(new CaddySettings(), new UiSettings { HttpsEnabled = true, HttpsPort = 9443 });
        Assert.Equal([81, 2019, 9443], https.Keys.Order().ToArray());
    }

    // ------------------------------------------------------------------ paths

    private PathProblem? Root(string root, bool admin = false) =>
        PathGuard.CheckStaticRoot(root, _env.Paths, _env.Paths.DefaultCertificateStore, admin);

    [Theory]
    [InlineData(@"C:\")]
    [InlineData("C:/")]
    [InlineData(@"D:\")]
    [InlineData("/")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"c:\windows\system32\drivers\etc")]
    [InlineData(@"C:\Windows.\System32")]
    [InlineData(@"C:\Program Files\Caddy Proxy Manager")]
    [InlineData(@"C:\Program Files (x86)")]
    [InlineData(@"C:\PROGRA~1\x")]
    [InlineData(@"C:\sites\..\Windows")]
    [InlineData(@"C:\sites\x:stream")]
    [InlineData(@"\\?\C:\sites")]
    [InlineData(@"\\.\C:\sites")]
    [InlineData(@"\\fileserver\C$\www")]
    [InlineData("relative/path")]
    public void Static_roots_refused_for_everyone(string root)
    {
        var p = Root(root, admin: true);
        Assert.NotNull(p);
        Assert.Equal(400, p!.Status);
    }

    [Fact]
    public void Static_roots_inside_or_above_the_data_folder_are_refused()
    {
        Assert.Contains("the same as", Root(_env.Paths.DataDir)!.Message);
        Assert.Contains("inside", Root(Path.Combine(_env.Paths.DataDir, "www"))!.Message);
        Assert.Equal(400, Root(_env.Paths.CaddyStorageDir)!.Status); // reported as inside the data folder
        Assert.Contains("certificate store", PathGuard.CheckStaticRoot("/srv/certs/x", _env.Paths, "/srv/certs", false)!.Message);
        Assert.Contains("a parent of", Root(Path.GetDirectoryName(_env.Paths.DataDir)!)!.Message);
    }

    [Fact]
    public void Static_roots_resolve_symbolic_links()
    {
        if (OperatingSystem.IsWindows()) return; // creating symlinks needs a privilege on Windows
        var outside = Path.Combine(Path.GetTempPath(), "cpm-link-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateSymbolicLink(outside, _env.Paths.DataDir);
            Assert.NotNull(Root(outside));
        }
        finally
        {
            try { Directory.Delete(outside); } catch (IOException) { }
        }
    }

    [Theory]
    [InlineData(@"D:\www\example")]
    [InlineData(@"C:\inetpub\wwwroot")]
    [InlineData("/srv/www")]
    public void Ordinary_static_roots_are_allowed(string root) => Assert.Null(Root(root));

    [Fact]
    public void Unc_roots_are_for_administrators()
    {
        Assert.Equal(403, Root(@"\\fileserver\web\site")!.Status);
        Assert.Null(Root(@"\\fileserver\web\site", admin: true));
        Assert.Null(Root("//fileserver/web", admin: true));
    }

    [Fact]
    public void Certificate_file_rules()
    {
        var store = _env.Paths.DefaultCertificateStore;
        string? Check(string path, string[]? ext = null) =>
            PathGuard.CheckCertificateFile(path, "certificate", ext ?? PathGuard.CertificateFileExtensions, _env.Paths, store);

        Assert.Null(Check(@"C:\certs\site.pem"));
        Assert.Null(Check(@"\\ca01\certs\site.cer"));
        Assert.Null(Check(Path.Combine(store, "abc", "fullchain.pem")));
        Assert.Contains("extensions", Check(@"C:\certs\site.txt"));
        Assert.Contains("extensions", Check(@"C:\certs\site.pfx"));
        Assert.Null(Check(@"C:\certs\site.PFX", PathGuard.PfxFileExtensions));
        Assert.Contains("data folder", Check(Path.Combine(_env.Paths.DataDir, "db", "secret.key")));
        Assert.Contains("data folder", Check(Path.Combine(_env.Paths.CaddyStorageDir, "certificates", "x", "x.key")));
        Assert.Contains("'..'", Check(@"C:\certs\..\x.pem"));
        Assert.NotNull(Check(""));
    }

    // ------------------------------------------------------------------ redaction

    [Fact]
    public void Redactor_masks_secrets_only()
    {
        var json = """
            {"apps":{"tls":{"automation":{"policies":[{"issuers":[{"module":"acme","email":"ops@example.com",
              "external_account":{"key_id":"kid","mac_key":"SECRETMAC"},
              "challenges":{"dns":{"provider":{"name":"cloudflare","api_token":"CFTOKEN","zone":{"nested":"X"}},"resolvers":["1.1.1.1"]}}}]}]},
              "certificates":{"load_pem":[{"certificate":"-----BEGIN CERTIFICATE-----","key":"-----BEGIN PRIVATE KEY-----\nabc"}],
                              "load_files":[{"certificate":"/c.pem","key":"/k.pem"}]}},
             "http":{"servers":{"s":{"routes":[{"handle":[{"handler":"authentication","providers":{"http_basic":{"accounts":[{"username":"bob","password":"$2a$14$HASH"}]}}}]}]}}}}}
            """;
        var redacted = JsonNode.Parse(ConfigRedactor.Redact(json))!;
        var text = redacted.ToJsonString();
        foreach (var secret in new[] { "SECRETMAC", "CFTOKEN", "$2a$14$HASH", "BEGIN PRIVATE KEY", "\"X\"" })
            Assert.DoesNotContain(secret, text);
        foreach (var kept in new[] { "cloudflare", "kid", "ops@example.com", "bob", "/k.pem", "1.1.1.1", "BEGIN CERTIFICATE" })
            Assert.Contains(kept, text);
        Assert.Equal("not json", ConfigRedactor.Redact("not json"));
    }

    [Fact]
    public void Advanced_routes_redaction_keeps_text_without_secrets()
    {
        const string plain = """[{"handle":[{"handler":"static_response","body":"hi"}]}]""";
        Assert.Same(plain, ConfigRedactor.RedactRoutes(plain));
        var withHash = """[{"handle":[{"handler":"authentication","providers":{"http_basic":{"accounts":[{"username":"u","password":"$2a$HASH"}]}}}]}]""";
        Assert.DoesNotContain("$2a$HASH", ConfigRedactor.RedactRoutes(withHash));
    }
}
