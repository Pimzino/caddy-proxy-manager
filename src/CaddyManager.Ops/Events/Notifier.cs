using System.Net;
using System.Net.Http.Json;
using System.Text;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace CaddyManager.Ops.Events;

/// <summary>A message to deliver through all enabled channels.</summary>
internal sealed record Notification(
    string Title,
    string Text,
    string Severity,
    string Category,
    DateTime TimeUtc,
    string? Details = null);

/// <summary>
/// Delivers notifications via SMTP (MailKit; password or Microsoft 365 OAuth2 client-credentials authentication) and a
/// webhook (generic JSON, Slack or Teams Workflows Adaptive Card). Returns per-channel error messages instead of throwing.
/// </summary>
internal sealed class Notifier(
    IStore store,
    ISecretProtector secrets,
    NotificationHttp http,
    OAuthTokenProvider oauth,
    ILogger<Notifier> logger) : INotifier
{
    private static readonly TimeSpan ChannelTimeout = TimeSpan.FromSeconds(30);

    public static bool HasEnabledChannel(NotificationSettings s) =>
        s.SmtpEnabled || (s.WebhookEnabled && !string.IsNullOrWhiteSpace(s.WebhookUrl));

    public Task<List<string>> SendAsync(string subject, string body, CancellationToken ct = default) =>
        SendAsync(new Notification(subject, body, "info", "general", DateTime.UtcNow), store.GetSettings<NotificationSettings>(), ct);

    public Task<List<string>> SendEventAsync(EventEntry e, CancellationToken ct = default)
    {
        var settings = store.GetSettings<NotificationSettings>();
        var server = NotificationFormatter.ServerName(store);
        return SendAsync(new Notification(
            NotificationFormatter.Subject(e, server),
            e.Message,
            NotificationFormatter.SeverityWord(e.Severity).ToLowerInvariant(),
            e.Category,
            e.CreatedAt,
            e.Details), settings, ct);
    }

    /// <summary>Sends to every enabled channel. Empty list = all succeeded (or none enabled).</summary>
    public async Task<List<string>> SendAsync(Notification n, NotificationSettings s, CancellationToken ct)
    {
        var errors = new List<string>();
        var server = NotificationFormatter.ServerName(store);
        var uiUrl = NotificationFormatter.UiUrl(store);

        if (s.SmtpEnabled)
        {
            try
            {
                await SendEmailAsync(n, s, server, uiUrl, ct);
                logger.LogInformation("E-mail notification sent to {Count} recipient(s): {Title}", s.Recipients.Count, n.Title);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "E-mail notification via {Host}:{Port} failed", s.SmtpHost, s.SmtpPort);
                errors.Add($"E-mail (SMTP {s.SmtpHost}:{s.SmtpPort}): {Describe(ex)}");
            }
        }

        if (s.WebhookEnabled && !string.IsNullOrWhiteSpace(s.WebhookUrl))
        {
            try
            {
                await SendWebhookAsync(n, s.WebhookUrl!, s.WebhookFormat, server, uiUrl, ct);
                logger.LogInformation("Webhook notification sent: {Title}", n.Title);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Webhook notification failed");
                errors.Add($"Webhook: {Describe(ex)}");
            }
        }
        return errors;
    }

    private async Task SendEmailAsync(Notification n, NotificationSettings s, string server, string? uiUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.SmtpHost)) throw new InvalidOperationException("SMTP host is not configured.");
        if (s.Recipients.Count == 0) throw new InvalidOperationException("No recipients are configured.");

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(string.IsNullOrWhiteSpace(s.SmtpFrom) ? $"caddy-proxy-manager@{Environment.MachineName}" : s.SmtpFrom));
        foreach (var r in s.Recipients.Where(r => !string.IsNullOrWhiteSpace(r)))
            message.To.Add(MailboxAddress.Parse(r.Trim()));
        message.Subject = n.Title;
        message.Headers.Add("X-Mailer", AppPaths.ProductName);
        message.Body = new BodyBuilder
        {
            TextBody = NotificationFormatter.PlainText(n, server, uiUrl),
            HtmlBody = NotificationFormatter.Html(n, server, uiUrl),
        }.ToMessageBody();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ChannelTimeout);
        using var client = new SmtpClient { Timeout = (int)ChannelTimeout.TotalMilliseconds };
        if (s.AllowInvalidCertificate)
            client.ServerCertificateValidationCallback = (_, _, _, _) => true;
        var security = SocketOptions(s);
        // Fetch the OAuth token before connecting so an Entra ID problem is reported as such.
        string? token = null;
        if (s.SmtpAuth == SmtpAuthMode.OAuth2ClientCredentials)
        {
            if (string.IsNullOrWhiteSpace(s.SmtpUsername))
                throw new InvalidOperationException("OAuth2 needs the sending mailbox in 'SMTP username' (e.g. alerts@contoso.com).");
            if (string.IsNullOrWhiteSpace(s.OAuthTenantId) || string.IsNullOrWhiteSpace(s.OAuthClientId) || string.IsNullOrEmpty(s.OAuthClientSecretProtected))
                throw new InvalidOperationException("OAuth2 needs the tenant ID, client ID and client secret of the Entra ID app registration.");
            token = await oauth.GetTokenAsync(s.OAuthTenantId, s.OAuthClientId, secrets.Unprotect(s.OAuthClientSecretProtected), timeout.Token);
        }

        await client.ConnectAsync(s.SmtpHost.Trim(), s.SmtpPort, security, timeout.Token);
        try
        {
            await AuthenticateAndSendAsync(client, message, s, token, timeout.Token);
        }
        catch (AuthenticationException ex) when (s.SmtpAuth == SmtpAuthMode.Password && IsExchangeOnline(s.SmtpHost))
        {
            throw new InvalidOperationException($"{ex.Message.TrimEnd('.')}. {BasicAuthRetirementHint}", ex);
        }
        catch (SmtpCommandException ex) when (s.SmtpAuth == SmtpAuthMode.OAuth2ClientCredentials && SendsAsOtherMailbox(s)
                                              && ex.ErrorCode is SmtpErrorCode.SenderNotAccepted or SmtpErrorCode.MessageNotAccepted)
        {
            var from = MailboxAddress.Parse(s.SmtpFrom.Trim()).Address;
            throw new InvalidOperationException(
                $"{ex.Message.TrimEnd('.')}. The sender address {from} differs from the authenticated mailbox {s.SmtpUsername!.Trim()}: " +
                $"with OAuth2 client credentials Exchange Online also needs SendAs for the app's service principal " +
                $"(Add-RecipientPermission -Identity {from} -Trustee <service principal> -AccessRights SendAs), or use the mailbox itself as sender.", ex);
        }
        await client.DisconnectAsync(true, timeout.Token);
    }

    /// <summary>
    /// Microsoft 365 retires Basic authentication (password) for SMTP AUTH; OAuth2 client credentials keep working:
    /// https://learn.microsoft.com/en-us/exchange/clients-and-mobile-in-exchange-online/deprecation-of-basic-authentication-exchange-online
    /// </summary>
    internal const string BasicAuthRetirementHint =
        "Microsoft 365 is retiring Basic authentication (user name + password) for SMTP AUTH (disabled by default for existing tenants " +
        "from the end of December 2026). Switch 'Authentication' to 'Microsoft 365 OAuth2 (client credentials)' in Settings → Notifications " +
        "(see docs/notifications.md).";

    /// <summary>True for Exchange Online SMTP endpoints (smtp.office365.com, *.mail.protection.outlook.com, smtp-mail.outlook.com).</summary>
    internal static bool IsExchangeOnline(string? host)
    {
        var h = (host ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        return h.EndsWith(".office365.com", StringComparison.Ordinal) || h.EndsWith(".outlook.com", StringComparison.Ordinal);
    }

    /// <summary>
    /// True when the From address is not the mailbox the OAuth2 token authenticates. Exchange Online then needs SendAs:
    /// "If you're trying to use Client Credential Grant Flow with SendAs, you need to grant SendAs permissions to the sender"
    /// (https://learn.microsoft.com/en-us/exchange/client-developer/legacy-protocols/how-to-authenticate-an-imap-pop-smtp-application-by-using-oauth).
    /// </summary>
    internal static bool SendsAsOtherMailbox(NotificationSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.SmtpFrom) || string.IsNullOrWhiteSpace(s.SmtpUsername)) return false;
        if (!MailboxAddress.TryParse(s.SmtpFrom.Trim(), out var from)) return false;
        return !string.Equals(from.Address, s.SmtpUsername.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// MailKit's SecureSocketOptions.Auto continues without encryption when the server does not offer TLS
    /// ("If the server does not support SSL or TLS, then the connection will continue without any encryption":
    /// https://mimekit.net/docs/html/T_MailKit_Security_SecureSocketOptions.htm), which would expose the SMTP password or the
    /// OAuth2 bearer token to a STARTTLS-stripping attacker. When credentials are sent, "Auto" therefore requires TLS:
    /// implicit TLS on port 465, STARTTLS (fails if not offered) otherwise.
    /// </summary>
    internal static SecureSocketOptions SocketOptions(NotificationSettings s) => s.SmtpSecurity switch
    {
        SmtpSecurity.None => SecureSocketOptions.None,
        SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
        SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        _ when SendsCredentials(s) => s.SmtpPort == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls,
        _ => SecureSocketOptions.Auto,
    };

    internal static bool SendsCredentials(NotificationSettings s) =>
        s.SmtpAuth == SmtpAuthMode.OAuth2ClientCredentials ||
        (s.SmtpAuth == SmtpAuthMode.Password && !string.IsNullOrWhiteSpace(s.SmtpUsername));

    private async Task AuthenticateAndSendAsync(SmtpClient client, MimeMessage message, NotificationSettings s, string? token, CancellationToken ct)
    {
        switch (s.SmtpAuth)
        {
            case SmtpAuthMode.OAuth2ClientCredentials:
                try
                {
                    await client.AuthenticateAsync(new SaslMechanismOAuth2(s.SmtpUsername!.Trim(), token!), ct);
                }
                catch (AuthenticationException ex)
                {
                    oauth.Invalidate();
                    throw new InvalidOperationException(
                        $"The SMTP server rejected the OAuth2 token for {s.SmtpUsername!.Trim()} ({ex.Message.TrimEnd('.')}). In Exchange Online: register the app's " +
                        "service principal (New-ServicePrincipal -AppId <application ID> -ObjectId <object ID of the Enterprise application, not of the " +
                        "app registration>), grant it FullAccess to the mailbox (Add-MailboxPermission) and make sure SMTP AUTH is " +
                        "enabled for the mailbox (Set-CASMailbox -SmtpClientAuthenticationDisabled $false).", ex);
                }
                break;
            case SmtpAuthMode.Password when !string.IsNullOrWhiteSpace(s.SmtpUsername):
                var password = string.IsNullOrEmpty(s.SmtpPasswordProtected) ? "" : secrets.Unprotect(s.SmtpPasswordProtected);
                await client.AuthenticateAsync(s.SmtpUsername.Trim(), password, ct);
                break;
        }
        await client.SendAsync(message, ct);
    }

    private async Task SendWebhookAsync(Notification n, string url, WebhookFormat format, string server, string? uiUrl, CancellationToken ct)
    {
        var payload = WebhookPayloads.Build(format, n, server, uiUrl);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ChannelTimeout);
        // Through the outbound proxy of Settings → Updates when one is set (NotificationHttp).
        using var resp = await http.Client.PostAsJsonAsync(url, payload, timeout.Token);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(timeout.Token);
            if (body.Length > 300) body = body[..300] + "…";
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}{(string.IsNullOrWhiteSpace(body) ? "" : ": " + body)}");
        }
    }

    private static string Describe(Exception ex)
    {
        if (ex is OperationCanceledException) return "timed out";
        var msg = ex.Message;
        if (ex.InnerException is { } inner && !msg.Contains(inner.Message, StringComparison.Ordinal))
            msg += " (" + inner.Message + ")";
        return msg;
    }
}

