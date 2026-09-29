# Caddy settings

**Settings › Caddy** holds the global Caddy behaviour: listener ports, what happens to unknown host names, client IPs behind a proxy, logging, plugin configuration and the admin API address. Every role can open the tab; only the **Admin** role can change it. The ACME sections at the top of the tab are described in [ACME settings](acme.md).

## Save and apply

1. Open **Settings** and the **Caddy** tab.
2. Change the fields. **Unsaved changes** appears next to the buttons.
3. Select **Save**. **Discard** returns to the stored values.

Saving applies the new configuration to Caddy at once; no restart of Caddy or the manager is needed. You see **Settings saved and applied**. If Caddy rejects the result, the settings are not saved, the previous configuration keeps running, and a window shows Caddy's error. If Caddy is not running, the settings are saved and used when Caddy starts. See [How configuration is applied](configuration.md#how-configuration-is-applied).

Operators and viewers see the values read-only and **Only administrators can change these settings.** The fields **Server options (JSON)** and **Extra apps (JSON object)** are hidden from them, because they can contain credentials.

### On a cluster node

On a server managed by a cluster primary, most Caddy settings come from the primary, and the tab shows **Read-only on this node**, with the name of the primary. These fields belong to each server and stay editable: **HTTP port**, **HTTPS port**, **Public HTTPS port**, **Bind addresses**, **Admin API listen address**, **Certificate store path** and the custom CA's **CA root certificate (path)**. See [Cluster](cluster.md).

## Certificates and ACME challenge

The **Certificates (ACME)** and **ACME challenge** sections set the ACME account e-mail, the certificate authority, external account binding, the challenge types and DNS providers. See [ACME settings](acme.md).

## Listeners

The ports Caddy listens on for all sites. Remember to allow them in Windows Firewall; the [Readiness](readiness.md) checks tell you which rules are missing.

![The Listeners section of Settings › Caddy with the HTTP, HTTPS and public HTTPS ports, HTTP/3 and bind addresses](images/caddy-settings-listeners.webp)

| Field | Default | Description |
|---|---|---|
| HTTP port | `80` | Port for plain HTTP, the HTTP-01 challenge and redirects to HTTPS. 1–65535. |
| HTTPS port | `443` | Port for HTTPS and the TLS-ALPN-01 challenge. Must differ from the HTTP port. |
| Public HTTPS port | empty | The port clients use for HTTPS when a router or firewall forwards a different port to the HTTPS port, for example public `443` to `8443`. Leave empty when clients connect to the HTTPS port directly. |
| HTTP/3 (QUIC) | off | Also listen on UDP on the HTTPS port for HTTP/3. Needs an inbound UDP firewall rule. |
| Bind addresses | empty | IP addresses of this server that Caddy listens on. Empty means all interfaces. |

The HTTP and HTTPS ports cannot be ports that the management console or Caddy's admin API already use.

### Public HTTPS port

**Public HTTPS port** only changes where **Force HTTPS** redirects send clients. It does not change what Caddy listens on or the firewall rules. Below the field, the tab shows the resulting target, for example `https://host/` (no port, because 443 is the default) or `https://host:8443/`. Certificates from a public CA still need public ports 80 or 443 forwarded to this server, or the DNS challenge. See [Installation](installation.md).

### HTTP/3

HTTP/3 is off by default. It is optional: browsers use HTTP/2 when it is off. When you turn it on:

- Caddy also listens on UDP on the HTTPS port. Allow that port in the firewall.
- QUIC 0-RTT (early data) stays disabled. Early data can be replayed, and it arrives before the client's address is confirmed, so hosts with an IP access list would answer it with `425 Too Early`.
- Caddy buffers the first 4 KB of each request body it passes to a backend. This lets requests without a body reach HTTP/2 backends correctly.

## Unknown hosts

What Caddy does with a request for a host name that no host is configured for.

![The Unknown hosts section of Settings › Caddy with the Default site choice](images/caddy-settings-unknown-hosts.webp)

| Field | Default | Description |
|---|---|---|
| Default site | Respond 404 Not Found | **Respond 404 Not Found**, **Close the connection**, **Redirect to a URL** or **Show the Caddy welcome text**. |
| Redirect URL | empty | Shown for **Redirect to a URL**. An absolute `http://` or `https://` URL. Clients get a 302 redirect to it. |

**Show the Caddy welcome text** answers with the text *Caddy works! This server is managed by Caddy Proxy Manager, but no site is configured for this host name.*

Over HTTPS the default site is only reached for names that have a certificate. A browser that opens `https://` with an unknown name, or with the server's IP address, gets a TLS error first, because Caddy has no certificate for that name. To answer those requests too, add this to **TLS connection policy (JSON object)** under [Plugins & advanced](#plugins--advanced), with the name of one of your HTTPS hosts:

```json
{ "fallback_sni": "app.example.com", "default_sni": "app.example.com" }
```

Clients then see that host's certificate and the default site's response. The example button **Answer HTTPS for unknown names and IPs** fills this in.

## Client IPs

When Caddy is behind a load balancer, reverse proxy or CDN, every request seems to come from that proxy. List the proxies in **Trusted proxies** so that Caddy takes the real client IP from the `X-Forwarded-For` header. Access lists, IP-hash load balancing and logs then use the real client IP.

| Field | Default | Description |
|---|---|---|
| Trusted proxies | empty | IP addresses or CIDR ranges of the proxies in front of Caddy, for example `10.0.0.0/8`. `all` is not accepted. |

