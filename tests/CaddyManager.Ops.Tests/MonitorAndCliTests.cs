using System.Net;
using System.Net.Http.Json;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Infrastructure;
using CaddyManager.Core.Models;
using CaddyManager.Ops.Monitoring;

namespace CaddyManager.Ops.Tests;

public class MonitorTests
{
    private static List<EventEntry> Events(TestApp app, string? key = null) =>
        app.Store.Col<EventEntry>().FindAll().Where(e => key is null || e.Key == key).OrderBy(e => e.CreatedAt).ToList();

    [Fact]
    public async Task Caddy_down_raises_event_auto_restarts_and_recovers()
    {
        await using var app = await TestApp.StartAsync();
        var monitor = app.Services.GetRequiredService<MonitorService>();
        app.CaddyHost.Status = app.CaddyHost.Status with { State = CaddyRunState.Stopped, AdminReachable = false };

        await monitor.TickAsync(CancellationToken.None); // first failure: below threshold
        Assert.Empty(Events(app, MonitorService.CaddyDownKey));
        Assert.Equal(0, app.CaddyHost.StartCalls);

        app.CaddyHost.StartMakesHealthy = true;
        await monitor.TickAsync(CancellationToken.None);
        var down = Assert.Single(Events(app, MonitorService.CaddyDownKey));
        Assert.Equal(EventSeverity.Error, down.Severity);
        Assert.Equal(1, app.CaddyHost.StartCalls);
        await HttpExtensions.WaitUntilAsync(() => app.Notifier!.Sent.Count == 1);
        Assert.Contains(app.Store.Col<AuditEntry>().FindAll(), a => a.Action == "autoRestart" && a.UserName == "system");

        await monitor.TickAsync(CancellationToken.None); // healthy now
        var events = Events(app, MonitorService.CaddyDownKey);
        Assert.Equal(2, events.Count);
        Assert.Equal(EventSeverity.Recovered, events[1].Severity);
        await HttpExtensions.WaitUntilAsync(() => app.Notifier!.Sent.Count == 2);
    }

    [Fact]
    public async Task Auto_restart_is_limited()
    {
        await using var app = await TestApp.StartAsync(o => o.CaddyDownThreshold = 1);
        var monitor = app.Services.GetRequiredService<MonitorService>();
        app.CaddyHost.Status = app.CaddyHost.Status with { State = CaddyRunState.Stopped, AdminReachable = false };
        for (var i = 0; i < 6; i++) await monitor.TickAsync(CancellationToken.None);
        Assert.Equal(3, app.CaddyHost.StartCalls);
        Assert.Single(Events(app, MonitorService.AutoRestartLimitKey));
    }

    [Fact]
    public async Task No_alert_when_stopped_intentionally_or_not_installed()
    {
        await using var app = await TestApp.StartAsync(o => o.CaddyDownThreshold = 1);
        var monitor = app.Services.GetRequiredService<MonitorService>();
        app.CaddyHost.Status = app.CaddyHost.Status with { State = CaddyRunState.Stopped, AdminReachable = false, ServiceInstalled = false };
        await monitor.TickAsync(CancellationToken.None);
        Assert.Empty(Events(app, MonitorService.CaddyDownKey));

        app.CaddyHost.Status = app.CaddyHost.Status with { ServiceInstalled = true };
        app.Store.Col<AuditEntry>().Insert(new AuditEntry { Action = "stop", ObjectType = "caddy", UserName = "admin@example.com" });
        await monitor.TickAsync(CancellationToken.None);
        Assert.Empty(Events(app, MonitorService.CaddyDownKey));
        Assert.Equal(0, app.CaddyHost.StartCalls);

        app.Store.Col<AuditEntry>().Insert(new AuditEntry { Action = "start", ObjectType = "caddy", UserName = "admin@example.com", CreatedAt = DateTime.UtcNow.AddSeconds(1) });
        await monitor.TickAsync(CancellationToken.None);
        Assert.Single(Events(app, MonitorService.CaddyDownKey));
    }

