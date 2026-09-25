using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using CaddyManager.Ops.Events;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CaddyManager.Ops.Tests;

/// <summary>Records outgoing HTTP requests and answers them with a scripted response.</summary>
public sealed class FakeHttpHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
{
    public ConcurrentQueue<(HttpRequestMessage Request, string Body)> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Requests.Enqueue((request, body));
        return respond(request, body);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

internal sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

public class OAuthTokenProviderTests
{
    private const string Tenant = "contoso.onmicrosoft.com";
    private const string ClientId = "0b8d7f1e-2f5a-4c1e-9a53-2f7d6c1b9e01";

    [Fact]
    public async Task Requests_client_credentials_token_and_caches_until_shortly_before_expiry()
    {
        var n = 0;
        var handler = new FakeHttpHandler((_, _) =>
            FakeHttpHandler.Json(HttpStatusCode.OK, $"{{\"token_type\":\"Bearer\",\"expires_in\":3599,\"access_token\":\"tok-{Interlocked.Increment(ref n)}\"}}"));
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-25T10:00:00Z"));
        var provider = new OAuthTokenProvider(new SingleClientFactory(handler), clock);

        Assert.Equal("tok-1", await provider.GetTokenAsync(Tenant, ClientId, "s3cret", default));
        Assert.Equal("tok-1", await provider.GetTokenAsync(Tenant, ClientId, "s3cret", default));
        Assert.Single(handler.Requests);

        var (req, body) = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal($"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/token", req.RequestUri!.ToString());
        Assert.Equal("application/x-www-form-urlencoded", req.Content!.Headers.ContentType!.MediaType);
        var form = body.Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
        Assert.Equal("client_credentials", form["grant_type"]);
        Assert.Equal("https://outlook.office365.com/.default", form["scope"]);
        Assert.Equal(ClientId, form["client_id"]);
        Assert.Equal("s3cret", form["client_secret"]);

        // A changed secret is a different credential: no reuse of the cached token.
        Assert.Equal("tok-2", await provider.GetTokenAsync(Tenant, ClientId, "rotated", default));
        clock.Advance(TimeSpan.FromMinutes(50));
        Assert.Equal("tok-2", await provider.GetTokenAsync(Tenant, ClientId, "rotated", default));
        clock.Advance(TimeSpan.FromMinutes(5)); // within 5 minutes of expiry → refresh
        Assert.Equal("tok-3", await provider.GetTokenAsync(Tenant, ClientId, "rotated", default));
        provider.Invalidate();
        Assert.Equal("tok-4", await provider.GetTokenAsync(Tenant, ClientId, "rotated", default));
    }

    [Fact]
    public async Task Entra_errors_are_explained_and_bad_tenants_rejected()
    {
        var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Json(HttpStatusCode.Unauthorized,
            "{\"error\":\"invalid_client\",\"error_description\":\"AADSTS7000215: Invalid client secret provided.\\r\\nTrace ID: abc\\r\\nCorrelation ID: def\"}"));
        var provider = new OAuthTokenProvider(new SingleClientFactory(handler), TimeProvider.System);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetTokenAsync(Tenant, ClientId, "bad", default));
        Assert.Contains("HTTP 401", ex.Message);
        Assert.Contains("invalid_client", ex.Message);
        Assert.Contains("AADSTS7000215", ex.Message);
        Assert.DoesNotContain("Trace ID", ex.Message);
        Assert.Contains("SMTP.SendAsApp", ex.Message);
        Assert.DoesNotContain("client_secret", ex.Message);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetTokenAsync("evil.com/../x", ClientId, "s", default));
        Assert.True(OAuthTokenProvider.IsValidTenant("72f988bf-86f1-41af-91ab-2d7cd011db47"));
        Assert.False(OAuthTokenProvider.IsValidTenant("a b"));
    }
}

