using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using CaddyManager.Core;
using CaddyManager.Core.Infrastructure;
using CaddyManager.Core.Models;
using CaddyManager.Ops.Auth;
using CaddyManager.Ops.Backup;
using CaddyManager.Ops.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CaddyManager.Ops.Tests;

public class LoginThrottleTests
{
    [Theory]
    [InlineData("api/auth/login")]
    [InlineData("api/auth/login/")]
    [InlineData("API/AUTH/LOGIN/")]
    public async Task Login_throttle_cannot_be_bypassed_with_path_variants(string path)
    {
        await using var app = await TestApp.StartAsync(o => o.LoginAttemptsPerMinute = 3);
        await app.SetupAdminAsync();
        var c = app.Client(remoteIp: "10.9.9.9");
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
            codes.Add((await c.PostAsJsonAsync(path, new { email = TestApp.AdminEmail, password = "wrong password!!" })).StatusCode);
        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized,
            (HttpStatusCode)429, (HttpStatusCode)429], codes);

        // Even the right password is refused while throttled (no oracle), and other clients are unaffected.
        var limited = await c.PostAsJsonAsync("api/auth/login", new { email = TestApp.AdminEmail, password = TestApp.AdminPassword });
        Assert.Equal((HttpStatusCode)429, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"));
        var other = await app.Client(remoteIp: "10.9.9.10").PostAsJsonAsync("api/auth/login",
            new { email = TestApp.AdminEmail, password = TestApp.AdminPassword });
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task Setup_throttle_applies_with_trailing_slash()
    {
        await using var app = await TestApp.StartAsync(o => o.LoginAttemptsPerMinute = 2);
        var c = app.Client(remoteIp: "10.1.1.1");
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
            codes.Add((await c.PostAsJsonAsync("api/setup/", new { token = "guess" + i, email = "a@b.com", name = "A", password = "long enough password" })).StatusCode);
        Assert.Equal([HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, (HttpStatusCode)429, (HttpStatusCode)429], codes);
    }

    [Fact]
    public async Task Ipv6_clients_are_throttled_per_64_prefix()
    {
        await using var app = await TestApp.StartAsync(o => o.LoginAttemptsPerMinute = 2);
        await app.SetupAdminAsync();
        var codes = new List<HttpStatusCode>();
        foreach (var ip in new[] { "2001:db8:1:2::1", "2001:db8:1:2::2", "2001:db8:1:2:ffff::3" })
            codes.Add((await app.Client(remoteIp: ip).PostAsJsonAsync("api/auth/login",
                new { email = TestApp.AdminEmail, password = "wrong password!!" })).StatusCode);
        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, (HttpStatusCode)429], codes);

        Assert.Equal("2001:db8:1:2::/64", LoginThrottle.PartitionKey(IPAddress.Parse("2001:db8:1:2:abcd::9")));
        Assert.Equal("10.0.0.1", LoginThrottle.PartitionKey(IPAddress.Parse("::ffff:10.0.0.1")));
        Assert.Equal("::1", LoginThrottle.PartitionKey(IPAddress.IPv6Loopback));
    }
}