    [Fact]
    public async Task Upstreams_certificates_and_readiness_raise_and_recover()
    {
        await using var app = await TestApp.StartAsync(o => o.CertificateCheckInterval = TimeSpan.Zero);
        var monitor = app.Services.GetRequiredService<MonitorService>();
        app.Admin.Upstreams = [new UpstreamHealth { Address = "10.0.0.5:8080", Healthy = false, Fails = 3 }];
        app.Certificates.Certificates =
        [
            new CertificateInfo { Id = "c1", Kind = CertificateKind.Custom, Name = "wildcard", DaysRemaining = 5, NotAfter = DateTime.UtcNow.AddDays(5), Subjects = ["*.example.com"] },
            new CertificateInfo { Id = "c2", Kind = CertificateKind.Internal, DaysRemaining = 0 },
        ];
        app.Readiness.NextReport = new ReadinessReport
        {
            RanAt = DateTime.UtcNow,
            Checks = [new ReadinessCheck { Id = "firewall.tcp443", Title = "Inbound TCP 443", Status = CheckStatus.Fail, Summary = "No rule" }],
        };

        await monitor.TickAsync(CancellationToken.None);
        Assert.Equal(EventSeverity.Warning, Assert.Single(Events(app, "upstream:10.0.0.5:8080")).Severity);
        Assert.Single(Events(app, "cert-expiry:c1"));
        Assert.Empty(Events(app, "cert-expiry:c2"));
        Assert.Single(Events(app, "readiness:firewall.tcp443"));
        Assert.Equal(1, app.Readiness.Runs);

        // unchanged state: nothing new (cooldown / only-new-failures), readiness not re-run within a day
        await monitor.TickAsync(CancellationToken.None);
        Assert.Single(Events(app, "upstream:10.0.0.5:8080"));
        Assert.Single(Events(app, "readiness:firewall.tcp443"));
        Assert.Equal(1, app.Readiness.Runs);

        // everything healthy
        app.Admin.Upstreams = [new UpstreamHealth { Address = "10.0.0.5:8080", Healthy = true }];
        app.Certificates.Certificates = [new CertificateInfo { Id = "c1", Kind = CertificateKind.Custom, DaysRemaining = 300 }];
        app.Readiness.LastReport = new ReadinessReport
        {
            RanAt = DateTime.UtcNow.AddSeconds(5),
            Checks = [new ReadinessCheck { Id = "firewall.tcp443", Title = "Inbound TCP 443", Status = CheckStatus.Pass }],
        };
        await monitor.TickAsync(CancellationToken.None);
        Assert.Equal(EventSeverity.Recovered, Events(app, "upstream:10.0.0.5:8080").Last().Severity);
        Assert.Equal(EventSeverity.Recovered, Events(app, "cert-expiry:c1").Last().Severity);
        Assert.Equal(EventSeverity.Recovered, Events(app, "readiness:firewall.tcp443").Last().Severity);
    }

    [Fact]
    public async Task Monitor_tolerates_throwing_dependencies()
    {
        await using var app = await TestApp.StartAsync(o => o.CertificateCheckInterval = TimeSpan.Zero);
        var monitor = app.Services.GetRequiredService<MonitorService>();
        app.CaddyHost.Throw = new InvalidOperationException("scm");
        app.Certificates.Throw = new IOException("share");
        app.Readiness.ThrowOnLastReport = new Exception("boom");
        await monitor.TickAsync(CancellationToken.None);
        Assert.Empty(Events(app));
    }

    [Fact]
    public async Task Background_monitor_runs_on_its_own()
    {
        await using var app = await TestApp.StartAsync(o =>
        {
            o.EnableBackgroundServices = true;
            o.MonitorStartDelay = TimeSpan.FromMilliseconds(10);
            o.MonitorInterval = TimeSpan.FromMilliseconds(50);
            o.CaddyDownThreshold = 1;
        });
        app.CaddyHost.Status = app.CaddyHost.Status with { State = CaddyRunState.Stopped, AdminReachable = false };
        await HttpExtensions.WaitUntilAsync(() => app.CaddyHost.StartCalls > 0);
    }

