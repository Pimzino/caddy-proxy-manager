# Research: Caddy v2.11.4 DNS-01 (verified against v2.11.4, certmagic v0.25.3, acmez v3.1.6, caddy-dns HEAD)

## 1. DNS challenge JSON (`apps.tls.automation.policies[].issuers[]`)
Docs: https://caddyserver.com/docs/json/apps/tls/automation/policies/issuers/acme/challenges/dns/ ; source modules/caddytls/automation.go (DNSChallengeConfig).

```json
{"module":"acme","email":"...","challenges":{"dns":{
  "provider":{"name":"cloudflare","api_token":"..."},
  "ttl":"2m","propagation_delay":"30s","propagation_timeout":"5m",
  "resolvers":["1.1.1.1:53"],"override_domain":"_acme-challenge.delegated.example.net"}}}
```
- `provider.name` = suffix of module ID `dns.providers.<name>`; provider fields sit next to `name`. Strings generally expand `{env.X}`.
- `ttl`/`propagation_delay`/`propagation_timeout` are caddy.Duration: int ns or string ("30s","2m","1d"). propagation_timeout default 2m; `-1` disables propagation checks. propagation_delay default 0. ttl 0 = provider default.
- Durations INSIDE a provider are plain Go time.Duration → integer nanoseconds (e.g. route53 `route53_max_wait`).
- `resolvers` lives in `challenges.dns` (not inside provider).
- `override_domain` placeholders expanded since 2.11.3.
- New in 2.10: `apps.tls.dns` = global default provider (`{"name":...}`); issuer may use `"challenges":{"dns":{}}` and falls back to it. Neither set → provisioning error "DNS challenge enabled, but no DNS provider configured". ECH uses the same global provider. Wildcard certs now cover matching subdomains by default. All providers moved to libdns 1.x.
- `apps.tls.resolvers` is NOT read by the ACME issuer in v2.11.4 → set `resolvers` on every issuer's challenges.dns.
- Enabling DNS challenge disables HTTP-01 and TLS-ALPN-01 (certmagic acmeclient.go ~191; docs: "If the DNS challenge is enabled, other challenges are disabled by default").
- Wildcards require DNS-01 for Let's Encrypt.
- `zerossl` issuer module (API, not ACME) uses `cname_validation` instead; ZeroSSL via ACME (`module:"acme"`, ca DV90 + external_account) uses the same challenges.dns shape.

## 2. Providers (package `github.com/caddy-dns/<name>`, module `dns.providers.<name>`; S = secret; (req) = required)
| name | JSON fields |
|---|---|
| cloudflare | `api_token` S (req), `zone_token` S |
| route53 | `access_key_id`, `secret_access_key` S, `session_token` S, `region`, `profile`, `hosted_zone_id`, `max_retries` (int), `route53_max_wait` (ns int), `wait_for_route53_sync` (bool), `skip_route53_sync_on_delete` (bool), `debug_logging` (bool). Credentials optional (AWS default chain). |
| azure | `subscription_id` (req), `resource_group_name` (req), `tenant_id`, `client_id`, `client_secret` S (omit all three → managed identity) |
| digitalocean | `auth_token` S (req) |
| gandi | `bearer_token` S (req) |
| duckdns | `api_token` S (req), `override_domain`, `resolver` |
| godaddy | `api_token` S (req) — format "<key>:<secret>" |
| hetzner | `api_token` S (req) — v2 module targets Hetzner Cloud DNS API (Cloud API token). Never build together with caddy-dns/he (duplicate module id). |
| ovh | `endpoint` (e.g. ovh-eu), `application_key`, `application_secret` S, `consumer_key` S (all req) |
| porkbun | `api_key` S, `api_secret_key` S (both req) |
| namecheap | `api_key` S (req), `user` (req), `api_endpoint`, `client_ip` |
| googleclouddns | `gcp_project` (req), `gcp_application_default` (path to service-account JSON; else ADC) |
| ionos | `auth_api_token` S (req) |
| desec | `token` S (req) |
| rfc2136 | `server` (host:port), `key_name`, `key_alg` (e.g. hmac-sha256), `key` S (base64) — all four needed for TSIG |
| powerdns | `server_url` (req), `api_token` S (req), `server_id` (default localhost), `debug` (string) |
| acmedns | `username`, `password` S, `subdomain`, `server_url` (or `config` map / `config_file_path`) |
| alidns | `access_key_id` (req), `access_key_secret` S (req), `region_id`, `security_token` S |
| linode | `api_token` S (req), `api_url`, `api_version` |
| vultr | `api_token` S (req) |
| netlify | `personal_access_token` S (req) |
| namesilo | `api_token` S (req) |
| bunny | `access_key` S (req) |
| dnsimple | `api_access_token` S (req), `account_id` (req with user token), `api_url` |

- cloudflare: recommended single `api_token` with Zone.Zone:Read + Zone.DNS:Edit. Provision rejects tokens not matching `^[A-Za-z0-9_-]{35,50}$` or `^cf(ut|at)_…` and that error message INCLUDES THE TOKEN → scrub secrets from any Caddy error shown in the UI/events.
- Duplicate module ids from third-party packages exist (tosie/caddy-dns-linode, xcaddyplugins/caddy-dns-godaddy, caddy-dns/he for hetzner) → always use `github.com/caddy-dns/*`.
- All 24 are listed/available in https://caddyserver.com/api/packages.

## 4. Log signatures
- `tls.obtain` error: "could not get certificate from issuer" (identifier, issuer, error); "will retry"; "final attempt; giving up". Info: "obtaining certificate", "certificate obtained successfully". Renewals: `tls.renew`.
- `tls.issuance.acme.acme_client`: info "trying to solve challenge" (challenge_type "dns-01"); error "challenge failed" (problem), "validating authorization", "cleaning up solver". Record create failure: `presenting for challenge: …`.
- `tls.issuance.acme.dns_manager` (debug): "creating DNS record", "checking DNS propagation", "deleting DNS record".
- Error strings: `could not determine zone for domain …`, `adding temporary record for zone …`, `timed out waiting for record to fully propagate; …`, `deleting temporary record …`.
- Config load: `loading DNS provider module: …` (provider not compiled in), `DNS challenge enabled, but no DNS provider configured`.
