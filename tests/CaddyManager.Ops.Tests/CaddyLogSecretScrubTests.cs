using System.Text.Json.Nodes;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using CaddyManager.Ops.Monitoring;
using Microsoft.Extensions.DependencyInjection;

namespace CaddyManager.Ops.Tests;

/// <summary>
/// SEC-1 (Ops half): caddy.log text reaches viewers (GET /api/logs/caddy) and events / notifications (the certificate
/// alert quotes Caddy's issuance errors) only through the Config module's ISecretScrubber. The scrubbing rules themselves
/// are tested against real Caddy output in the Config tests (CaddyLogScrubE2ETests); here a scrubber that masks one
/// known token stands in for it, over real files and the real HTTP pipeline (sign-in, roles). Artifact: ops-log-scrub.json.
///
/// Ways it could fail:
/// (1) a viewer reads a DNS provider token from caddy.log through the log viewer;
/// (2) the q filter works on the raw text, so a viewer can confirm a guessed token (or its prefix) by searching for it
///     even though the returned line is scrubbed;
/// (3) lines without secrets or other filters change (the filter must still find ordinary text);
/// (4) the certificate alert (event details, notification e-mail) quotes the token from the tls.obtain error, or keeps
///     part of it because the line is cut at 800 characters before scrubbing;
/// (5) without the Config module (no ISecretScrubber) the endpoints fail instead of returning the text.
/// </summary>
public sealed class CaddyLogSecretScrubTests
{
    private const string Token = "cpm-duck-token-0123456789abcdef-4711";

    private sealed class TokenScrubber : ISecretScrubber
    {
        public int Calls;
        public string Scrub(string text)
        {
            Interlocked.Increment(ref Calls);
            return text.Replace(Token, "***", StringComparison.Ordinal);
        }
    }

    private static string DuckDnsError(string domain, int padding = 0) =>
        "{\"level\":\"error\",\"ts\":2,\"logger\":\"tls.obtain\",\"msg\":\"could not get certificate from issuer\",\"identifier\":\"" + domain +
        "\",\"issuer\":\"acme-v02.api.letsencrypt.org-directory\",\"error\":\"" + new string('x', padding) + "[" + domain +
        "] solving challenges: presenting for challenge: adding temporary record for zone \\\"duckdns.org.\\\": DuckDNS request failed, expected (OK) but got (KO), url: [https://www.duckdns.org/update?domains=" +
        domain + "&token=" + Token + "&txt=abc&verbose=true]\"}";

    [Fact]
    public async Task Caddy_log_viewer_and_certificate_alerts_never_show_configured_secrets()
    {
        var report = E2EArtifacts.Report(nameof(Caddy_log_viewer_and_certificate_alerts_never_show_configured_secrets));
        var scrubber = new TokenScrubber();
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-25T10:00:00Z"));
        await using var app = await TestApp.StartAsync(o => o.CertificateMissingInterval = TimeSpan.Zero, time: clock,
            services: s => s.AddSingleton<ISecretScrubber>(scrubber));
        await app.SetupAdminAsync();
        app.CreateUser("viewer@example.com", UserRole.Viewer);
        var viewer = await app.LoginAsync("viewer@example.com");

        Directory.CreateDirectory(Path.GetDirectoryName(app.Paths.CaddyProcessLog)!);
        // (4) the 800-character cut would fall inside the token if the line were cut before scrubbing
        var cutInside = 800 - DuckDnsError("shop.example.com").IndexOf(Token, StringComparison.Ordinal) - 10;
        File.WriteAllLines(app.Paths.CaddyProcessLog,
        [
            "{\"level\":\"info\",\"ts\":1,\"logger\":\"tls.obtain\",\"msg\":\"acquiring lock\",\"identifier\":\"shop.example.com\"}",
            DuckDnsError("shop.example.com", Math.Max(0, cutInside)),
        ]);

        // (1)(3)
        var all = await (await viewer.GetAsync("api/logs/caddy?lines=50")).JsonAsync();
        var lines = all.GetProperty("lines").EnumerateArray().Select(e => e.GetString()!).ToList();
        report["viewerLines"] = new JsonArray(lines.Select(l => (JsonNode)l).ToArray());
        Assert.Equal(2, lines.Count);
        Assert.DoesNotContain(lines, l => l.Contains("0123456789abcdef", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("token=***", StringComparison.Ordinal));
        Assert.Contains("acquiring lock", lines[0]);
        var ordinary = await (await viewer.GetAsync("api/logs/caddy?lines=50&q=DuckDNS request failed")).JsonAsync();
        Assert.Equal(1, ordinary.GetProperty("lines").GetArrayLength());
        // (2) searching for the token (or a prefix of it) finds nothing
        foreach (var probe in new[] { Token, "cpm-duck-token-0123", "token=cpm-duck" })
        {
            var r = await (await viewer.GetAsync("api/logs/caddy?lines=50&q=" + Uri.EscapeDataString(probe))).JsonAsync();
            Assert.Equal(0, r.GetProperty("lines").GetArrayLength());
        }

        // (4) the certificate alert
        var old = clock.GetUtcNow().UtcDateTime.AddMinutes(-30);
        app.Store.Col<SiteHost>().Insert(new SiteHost { Domains = ["shop.example.com"], Tls = TlsMode.Acme, Enabled = true, UpdatedAt = old, CreatedAt = old });
        await app.Services.GetRequiredService<MonitorService>().TickAsync(CancellationToken.None);
        var ev = Assert.Single(app.Store.Col<EventEntry>().Find(e => e.Key == "cert-missing:shop.example.com"));
        report["eventDetails"] = ev.Details;
        Assert.Contains("DuckDNS request failed", ev.Details);
        Assert.Contains("token=***", ev.Details);
        Assert.DoesNotContain("cpm-duck", ev.Details);
        await HttpExtensions.WaitUntilAsync(() => !app.Notifier!.Sent.IsEmpty);
        Assert.All(app.Notifier!.Sent, n => Assert.DoesNotContain("cpm-duck", n.Body));
        report["passed"] = true;
        E2EArtifacts.Write("ops-log-scrub.json", report);
    }

    [Fact]
    public async Task Without_a_secret_scrubber_the_log_viewer_still_works()
    {
        await using var app = await TestApp.StartAsync();
        var admin = await app.SetupAdminAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(app.Paths.CaddyProcessLog)!);
        File.WriteAllLines(app.Paths.CaddyProcessLog, ["plain line one", "plain line two"]);
        var r = await (await admin.GetAsync("api/logs/caddy?q=two")).JsonAsync(); // (5)
        Assert.Equal(["plain line two"], r.GetProperty("lines").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }
}
