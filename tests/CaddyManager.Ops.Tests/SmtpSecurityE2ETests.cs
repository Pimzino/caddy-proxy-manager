using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CaddyManager.Ops.Tests;

/// <summary>
/// End to end through the API, the real MailKit client and an SMTP server on a real socket.
///
/// "Auto" security: MailKit's SecureSocketOptions.Auto "will continue without any encryption" when the server does not
/// offer TLS (https://mimekit.net/docs/html/T_MailKit_Security_SecureSocketOptions.htm).
/// Ways this can fail (written before the fix):
///  1. With a password (or an OAuth2 bearer token) and "Auto", a server — or an attacker stripping STARTTLS — that does
///     not advertise STARTTLS receives the credentials in plain text.
///  2. The refusal is unclear (a generic socket error instead of "does not support STARTTLS").
///  3. "Auto" without credentials (anonymous internal relays) stops working.
///
/// SendAs with OAuth2: "If you're trying to use Client Credential Grant Flow with SendAs, you need to grant SendAs
/// permissions to the sender" (https://learn.microsoft.com/en-us/exchange/client-developer/legacy-protocols/how-to-authenticate-an-imap-pop-smtp-application-by-using-oauth).
///  4. When the From address differs from the authenticated mailbox, Exchange rejects the message after DATA
///     (554 5.2.252 SendAsDenied) and the administrator only sees the raw SMTP reply, with no hint what to grant.
///  5. The hint appears for other errors or other modes (misleading).
/// </summary>
public class SmtpSecurityE2ETests
{
    [Fact]
    public async Task Auto_security_never_sends_credentials_without_tls()
    {
        var report = E2EArtifacts.Report(nameof(Auto_security_never_sends_credentials_without_tls));
        await using var smtp = new SmtpTestServer(); // no STARTTLS
        await using var app = await TestApp.StartAsync(fakeNotifier: false);
        var admin = await app.SetupAdminAsync();

        // (1)(2) Auto + password.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutJsonAsync("api/settings/notifications", new
        {
            smtpEnabled = true, smtpHost = "127.0.0.1", smtpPort = smtp.Port, smtpSecurity = "auto", smtpFrom = "cpm@example.com",
            recipients = new[] { "ops@example.com" }, smtpAuth = "password", smtpUsername = "cpm@example.com", smtpPassword = "Sup3r-secret",
        })).StatusCode);
        var res = await (await admin.PostAsJsonAsync("api/settings/notifications/test", new { })).JsonAsync();
        report["autoWithPassword"] = res.ToString();
        report["serverSawWithPassword"] = new JsonArray(smtp.Commands.Select(c => (JsonNode)c).ToArray());
        Assert.False(res.GetProperty("ok").GetBoolean());
        Assert.Contains("STARTTLS", res.GetProperty("errors")[0].GetString());
        Assert.DoesNotContain("AUTH", smtp.Commands);
        Assert.DoesNotContain(smtp.Commands, c => c.StartsWith("MAIL", StringComparison.Ordinal));
        Assert.Empty(smtp.Messages);

        // (3) Auto without credentials still delivers to a plain relay.
        await admin.PutJsonAsync("api/settings/notifications", new { smtpAuth = "none", smtpUsername = "" });
        res = await (await admin.PostAsJsonAsync("api/settings/notifications/test", new { })).JsonAsync();
        report["autoAnonymous"] = res.ToString();
        Assert.True(res.GetProperty("ok").GetBoolean(), res.ToString());
        Assert.Single(smtp.Messages);
        E2EArtifacts.Write("smtp-auto-security.json", report);
    }

    [Fact]
    public async Task SendAs_denial_with_oauth2_explains_the_missing_permission()
    {
        var report = E2EArtifacts.Report(nameof(SendAs_denial_with_oauth2_explains_the_missing_permission));
        await using var smtp = new SmtpTestServer(oauth: true)
        {
            AcceptToken = "entra-token",
            DataReply = "554 5.2.252 SendAsDenied; alerts@contoso.com not allowed to send as noreply@contoso.com",
        };
        var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Json(HttpStatusCode.OK, "{\"expires_in\":3599,\"access_token\":\"entra-token\"}"));
        await using var app = await TestApp.StartAsync(fakeNotifier: false,
            services: s => s.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(new SingleClientFactory(handler))));
        var admin = await app.SetupAdminAsync();
        object Settings(string from) => new
        {
            smtpEnabled = true, smtpHost = "127.0.0.1", smtpPort = smtp.Port, smtpSecurity = "none", smtpFrom = from,
            recipients = new[] { "ops@contoso.com" }, smtpAuth = "oAuth2ClientCredentials", smtpUsername = "alerts@contoso.com",
            oAuthTenantId = "contoso.onmicrosoft.com", oAuthClientId = "0b8d7f1e-2f5a-4c1e-9a53-2f7d6c1b9e01", oAuthClientSecret = "client-s3cret",
        };

        // (4) From differs from the mailbox → SendAs hint.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutJsonAsync("api/settings/notifications", Settings("Caddy <noreply@contoso.com>"))).StatusCode);
        var res = await (await admin.PostAsJsonAsync("api/settings/notifications/test", new { })).JsonAsync();
        var error = res.GetProperty("errors")[0].GetString()!;
        report["fromOtherMailbox"] = error;
        Assert.False(res.GetProperty("ok").GetBoolean());
        Assert.Contains("SendAsDenied", error);
        Assert.Contains("Add-RecipientPermission -Identity noreply@contoso.com", error);
        Assert.Contains("-AccessRights SendAs", error);

        // (5) Same rejection when From is the mailbox itself → no SendAs advice.
        await admin.PutJsonAsync("api/settings/notifications", Settings("alerts@contoso.com"));
        res = await (await admin.PostAsJsonAsync("api/settings/notifications/test", new { })).JsonAsync();
        error = res.GetProperty("errors")[0].GetString()!;
        report["fromMailbox"] = error;
        Assert.Contains("SendAsDenied", error);
        Assert.DoesNotContain("Add-RecipientPermission", error);
        E2EArtifacts.Write("smtp-oauth-sendas.json", report);
    }
}