internal static class NotificationFormatter
{
    public static string SeverityWord(EventSeverity s) => s switch
    {
        EventSeverity.Error => "Error",
        EventSeverity.Warning => "Warning",
        EventSeverity.Recovered => "Recovered",
        _ => "Info",
    };

    public static string Subject(EventEntry e, string server) => $"[{server}] {SeverityWord(e.Severity)}: {e.Message}";

    public static string ServerName(IStore store)
    {
        try
        {
            var name = store.GetSettings<UiSettings>().DisplayName;
            if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
        }
        catch { /* fall back */ }
        return Environment.MachineName;
    }

    private static string? _fqdn;

    /// <summary>Best-effort URL of the management UI for links in notifications.</summary>
    public static string? UiUrl(IStore store)
    {
        try
        {
            var ui = store.GetSettings<UiSettings>();
            if (_fqdn is null)
            {
                try { _fqdn = Dns.GetHostEntry(Environment.MachineName).HostName; }
                catch { _fqdn = Environment.MachineName; }
            }
            var host = ui.BindAddress is "0.0.0.0" or "::" or "" ? _fqdn : ui.BindAddress;
            return ui.HttpsEnabled ? $"https://{host}:{ui.HttpsPort}/" : $"http://{host}:{ui.Port}/";
        }
        catch
        {
            return null;
        }
    }

