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
   DigitalOcean, Google Cloud DNS, OVHcloud, Hetzner, GoDaddy, Porkbun, Namecheap, Gandi, Duck DNS, IONOS, deSEC,
   Linode, Vultr, Netlify, DNSimple, Bunny, NameSilo, Alibaba Cloud, PowerDNS, ACME-DNS, RFC 2136) and fill in its
   fields. Credentials (tokens, secrets, TSIG keys) are **write-only**: stored encrypted, never shown again, and
   replaced by `***` in any Caddy error message, event or configuration a non-administrator can see. Other providers
   can be used by their module name (`dns.providers.<name>`) with plain options.
3. Choose where DNS-01 is used: **Default challenge** = DNS for every ACME host, or per host on the TLS tab
   (*ACME challenge*: Default / HTTP / DNS). Wildcard names always use DNS once a provider is configured.

Optional settings: **propagation delay** (wait before the first check), **propagation timeout** (default 2 minutes;
`-1` skips the check — useful when the check cannot see your authoritative servers), **TTL** of the TXT record,
**resolvers** (`host:port`, e.g. `10.0.0.53:53`; used for the zone lookup and propagation check — set them behind
split-horizon DNS where the internal resolvers do not show the public zone), and the **default delegation name** for
delegated challenges (next section).

*RFC 2136* works with BIND, Knot, PowerDNS and Windows DNS configured for secure dynamic updates with a TSIG key
(`server` host:port, key name, algorithm such as `hmac-sha256`, base64 secret).

Enabling DNS disables the HTTP and TLS-ALPN challenges for those names. The *ACME issuer JSON* (Plugins & advanced)
is still merged into every ACME issuer after generation, for options the form does not cover.

## Delegating the DNS challenge (CNAME)

