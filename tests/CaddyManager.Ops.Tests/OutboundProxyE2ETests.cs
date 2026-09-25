using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using CaddyManager.Core.Models;
using CaddyManager.Ops.Events;

namespace CaddyManager.Ops.Tests;

/// <summary>
/// End to end through the real API, the real HttpClient stack and real sockets: alerts (webhooks) and the Microsoft Entra
/// ID token request go through the outbound proxy configured in Settings → Updates. Before the fix they used the
/// "default" client, i.e. HttpClient.DefaultProxy, which on Windows reads the proxy environment variables or the WinINet
/// settings of the service account (LocalSystem), never the proxy configured in the UI
/// (https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclient.defaultproxy).
///
/// Ways this can fail (written before the fix):
///  1. Webhooks bypass the configured proxy: on a server that reaches the Internet only through it, Teams/Slack alerts
///     are lost while update checks work.
///  2. The Entra ID token request bypasses it: Microsoft 365 OAuth2 e-mail fails with a connect error.
///  3. Proxy credentials from the URL (http://user:pass@proxy:port, percent-encoded) are not sent, or not in response to
///     the 407 challenge, so an authenticating proxy rejects every alert.
///  4. Loopback / NO_PROXY destinations (internal webhook receivers) are sent to the proxy, which cannot reach them.
///  5. SMTP is tunnelled through the HTTP proxy (it must not be: MailKit connects directly; documented).
///  6. A changed proxy setting is not picked up without a restart (cached client keyed on the old value).
///  7. Removing the proxy keeps using it.
/// </summary>
public class OutboundProxyE2ETests
{
    private const string Tenant = "contoso.onmicrosoft.com";