public class SmtpOAuthTests
{
    private static (FakeHttpHandler Handler, Action<IServiceCollection> Services) TokenEndpoint(string token = "entra-token")
    {
        var handler = new FakeHttpHandler((req, _) => req.RequestUri!.Host == "login.microsoftonline.com"
            ? FakeHttpHandler.Json(HttpStatusCode.OK, $"{{\"expires_in\":3599,\"access_token\":\"{token}\"}}")
            : new HttpResponseMessage(HttpStatusCode.OK));
        return (handler, s => s.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(new SingleClientFactory(handler))));
    }

    private static object OAuthSettings(int port) => new
    {
        smtpEnabled = true, smtpHost = "127.0.0.1", smtpPort = port, smtpSecurity = "none", smtpFrom = "alerts@contoso.com",
        recipients = new[] { "ops@contoso.com" }, smtpAuth = "oAuth2ClientCredentials", smtpUsername = "alerts@contoso.com",
        oAuthTenantId = "contoso.onmicrosoft.com", oAuthClientId = "0b8d7f1e-2f5a-4c1e-9a53-2f7d6c1b9e01", oAuthClientSecret = "client-s3cret",
    };

    [Fact]
    public async Task Sends_mail_with_xoauth2_using_the_entra_token()
    {
        await using var smtp = new SmtpTestServer(oauth: true) { AcceptToken = "entra-token" };
        var (handler, services) = TokenEndpoint();
        await using var app = await TestApp.StartAsync(fakeNotifier: false, services: services);
        var admin = await app.SetupAdminAsync();

        var put = await admin.PutJsonAsync("api/settings/notifications", OAuthSettings(smtp.Port));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var text = await put.Content.ReadAsStringAsync();
        Assert.DoesNotContain("client-s3cret", text);
        var wire = JsonDocument.Parse(text).RootElement;
        Assert.True(wire.GetProperty("hasOAuthClientSecret").GetBoolean());
        Assert.False(wire.TryGetProperty("oAuthClientSecretProtected", out _));
        Assert.Equal("oAuth2ClientCredentials", wire.GetProperty("smtpAuth").GetString());
        Assert.Equal("contoso.onmicrosoft.com", wire.GetProperty("oAuthTenantId").GetString());

        var res = await (await admin.PostAsJsonAsync("api/settings/notifications/test", new { })).JsonAsync();
        Assert.True(res.GetProperty("ok").GetBoolean(), res.ToString());
        Assert.True(smtp.AuthAttempts.TryDequeue(out var auth));
        Assert.Equal("user=alerts@contoso.com\u0001auth=Bearer entra-token\u0001\u0001", auth);
        Assert.Single(smtp.Messages);

        // Second send reuses the cached token.
        Assert.True((await (await admin.PostAsJsonAsync("api/settings/notifications/test", new { })).JsonAsync()).GetProperty("ok").GetBoolean());
        Assert.Single(handler.Requests, r => r.Request.RequestUri!.Host == "login.microsoftonline.com");

        Assert.Contains(app.Store.Col<AuditEntry>().FindAll(), a => (a.Details ?? "").Contains("OAuth client secret changed"));
        Assert.DoesNotContain(app.Store.Col<AuditEntry>().FindAll(), a => (a.Details ?? "").Contains("client-s3cret"));
    }

    [Fact]
    public async Task Rejected_token_is_explained_and_not_reused()
    {
        await using var smtp = new SmtpTestServer(oauth: true) { AcceptToken = "something-else" };
        var (handler, services) = TokenEndpoint();
        await using var app = await TestApp.StartAsync(fakeNotifier: false, services: services);
        var admin = await app.SetupAdminAsync();
        await admin.PutJsonAsync("api/settings/notifications", OAuthSettings(smtp.Port));

        var res = await (await admin.PostAsJsonAsync("api/settings/notifications/test", new { })).JsonAsync();
        Assert.False(res.GetProperty("ok").GetBoolean());
        var error = res.GetProperty("errors")[0].GetString()!;
        Assert.Contains("rejected the OAuth2 token", error);
        Assert.Contains("Set-CASMailbox", error);
        await admin.PostAsJsonAsync("api/settings/notifications/test", new { });
        Assert.Equal(2, handler.Requests.Count(r => r.Request.RequestUri!.Host == "login.microsoftonline.com"));
    }

    [Fact]
    public async Task OAuth_settings_are_validated_and_password_mode_is_unchanged()
    {
        await using var smtp = new SmtpTestServer();
        await using var app = await TestApp.StartAsync(fakeNotifier: false);
        var admin = await app.SetupAdminAsync();

        var missing = await admin.PutJsonAsync("api/settings/notifications", new
        {
            smtpEnabled = true, smtpHost = "smtp.office365.com", smtpFrom = "alerts@contoso.com", recipients = new[] { "ops@contoso.com" },
            smtpAuth = "oAuth2ClientCredentials", smtpUsername = "not-an-address", oAuthTenantId = "bad tenant", oAuthClientId = "abc",
        });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        var errors = (await missing.JsonAsync()).GetProperty("errors");
        foreach (var f in new[] { "smtpUsername", "oAuthTenantId", "oAuthClientId", "oAuthClientSecret" })
            Assert.True(errors.TryGetProperty(f, out _), $"missing error for {f}: {errors}");

        // Defaults: password mode; "none" never authenticates even with a username.
        var get = await (await admin.GetAsync("api/settings/notifications")).JsonAsync();
        Assert.Equal("password", get.GetProperty("smtpAuth").GetString());
        Assert.Equal("generic", get.GetProperty("webhookFormat").GetString());
        Assert.False(get.GetProperty("hasOAuthClientSecret").GetBoolean());
        var ok = await admin.PutJsonAsync("api/settings/notifications", new
        {
            smtpEnabled = true, smtpHost = "127.0.0.1", smtpPort = smtp.Port, smtpSecurity = "none", smtpFrom = "cpm@example.com",
            recipients = new[] { "ops@example.com" }, smtpAuth = "none", smtpUsername = "ignored-user",
        });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var res = await (await admin.PostAsJsonAsync("api/settings/notifications/test", new { })).JsonAsync();
        Assert.True(res.GetProperty("ok").GetBoolean(), res.ToString());
        Assert.Empty(smtp.AuthAttempts);
    }
}