public class AuthorizationCoverageTests
{
    private static readonly HashSet<string> Anonymous = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET /api/setup/status", "POST /api/setup/", "POST /api/auth/login", "POST /api/auth/logout",
    };

    [Fact]
    public async Task Every_ops_api_endpoint_declares_a_policy_or_is_a_known_anonymous_endpoint()
    {
        await using var app = await TestApp.StartAsync();
        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => ("/" + e.RoutePattern.RawText?.TrimStart('/')).StartsWith("/api", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(endpoints);
        foreach (var e in endpoints)
        {
            var methods = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"];
            foreach (var m in methods)
            {
                var name = $"{m} /{e.RoutePattern.RawText!.TrimStart('/')}";
                var anonymous = e.Metadata.GetMetadata<IAllowAnonymous>() is not null;
                var policies = e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).Where(p => p is not null).ToList();
                if (Anonymous.Contains(name)) Assert.True(anonymous, $"{name} should be anonymous");
                else
                {
                    Assert.False(anonymous, $"{name} is anonymous but not in the allow-list");
                    Assert.True(policies.Count > 0, $"{name} has no authorization policy");
                }
            }
        }
    }

    [Fact]
    public async Task Endpoint_without_policy_requires_sign_in_through_fallback_policy()
    {
        await using var app = await TestApp.StartAsync(mapExtra: a => a.MapGet("/api/forgotten", () => "secret"));
        var anon = await app.Client().GetAsync("api/forgotten");
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);
        var admin = await app.SetupAdminAsync();
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("api/forgotten")).StatusCode);
    }

    [Theory]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("PUT")]
    public async Task Csrf_header_is_required_for_all_unsafe_methods(string method)
    {
        await using var app = await TestApp.StartAsync();
        var admin = await app.SetupAdminAsync();
        admin.DefaultRequestHeaders.Remove("X-CPM-Request");
        var resp = await admin.SendAsync(new HttpRequestMessage(new HttpMethod(method), "api/users/whatever")
        {
            Content = JsonContent.Create(new { name = "x" }),
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("Missing request header", (await resp.JsonAsync()).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Csrf_header_is_required_for_multipart_uploads()
    {
        await using var app = await TestApp.StartAsync();
        var admin = await app.SetupAdminAsync();
        admin.DefaultRequestHeaders.Remove("X-CPM-Request");
        var form = new MultipartFormDataContent { { new ByteArrayContent([1, 2, 3]), "file", "backup.zip" } };
        var resp = await admin.PostAsync("api/backup/restore", form);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.False(RestoreStager.HasPendingRestore(app.Paths));
    }
}

public class RestoreHardeningTests
{
    [Fact]
    public void Crafted_manifest_cannot_redirect_certificate_restore_outside_the_configured_store()
    {
        var root = TempDir();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "data"));
            paths.EnsureCreated();
            var outside = Path.Combine(root, "outside");
            var zip = MakeBackup(root, dbStorePath: null, manifestStorePath: outside,
                ("certificates/abc123/privkey.pem", "KEY"),
                ("certificates/evil.dll", "MZ"),
                ("certificates/abc123/run.cmd", "calc"),
                ("certificates/a/b/c.pem", "deep"));
            RestoreStager.Stage(paths, new MemoryStream(zip));
            var outcome = RestoreStager.ApplyPendingRestore(paths);
            Assert.False(RestoreStager.LastOutcomeFailed, outcome);

            Assert.False(Directory.Exists(outside) && Directory.EnumerateFileSystemEntries(outside).Any(), "wrote outside the store");
            Assert.Equal("KEY", File.ReadAllText(Path.Combine(paths.DefaultCertificateStore, "abc123", "privkey.pem")));
            Assert.False(File.Exists(Path.Combine(paths.DefaultCertificateStore, "evil.dll")));
            Assert.False(File.Exists(Path.Combine(paths.DefaultCertificateStore, "abc123", "run.cmd")));
            Assert.False(Directory.Exists(Path.Combine(paths.DefaultCertificateStore, "a")));
            Assert.Contains("3 unexpected file(s)", outcome);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void Custom_store_is_used_when_the_restored_database_configures_it()
    {
        var root = TempDir();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "data"));
            paths.EnsureCreated();
            var share = Path.Combine(root, "share");
            var zip = MakeBackup(root, dbStorePath: share, manifestStorePath: share, ("certificates/abc123/fullchain.pem", "CERT"));
            RestoreStager.Stage(paths, new MemoryStream(zip));
            var outcome = RestoreStager.ApplyPendingRestore(paths);
            Assert.False(RestoreStager.LastOutcomeFailed, outcome);
            Assert.Equal("CERT", File.ReadAllText(Path.Combine(share, "abc123", "fullchain.pem")));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void Archive_expanding_beyond_the_limit_is_rejected_and_nothing_is_staged()
    {
        var root = TempDir();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "data"));
            paths.EnsureCreated();
            var zip = MakeBackup(root, null, null, ("caddy-data/big.bin", new string('\0', 3 * 1024 * 1024)));
            var ex = Assert.Throws<InvalidDataException>(() => RestoreStager.Stage(paths, new MemoryStream(zip), maxTotalBytes: 1024 * 1024));
            Assert.Contains("expands to more than", ex.Message);
            Assert.False(RestoreStager.HasPendingRestore(paths));
            Assert.Empty(Directory.EnumerateDirectories(paths.DataDir, RestoreStager.PendingDirName + "*"));
        }
        finally { Cleanup(root); }
    }

    [Theory]
    [InlineData("abc123/fullchain.pem", true)]
    [InlineData("abc123/privkey.pem", true)]
    [InlineData("evil.dll", false)]
    [InlineData("abc/x.exe", false)]
    [InlineData("a/b/c.pem", false)]
    [InlineData("../x/y.pem", false)]
    [InlineData(".hidden/x.pem", false)]
    public void Certificate_store_layout_filter(string rel, bool expected) =>
        Assert.Equal(expected, RestoreStager.IsCertificateStoreFile(rel));

    private static byte[] MakeBackup(string root, string? dbStorePath, string? manifestStorePath, params (string Name, string Content)[] extra)
    {
        var src = new AppPaths(Path.Combine(root, "src-" + Guid.NewGuid().ToString("N")[..6]));
        src.EnsureCreated();
        using (var store = new LiteStore(src))
        {
            store.Col<User>().Insert(new User { Email = "admin@example.com", Name = "A", Role = UserRole.Admin, PasswordHash = "x" });
            if (dbStorePath is not null) store.SaveSettings(new CaddySettings { CertificateStorePath = dbStorePath });
        }
        var manifest = new BackupManifest
        {
            CreatedAt = DateTime.UtcNow,
            Machine = "test",
            CertificateStoreIsDefault = manifestStorePath is null,
            CertificateStorePath = manifestStorePath ?? "",
        };
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            zip.CreateEntryFromFile(src.DbFile, BackupService.DbEntry);
            using (var s = zip.CreateEntry(BackupService.ManifestEntry).Open())
                JsonSerializer.Serialize(s, manifest, JsonDefaults.Api);
            foreach (var (name, content) in extra)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(Encoding.UTF8.GetBytes(content));
            }
        }
        return ms.ToArray();
    }

    internal static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "cpm-sec-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    internal static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
    }
}

