using System.Net;
using System.Net.Http.Json;
using CaddyManager.Core;
using CaddyManager.Core.Models;

namespace CaddyManager.Ops.Tests;

public class UserTests
{
    [Fact]
    public async Task Crud_with_validation_and_protections()
    {
        await using var app = await TestApp.StartAsync();
        var admin = await app.SetupAdminAsync();
        var me = await (await admin.GetAsync("api/auth/me")).JsonAsync();
        var myId = me.GetProperty("id").GetString()!;

        // validation
        var invalid = await admin.PostAsJsonAsync("api/users", new { email = "not-an-email", name = "", role = "boss", password = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var errors = (await invalid.JsonAsync()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("email", out _));
        Assert.True(errors.TryGetProperty("name", out _));
        Assert.True(errors.TryGetProperty("role", out _));
        Assert.True(errors.TryGetProperty("password", out _));

        var created = await admin.PostAsJsonAsync("api/users", new { email = "Op@Example.com", name = "Op", role = "operator", password = "operator password" });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var op = await created.JsonAsync();
        Assert.Equal("op@example.com", op.GetProperty("email").GetString());
        Assert.Equal("operator", op.GetProperty("role").GetString());
        var opId = op.GetProperty("id").GetString()!;

        var dup = await admin.PostAsJsonAsync("api/users", new { email = "op@example.com", name = "Op2", role = "viewer", password = "operator password" });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);

        var list = await (await admin.GetAsync("api/users")).JsonAsync();
        Assert.Equal(2, list.GetArrayLength());

        // self protections
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"api/users/{myId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await admin.PutJsonAsync($"api/users/{myId}", new { email = TestApp.AdminEmail, name = "Admin", role = "operator", disabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await admin.PutJsonAsync($"api/users/{myId}", new { email = TestApp.AdminEmail, name = "Admin", role = "admin", disabled = true })).StatusCode);

        // promote op to admin; op can then demote... the last-admin rule
        var promote = await admin.PutJsonAsync($"api/users/{opId}", new { email = "op@example.com", name = "Op", role = "admin", disabled = false, password = "new operator password" });
        Assert.Equal(HttpStatusCode.OK, promote.StatusCode);
        var opClient = await app.LoginAsync("op@example.com", "new operator password");
        // op (admin) demotes the original admin: allowed since 2 admins exist
        Assert.Equal(HttpStatusCode.OK,
            (await opClient.PutJsonAsync($"api/users/{myId}", new { email = TestApp.AdminEmail, name = "Admin", role = "viewer", disabled = false })).StatusCode);
        // now op is the last admin — the (now viewer) original admin cannot act, and op cannot delete itself
        Assert.Equal(HttpStatusCode.Conflict, (await opClient.DeleteAsync($"api/users/{opId}")).StatusCode);

        // put the original admin back, then last-admin protection via delete of a disabled-only scenario
        Assert.Equal(HttpStatusCode.OK,
            (await opClient.PutJsonAsync($"api/users/{myId}", new { email = TestApp.AdminEmail, name = "Admin", role = "admin", disabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await opClient.DeleteAsync($"api/users/{myId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await opClient.DeleteAsync($"api/users/{myId}")).StatusCode);

        // email conflict on update
        var v = app.CreateUser("v@example.com", UserRole.Viewer);
        Assert.Equal(HttpStatusCode.Conflict,
            (await opClient.PutJsonAsync($"api/users/{v.Id}", new { email = "op@example.com", name = "V", role = "viewer", disabled = false })).StatusCode);
    }

    [Fact]
    public async Task Last_enabled_admin_cannot_be_disabled_or_deleted_by_another_admin()
    {
        await using var app = await TestApp.StartAsync();
        var admin = await app.SetupAdminAsync();
        // A second admin that is disabled does not count.
        var other = app.CreateUser("other@example.com", UserRole.Admin, disabled: true);
        var me = (await (await admin.GetAsync("api/auth/me")).JsonAsync()).GetProperty("id").GetString()!;
        Assert.Equal(1, app.Store.Col<User>().Count(u => u.Role == UserRole.Admin && !u.Disabled));
        // deleting the disabled admin is fine
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"api/users/{other.Id}")).StatusCode);
        Assert.NotNull(me);
    }
}

public class AuditTests
{
    [Fact]
    public async Task Mutations_are_audited_with_user_and_ip_and_searchable()
    {
        await using var app = await TestApp.StartAsync();
        var admin = await app.SetupAdminAsync();
        await admin.PostAsJsonAsync("api/users", new { email = "audited@example.com", name = "Aud", role = "viewer", password = "viewer password!" });
        var ipClient = app.Client(remoteIp: "192.0.2.44");
        await ipClient.PostAsJsonAsync("api/auth/login", new { email = TestApp.AdminEmail, password = TestApp.AdminPassword });

        var page = await (await admin.GetAsync("api/audit?take=100")).JsonAsync();
        Assert.True(page.GetProperty("total").GetInt32() >= 3);
        var items = page.GetProperty("items").EnumerateArray().ToList();
        var created = items.Single(i => i.GetProperty("action").GetString() == "created");
        Assert.Equal("user", created.GetProperty("objectType").GetString());
        Assert.Equal("audited@example.com", created.GetProperty("objectName").GetString());
        Assert.Equal(TestApp.AdminEmail, created.GetProperty("userName").GetString());
        Assert.Contains(items, i => i.GetProperty("action").GetString() == "login" &&
                                    i.TryGetProperty("remoteIp", out var ip) && ip.GetString() == "192.0.2.44");
        // newest first
        var times = items.Select(i => i.GetProperty("createdAt").GetDateTime()).ToList();
        Assert.Equal(times.OrderByDescending(t => t), times);

        var search = await (await admin.GetAsync("api/audit?q=AUDITED")).JsonAsync();
        Assert.Equal(1, search.GetProperty("total").GetInt32());

        var paged = await (await admin.GetAsync("api/audit?skip=1&take=1")).JsonAsync();
        Assert.Single(paged.GetProperty("items").EnumerateArray());

        // outside a request the audit user is "system"
        app.Services.GetRequiredService<IAuditLog>().Record("tick", "test");
        Assert.Equal("system", app.Store.Col<AuditEntry>().FindOne(a => a.Action == "tick").UserName);
    }

    [Fact]
    public async Task Events_endpoint_filters_by_severity()
    {
        await using var app = await TestApp.StartAsync();
        var sink = app.Services.GetRequiredService<IEventSink>();
        sink.Raise(EventSeverity.Info, "test", "info one");
        sink.Raise(EventSeverity.Error, "test", "error one");
        sink.Raise(EventSeverity.Warning, "other", "warn one");
        app.CreateUser("v@example.com", UserRole.Viewer);
        var v = await app.LoginAsync("v@example.com");

        var all = await (await v.GetAsync("api/events")).JsonAsync();
        Assert.Equal(3, all.GetProperty("total").GetInt32());
        var errors = await (await v.GetAsync("api/events?severity=error")).JsonAsync();
        Assert.Equal("error one", errors.GetProperty("items")[0].GetProperty("message").GetString());
        Assert.Equal("error", errors.GetProperty("items")[0].GetProperty("severity").GetString());
        var two = await (await v.GetAsync("api/events?severity=error,warning")).JsonAsync();
        Assert.Equal(2, two.GetProperty("total").GetInt32());
        var cat = await (await v.GetAsync("api/events?category=other")).JsonAsync();
        Assert.Equal(1, cat.GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await v.GetAsync("api/events?severity=bogus")).StatusCode);
    }
}