    private static IPAddress? PrivateLanAddress() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(a => a.Address))
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork &&
                                 (a.GetAddressBytes() is [10, ..] or [192, 168, ..] || a.GetAddressBytes() is [172, var b, ..] && b is >= 16 and <= 31));

    [Fact]
    public async Task Webhooks_and_entra_token_requests_use_the_configured_proxy()
    {
        var report = E2EArtifacts.Report(nameof(Webhooks_and_entra_token_requests_use_the_configured_proxy));
        await using var proxy = new RecordingHttpServer(r => r.Host switch
        {
            "login.cpm-e2e.invalid" => (200, "application/json", "{\"token_type\":\"Bearer\",\"expires_in\":3599,\"access_token\":\"proxied-token\"}"),
            "hooks.cpm-e2e.invalid" => (202, "text/plain", ""),
            _ => (502, "text/plain", "unexpected host"),
        }) { ProxyCredentials = "cpm:p@ss w0rd" };
        await using var origin = new RecordingHttpServer(_ => (200, "text/plain", "ok"));
        await using var smtp = new SmtpTestServer(oauth: true) { AcceptToken = "proxied-token" };
        await using var app = await TestApp.StartAsync(fakeNotifier: false);
        var admin = await app.SetupAdminAsync();
        app.Services.GetRequiredService<OAuthTokenProvider>().Authority = "http://login.cpm-e2e.invalid";
        var binary = app.Store.GetSettings<BinarySettings>();
        binary.OutboundProxy = $"http://cpm:p%40ss%20w0rd@127.0.0.1:{proxy.Port}";
        app.Store.SaveSettings(binary);

        Assert.Equal(HttpStatusCode.OK, (await admin.PutJsonAsync("api/settings/notifications", new
        {
            smtpEnabled = true, smtpHost = "127.0.0.1", smtpPort = smtp.Port, smtpSecurity = "none", smtpFrom = "alerts@contoso.com",
            recipients = new[] { "ops@contoso.com" }, smtpAuth = "oAuth2ClientCredentials", smtpUsername = "alerts@contoso.com",
            oAuthTenantId = Tenant, oAuthClientId = "0b8d7f1e-2f5a-4c1e-9a53-2f7d6c1b9e01", oAuthClientSecret = "client-s3cret",
            webhookEnabled = true, webhookUrl = "http://hooks.cpm-e2e.invalid/workflows/abc", webhookFormat = "teamsWorkflow",
        })).StatusCode);

        // (1)(2)(3)(5)
        var res = await (await admin.PostAsJsonAsync("api/settings/notifications/test", new { })).JsonAsync();
        report["viaProxy"] = res.ToString();
        Assert.True(res.GetProperty("ok").GetBoolean(), res.ToString());
        var seen = proxy.Requests.ToList();
        report["proxyRequests"] = new JsonArray(seen.Select(r => (JsonNode)$"{r.Method} {r.Target} auth={r.Headers.ContainsKey("Proxy-Authorization")}").ToArray());
        var token = seen.Where(r => r.Target == $"http://login.cpm-e2e.invalid/{Tenant}/oauth2/v2.0/token").ToList();
        Assert.Contains(token, r => r.Headers.ContainsKey("Proxy-Authorization") && r.Body.Contains("grant_type=client_credentials"));
        var hook = seen.Where(r => r.Target == "http://hooks.cpm-e2e.invalid/workflows/abc").ToList();
        Assert.Contains(hook, r => r.Headers.ContainsKey("Proxy-Authorization") && JsonDocument.Parse(r.Body).RootElement.GetProperty("type").GetString() == "message");
        Assert.True(smtp.AuthAttempts.TryDequeue(out var auth));
        Assert.Contains("auth=Bearer proxied-token", auth);
        Assert.Single(smtp.Messages); // SMTP went straight to the mail server
        Assert.All(seen, r => Assert.True(r.AbsoluteForm, r.Target)); // nothing but HTTP went to the proxy

        // (4) Loopback receivers bypass the proxy.
        var before = proxy.Requests.Count;
        await admin.PutJsonAsync("api/settings/notifications", new { smtpEnabled = false, webhookEnabled = true, webhookUrl = $"http://127.0.0.1:{origin.Port}/hook", webhookFormat = "generic" });
        res = await (await admin.PostAsJsonAsync("api/settings/notifications/test", new { })).JsonAsync();
        Assert.True(res.GetProperty("ok").GetBoolean(), res.ToString());
        Assert.Single(origin.Requests, r => !r.AbsoluteForm && r.Target == "/hook");
        Assert.Equal(before, proxy.Requests.Count);

        // (4) A private-range receiver matches the default NO_PROXY list (10/8, 172.16/12, 192.168/16) and goes direct.
        if (PrivateLanAddress() is { } lan)
        {
            await using var lanOrigin = new RecordingHttpServer(_ => (200, "text/plain", "ok"), lan);
            await admin.PutJsonAsync("api/settings/notifications", new { webhookEnabled = true, webhookUrl = $"http://{lan}:{lanOrigin.Port}/lan", webhookFormat = "generic" });
            res = await (await admin.PostAsJsonAsync("api/settings/notifications/test", new { })).JsonAsync();
            report["noProxyLan"] = $"{lan}: {res}";
            Assert.True(res.GetProperty("ok").GetBoolean(), res.ToString());
            Assert.Single(lanOrigin.Requests);
            Assert.Equal(before, proxy.Requests.Count);
        }
        else report["noProxyLan"] = "skipped: no private IPv4 address on this machine";

        // (6) A new proxy is used without a restart; (7) no proxy → direct (the .invalid host then cannot resolve).
        await using var proxy2 = new RecordingHttpServer(_ => (202, "text/plain", ""));
        binary.OutboundProxy = $"http://127.0.0.1:{proxy2.Port}";
        app.Store.SaveSettings(binary);
        await admin.PutJsonAsync("api/settings/notifications", new { webhookEnabled = true, webhookUrl = "http://hooks.cpm-e2e.invalid/second", webhookFormat = "generic" });
        res = await (await admin.PostAsJsonAsync("api/settings/notifications/test", new { })).JsonAsync();
        Assert.True(res.GetProperty("ok").GetBoolean(), res.ToString());
        Assert.Single(proxy2.Requests, r => r.Target == "http://hooks.cpm-e2e.invalid/second");

        binary.OutboundProxy = null;
        app.Store.SaveSettings(binary);
        res = await (await admin.PostAsJsonAsync("api/settings/notifications/test", new { })).JsonAsync();
        report["withoutProxy"] = res.ToString();
        Assert.False(res.GetProperty("ok").GetBoolean());
        Assert.Single(proxy2.Requests);
        E2EArtifacts.Write("outbound-proxy-notifications.json", report);
    }
}