**Why.** With a plain DNS challenge the provider credentials can edit your production zone: whoever reads them could
change any record of `example.com`. Delegation removes that risk. You create, once per domain, a CNAME from the
challenge record to a name in a **separate validation zone**, and give the manager credentials for that zone only. The CA
follows the CNAME when it validates, and Caddy writes the TXT record at the delegated name
([Let's Encrypt: DNS-01](https://letsencrypt.org/docs/challenge-types/#dns-01-challenge),
[Caddy: `override_domain`](https://caddyserver.com/docs/json/apps/tls/automation/policies/issuers/acme/challenges/dns/override_domain/)).
The manager never writes to the production zone.

**The records.** For every domain that uses the DNS challenge:

```
_acme-challenge.<domain>   CNAME   <delegation name>
```

For example, with the delegation name `_acme-challenge.validation.example.net`:

| Host domain | Record to create in the production zone | Type | Points to |
|---|---|---|---|
| `shop.example.com` | `_acme-challenge.shop.example.com` | CNAME | `_acme-challenge.validation.example.net` |
| `*.example.com` | `_acme-challenge.example.com` | CNAME | `_acme-challenge.validation.example.net` |
| `example.com` | `_acme-challenge.example.com` | CNAME | (same record as the wildcard) |

A wildcard `*.example.com` is validated at `_acme-challenge.example.com` (the `*.` is dropped), so a wildcard and its
base name share one CNAME. Many domains may point to the same delegation name: each certificate order adds and later
removes its own TXT value there. Use different names (per host, below) to keep teams or customers apart.

**Setting it up.**

1. Create the validation zone at a DNS provider the manager supports: a separate domain (e.g. `example.net`) or a
   sub-zone of your own that has its own SOA (delegated with NS records, if your provider hosts sub-zones). The
   delegated name must be inside a zone the credentials can edit: Caddy looks up the zone of the delegated name (its SOA)
   and writes there.
2. Create credentials **scoped to that zone only** — this is the recommended setup:
   - Cloudflare: an API token with *Zone › DNS › Edit* and *Zone › Zone › Read*, *Zone Resources: Include › Specific zone ›
     validation zone*;
   - Route 53: an IAM policy allowing `route53:ChangeResourceRecordSets` / `ListResourceRecordSets` / `GetChange` on the
     validation hosted zone's ARN only (plus `route53:ListHostedZonesByName`);
   - Azure DNS: the *DNS Zone Contributor* role assigned on the validation zone, not on the resource group;
   - RFC 2136 (BIND, Windows DNS, ...): a TSIG key allowed to update the validation zone only (`update-policy`).
3. **Settings › Caddy › ACME challenge**: select the provider with those credentials and enter the **default delegation
   name** (e.g. `_acme-challenge.validation.example.net`; letters, digits, `-` and `_`; no wildcard; case and a trailing
   dot do not matter). The *Challenge delegation (CNAME)* panel lists the CNAME records every DNS-challenge host needs,
   with copy buttons.
4. Create those CNAME records in the production zone (by hand, once; they never change).
5. Click **Check DNS**. For every domain it looks up `_acme-challenge.<domain>` (following up to 8 CNAMEs) through the
   *resolvers* of Settings › Caddy, or the server's own resolvers when none are set (the API can also ask the public
   resolvers 1.1.1.1 / 8.8.8.8, which is what the CA sees). Results are never cached, so a check right after creating a
   record shows it; each domain gets at most 5 seconds.
   - **OK** — the CNAME (or chain) reaches the delegation name;
   - **Missing** — no CNAME yet: create the record shown;
   - **Wrong** — the CNAME points elsewhere, or a TXT record sits there instead of a CNAME (e.g. left over from a manual
     challenge): delete it and create the CNAME;
   - **Error** — the lookup failed (timeout, resolver not reachable, SERVFAIL).

**Per host** (TLS tab, DNS challenge): *Delegation* = **Use default** (the name from the settings; none when it is empty),
**Off** (no delegation: the TXT record is written in the domain's own zone, which the credentials must then be able to
edit) or **Custom name** (this host's own delegation name). The tab lists the host's records and has its own *Check DNS*.
Hosts are grouped by their effective delegation name into separate Caddy automation policies.

Delegation only exists for ACME hosts: other TLS modes drop the choice when saved (like the ACME challenge choice). On an
ACME host that uses the HTTP challenge, choosing *Off* or *Custom name* is refused (field error on *Delegation*) — select
the DNS challenge first. A host with a wildcard uses DNS for the wildcard as soon as a provider is configured, so it may
delegate even with the HTTP challenge selected. If the default challenge is later switched from DNS to HTTP, stored
delegation choices simply stop being used.

**Alternative: ACME-DNS.** [acme-dns](https://github.com/joohoi/acme-dns) is a tiny DNS server whose only job is to hold
challenge TXT records, and the **ACME-DNS** provider (`github.com/caddy-dns/acmedns`) updates it through its API with
per-account credentials that cannot touch any other record. Register an account (`POST <server>/register`), enter
`username`, `password`, `subdomain` and `server_url` as the provider fields, and enter the returned `fulldomain`
(e.g. `d420c923-….auth.acme-dns.io`) as the delegation name: the CNAME records and *Check DNS* then work as above.
The provider fields hold a single account for all hosts. An acme-dns account keeps only its two newest TXT values (enough
for a name and its wildcard), so when several names are issued at the same moment a validation can find its value
already replaced; Caddy retries it later. With many names, prefer a validation zone at a regular provider (above).

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
| Redis | `host:port` addresses, database, user/password, key prefix, optional TLS and encryption key | Needs the plugin `github.com/pberkel/caddy-storage-redis`. |
| Custom | Storage JSON with `"module"` (e.g. consul, s3, postgres) | Needs the plugin providing `caddy.storage.<module>`; stored encrypted. |

Switching from **Local** to a shared folder copies `certificates\`, `acme\`, `pki\` and `ocsp\` to the new folder when
they do not exist there yet (existing data is never overwritten), so issued certificates and the internal CA root are
kept; the result message lists what was copied. With Redis or a custom module the Certificates page lists only custom
certificates (ACME/internal certificates live in that storage).

## Internal CA root

**Certificates › Internal root CA** downloads Caddy's root certificate. Deploy it to *Trusted Root Certification
Authorities* with Group Policy (see [group-policy.md](group-policy.md)). The root key lives in
`C:\ProgramData\CaddyProxyManager\caddy\data\pki` (or `pki\` in the shared storage folder) — back it up (it is included in backups) and protect it.

## Monitoring

Certificates expiring within *N* days (Notifications › alert rules), certificates that fail to be issued (ACME
errors from Caddy's log are included in the alert) and sync failures of file/PFX/store certificates raise alerts.
