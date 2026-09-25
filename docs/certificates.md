# Certificates

Every site has a **TLS mode** (host editor › TLS):

| Mode | Use for | How |
|---|---|---|
| Automatic (ACME) | Public names | Caddy obtains and renews certificates from the CA in **Settings › Caddy** (Let's Encrypt, Let's Encrypt staging, ZeroSSL, or a custom ACME directory such as an internal step-ca / AD CS ACME). Needs public DNS pointing at the server and inbound 80/443 — unless a DNS challenge is configured. |
| Internal CA | Internal names (`app.corp.local`) | Caddy's own CA issues short-lived certificates (≈12 h, auto-renewed). Distribute the root once via GPO. |
| Custom certificate | Your PKI / purchased certs | A certificate managed on the **Certificates** page, assigned to the host. |
| None (HTTP only) | Trusted networks, TLS terminated upstream | Plain HTTP on port 80. |

## Your own certificates ("point domain X at certificate Y")

**Security › Certificates › Add certificate**, then pick it on the host's TLS tab (*Custom certificate*).
The host editor warns when the certificate does not cover the host's domains.

| Source | When | Renewal |
|---|---|---|
| Upload PEM (certificate + key) | One-off / purchased certificates | Replace via the row menu |
| Upload PFX (+ password) | Exports from Windows / CA portals | Replace via the row menu |
| Paste PEM | Quick import | Replace |
| PEM files on disk or share (admin) | Another tool renews files in place (e.g. win-acme PEM output, a script) | Detected automatically (file watcher + 5-minute poll); Caddy reloads |
| PFX file on disk or share (admin) | win-acme default output, CA exports | Re-converted when the PFX changes |
| Windows certificate store (admin) | AD CS auto-enrolment / `certreq` into `LocalMachine\My` | *Follow renewals by subject*: the newest valid certificate with a private key and matching subject/SAN is exported every 15 minutes; or pin a thumbprint |

Uploaded and converted certificates are written to the **certificate store**
(`C:\ProgramData\CaddyProxyManager\certificates\<id>\fullchain.pem` + `privkey.pem`, SYSTEM/Administrators only).
Set **Settings › Caddy › Certificate store path** to use a shared location instead, e.g.
`\\fileserver\pki$\caddy`. The services run as LocalSystem and reach shares as the computer account
(`DOMAIN\SERVER$`): grant that account share and NTFS *Modify* on the folder.

Windows-store certificates must have an **exportable private key** (certificate template: *Allow private key to be
exported*), because Caddy reads PEM files.

## Wildcard certificates

- **Internal CA / custom**: supported directly.
- **ACME**: wildcard names require the DNS-01 challenge, which needs a DNS provider plugin:
  1. **Caddy › Plugins**: add e.g. `github.com/caddy-dns/cloudflare`, then *Rebuild & install*.
  2. **Settings › Caddy › Plugins & advanced › ACME issuer JSON** (stored encrypted):
     ```json
     { "challenges": { "dns": { "provider": { "name": "cloudflare", "api_token": "<token>" } } } }
     ```

## Internal CA root

**Certificates › Internal root CA** downloads Caddy's root certificate. Deploy it to *Trusted Root Certification
Authorities* with Group Policy (see [group-policy.md](group-policy.md)). The root key lives in
`C:\ProgramData\CaddyProxyManager\caddy\data\pki` — back it up (it is included in backups) and protect it.

## Monitoring

Certificates expiring within *N* days (Notifications › alert rules), certificates that fail to be issued (ACME
errors from Caddy's log are included in the alert) and sync failures of file/PFX/store certificates raise alerts.
