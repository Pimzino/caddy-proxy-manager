using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using CaddyManager.Ops.Monitoring;

namespace CaddyManager.Ops.Tests;

public class CertMissingTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-25T10:00:00Z");

    private static List<EventEntry> Events(TestApp app, string key) =>
        app.Store.Col<EventEntry>().Find(e => e.Key == key).OrderBy(e => e.CreatedAt).ToList();

    private static SiteHost Host(TestApp app, TlsMode tls, DateTime updatedAt, bool enabled = true, params string[] domains)
    {
        var h = new SiteHost { Domains = domains.ToList(), Tls = tls, Enabled = enabled, UpdatedAt = updatedAt, CreatedAt = updatedAt };
        app.Store.Col<SiteHost>().Insert(h);
        return h;
    }

    [Fact]
    public async Task Missing_certificates_are_reported_with_caddy_log_errors_and_recover_when_issued()
    {
        var clock = new ManualTimeProvider(Start);
        await using var app = await TestApp.StartAsync(o => o.CertificateMissingInterval = TimeSpan.Zero, time: clock);
        var monitor = app.Services.GetRequiredService<MonitorService>();
        var now = Start.UtcDateTime;
        var old = now.AddMinutes(-30);

        Host(app, TlsMode.Acme, old, true, "shop.example.com", "*.example.com");  // wildcard skipped, shop missing
        Host(app, TlsMode.Acme, old, true, "www.example.org");                     // covered by ACME cert
        Host(app, TlsMode.Internal, old, true, "api.example.org");                 // covered by *.example.org
        Host(app, TlsMode.Internal, old, true, "intranet.corp");                   // internal, missing
        Host(app, TlsMode.Acme, now.AddMinutes(-2), true, "new.example.com");      // changed 2 minutes ago: grace
        Host(app, TlsMode.None, old, true, "plain.example.com");                   // no TLS
        Host(app, TlsMode.Acme, old, false, "disabled.example.com");               // disabled
        app.Certificates.Certificates =
        [
            new CertificateInfo { Id = "a1", Kind = CertificateKind.Acme, Subjects = ["www.example.org"], NotAfter = now.AddDays(60) },
            new CertificateInfo { Id = "i1", Kind = CertificateKind.Internal, Subjects = ["*.example.org"], NotAfter = now.AddDays(6) },
            new CertificateInfo { Id = "x1", Kind = CertificateKind.Acme, Subjects = ["shop.example.com"], NotAfter = now.AddDays(-1) }, // expired
        ];
        Directory.CreateDirectory(Path.GetDirectoryName(app.Paths.CaddyProcessLog)!);
        File.WriteAllLines(app.Paths.CaddyProcessLog,
        [
            "{\"level\":\"info\",\"ts\":1,\"logger\":\"tls.obtain\",\"msg\":\"acquiring lock\",\"identifier\":\"shop.example.com\"}",
            "{\"level\":\"error\",\"ts\":2,\"logger\":\"tls.issuance.acme.acme_client\",\"msg\":\"challenge failed\",\"identifier\":\"shop.example.com\",\"problem\":{\"type\":\"urn:ietf:params:acme:error:connection\"}}",
            "{\"level\":\"error\",\"ts\":3,\"logger\":\"tls.obtain\",\"msg\":\"could not get certificate from issuer\",\"identifier\":\"shop.example.com\",\"error\":\"HTTP 400 urn:ietf:params:acme:error:connection - Timeout during connect (likely firewall problem)\"}",
            "{\"level\":\"error\",\"ts\":4,\"logger\":\"tls.obtain\",\"msg\":\"could not get certificate from issuer\",\"identifier\":\"other.example.net\"}",
        ]);

        await monitor.TickAsync(CancellationToken.None);

        var shop = Assert.Single(Events(app, "cert-missing:shop.example.com"));
        Assert.Equal(EventSeverity.Warning, shop.Severity);
        Assert.Equal("certificate", shop.Category);
        Assert.Contains("No certificate has been issued for shop.example.com", shop.Message);
        Assert.Contains("Timeout during connect", shop.Details);
        Assert.Contains("challenge failed", shop.Details);
        Assert.DoesNotContain("acquiring lock", shop.Details);   // info lines are not errors
        Assert.DoesNotContain("other.example.net", shop.Details);
        Assert.Contains("TCP 80 and 443", shop.Details);
        Assert.Single(Events(app, "cert-missing:intranet.corp"));
        foreach (var d in new[] { "www.example.org", "api.example.org", "new.example.com", "plain.example.com", "disabled.example.com", "*.example.com" })
            Assert.Empty(Events(app, "cert-missing:" + d));
        await HttpExtensions.WaitUntilAsync(() => app.Notifier!.Sent.Count >= 2);

        // Nothing new while unchanged (cooldown), and nothing at all while Caddy is down.
        await monitor.TickAsync(CancellationToken.None);
        Assert.Single(Events(app, "cert-missing:shop.example.com"));
        clock.Advance(TimeSpan.FromMinutes(15));
        app.CaddyHost.Status = app.CaddyHost.Status with { State = CaddyRunState.Stopped, AdminReachable = false };
        app.Certificates.Certificates.Add(new CertificateInfo { Id = "a2", Kind = CertificateKind.Acme, Subjects = ["shop.example.com"], NotAfter = now.AddDays(90) });
        await monitor.TickAsync(CancellationToken.None);
        Assert.Single(Events(app, "cert-missing:shop.example.com"));
        Assert.Empty(Events(app, "cert-missing:new.example.com"));

        // Caddy back: shop issued → recovered; new.example.com is now past the grace period → missing.
        app.CaddyHost.Status = app.CaddyHost.Status with { State = CaddyRunState.Running, AdminReachable = true };
        await monitor.TickAsync(CancellationToken.None);
        var shopEvents = Events(app, "cert-missing:shop.example.com");
        Assert.Equal(2, shopEvents.Count);
        Assert.Equal(EventSeverity.Recovered, shopEvents[1].Severity);
        Assert.Contains("has been issued", shopEvents[1].Message);
        Assert.Single(Events(app, "cert-missing:new.example.com"));

        // Host removed → its open alert is closed with an explanation.
        foreach (var gone in app.Store.Col<SiteHost>().FindAll().Where(h => h.Domains.Contains("intranet.corp")).ToList())
            app.Store.Col<SiteHost>().Delete(gone.Id);
        await monitor.TickAsync(CancellationToken.None);
        var intranet = Events(app, "cert-missing:intranet.corp");
        Assert.Equal(EventSeverity.Recovered, intranet.Last().Severity);
        Assert.Contains("no longer needs", intranet.Last().Message);
    }

    [Fact]
    public async Task Recently_changed_host_keeps_an_open_alert_open()
    {
        var clock = new ManualTimeProvider(Start);
        await using var app = await TestApp.StartAsync(o => o.CertificateMissingInterval = TimeSpan.Zero, time: clock);
        var monitor = app.Services.GetRequiredService<MonitorService>();
        var h = Host(app, TlsMode.Acme, Start.UtcDateTime.AddHours(-1), true, "edge.example.com");
        await monitor.TickAsync(CancellationToken.None);
        Assert.Single(Events(app, "cert-missing:edge.example.com"));

        h.UpdatedAt = Start.UtcDateTime; // edited just now, still no certificate
        app.Store.Col<SiteHost>().Update(h);
        await monitor.TickAsync(CancellationToken.None);
        Assert.Single(Events(app, "cert-missing:edge.example.com")); // neither recovered nor re-raised
    }

    [Theory]
    [InlineData("a.example.com", "*.example.com", true)]
    [InlineData("a.b.example.com", "*.example.com", false)]
    [InlineData("example.com", "*.example.com", false)]
    [InlineData("A.Example.com", "a.example.com", true)]
    [InlineData("a.example.com", "b.example.com", false)]
    public void Coverage_matches_exact_names_and_single_label_wildcards(string domain, string subject, bool expected) =>
        Assert.Equal(expected, MonitorService.IsCovered(domain,
            [new CertificateInfo { Kind = CertificateKind.Acme, Subjects = [subject], NotAfter = DateTime.UtcNow.AddDays(1) }], DateTime.UtcNow));
}
