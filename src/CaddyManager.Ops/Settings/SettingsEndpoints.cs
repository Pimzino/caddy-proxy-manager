using System.Net;
using System.Net.Mail;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using CaddyManager.Ops.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Ops.Settings;

internal static class SettingsEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/settings").RequireAuthorization(Policies.Admin);

        // ------------------------------------------------------------ notifications
        g.MapGet("/notifications", (IStore store) => Results.Json(SettingsWire.ToWire(store.GetSettings<NotificationSettings>())));

        g.MapPut("/notifications", (JsonObject body, IStore store, ISecretProtector secrets, IAuditLog audit) =>
        {
            var current = store.GetSettings<NotificationSettings>();
            NotificationSettings next;
            try { next = SettingsWire.Apply(current, body, secrets); }
            catch (SettingsInputException ex) { return ApiResults.BadRequest(ex.Message); }

            next.SmtpHost = next.SmtpHost?.Trim() ?? "";
            next.SmtpFrom = next.SmtpFrom?.Trim() ?? "";
            next.SmtpUsername = string.IsNullOrWhiteSpace(next.SmtpUsername) ? null : next.SmtpUsername.Trim();
            next.Recipients = (next.Recipients ?? []).SelectMany(r => (r ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            next.WebhookUrl = string.IsNullOrWhiteSpace(next.WebhookUrl) ? null : next.WebhookUrl.Trim();

            var v = new Validator();
            if (next.SmtpEnabled)
            {
                v.Require(next.SmtpHost.Length > 0, "smtpHost", "SMTP host is required when e-mail is enabled.");
                v.Require(IsValidAddress(next.SmtpFrom), "smtpFrom", "A valid sender address is required when e-mail is enabled.");
                v.Require(next.Recipients.Count > 0, "recipients", "At least one recipient is required when e-mail is enabled.");
            }
            v.Require(next.SmtpPort is >= 1 and <= 65535, "smtpPort", "SMTP port must be between 1 and 65535.");
            foreach (var r in next.Recipients.Where(r => !IsValidAddress(r)))
                v.Add("recipients", $"'{r}' is not a valid e-mail address.");
            if (next.WebhookEnabled || next.WebhookUrl is not null)
                v.Require(next.WebhookUrl is not null && Uri.TryCreate(next.WebhookUrl, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http"),
                    "webhookUrl", "Webhook URL must be an absolute http(s) URL.");
            v.Require(next.CooldownMinutes is >= 0 and <= 10080, "cooldownMinutes", "Cooldown must be between 0 and 10080 minutes.");
            v.Require(next.CertificateExpiryDays is >= 1 and <= 365, "certificateExpiryDays", "Certificate expiry warning must be between 1 and 365 days.");
            if (!v.IsValid) return v.ToResult();

            store.SaveSettings(next);
            var detail = SettingsWire.TouchesSecret(body, "smtpPassword") ? "SMTP password changed" : null;
            audit.Record("updated", "settings", "notifications", "Notification settings", detail);
            return Results.Json(SettingsWire.ToWire(next));
        });

        g.MapPost("/notifications/test", async (IStore store, INotifier notifier, ICurrentUser current, IAuditLog audit,
            ILogger<Notifier> logger, CancellationToken ct) =>
        {
            var s = store.GetSettings<NotificationSettings>();
            if (!Notifier.HasEnabledChannel(s))
                return Results.Ok(new { ok = false, errors = new[] { "No notification channel is enabled. Enable e-mail (SMTP) or a webhook and save first." } });
            var server = NotificationFormatter.ServerName(store);
            List<string> errors;
            try
            {
                var n = new Notification($"[{server}] Test notification", $"This is a test notification from {AppPaths.ProductName}, sent by {current.UserName}.",
                    "info", "test", DateTime.UtcNow, "If you received this message, notifications are configured correctly.");
                errors = notifier is Notifier real
                    ? await real.SendAsync(n, s, ct)
                    : await notifier.SendAsync(n.Title, n.Text, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Test notification failed");
                errors = [ex.Message];
            }
            audit.Record("test", "settings", "notifications", "Notification settings", errors.Count == 0 ? "Test sent" : "Test failed: " + string.Join("; ", errors));
            return Results.Ok(new { ok = errors.Count == 0, errors });
        });

        // ------------------------------------------------------------ UI listener
        g.MapGet("/ui", (IStore store) => Results.Json(SettingsWire.ToWire(store.GetSettings<UiSettings>())));

        g.MapPut("/ui", (JsonObject body, IStore store, ISecretProtector secrets, IAuditLog audit) =>
        {
            var current = store.GetSettings<UiSettings>();
            UiSettings next;
            try { next = SettingsWire.Apply(current, body, secrets); }
            catch (SettingsInputException ex) { return ApiResults.BadRequest(ex.Message); }

            next.BindAddress = string.IsNullOrWhiteSpace(next.BindAddress) ? "0.0.0.0" : next.BindAddress.Trim();
            next.HttpsPfxPath = string.IsNullOrWhiteSpace(next.HttpsPfxPath) ? null : next.HttpsPfxPath.Trim();
            next.DisplayName = string.IsNullOrWhiteSpace(next.DisplayName) ? null : next.DisplayName.Trim();

            var v = new Validator();
            v.Require(next.Port is >= 1 and <= 65535, "port", "Port must be between 1 and 65535.");
            v.Require(next.HttpsPort is >= 1 and <= 65535, "httpsPort", "HTTPS port must be between 1 and 65535.");
            v.Require(!next.HttpsEnabled || next.HttpsPort != next.Port, "httpsPort", "HTTPS port must differ from the HTTP port.");
            v.Require(IPAddress.TryParse(next.BindAddress, out _), "bindAddress", "Bind address must be an IP address (0.0.0.0 = all interfaces, 127.0.0.1 = local only).");
            v.Require(next.SessionHours is >= 1 and <= 720, "sessionHours", "Session length must be between 1 and 720 hours.");
            if (next.HttpsEnabled && next.HttpsPfxPath is not null)
            {
                if (!File.Exists(next.HttpsPfxPath))
                    v.Add("httpsPfxPath", $"PFX file '{next.HttpsPfxPath}' does not exist or is not accessible to the service.");
                else if (TryLoadPfx(next.HttpsPfxPath, next.HttpsPfxPasswordProtected, secrets) is { } pfxError)
                    v.Add("httpsPfxPath", pfxError);
            }
            if (!v.IsValid) return v.ToResult();

            var restartRequired = current.Port != next.Port || current.BindAddress != next.BindAddress ||
                                  current.HttpsEnabled != next.HttpsEnabled || current.HttpsPort != next.HttpsPort ||
                                  current.HttpsPfxPath != next.HttpsPfxPath ||
                                  current.HttpsPfxPasswordProtected != next.HttpsPfxPasswordProtected;
            store.SaveSettings(next);
            audit.Record("updated", "settings", "ui", "UI settings", restartRequired ? "Listener changed (restart required)" : null);
            return Results.Ok(new { item = SettingsWire.ToWire(next), restartRequired });
        });
    }

    private static bool IsValidAddress(string? s) =>
        !string.IsNullOrWhiteSpace(s) && MailAddress.TryCreate(s, out var a) && a.Address.Contains('@');

    private static string? TryLoadPfx(string path, string? protectedPassword, ISecretProtector secrets)
    {
        try
        {
            var pwd = string.IsNullOrEmpty(protectedPassword) ? null : secrets.Unprotect(protectedPassword);
            using var cert = X509CertificateLoader.LoadPkcs12FromFile(path, pwd,
                OperatingSystem.IsWindows() ? X509KeyStorageFlags.EphemeralKeySet : X509KeyStorageFlags.DefaultKeySet);
            return cert.HasPrivateKey ? null : "The PFX file does not contain a private key.";
        }
        catch (Exception ex)
        {
            return $"The PFX file could not be opened (wrong password or corrupt file): {ex.Message}";
        }
    }
}