public class WebhookFormatTests
{
    private static readonly Notification Sample = new("[EDGE01] Error: Caddy is not running", "Caddy is not running (state: Stopped)", "error", "caddy",
        new DateTime(2026, 9, 25, 10, 0, 0, DateTimeKind.Utc), "Host mode: windows-service\nLast error: <exit 1> & more");

    [Fact]
    public void Slack_payload_is_escaped_mrkdwn_text_only()
    {
        var json = WebhookPayloads.Build(WebhookFormat.Slack, Sample, "EDGE01", "https://edge01:8443/");
        Assert.Equal(["text"], json.Select(kv => kv.Key).ToArray());
        var text = json["text"]!.GetValue<string>();
        Assert.StartsWith("*[EDGE01] Error: Caddy is not running*\n", text);
        Assert.Contains("*Severity:* Error", text);
        Assert.Contains("```Host mode: windows-service\nLast error: &lt;exit 1&gt; &amp; more```", text);
        Assert.EndsWith("<https://edge01:8443/|Open Caddy Proxy Manager>", text);
    }

    [Fact]
    public void Teams_workflow_payload_is_an_adaptive_card_message()
    {
        var json = JsonDocument.Parse(WebhookPayloads.Build(WebhookFormat.TeamsWorkflow, Sample, "EDGE01", "https://edge01:8443/").ToJsonString()).RootElement;
        Assert.Equal("message", json.GetProperty("type").GetString());
        var attachment = json.GetProperty("attachments")[0];
        Assert.Equal("application/vnd.microsoft.card.adaptive", attachment.GetProperty("contentType").GetString());
        var card = attachment.GetProperty("content");
        Assert.Equal("AdaptiveCard", card.GetProperty("type").GetString());
        Assert.Equal("1.4", card.GetProperty("version").GetString());
        var body = card.GetProperty("body");
        Assert.Equal("[EDGE01] Error: Caddy is not running", body[0].GetProperty("text").GetString());
        Assert.Equal("Attention", body[0].GetProperty("color").GetString());
        var facts = body.EnumerateArray().Single(b => b.GetProperty("type").GetString() == "FactSet").GetProperty("facts")
            .EnumerateArray().ToDictionary(f => f.GetProperty("title").GetString()!, f => f.GetProperty("value").GetString()!);
        Assert.Equal("Error", facts["Severity"]);
        Assert.Equal("EDGE01", facts["Server"]);
        Assert.Equal("caddy", facts["Category"]);
        Assert.Equal("2026-09-25 10:00:00 UTC", facts["Time"]);
        Assert.Contains(body.EnumerateArray(), b => b.TryGetProperty("fontType", out var ft) && ft.GetString() == "Monospace");
        var action = card.GetProperty("actions")[0];
        Assert.Equal("Action.OpenUrl", action.GetProperty("type").GetString());
        Assert.Equal("https://edge01:8443/", action.GetProperty("url").GetString());

        var recovered = WebhookPayloads.Build(WebhookFormat.TeamsWorkflow, Sample with { Severity = "recovered", Details = null }, "EDGE01", null);
        var rc = recovered["attachments"]![0]!["content"]!;
        Assert.Equal("Good", rc["body"]![0]!["color"]!.GetValue<string>());
        Assert.Null(rc["actions"]);
    }

    [Fact]
    public void Generic_payload_keeps_the_existing_shape()
    {
        var json = WebhookPayloads.Build(WebhookFormat.Generic, Sample, "EDGE01", null);
        Assert.Equal(new[] { "title", "text", "severity", "category", "server", "time" }, json.Select(kv => kv.Key).ToArray());
    }

    [Fact]
    public async Task Configured_format_is_posted_by_the_notifier()
    {
        var handler = new FakeHttpHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Accepted));
        await using var app = await TestApp.StartAsync(fakeNotifier: false,
            services: s => s.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(new SingleClientFactory(handler))));
        var admin = await app.SetupAdminAsync();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutJsonAsync("api/settings/notifications",
            new { webhookEnabled = true, webhookUrl = "https://prod-00.westeurope.logic.azure.com/workflows/abc", webhookFormat = "teamsWorkflow" })).StatusCode);
        var res = await (await admin.PostAsJsonAsync("api/settings/notifications/test", new { })).JsonAsync();
        Assert.True(res.GetProperty("ok").GetBoolean(), res.ToString()); // Workflows answer 202 Accepted
        var body = JsonDocument.Parse(handler.Requests.Single().Body).RootElement;
        Assert.Equal("message", body.GetProperty("type").GetString());
        Assert.Contains("Test notification", body.GetProperty("attachments")[0].GetProperty("content").GetProperty("body")[0].GetProperty("text").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutJsonAsync("api/settings/notifications", new { webhookFormat = "discord" })).StatusCode);
    }
}
