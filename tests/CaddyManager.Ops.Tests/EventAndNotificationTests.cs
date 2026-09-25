using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using CaddyManager.Ops.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace CaddyManager.Ops.Tests;

public class EventSinkTests
{
    private static void EnableChannels(TestApp app, Action<NotificationSettings>? tweak = null)
    {
        var s = app.Store.GetSettings<NotificationSettings>();
        s.CooldownMinutes = 30;
        tweak?.Invoke(s);
        app.Store.SaveSettings(s);
    }

    [Fact]
    public async Task Cooldown_suppresses_duplicates_and_recovery_notifies_once()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var app = await TestApp.StartAsync(time: time);
        EnableChannels(app);
        var sink = app.Services.GetRequiredService<IEventSink>();
        var notifier = app.Notifier!;

        sink.Raise(EventSeverity.Error, "caddy", "Caddy is not running", key: "caddy-down", alertRule: "caddyDown");
        await HttpExtensions.WaitUntilAsync(() => notifier.Sent.Count == 1);

        // same key within cooldown: not persisted, not notified
        sink.Raise(EventSeverity.Error, "caddy", "Caddy is not running", key: "caddy-down", alertRule: "caddyDown");
        time.Advance(TimeSpan.FromMinutes(10));
        sink.Raise(EventSeverity.Warning, "caddy", "Caddy is not running (lower)", key: "caddy-down", alertRule: "caddyDown");
        await Task.Delay(200);
        Assert.Single(notifier.Sent);
        Assert.Equal(1, app.Store.Col<EventEntry>().Count(e => e.Key == "caddy-down"));
        var first = app.Store.Col<EventEntry>().FindOne(e => e.Key == "caddy-down");
        await HttpExtensions.WaitUntilAsync(() => app.Store.Col<EventEntry>().FindById(first.Id).Notified);

        // recovery → one notice
        sink.Raise(EventSeverity.Recovered, "caddy", "Caddy is running again", key: "caddy-down", alertRule: "caddyDown");
        await HttpExtensions.WaitUntilAsync(() => notifier.Sent.Count == 2);
        Assert.Contains("Recovered", notifier.Sent.Last().Subject);
        sink.Raise(EventSeverity.Recovered, "caddy", "Caddy is running again", key: "caddy-down", alertRule: "caddyDown");
        await Task.Delay(200);
        Assert.Equal(2, notifier.Sent.Count);
        Assert.Equal(2, app.Store.Col<EventEntry>().Count(e => e.Key == "caddy-down"));

        // after recovery a new failure notifies immediately (no cooldown carry-over)
        sink.Raise(EventSeverity.Error, "caddy", "Caddy is not running", key: "caddy-down", alertRule: "caddyDown");
        await HttpExtensions.WaitUntilAsync(() => notifier.Sent.Count == 3);

        // cooldown expiry re-notifies (reminder)
        time.Advance(TimeSpan.FromMinutes(31));
        sink.Raise(EventSeverity.Error, "caddy", "Caddy is not running", key: "caddy-down", alertRule: "caddyDown");
        await HttpExtensions.WaitUntilAsync(() => notifier.Sent.Count == 4);