    public static string PlainText(EventEntry e, string server, string? uiUrl) =>
        PlainText(new Notification(Subject(e, server), e.Message, SeverityWord(e.Severity), e.Category, e.CreatedAt, e.Details), server, uiUrl);

    private static (string Utc, string Local) Times(DateTime utc)
    {
        utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local);
        var zone = TimeZoneInfo.Local.IsDaylightSavingTime(local) ? TimeZoneInfo.Local.DaylightName : TimeZoneInfo.Local.StandardName;
        return (utc.ToString("yyyy-MM-dd HH:mm:ss") + " UTC", local.ToString("yyyy-MM-dd HH:mm:ss") + " " + zone);
    }

    public static string PlainText(Notification n, string server, string? uiUrl)
    {
        var (utc, local) = Times(n.TimeUtc);
        var sb = new StringBuilder()
            .AppendLine(n.Text)
            .AppendLine()
            .AppendLine($"Server:   {server}")
            .AppendLine($"Severity: {Capitalize(n.Severity)}")
            .AppendLine($"Category: {n.Category}")
            .AppendLine($"Time:     {utc} ({local})");
        if (!string.IsNullOrWhiteSpace(n.Details)) sb.AppendLine().AppendLine("Details:").AppendLine(n.Details);
        if (uiUrl is not null) sb.AppendLine().AppendLine($"Open {AppPaths.ProductName}: {uiUrl}");
        sb.AppendLine().AppendLine($"-- {AppPaths.ProductName}");
        return sb.ToString();
    }

    public static string WebhookText(Notification n, string server, string? uiUrl)
    {
        var (utc, _) = Times(n.TimeUtc);
        var sb = new StringBuilder().Append($"*{n.Title}*\n{n.Text}\nServer: {server} · Severity: {Capitalize(n.Severity)} · Category: {n.Category} · {utc}");
        if (!string.IsNullOrWhiteSpace(n.Details))
        {
            var d = n.Details.Length > 1500 ? n.Details[..1500] + "…" : n.Details;
            sb.Append("\n").Append(d);
        }
        if (uiUrl is not null) sb.Append("\n").Append(uiUrl);
        return sb.ToString();
    }

    public static string Html(Notification n, string server, string? uiUrl)
    {
        static string H(string? s) => WebUtility.HtmlEncode(s ?? "");
        var (utc, local) = Times(n.TimeUtc);
        var color = n.Severity.ToLowerInvariant() switch
        {
            "error" => "#b91c1c",
            "warning" => "#b45309",
            "recovered" => "#047857",
            _ => "#334155",
        };
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><body style=\"margin:0;padding:24px;background:#f8fafc;font-family:Segoe UI,Arial,sans-serif;color:#0f172a;font-size:14px\">");
        sb.Append("<table role=\"presentation\" style=\"max-width:640px;width:100%;background:#ffffff;border:1px solid #e2e8f0;border-radius:6px;border-collapse:separate\">");
        sb.Append($"<tr><td style=\"padding:16px 20px;border-bottom:1px solid #e2e8f0\"><span style=\"display:inline-block;padding:2px 8px;border-radius:4px;background:{color};color:#fff;font-size:12px;font-weight:600\">{H(Capitalize(n.Severity))}</span>");
        sb.Append($"<div style=\"margin-top:8px;font-size:16px;font-weight:600\">{H(n.Text)}</div></td></tr>");
        sb.Append("<tr><td style=\"padding:12px 20px\"><table role=\"presentation\" style=\"border-collapse:collapse;font-size:13px\">");
        void Row(string k, string v) =>
            sb.Append($"<tr><td style=\"padding:3px 16px 3px 0;color:#64748b\">{H(k)}</td><td style=\"padding:3px 0\">{H(v)}</td></tr>");
        Row("Server", server);
        Row("Category", n.Category);
        Row("Time (UTC)", utc);
        Row("Time (local)", local);
        sb.Append("</table></td></tr>");
        if (!string.IsNullOrWhiteSpace(n.Details))
            sb.Append($"<tr><td style=\"padding:0 20px 16px\"><pre style=\"margin:0;padding:12px;background:#f1f5f9;border-radius:4px;font-family:Consolas,monospace;font-size:12px;white-space:pre-wrap\">{H(n.Details)}</pre></td></tr>");
        if (uiUrl is not null)
            sb.Append($"<tr><td style=\"padding:0 20px 16px\"><a href=\"{H(uiUrl)}\" style=\"color:#0f766e\">Open {H(AppPaths.ProductName)}</a></td></tr>");
        sb.Append($"<tr><td style=\"padding:12px 20px;border-top:1px solid #e2e8f0;color:#94a3b8;font-size:12px\">{H(AppPaths.ProductName)} on {H(server)}</td></tr>");
        sb.Append("</table></body></html>");
        return sb.ToString();
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
