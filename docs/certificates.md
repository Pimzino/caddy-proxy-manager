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

## DNS-01 challenge (wildcards, servers not reachable from the internet)

With the DNS challenge the CA checks a TXT record `_acme-challenge.<domain>` that Caddy creates through your DNS
provider's API, so no inbound port 80/443 is needed and **wildcard** names (`*.example.com`) can be issued.

1. **Caddy › Plugins**: add the provider's plugin (e.g. `github.com/caddy-dns/cloudflare`) and *Rebuild & install*.
   Settings › Caddy offers this when the selected provider is not in the installed Caddy.
2. **Settings › Caddy › ACME challenge**: pick the **DNS provider** from the list (Cloudflare, Route 53, Azure DNS,
   DigitalOcean, Google Cloud DNS, OVHcloud, GoDaddy, Porkbun, Namecheap, Gandi, Duck DNS, IONOS, deSEC,
   Linode, Vultr, Netlify, DNSimple, Bunny, NameSilo, Alibaba Cloud, PowerDNS, ACME-DNS, RFC 2136) and fill in its
   fields. Credentials (tokens, secrets, TSIG keys) are **write-only**: stored encrypted, never shown again, and
   replaced by `***` (also in their URL-encoded forms) in Caddy error messages, `caddy.log` as shown by *Logs › Caddy*,
   certificate events and notifications, Caddy start errors, and any configuration a non-administrator can see. Other
   providers can be used by their module name (`dns.providers.<name>`) with plain options. The settings are refused
   while the selected provider's plugin is missing from the installed Caddy and a host uses the DNS challenge.
   *Hetzner* is not offered: the only Hetzner plugin the Caddy download service builds (`caddy-dns/hetzner` v1) uses
   the old DNS Console API, which Hetzner shut down in May 2026; use the HTTP challenge, or host the zone at a
   supported provider.
3. Choose where DNS-01 is used: **Default challenge** = DNS for every ACME host, or per host on the TLS tab
   (*ACME challenge*: Default / HTTP / DNS). Wildcard names always use DNS once a provider is configured.

Optional settings: **propagation delay** (wait before the first check), **propagation timeout** (default 2 minutes;
`-1` skips the check — useful when the check cannot see your authoritative servers), **TTL** of the TXT record,
**resolvers** (`host:port`, e.g. `10.0.0.53:53`; used for the zone lookup and propagation check — set them behind
split-horizon DNS where the internal resolvers do not show the public zone).

*RFC 2136* works with BIND, Knot, PowerDNS and other servers that accept dynamic updates signed with a TSIG key
(`server` host:port, key name, algorithm such as `hmac-sha256`, base64 secret). **Windows DNS does not work with it**:
Active Directory-integrated zones accept only Kerberos-signed (GSS-TSIG) secure updates, which the provider cannot send.
For a zone on Windows DNS, use the HTTP challenge, or host the zone on a TSIG-capable server or at a supported DNS
provider.

*ACME-DNS* ([acme-dns](https://github.com/joohoi/acme-dns)) is a small DNS server that only holds challenge records.
Register an account (`POST <server>/register`), enter `username`, `password`, `subdomain` and `server_url` as the
provider fields, and create the CNAME `_acme-challenge.<domain>` → the returned `fulldomain` yourself, once per domain.
An acme-dns account keeps only its two newest TXT values, so with many names issued at once a validation can fail and
Caddy retries it later.

Enabling DNS disables the HTTP and TLS-ALPN challenges for those names; IP addresses on an ACME host always keep the
HTTP / TLS-ALPN challenges (the DNS challenge cannot validate IP addresses). The *ACME issuer JSON* (Plugins & advanced)
is still merged into every ACME issuer after generation, for options the form does not cover, except
`challenges.dns.provider`: the provider is configured under *ACME challenge* only, and saving the settings with both is
refused (remove `challenges.dns.provider` from the JSON).

**No set-once TXT record yet.** A standard DNS-01 TXT value changes with every issuance and renewal, so the manager
does not offer a manual TXT flow. The new `dns-persist-01` challenge (one TXT record `_validation-persist.<domain>`,
set once) will be added when Let's Encrypt offers it in production and Caddy supports it
([Let's Encrypt announcement](https://letsencrypt.org/2026/02/18/dns-persist-01),
[Caddy issue #7495](https://github.com/caddyserver/caddy/issues/7495)).

## Wildcard certificates

- **Internal CA / custom**: supported directly.
- **ACME**: needs the DNS-01 challenge (above). Without a DNS provider the configuration warns and issuance fails;
  exact names covered by such a wildcard get their own certificates.

## Shared storage (several servers)

Caddy keeps ACME accounts, issued certificates, locks and the internal CA in its **storage**. Servers configured with
the **same storage** coordinate as one certificate cluster: one server obtains or renews a certificate, the others
load it, and they share the internal CA root. HTTP-01 and TLS-ALPN-01 challenges are answered by any of them, so they
work behind a load balancer. (Configuration itself is not shared by storage — the manager's cluster feature pushes it.)

**Settings › Cluster › Shared storage**:

| Backend | Setting | Notes |
|---|---|---|
| Local (default) | `C:\ProgramData\CaddyProxyManager\caddy\data` | Not shared. |
| Shared folder | Local path or UNC share, e.g. `\\fileserver\caddy$` | Grant the computer accounts (`DOMAIN\SERVER$`) *Modify*; mapped drive letters are not visible to services. The manager tests that it can write there before saving. |
| Redis | `host:port` of the Redis server, database, user/password, key prefix, optional TLS and encryption key | Needs the plugin `github.com/pberkel/caddy-storage-redis`. One address only: with several, the plugin switches to a Redis Cluster client, which a normal (primary/replica) Redis rejects; Redis Cluster and Sentinel are not supported yet. |
| Custom | Storage JSON with `"module"` (e.g. consul, s3, postgres) | Needs the plugin providing `caddy.storage.<module>`; stored encrypted. |

Switching from **Local** to a shared folder copies `certificates\`, `acme\`, `pki\` and `ocsp\` to the new folder when
they do not exist there yet (existing data is never overwritten), so issued certificates and the internal CA root are
kept; the result message lists what was copied. With Redis or a custom module the Certificates page lists only custom
certificates (ACME/internal certificates live in that storage).

The shared folder and the certificate store hold private keys, so neither may be a static site's root folder, inside
one, or above one. Saving a storage folder or certificate store that conflicts with an existing static site is refused
(the message names the sites). Every server also checks this itself when it builds its configuration, against its own
folders (data folder, Caddy storage, its certificate store, the shared folder, program and Windows folders): a static
site whose root is such a folder — for example a replicated host whose root is a node's own certificate store — answers
**403** on that server and the configuration result warns about it, until its root folder is changed.

## Internal CA root

**Certificates › Internal root CA** downloads Caddy's root certificate. Deploy it to *Trusted Root Certification
Authorities* with Group Policy (see [group-policy.md](group-policy.md)). The root key lives in
`C:\ProgramData\CaddyProxyManager\caddy\data\pki` (or `pki\` in the shared storage folder) — back it up (it is included in backups) and protect it.

## Monitoring

Certificates expiring within *N* days (Notifications › alert rules), certificates that fail to be issued (ACME
errors from Caddy's log are included in the alert, with every configured credential replaced by `***`) and sync
failures of file/PFX/store certificates raise alerts.