        // escalation within cooldown is not suppressed
        sink.Raise(EventSeverity.Warning, "upstream", "slow", key: "upstream:a", alertRule: "upstreamUnhealthy");
        sink.Raise(EventSeverity.Error, "upstream", "down", key: "upstream:a", alertRule: "upstreamUnhealthy");
        await HttpExtensions.WaitUntilAsync(() => notifier.Sent.Count == 6);
    }

    [Fact]
    public async Task Disabled_alert_rule_records_but_does_not_notify()
    {
        await using var app = await TestApp.StartAsync();
        EnableChannels(app, s => s.AlertUpstreamUnhealthy = false);
        var sink = app.Services.GetRequiredService<IEventSink>();
        sink.Raise(EventSeverity.Warning, "upstream", "Upstream 10.0.0.1:80 is unhealthy", key: "upstream:10.0.0.1:80", alertRule: "upstreamUnhealthy");
        sink.Raise(EventSeverity.Info, "misc", "no rule");
        sink.Raise(EventSeverity.Error, "misc", "unknown rule", alertRule: "somethingElse");
        await Task.Delay(300);
        Assert.Empty(app.Notifier!.Sent);
        Assert.Equal(3, app.Store.Col<EventEntry>().Count());
    }

    [Fact]
    public async Task Alert_state_survives_restart_so_recovery_is_sent()
    {
        await using var app = await TestApp.StartAsync();
        EnableChannels(app);
        // Simulate an alert persisted by a previous manager process.
        app.Store.Col<EventEntry>().Insert(new EventEntry { Severity = EventSeverity.Warning, Category = "upstream", Message = "down", Key = "upstream:x:1" });
        var sink = app.Services.GetRequiredService<IEventSink>();
        Assert.True(((IAlertState)sink).IsActive("upstream:x:1"));
        sink.Raise(EventSeverity.Recovered, "upstream", "up", key: "upstream:x:1", alertRule: "upstreamUnhealthy");
        await HttpExtensions.WaitUntilAsync(() => app.Notifier!.Sent.Count == 1);
    }

    [Fact]
    public async Task Notification_failure_is_recorded_without_recursion()
    {
        await using var app = await TestApp.StartAsync();
        EnableChannels(app);
        app.Notifier!.Fail = true;
        var sink = app.Services.GetRequiredService<IEventSink>();
        sink.Raise(EventSeverity.Error, "config", "Config apply failed", key: "config-apply", alertRule: "configFailure");
        await HttpExtensions.WaitUntilAsync(() => app.Store.Col<EventEntry>().Count(e => e.Category == "notification") == 1);
        await Task.Delay(300);
        Assert.Equal(1, app.Notifier.Calls);
        var failure = app.Store.Col<EventEntry>().FindOne(e => e.Category == "notification");
        Assert.Contains("connection refused", failure.Details);
        Assert.False(app.Store.Col<EventEntry>().FindOne(e => e.Category == "config").Notified);
    }
}