Caddy reads the header strictly from right to left: the client IP is the first address, from the right, that is not a trusted proxy. Addresses a client writes at the left of the header itself are ignored, so a client cannot fake its address. List every proxy in front of Caddy, including all ranges of a CDN, or real clients are seen as the last proxy.

## Logging & storage

| Field | Default | Description |
|---|---|---|
| Caddy log level | Info | **Debug (verbose)**, **Info**, **Warning** or **Error**. Applies to the Caddy log. |
| Certificate store path | empty | Where uploaded certificates are written. Empty means `C:\ProgramData\CaddyProxyManager\certificates`. Use an absolute path or a UNC share. |
| Traffic statistics | on | Writes one line per request to a local log for the Traffic page. See [Traffic statistics](#traffic-statistics). |

The Caddy log is `C:\ProgramData\CaddyProxyManager\logs\caddy\caddy.log`. It is rotated at 20 MB and 10 files are kept. Messages about the manager's own calls to Caddy's admin API are logged only from **Warning** up, so the manager's regular checks do not fill the log. You read the log on [Logs](logs.md).

A UNC share for the certificate store must be readable by the computer account of this server, because the services run as LocalSystem. The certificate store must not be inside a folder that an enabled static site serves; the manager refuses such a path. See [Certificates](certificates.md).

### Traffic statistics

When **Traffic statistics** is on, Caddy writes one line per HTTP request to a local log: client IP, host, method, URI, status, bytes and duration, but no headers. The manager reads it for the [Traffic statistics](traffic-statistics.md) page. The log is rotated at 10 MB and 5 files are kept. It works in Managed mode only. Per-host access logs, which you turn on for each host, are not affected.

## Plugins & advanced

Configuration for Caddy plugins that the manager does not model with its own fields. It is used in Managed mode only. Install the plugin first on the [Plugins](plugins.md) page. If Caddy rejects the result when you save, the change is not kept and the error is shown.

| Field | Default | Description |
|---|---|---|
| ACME issuer options (secret JSON object) | empty | JSON merged into every ACME issuer, for issuer options the manager does not model. Stored encrypted; the form never shows it again, and configuration views show it to Admins only. |
| TLS connection policy (JSON object) | empty | JSON merged into every TLS connection policy, so it applies to all HTTPS sites: minimum TLS version, curves, cipher suites or client certificates (mTLS). |
| Extra apps (JSON object) | empty | Top-level Caddy apps that plugins add, keyed by app name, for example `dynamic_dns` or `crowdsec`. Not encrypted. |

Admins see **Examples** buttons under each field. An example replaces the field's text; when the field already has a value, you confirm first. Fill in the example's placeholders before you save. Examples that need a plugin name it in their tooltip.

| Field | Examples |
|---|---|
| ACME issuer options | Cloudflare DNS challenge, Azure DNS challenge, DNS challenge with public resolvers (split-horizon DNS) |
| TLS connection policy | TLS 1.3 only, TLS 1.2+ with modern curves, Answer HTTPS for unknown names and IPs, Require client certificates (mTLS) |
| Extra apps | Dynamic DNS, CrowdSec bouncer |

Rules and hints:

- For DNS providers, prefer the **ACME challenge** section (see [ACME settings](acme.md)). The ACME issuer field cannot set `module`.
- The TLS connection policy cannot set `match` or `certificate_selection`; the manager controls them.
- **Extra apps** cannot contain `http`, `tls`, `pki` or `layer4`; the manager generates those. Use the host, stream and certificate pages instead. Because this field is not encrypted, keep DNS credentials for certificates in the ACME fields.
- When the installed Caddy lacks the module that a DNS provider or an extra app needs, a warning appears with a link to the [Plugins](plugins.md) page. Without the plugin, Caddy rejects the configuration.
- Client-certificate authentication in the TLS connection policy applies to every HTTPS site on this server, including sites that browsers without a client certificate use. The CA file must be readable by LocalSystem.

## Advanced

| Field | Default | Description |
|---|---|---|
| Configuration mode | Managed | Shows **Managed** or **Caddyfile**. Change it on the [Configuration](configuration.md#caddyfile-mode) page. |
| Admin API listen address | `127.0.0.1:2019` | The address of Caddy's admin API, which the manager uses to control Caddy. Must be a loopback address with a port. |
| Server options (JSON) | empty | A JSON object merged into every Caddy HTTP server, for example timeouts or `max_header_bytes`. Leave empty unless you need it. |

### Admin API listen address

Caddy's admin API has no authentication, so anyone who can reach it can reconfigure Caddy. The manager therefore accepts only a loopback address: `127.0.0.1`, `::1` (written `[::1]:port`) or `localhost`, with a port. The port must not be the HTTP or HTTPS port or a port of the management console. Change it only if another program already uses port 2019. See [Security](security.md).

### Server options

**Server options (JSON)** is merged into both of Caddy's HTTP servers (the HTTP and the HTTPS listener). For example:

```json
{
  "timeouts": { "read_header": "10s" }
}
```

- `listen` and `routes` are managed by the manager and ignored, with a warning.
- `logs` is merged with the generated logging settings: per-host access logs and traffic statistics keep working.
- The value must be a JSON object.

See the Caddy documentation for the available options: https://caddyserver.com/docs/json/apps/http/servers/

## Related

- [ACME settings](acme.md)
- [Configuration](configuration.md)
- [Plugins](plugins.md)
- [Cluster](cluster.md)
- [Readiness](readiness.md)
- [Security](security.md)