public class UiListenerTests
{
    private static readonly Func<System.Net.IPEndPoint, bool> Always = _ => true;

    [Fact]
    public void Valid_settings_are_used_as_is()
    {
        WithPaths(paths =>
        {
            var plan = UiListener.Plan(paths, new UiSettings { Port = 8181, BindAddress = "127.0.0.1" }, null, new SecretProtector(paths), Always);
            Assert.Equal(new System.Net.IPEndPoint(IPAddress.Loopback, 8181), plan.Http);
            Assert.Null(plan.Https);
            Assert.Empty(plan.Warnings);
        });
    }

    [Fact]
    public void Bad_bind_address_and_port_fall_back_to_defaults()
    {
        WithPaths(paths =>
        {
            var plan = UiListener.Plan(paths, new UiSettings { Port = 70000, BindAddress = "not-an-ip" }, null, new SecretProtector(paths), Always);
            Assert.Equal(new System.Net.IPEndPoint(IPAddress.Any, 81), plan.Http);
            Assert.Equal(2, plan.Warnings.Count);

            var env = UiListener.Plan(paths, new UiSettings { Port = 9000 }, "abc", new SecretProtector(paths), Always);
            Assert.Equal(9000, env.Http.Port);
            Assert.Single(env.Warnings);
            Assert.Equal(5081, UiListener.Plan(paths, new UiSettings(), "5081", new SecretProtector(paths), Always).Http.Port);
        });
    }

    [Fact]
    public void Unavailable_address_falls_back_to_all_interfaces_on_the_default_port()
    {
        WithPaths(paths =>
        {
            var plan = UiListener.Plan(paths, new UiSettings { Port = 8181, BindAddress = "10.123.45.67" }, null, new SecretProtector(paths),
                ep => ep.Port == 81);
            Assert.Equal(new System.Net.IPEndPoint(IPAddress.Any, 81), plan.Http);
            Assert.Contains(plan.Warnings, w => w.Contains("falling back"));
        });
    }

    [Fact]
    public void Real_probe_detects_a_port_in_use()
    {
        using var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        Assert.False(UiListener.CanBind(new System.Net.IPEndPoint(IPAddress.Loopback, port)));
        Assert.False(UiListener.CanBind(new System.Net.IPEndPoint(IPAddress.Parse("203.0.113.9"), 18181))); // not a local address
    }

