using System.Net;
using System.Net.Http.Json;
using CaddyManager.Core;
using CaddyManager.Core.Models;

namespace CaddyManager.Ops.Tests;

public class SetupTests
{
    [Fact]
    public async Task Setup_requires_token_and_runs_only_once()
    {
        await using var app = await TestApp.StartAsync();
        var anon = app.Client();

        var status = await (await anon.GetAsync("api/setup/status")).JsonAsync();
        Assert.True(status.GetProperty("needsSetup").GetBoolean());
        Assert.Equal(app.Paths.SetupTokenFile, status.GetProperty("setupTokenPath").GetString());
        Assert.True(File.Exists(app.Paths.SetupTokenFile));
        var token = app.ReadSetupToken();
        Assert.True(token.Length >= 32);

        // wrong token
        var bad = await anon.PostAsJsonAsync("api/setup", new { token = "nope", email = "a@b.com", name = "A", password = "long enough password" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("application/problem+json", bad.Content.Headers.ContentType?.MediaType);

        // weak password
        var weak = await anon.PostAsJsonAsync("api/setup", new { token, email = "a@b.com", name = "A", password = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.True((await weak.JsonAsync()).GetProperty("errors").TryGetProperty("password", out _));

        var ok = await anon.PostAsJsonAsync("api/setup", new { token, email = "Admin@Example.com", name = "Admin", password = "long enough password" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var user = await ok.JsonAsync();
        Assert.Equal("admin@example.com", user.GetProperty("email").GetString());
        Assert.Equal("admin", user.GetProperty("role").GetString());
        Assert.False(user.TryGetProperty("passwordHash", out _));
        Assert.False(File.Exists(app.Paths.SetupTokenFile));

        // signed in by setup
        var me = await anon.GetAsync("api/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        // second setup refused even with the old token
        var again = await app.Client().PostAsJsonAsync("api/setup", new { token, email = "x@b.com", name = "X", password = "long enough password" });
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
        Assert.False((await (await anon.GetAsync("api/setup/status")).JsonAsync()).GetProperty("needsSetup").GetBoolean());

        var audit = app.Store.Col<AuditEntry>().FindAll().ToList();
        Assert.Contains(audit, a => a.Action == "setup" && a.UserName == "admin@example.com");
    }
}

public class AuthTests
{
    [Fact]
    public async Task Login_me_logout_roundtrip()
    {
        await using var app = await TestApp.StartAsync();
        await app.SetupAdminAsync();
        var c = app.Client();

        var anonMe = await c.GetAsync("api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, anonMe.StatusCode);
        Assert.Equal("application/problem+json", anonMe.Content.Headers.ContentType?.MediaType);

        var wrong = await c.PostAsJsonAsync("api/auth/login", new { email = TestApp.AdminEmail, password = "wrong password!!" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("Invalid credentials", (await wrong.JsonAsync()).GetProperty("title").GetString());

        var unknown = await c.PostAsJsonAsync("api/auth/login", new { email = "nobody@example.com", password = "whatever password" });
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);

        var login = await c.PostAsJsonAsync("api/auth/login", new { email = "ADMIN@example.com", password = TestApp.AdminPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var setCookie = string.Join(";", login.Headers.GetValues("Set-Cookie"));
        Assert.Contains("cpm_session=", setCookie);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);

        var me = await (await c.GetAsync("api/auth/me")).JsonAsync();
        Assert.Equal(TestApp.AdminEmail, me.GetProperty("email").GetString());
        Assert.True(me.TryGetProperty("lastLoginAt", out _));

        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsync("api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("api/auth/me")).StatusCode);

        var audit = app.Store.Col<AuditEntry>().FindAll().ToList();
        Assert.Contains(audit, a => a.Action == "loginFailed" && a.ObjectName == TestApp.AdminEmail && a.UserName == "anonymous");
        Assert.Contains(audit, a => a.Action == "login" && a.UserName == TestApp.AdminEmail);
        Assert.Contains(audit, a => a.Action == "logout");
    }

    [Fact]
    public async Task Session_survives_manager_restart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cpm-ops-tests", Guid.NewGuid().ToString("N"));
        var jar = new System.Net.CookieContainer();
        try
        {
            await using (var first = await TestApp.StartAsync(dataDir: dir))
            {
                first.CreateUser("v@example.com", UserRole.Viewer);
                var c = first.Client(jar: jar);
                Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("api/auth/login", new { email = "v@example.com", password = "another long password" })).StatusCode);
            }
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(dir, "keys"), "*.xml"));
            await using var second = await TestApp.StartAsync(dataDir: dir);
            Assert.Equal(HttpStatusCode.OK, (await second.Client(jar: jar).GetAsync("api/auth/me")).StatusCode);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task Disabled_user_cannot_login()
    {
        await using var app = await TestApp.StartAsync();
        app.CreateUser("off@example.com", UserRole.Viewer, disabled: true);
        var resp = await app.Client().PostAsJsonAsync("api/auth/login", new { email = "off@example.com", password = "another long password" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Change_password_invalidates_other_sessions_but_keeps_current()
    {
        await using var app = await TestApp.StartAsync(o => o.PrincipalCacheDuration = TimeSpan.Zero);
        app.CreateUser("op@example.com", UserRole.Operator);
        var s1 = await app.LoginAsync("op@example.com");
        var s2 = await app.LoginAsync("op@example.com");

        var tooShort = await s1.PostAsJsonAsync("api/auth/change-password", new { currentPassword = "another long password", newPassword = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        var wrongCurrent = await s1.PostAsJsonAsync("api/auth/change-password", new { currentPassword = "nope nope nope", newPassword = "a brand new password" });
        Assert.Equal(HttpStatusCode.BadRequest, wrongCurrent.StatusCode);

        var ok = await s1.PostAsJsonAsync("api/auth/change-password", new { currentPassword = "another long password", newPassword = "a brand new password" });
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await s1.GetAsync("api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s2.GetAsync("api/auth/me")).StatusCode);
        await app.LoginAsync("op@example.com", "a brand new password");
        var u = app.Store.Col<User>().FindOne(x => x.Email == "op@example.com");
        Assert.Equal(1, u.SecurityStamp);
    }

    [Fact]
    public async Task Roles_map_to_401_and_403()
    {
        await using var app = await TestApp.StartAsync();
        app.CreateUser("viewer@example.com", UserRole.Viewer);
        app.CreateUser("op@example.com", UserRole.Operator);
        var anon = app.Client();
        var viewer = await app.LoginAsync("viewer@example.com");
        var op = await app.LoginAsync("op@example.com");

        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("api/events")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("api/events")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("api/dashboard")).StatusCode);

        var forbidden = await viewer.GetAsync("api/users");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal("application/problem+json", forbidden.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync("api/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync("api/settings/notifications")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("api/logs/manager")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("api/logs/caddy")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("api/backup")).StatusCode);
    }

    [Fact]
    public async Task Role_change_applies_to_live_session_and_disable_signs_out()
    {
        await using var app = await TestApp.StartAsync(o => o.PrincipalCacheDuration = TimeSpan.Zero);
        var admin = await app.SetupAdminAsync();
        var u = app.CreateUser("v@example.com", UserRole.Viewer);
        var v = await app.LoginAsync("v@example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await v.GetAsync("api/users")).StatusCode);

        var promote = await admin.PutJsonAsync($"api/users/{u.Id}", new { email = "v@example.com", name = "V", role = "admin", disabled = false });
        Assert.Equal(HttpStatusCode.OK, promote.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await v.GetAsync("api/users")).StatusCode);

        var disable = await admin.PutJsonAsync($"api/users/{u.Id}", new { email = "v@example.com", name = "V", role = "viewer", disabled = true });
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await v.GetAsync("api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Csrf_header_required_for_unsafe_api_methods()
    {
        await using var app = await TestApp.StartAsync();
        await app.SetupAdminAsync();
        var noHeader = app.Client(csrfHeader: false);

        // login is exempt
        var login = await noHeader.PostAsJsonAsync("api/auth/login", new { email = TestApp.AdminEmail, password = TestApp.AdminPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var logout = await noHeader.PostAsync("api/auth/logout", null);
        Assert.Equal(HttpStatusCode.BadRequest, logout.StatusCode);
        var problem = await logout.JsonAsync();
        Assert.Contains("X-CPM-Request", problem.GetProperty("detail").GetString());

        var put = await noHeader.PutAsJsonAsync("api/settings/ui", new { port = 81 });
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);

        // GET is fine without the header
        Assert.Equal(HttpStatusCode.OK, (await noHeader.GetAsync("api/auth/me")).StatusCode);

        var req = new HttpRequestMessage(HttpMethod.Post, "api/auth/logout");
        req.Headers.Add("X-CPM-Request", "1");
        Assert.Equal(HttpStatusCode.NoContent, (await noHeader.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Login_is_rate_limited_per_ip()
    {
        await using var app = await TestApp.StartAsync();
        app.CreateUser("x@example.com", UserRole.Viewer);
        var attacker = app.Client(remoteIp: "203.0.113.9");
        for (var i = 0; i < 10; i++)
        {
            var r = await attacker.PostAsJsonAsync("api/auth/login", new { email = "x@example.com", password = "wrong password " + i });
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        }
        var limited = await attacker.PostAsJsonAsync("api/auth/login", new { email = "x@example.com", password = "another long password" });
        Assert.Equal((HttpStatusCode)429, limited.StatusCode);
        Assert.Equal("application/problem+json", limited.Content.Headers.ContentType?.MediaType);

        // another address is unaffected
        var other = app.Client(remoteIp: "198.51.100.7");
        var ok = await other.PostAsJsonAsync("api/auth/login", new { email = "x@example.com", password = "another long password" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        // other endpoints from the limited address still work
        Assert.Equal(HttpStatusCode.OK, (await attacker.GetAsync("api/setup/status")).StatusCode);
    }
}
