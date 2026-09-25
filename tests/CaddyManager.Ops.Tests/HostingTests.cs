using System.Net;
using System.Net.Http.Json;
using CaddyManager.Core.Models;
using CaddyManager.Ops.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace CaddyManager.Ops.Tests;

public class ForwardedHeadersTests
{
    private static Task<TestApp> StartAsync() =>
        TestApp.StartAsync(o => o.LoginAttemptsPerMinute = 2, pipeline: a => a.UseLoopbackForwardedHeaders());

    private static Task<HttpResponseMessage> BadLogin(HttpClient c, string? forwardedFor = null, string? proto = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "api/auth/login") { Content = JsonContent.Create(new { email = "x@example.com", password = "wrong" }) };
        if (forwardedFor is not null) req.Headers.Add("X-Forwarded-For", forwardedFor);
        if (proto is not null) req.Headers.Add("X-Forwarded-Proto", proto);
        return c.SendAsync(req);
    }

    [Fact]
    public async Task Rate_limiter_uses_the_forwarded_client_only_behind_a_loopback_proxy()
    {
        await using var app = await StartAsync();
        var viaCaddy = app.Client(remoteIp: "127.0.0.1");
        Assert.Equal(HttpStatusCode.Unauthorized, (await BadLogin(viaCaddy, "203.0.113.5")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await BadLogin(viaCaddy, "203.0.113.5")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await BadLogin(viaCaddy, "203.0.113.5")).StatusCode);
        // Another client behind the same local proxy has its own budget.
        Assert.Equal(HttpStatusCode.Unauthorized, (await BadLogin(viaCaddy, "203.0.113.6")).StatusCode);
        Assert.Contains(app.Store.Col<AuditEntry>().FindAll(), a => a.Action == "loginFailed" && a.RemoteIp == "203.0.113.5");

        // A remote peer cannot choose its partition (or its logged address) by sending X-Forwarded-For.
        var remote = app.Client(remoteIp: "198.51.100.7");
        Assert.Equal(HttpStatusCode.Unauthorized, (await BadLogin(remote, "192.0.2.1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await BadLogin(remote, "192.0.2.2")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await BadLogin(remote, "192.0.2.3")).StatusCode);
        Assert.DoesNotContain(app.Store.Col<AuditEntry>().FindAll(), a => (a.RemoteIp ?? "").StartsWith("192.0.2.", StringComparison.Ordinal));
        Assert.Contains(app.Store.Col<AuditEntry>().FindAll(), a => a.RemoteIp == "198.51.100.7");

        // IPv4-mapped loopback (dual-stack sockets) is loopback too.
        var mapped = app.Client(remoteIp: "::ffff:127.0.0.1");
        await BadLogin(mapped, "203.0.113.77");
        Assert.Contains(app.Store.Col<AuditEntry>().FindAll(), a => a.RemoteIp == "203.0.113.77");
    }

    [Fact]
    public async Task Forwarded_proto_from_loopback_marks_the_session_cookie_secure()
    {
        await using var app = await StartAsync();
        await app.SetupAdminAsync();
        async Task<string> CookieFor(string ip, string proto)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "api/auth/login")
            {
                Content = JsonContent.Create(new { email = TestApp.AdminEmail, password = TestApp.AdminPassword }),
            };
            req.Headers.Add("X-Forwarded-Proto", proto);
            req.Headers.Add("X-Forwarded-For", "203.0.113.9");
            var resp = await app.Client(remoteIp: ip).SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            return string.Join(";", resp.Headers.GetValues("Set-Cookie")).ToLowerInvariant();
        }
        Assert.Contains("secure", await CookieFor("127.0.0.1", "https"));
        Assert.DoesNotContain("secure", await CookieFor("198.51.100.8", "https"));
    }
}

public class HttpsRedirectTests
{
    private static Task<TestApp> StartAsync() => TestApp.StartAsync(
        pipeline: a =>
        {
            a.UseLoopbackForwardedHeaders();
            a.UseUiHttpsRedirect(8443);
        },
        mapExtra: a => a.MapGet("/api/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous());

    [Fact]
    public async Task Plain_http_is_redirected_except_health()
    {
        await using var app = await StartAsync();
        var http = app.Client();
        var resp = await http.GetAsync("api/auth/me?x=1");
        Assert.Equal(HttpStatusCode.TemporaryRedirect, resp.StatusCode);
        Assert.Equal("https://localhost:8443/api/auth/me?x=1", resp.Headers.Location!.ToString());

        var post = await http.PostAsJsonAsync("api/auth/login", new { email = "a@b.c", password = "x" });
        Assert.Equal(HttpStatusCode.TemporaryRedirect, post.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("api/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Client(https: true).GetAsync("api/auth/me")).StatusCode);

        // Published through Caddy over HTTPS (loopback proxy says so): no redirect loop.
        var viaCaddy = new HttpRequestMessage(HttpMethod.Get, "api/auth/me");
        viaCaddy.Headers.Add("X-Forwarded-Proto", "https");
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Client(remoteIp: "127.0.0.1").SendAsync(viaCaddy)).StatusCode);
        // ...but a remote client cannot skip the redirect with the header.
        var spoofed = new HttpRequestMessage(HttpMethod.Get, "api/auth/me");
        spoofed.Headers.Add("X-Forwarded-Proto", "https");
        Assert.Equal(HttpStatusCode.TemporaryRedirect, (await app.Client(remoteIp: "198.51.100.9").SendAsync(spoofed)).StatusCode);
    }

    [Fact]
    public async Task Redirect_flag_change_requires_restart()
    {
        await using var app = await TestApp.StartAsync();
        var admin = await app.SetupAdminAsync();
        var get = await (await admin.GetAsync("api/settings/ui")).JsonAsync();
        Assert.False(get.GetProperty("redirectHttpToHttps").GetBoolean());
        var put = await (await admin.PutJsonAsync("api/settings/ui", new { redirectHttpToHttps = true })).JsonAsync();
        Assert.True(put.GetProperty("restartRequired").GetBoolean());
        Assert.True(put.GetProperty("item").GetProperty("redirectHttpToHttps").GetBoolean());
        Assert.True(app.Store.GetSettings<UiSettings>().RedirectHttpToHttps);
    }
}