    [Fact]
    public void Broken_pfx_falls_back_to_self_signed_and_never_leaks_the_password()
    {
        WithPaths(paths =>
        {
            var secrets = new SecretProtector(paths);
            var pfx = Path.Combine(paths.DataDir, "ui.pfx");
            using (var rsa = RSA.Create(2048))
            {
                var req = new CertificateRequest("CN=ui.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using var c = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));
                File.WriteAllBytes(pfx, c.Export(X509ContentType.Pfx, "right-password"));
            }

            var good = UiListener.Plan(paths, new UiSettings
            {
                HttpsEnabled = true, HttpsPort = 8443, HttpsPfxPath = pfx, HttpsPfxPasswordProtected = secrets.Protect("right-password"),
            }, null, secrets, Always);
            Assert.Empty(good.Warnings);
            Assert.Equal("CN=ui.test", good.Certificate!.Subject);

            var wrong = UiListener.Plan(paths, new UiSettings
            {
                HttpsEnabled = true, HttpsPort = 8443, HttpsPfxPath = pfx, HttpsPfxPasswordProtected = secrets.Protect("s3cret-typo"),
            }, null, secrets, Always);
            Assert.NotNull(wrong.Https);
            Assert.NotNull(wrong.Certificate);
            Assert.NotEqual("CN=ui.test", wrong.Certificate!.Subject);
            Assert.Contains(wrong.Warnings, w => w.Contains("self-signed"));
            Assert.DoesNotContain(wrong.Warnings, w => w.Contains("s3cret-typo"));

            var missing = UiListener.Plan(paths, new UiSettings { HttpsEnabled = true, HttpsPfxPath = Path.Combine(paths.DataDir, "nope.pfx") },
                null, secrets, Always);
            Assert.NotNull(missing.Certificate);
            Assert.Single(missing.Warnings);
        });
    }

    [Fact]
    public void Corrupt_self_signed_file_is_regenerated_and_bad_https_port_only_disables_https()
    {
        WithPaths(paths =>
        {
            File.WriteAllText(UiListener.SelfSignedFile(paths), "garbage");
            var plan = UiListener.Plan(paths, new UiSettings { HttpsEnabled = true }, null, new SecretProtector(paths), Always);
            Assert.NotNull(plan.Certificate);
            Assert.True(plan.Certificate!.HasPrivateKey);
            Assert.Empty(plan.Warnings);

            var clash = UiListener.Plan(paths, new UiSettings { HttpsEnabled = true, Port = 8443, HttpsPort = 8443 }, null, new SecretProtector(paths), Always);
            Assert.Equal(8443, clash.Http.Port);
            Assert.Null(clash.Https);
            Assert.Single(clash.Warnings);
        });
    }

    private static void WithPaths(Action<AppPaths> test)
    {
        var root = RestoreHardeningTests.TempDir();
        try
        {
            var paths = new AppPaths(root);
            paths.EnsureCreated();
            test(paths);
        }
        finally { RestoreHardeningTests.Cleanup(root); }
    }
}

public class SessionRevocationTests
{
    [Fact]
    public async Task Copied_cookie_stops_working_after_logout_even_across_restart()
    {
        var dataDir = RestoreHardeningTests.TempDir();
        try
        {
            string stolen, live;
            await using (var app = await TestApp.StartAsync(dataDir: dataDir))
            {
                await app.SetupAdminAsync();
                var jar = new CookieContainer();
                var c = app.Client(jar: jar);
                Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("api/auth/login",
                    new { email = TestApp.AdminEmail, password = TestApp.AdminPassword })).StatusCode);
                stolen = jar.GetCookieHeader(new Uri("http://localhost/"));
                Assert.Contains("cpm_session=", stolen);

                var thief = app.Client(jar: new CookieContainer());
                thief.DefaultRequestHeaders.Add("Cookie", stolen);
                Assert.Equal(HttpStatusCode.OK, (await thief.GetAsync("api/auth/me")).StatusCode);

                Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsync("api/auth/logout", null)).StatusCode);
                Assert.Equal(HttpStatusCode.Unauthorized, (await thief.GetAsync("api/auth/me")).StatusCode);

                // A new sign-in still works.
                var jar2 = new CookieContainer();
                Assert.Equal(HttpStatusCode.OK, (await app.Client(jar: jar2).PostAsJsonAsync("api/auth/login",
                    new { email = TestApp.AdminEmail, password = TestApp.AdminPassword })).StatusCode);
                live = jar2.GetCookieHeader(new Uri("http://localhost/"));
            }

            // Revocation is persisted (same data dir and key ring).
            await using (var app = await TestApp.StartAsync(dataDir: dataDir))
            {
                var thief = app.Client(jar: new CookieContainer());
                thief.DefaultRequestHeaders.Add("Cookie", stolen);
                Assert.Equal(HttpStatusCode.Unauthorized, (await thief.GetAsync("api/auth/me")).StatusCode);
                var owner = app.Client(jar: new CookieContainer());
                owner.DefaultRequestHeaders.Add("Cookie", live);
                Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("api/auth/me")).StatusCode);
            }
        }
        finally { RestoreHardeningTests.Cleanup(dataDir); }
    }
}
