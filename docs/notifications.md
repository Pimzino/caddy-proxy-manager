# Notifications

**Administration › Notifications** — every alert is stored under **Server › Events**; enabled channels also get it.

## Channels

- **E-mail (SMTP)** — host, port, security (None / STARTTLS / TLS on connect / Auto), sender, recipients.
  Authentication:
  - *None* — e.g. an internal relay or the Microsoft 365 connector relay (port 25 to your tenant's MX, allowed by IP).
  - *Password* — classic SMTP AUTH.
  - *Microsoft 365 OAuth2* — for Exchange Online after Basic auth retirement:
    1. Entra ID → App registrations → New; create a client secret.
    2. API permissions → *Office 365 Exchange Online* → Application permission `SMTP.SendAsApp` → grant admin consent.
    3. Exchange Online PowerShell: `New-ServicePrincipal -AppId <appId> -ObjectId <enterprise app object id>` and
       `Add-MailboxPermission -Identity sender@contoso.com -User <service principal id> -AccessRights FullAccess`.
    4. In the manager: server `smtp.office365.com`, port 587, STARTTLS, username = the sender mailbox, tenant ID,
       client ID and secret.
- **Webhook** — Generic JSON, Slack (incoming webhook) or Microsoft Teams (*Workflows* "post to a channel when a
  webhook request is received"; Adaptive Card payload).
- **Windows Event Log** — Application log, source *Caddy Proxy Manager* (for SCOM / Sentinel / forwarding).

Use **Send test** after saving.

## Alert rules

| Rule | Fires when |
|---|---|
| Caddy down | The Caddy service is stopped/crashed or its admin API is unreachable (checked every 30 s; optional auto-restart, max 3 attempts per 10 minutes). Not raised when an operator stopped Caddy on purpose. |
| Configuration failure | Caddy rejected a configuration, a scheduled backup failed |
| Upstream unhealthy | A proxy backend fails health checks |
| Certificate expiry | A certificate expires within the configured days, fails to be issued, or a file/PFX/store certificate cannot be synced |
| Update available | A new Caddy (or manager) version is released |
| Readiness failure | The daily readiness run finds a new failure |

Repeated alerts with the same key are suppressed for the *cooldown* period; a *Recovered* notice is sent when the
problem clears.
