using System.Text.Json.Nodes;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;

namespace CaddyManager.Platform.Tests;

/// <summary>
/// A full readiness run (real ReadinessService, real store) flags e-mail that authenticates to Exchange Online with a
/// password. Microsoft retires Basic authentication for SMTP AUTH; per the Exchange team's updated timeline it is
/// disabled by default for existing tenants at the end of December 2026
/// (https://learn.microsoft.com/en-us/exchange/clients-and-mobile-in-exchange-online/deprecation-of-basic-authentication-exchange-online).
///
/// Ways this can fail: the check is missing, so alerts silently stop in 2027 with only "535 5.7.139" in the log; it fires
/// for other mail servers or for OAuth2 (noise); it breaks the report (duplicate id, unknown category).
/// </summary>
public class ReadinessSmtpAuthE2ETests
{
    private static async Task<ReadinessCheck?> RunAsync(NotificationSettings n)
    {
        using var env = new TempEnvironment();
        env.Store.SaveSettings(n);
        using var svc = new PlatformServices(env, DevCaddy.FreeTcpPort(), "{}");
        var report = await svc.Get<IReadinessService>().RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(report.Checks.Count, report.Checks.Select(c => c.Id).Distinct().Count());
        return report.Checks.SingleOrDefault(c => c.Id == "notifications.smtpauth");
    }

    [Fact]
    public async Task ExchangeOnlinePasswordAuthIsFlagged()
    {
        var artifact = E2EArtifacts.Report(nameof(ExchangeOnlinePasswordAuthIsFlagged));
        var basic = await RunAsync(new NotificationSettings { SmtpEnabled = true, SmtpHost = "smtp.office365.com", SmtpAuth = SmtpAuthMode.Password, SmtpUsername = "alerts@contoso.com" });
        var oauth = await RunAsync(new NotificationSettings { SmtpEnabled = true, SmtpHost = "smtp.office365.com", SmtpAuth = SmtpAuthMode.OAuth2ClientCredentials });
        var other = await RunAsync(new NotificationSettings { SmtpEnabled = true, SmtpHost = "mail.contoso.com", SmtpAuth = SmtpAuthMode.Password });
        artifact["password"] = basic is null ? null : $"{basic.Status}: {basic.Summary}";
        artifact["oauth2"] = oauth is null ? null : $"{oauth.Status}: {oauth.Summary}";
        artifact["otherServer"] = other is null ? "no check" : $"{other.Status}";
        E2EArtifacts.Write("readiness-smtp-auth.json", artifact);

        Assert.NotNull(basic);
        Assert.Equal(CheckStatus.Warn, basic.Status);
        Assert.Contains("December 2026", basic.Summary);
        Assert.Contains("OAuth2", basic.Remediation);
        Assert.Equal(CheckStatus.Pass, oauth!.Status);
        Assert.Null(other);
    }
}
