# Notifications

**Administration › Notifications** — every alert is stored under **Server › Events**; enabled channels also get it.

## Channels

- **E-mail (SMTP)** — host, port, security (None / STARTTLS / TLS on connect / Auto), sender, recipients.
  - *Auto* lets MailKit fall back to an unencrypted connection when the server offers no TLS
    ([SecureSocketOptions](https://mimekit.net/docs/html/T_MailKit_Security_SecureSocketOptions.htm)). The manager
    therefore treats *Auto* as "TLS required" whenever it sends credentials (a password or an OAuth2 token): TLS on
    connect for port 465, STARTTLS otherwise — a server (or an attacker) that does not offer STARTTLS gets an error,
    never the password. *Auto* without authentication still works with plain internal relays.
  - *None* sends everything, including credentials, in clear text: only for relays on a trusted network.
  - Revocation: the TLS certificate of the mail server is checked for revocation (MailKit's default). Windows downloads
    the CRL/OCSP data through the **WinHTTP proxy** (`netsh winhttp show proxy`), not through the proxy in Settings ›
    Updates; on servers that reach the Internet only through a proxy, set the WinHTTP proxy too (Readiness ›
    Connectivity shows both).

  Authentication:
  - *None* — e.g. an internal relay or the Microsoft 365 connector relay (port 25 to your tenant's MX, allowed by IP).
  - *Password* — classic SMTP AUTH. **Microsoft 365:** Basic authentication for SMTP AUTH is disabled by default for
    existing tenants at the end of December 2026 (administrators can re-enable it until Microsoft removes it; new
    tenants do not offer it) —
    [timeline](https://techcommunity.microsoft.com/blog/exchange/updated-exchange-online-smtp-auth-basic-authentication-deprecation-timeline/4489835).
    Readiness warns when `smtp.office365.com` is used with a password, and a rejected password mentions it. Switch to OAuth2.
  - *Microsoft 365 OAuth2* — client credentials
    ([Microsoft's guide](https://learn.microsoft.com/en-us/exchange/client-developer/legacy-protocols/how-to-authenticate-an-imap-pop-smtp-application-by-using-oauth)):
    1. Entra ID → App registrations → New; create a client secret.
    2. API permissions → *Office 365 Exchange Online* → Application permission `SMTP.SendAsApp` → grant admin consent.
    3. Exchange Online PowerShell: `New-ServicePrincipal -AppId <application (client) ID> -ObjectId <object ID>` — the
       **object ID of the Enterprise application** (Entra ID › Enterprise applications), *not* the object ID shown on
       the App registration — then
       `Add-MailboxPermission -Identity sender@contoso.com -User <service principal id> -AccessRights FullAccess`.
    4. In the manager: server `smtp.office365.com`, port 587, STARTTLS, username = the sender mailbox, tenant ID,
       client ID and secret.
    5. If the **sender address differs from that mailbox**, Exchange also needs SendAs:
       `Add-RecipientPermission -Identity <sender address> -Trustee <service principal> -AccessRights SendAs`.
       Otherwise the test fails with `554 5.2.252 SendAsDenied` (the error message says so).
- **Webhook** — Generic JSON, Slack (incoming webhook) or Microsoft Teams (*Workflows* "post to a channel when a
  webhook request is received"; Adaptive Card payload). For Teams, set the trigger's *Who can trigger the flow* to
  **Anyone**: the manager does not send an Entra ID token. Workflows answers `202 Accepted` when the flow run starts,
  so *Send test* succeeding means "accepted by Workflows" — check the channel (and the flow's run history if nothing
  arrives). Messages are limited to 28 KB (the manager keeps details far below that).
- **Windows Event Log** — Application log, source *Caddy Proxy Manager* (for SCOM / Sentinel / forwarding).

Use **Send test** after saving.

## Proxies and network access

- Webhooks and the Entra ID token request (`login.microsoftonline.com`) use the **outbound proxy of Settings ›
  Updates** when one is set, with its credentials (`http://user:password@proxy:port`). Destinations in the *No proxy*
  list (by default private address ranges, `localhost` and `.local`) and loopback addresses are contacted directly.
  Without an outbound proxy they use the service's system settings (proxy environment variables / LocalSystem's
  WinINet settings), like any .NET service.
- **SMTP is never sent through the HTTP proxy.** The server needs direct outbound TCP 587 (or 465 / 25) to the mail
  server; for Microsoft 365 behind a proxy-only network, use a local relay (connector) or open 587 to Exchange Online.

## Alert rules

| Rule | Fires when |
|---|---|
| Caddy down | The Caddy service is stopped/crashed or its admin API is unreachable (checked every 30 s; optional auto-restart, max 3 attempts per 10 minutes). Not raised when an operator stopped Caddy on purpose. |
| Configuration failure | Caddy rejected a configuration, a scheduled backup failed |
| Upstream unhealthy | A proxy backend fails its health checks: an active check, or passive checks on a host with several upstreams. A single upstream without an active check is not monitored (see [troubleshooting.md](troubleshooting.md)). |
| Certificate expiry | A certificate expires within the configured days, fails to be issued, or a file/PFX/store certificate cannot be synced |
| Update available | A new Caddy (or manager) version is released |
| Readiness failure | The daily readiness run finds a new failure |

Repeated alerts with the same key are suppressed for the *cooldown* period; a *Recovered* notice is sent when the
problem clears.