    [Fact]
    public async Task Retention_removes_old_events_and_audit()
    {
        await using var app = await TestApp.StartAsync();
        app.Store.Col<EventEntry>().Insert(new EventEntry { Message = "old", CreatedAt = DateTime.UtcNow.AddDays(-91) });
        app.Store.Col<EventEntry>().Insert(new EventEntry { Message = "new" });
        app.Store.Col<AuditEntry>().Insert(new AuditEntry { Action = "old", CreatedAt = DateTime.UtcNow.AddDays(-366) });
        app.Store.Col<AuditEntry>().Insert(new AuditEntry { Action = "recent", CreatedAt = DateTime.UtcNow.AddDays(-100) });
        var (ev, au) = app.Services.GetRequiredService<RetentionService>().RunOnce();
        Assert.Equal(1, ev);
        Assert.Equal(1, au);
        Assert.Equal("new", app.Store.Col<EventEntry>().FindAll().Single().Message);
        Assert.Equal("recent", app.Store.Col<AuditEntry>().FindAll().Single().Action);
    }
}

public class CliTests
{
    [Fact]
    public async Task Unknown_args_return_null()
    {
        Assert.Null(await OpsCli.TryRunAsync([]));
        Assert.Null(await OpsCli.TryRunAsync(["install"]));
        Assert.Null(await OpsCli.TryRunAsync(["--urls", "http://localhost:5000"]));
    }

    [Fact]
    public async Task Reset_password_and_list_users()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cpm-ops-cli", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(dir);
        paths.EnsureCreated();
        try
        {
            using (var store = new LiteStore(paths))
                store.Col<User>().Insert(new User { Email = "admin@example.com", Name = "Admin", Role = UserRole.Admin, PasswordHash = Passwords.Hash("old password 123"), Disabled = true });

            var outw = new StringWriter();
            var errw = new StringWriter();
            Assert.Equal(0, await OpsCli.TryRunAsync(["list-users", "--data-dir", dir], outw, errw));
            Assert.Contains("admin@example.com", outw.ToString());
            Assert.Contains("disabled", outw.ToString());

            outw = new StringWriter();
            Assert.Equal(2, await OpsCli.TryRunAsync(["reset-password", "--data-dir", dir, "--password", "short"], outw, errw));
            Assert.Equal(2, await OpsCli.TryRunAsync(["reset-password", "--email", "admin@example.com", "--data-dir", dir, "--password", "short"], outw, errw));
            Assert.Equal(1, await OpsCli.TryRunAsync(["reset-password", "--email", "who@example.com", "--data-dir", dir], outw, errw));

            outw = new StringWriter();
            Assert.Equal(0, await OpsCli.TryRunAsync(["reset-password", "--email", "ADMIN@example.com", "--data-dir", dir, "--enable"], outw, errw));
            var output = outw.ToString();
            var generated = output.Split('\n').Single(l => l.StartsWith("New password: ")).Trim()["New password: ".Length..];
            Assert.True(generated.Length >= 12);
            Assert.Contains("re-enabled", output);

            outw = new StringWriter();
            Assert.Equal(0, await OpsCli.TryRunAsync(["reset-password", "--email=admin@example.com", "--data-dir", dir, "--password", "explicit password 1"], outw, errw));
            Assert.DoesNotContain("New password", outw.ToString());

            using (var store = new LiteStore(paths))
            {
                var u = store.Col<User>().FindAll().Single();
                Assert.True(Passwords.Verify("explicit password 1", u.PasswordHash));
                Assert.False(u.Disabled);
                Assert.Equal(2, u.SecurityStamp);
                Assert.Contains(store.Col<AuditEntry>().FindAll(), a => a.Action == "passwordReset");
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task Missing_database_is_reported()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cpm-ops-cli", Guid.NewGuid().ToString("N"));
        var errw = new StringWriter();
        Assert.Equal(1, await OpsCli.TryRunAsync(["list-users", "--data-dir", dir], new StringWriter(), errw));
        Assert.Contains("No database", errw.ToString());
    }
}