public class NotificationChannelTests
{
    [Fact]
    public async Task Smtp_test_endpoint_sends_text_and_html_mail()
    {
        await using var smtp = new SmtpTestServer();
        await using var app = await TestApp.StartAsync(fakeNotifier: false);
        var admin = await app.SetupAdminAsync();

        var put = await admin.PutJsonAsync("api/settings/notifications", new
        {
            smtpEnabled = true,
            smtpHost = "127.0.0.1",
            smtpPort = smtp.Port,
            smtpSecurity = "none",
            smtpFrom = "cpm@example.com",
            recipients = new[] { "ops@example.com", "oncall@example.com" },
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var test = await (await admin.PostAsJsonAsync("api/settings/notifications/test", new { })).JsonAsync();
        Assert.True(test.GetProperty("ok").GetBoolean(), test.ToString());

        await HttpExtensions.WaitUntilAsync(() => smtp.Messages.Count == 1);
        smtp.Messages.TryPeek(out var received);
        Assert.Equal("cpm@example.com", received!.From);
        Assert.Equal(["ops@example.com", "oncall@example.com"], received.To);
        var msg = MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(received.Data)));
        Assert.Contains("Test notification", msg.Subject);
        Assert.Contains("Server:", msg.TextBody);
        Assert.Contains("UTC", msg.TextBody);
        Assert.NotNull(msg.HtmlBody);
        Assert.Contains("<table", msg.HtmlBody);

        // Events go through the real notifier as well.
        app.Services.GetRequiredService<IEventSink>().Raise(EventSeverity.Error, "caddy", "Caddy <down> & out", "details line",
            "caddy-down", "caddyDown");
        await HttpExtensions.WaitUntilAsync(() => smtp.Messages.Count == 2);
        var ev = MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(smtp.Messages.Last().Data)));
        Assert.Contains("Error: Caddy <down> & out", ev.Subject);
        Assert.Contains("Caddy &lt;down&gt; &amp; out", ev.HtmlBody);
        Assert.Contains("details line", ev.TextBody);
    }

    [Fact]
    public async Task Test_endpoint_reports_errors_for_unreachable_smtp_and_no_channels()
    {
        await using var app = await TestApp.StartAsync(fakeNotifier: false);
        var admin = await app.SetupAdminAsync();
        var none = await (await admin.PostAsJsonAsync("api/settings/notifications/test", new { })).JsonAsync();
        Assert.False(none.GetProperty("ok").GetBoolean());

        // grab a free port and close it so the connection is refused
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        await admin.PutJsonAsync("api/settings/notifications", new
        {
            smtpEnabled = true, smtpHost = "127.0.0.1", smtpPort = port, smtpSecurity = "none",
            smtpFrom = "cpm@example.com", recipients = new[] { "ops@example.com" },
        });
        var res = await (await admin.PostAsJsonAsync("api/settings/notifications/test", new { })).JsonAsync();
        Assert.False(res.GetProperty("ok").GetBoolean());
        Assert.Contains("SMTP", res.GetProperty("errors")[0].GetString());
    }

    [Fact]
    public async Task Webhook_posts_json_with_text_field()
    {
        var received = new ConcurrentQueue<string>();
        var hook = WebApplication.CreateBuilder();
        hook.Logging.ClearProviders();
        hook.WebHost.UseUrls("http://127.0.0.1:0");
        await using var hookApp = hook.Build();
        hookApp.MapPost("/hook", async (HttpRequest r) =>
        {
            using var sr = new StreamReader(r.Body);
            received.Enqueue(await sr.ReadToEndAsync());
            return Results.Ok();
        });
        hookApp.MapPost("/fail", () => Results.StatusCode(500));
        await hookApp.StartAsync();
        var url = hookApp.Urls.First();

        await using var app = await TestApp.StartAsync(fakeNotifier: false);
        var admin = await app.SetupAdminAsync();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutJsonAsync("api/settings/notifications", new { webhookEnabled = true, webhookUrl = url + "/hook" })).StatusCode);
        var res = await (await admin.PostAsJsonAsync("api/settings/notifications/test", new { })).JsonAsync();
        Assert.True(res.GetProperty("ok").GetBoolean(), res.ToString());
        Assert.True(received.TryDequeue(out var body));
        var json = JsonDocument.Parse(body!).RootElement;
        foreach (var f in new[] { "title", "text", "severity", "category", "server", "time" })
            Assert.True(json.TryGetProperty(f, out _), $"missing {f}");
        Assert.Contains("Test notification", json.GetProperty("title").GetString());

        await admin.PutJsonAsync("api/settings/notifications", new { webhookUrl = url + "/fail" });
        var fail = await (await admin.PostAsJsonAsync("api/settings/notifications/test", new { })).JsonAsync();
        Assert.False(fail.GetProperty("ok").GetBoolean());
        Assert.Contains("500", fail.GetProperty("errors")[0].GetString());
        await hookApp.StopAsync();
    }

    [Fact]
    public async Task Notification_settings_redact_secrets()
    {
        await using var app = await TestApp.StartAsync();
        var admin = await app.SetupAdminAsync();

        var initial = await (await admin.GetAsync("api/settings/notifications")).JsonAsync();
        Assert.False(initial.GetProperty("hasSmtpPassword").GetBoolean());
        Assert.False(initial.TryGetProperty("smtpPasswordProtected", out _));
        Assert.Equal("startTls", initial.GetProperty("smtpSecurity").GetString());

        var put = await admin.PutJsonAsync("api/settings/notifications", new
        {
            smtpUsername = "mailer", smtpPassword = "s3cret-value", smtpPasswordProtected = "attacker-controlled", hasSmtpPassword = false,
            smtpSecurity = "sslOnConnect", cooldownMinutes = 5,
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var text = await put.Content.ReadAsStringAsync();
        Assert.DoesNotContain("s3cret-value", text);
        Assert.DoesNotContain("smtpPasswordProtected", text);
        var body = JsonDocument.Parse(text).RootElement;
        Assert.True(body.GetProperty("hasSmtpPassword").GetBoolean());
        Assert.Equal("sslOnConnect", body.GetProperty("smtpSecurity").GetString());

        var stored = app.Store.GetSettings<NotificationSettings>();
        Assert.NotEqual("s3cret-value", stored.SmtpPasswordProtected);
        Assert.NotEqual("attacker-controlled", stored.SmtpPasswordProtected);
        Assert.Equal("s3cret-value", app.Services.GetRequiredService<ISecretProtector>().Unprotect(stored.SmtpPasswordProtected!));
        Assert.Equal(5, stored.CooldownMinutes);

        // absent/null = unchanged
        await admin.PutJsonAsync("api/settings/notifications", new { smtpPassword = (string?)null, cooldownMinutes = 7 });
        Assert.NotNull(app.Store.GetSettings<NotificationSettings>().SmtpPasswordProtected);
        Assert.Equal("mailer", app.Store.GetSettings<NotificationSettings>().SmtpUsername);
        // "" = clear
        var cleared = await (await admin.PutJsonAsync("api/settings/notifications", new { smtpPassword = "" })).JsonAsync();
        Assert.False(cleared.GetProperty("hasSmtpPassword").GetBoolean());
        Assert.Null(app.Store.GetSettings<NotificationSettings>().SmtpPasswordProtected);

        // validation
        var invalid = await admin.PutJsonAsync("api/settings/notifications", new { smtpEnabled = true, smtpHost = "", recipients = new[] { "nope" } });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var badType = await admin.PutJsonAsync("api/settings/notifications", new { smtpPort = "abc" });
        Assert.Equal(HttpStatusCode.BadRequest, badType.StatusCode);

        var audit = app.Store.Col<AuditEntry>().FindAll().Where(a => a.ObjectType == "settings").ToList();
        Assert.NotEmpty(audit);
        Assert.DoesNotContain(audit, a => (a.Details ?? "").Contains("s3cret"));
    }

    [Fact]
    public async Task Ui_settings_report_restart_required()
    {
        await using var app = await TestApp.StartAsync();
        var admin = await app.SetupAdminAsync();
        var get = await (await admin.GetAsync("api/settings/ui")).JsonAsync();
        Assert.Equal(81, get.GetProperty("port").GetInt32());
        Assert.False(get.GetProperty("hasHttpsPfxPassword").GetBoolean());

        var sessionOnly = await (await admin.PutJsonAsync("api/settings/ui", new { sessionHours = 8, displayName = "EDGE01" })).JsonAsync();
        Assert.False(sessionOnly.GetProperty("restartRequired").GetBoolean());
        Assert.Equal(8, sessionOnly.GetProperty("item").GetProperty("sessionHours").GetInt32());

        var port = await (await admin.PutJsonAsync("api/settings/ui", new { port = 8081 })).JsonAsync();
        Assert.True(port.GetProperty("restartRequired").GetBoolean());
        Assert.Equal(8081, app.Store.GetSettings<UiSettings>().Port);

        var bad = await admin.PutJsonAsync("api/settings/ui", new { bindAddress = "not-an-ip", port = 70000 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var badPfx = await admin.PutJsonAsync("api/settings/ui", new { httpsEnabled = true, httpsPfxPath = "/nonexistent/ui.pfx" });
        Assert.Equal(HttpStatusCode.BadRequest, badPfx.StatusCode);

        var pwd = await (await admin.PutJsonAsync("api/settings/ui", new { httpsPfxPassword = "pfx-pass" })).JsonAsync();
        Assert.True(pwd.GetProperty("restartRequired").GetBoolean());
        Assert.True(pwd.GetProperty("item").GetProperty("hasHttpsPfxPassword").GetBoolean());
        Assert.False(pwd.GetProperty("item").TryGetProperty("httpsPfxPasswordProtected", out _));
    }
}
