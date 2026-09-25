# Caddy Proxy Manager — Specification

A Windows-native reverse proxy manager for Caddy. One self-contained executable
(`CaddyManager.exe`) runs as a Windows service (LocalSystem — no service account and no
logged-on user needed) on 64-bit Windows Server 2019/2022/2025 (Desktop Experience or Server Core) and Windows 10/11. It:

- downloads/installs/updates the Caddy binary (optionally with plugins) and shows when updates exist;
- registers and supervises Caddy as its own Windows service (`Caddy`);
- generates Caddy's JSON config from a UI (proxy hosts, redirects, static sites, custom responses,
  L4 streams, access lists, certificates) and hot-applies it through Caddy's admin API;
- manages certificates: automatic ACME, Caddy internal CA, and user certificates (uploaded PEM/PFX
  written to a shared certificate store, or referenced by path) mapped to specific domains;
- checks server readiness (firewall rules, network profile, domain membership → GPO firewall rule
  script, port conflicts/IIS/http.sys, outbound ACME connectivity, DNS, clock, disk);
- monitors and alerts via SMTP e-mail / webhook / Windows Event Log;
- provides users/roles, audit log, events, log viewer and backup/restore.

## Repository layout and ownership

```
Directory.Build.props, CaddyManager.sln
src/CaddyManager.Core/        shared contracts (DO NOT MODIFY — owner: lead). Models, DTOs, interfaces, LiteStore, SecretProtector, JobRunner, JsonDefaults, ApiResults/Validator, Policies, Passwords.
src/CaddyManager.Config/      CONFIG builder: Caddy JSON generation, admin API client, apply/revisions, hosts/streams/access lists/certificates/caddy-settings/config endpoints, certificate inventory.
src/CaddyManager.Platform/    PLATFORM builder: Caddy host (Windows service / dev process), binary manager (download/update/plugins), bootstrapper, update checker, readiness checks + fixes + GPO script, system endpoints, CLI install/uninstall, installer (installer/), CI (.github/workflows).
src/CaddyManager.Ops/         OPS builder: authentication/authorization/users/setup, audit log, events + notifications (SMTP/webhook/EventLog), monitor, dashboard, logs viewer, backup/restore, notification + UI settings endpoints, reset-password CLI.
src/CaddyManager/             host exe (owner: lead). Program.cs composes modules.
web/                          WEB builder: React SPA, built to web/dist and embedded in the exe.
tests/CaddyManager.<Module>.Tests/   each backend builder owns its own xUnit test project.
```

Each module exposes exactly these entry points (already stubbed in `<Module>Module.cs`):
`services.Add<Module>Module()` and `app.Map<Module>Endpoints()`. Platform/Ops also expose
`PlatformCli.TryRunAsync(args)` / `OpsCli.TryRunAsync(args)`.

Modules only depend on Core. Cross-module calls go through the Core interfaces
(`src/CaddyManager.Core/Abstractions.cs`), resolved from DI at runtime.

## Runtime layout (Windows)

| What | Where |
|---|---|
| Manager exe | `C:\Program Files\Caddy Proxy Manager\CaddyManager.exe` (service `CaddyProxyManager`, LocalSystem, automatic delayed start, restart on failure) |
| Data root | `C:\ProgramData\CaddyProxyManager` (`AppPaths.DataDir`) |
| Caddy binary | `DataDir\caddy\bin\caddy.exe` (+ `caddy.exe.previous` for rollback) |
| Caddy service | `Caddy` → `"<caddy.exe>" run --config "<DataDir>\caddy\caddy.json"`, LocalSystem, auto start, restart on failure, env `XDG_DATA_HOME`/`XDG_CONFIG_HOME` set to `DataDir\caddy\data` / `DataDir\caddy\config` via the service's registry `Environment` value |
| Caddy storage (ACME certs, internal CA) | `DataDir\caddy\data` (also set as `storage` `file_system` root in generated config) |
| Certificate store (shared) | `DataDir\certificates\<certId>\fullchain.pem, privkey.pem` (override: `CaddySettings.CertificateStorePath`, may be a UNC path readable by the computer account) |
| Logs | `DataDir\logs\caddy\caddy.log` (Caddy process log, rolled), `DataDir\logs\access\<host>.log`, `DataDir\logs\manager\manager-yyyyMMdd.log` |
| DB | `DataDir\db\manager.db` (LiteDB) |
| Setup token | `DataDir\setup-token.txt` (first-run only) |

In development (macOS/Linux), `DataDir` = `./.devdata` (or `CM_DATA_DIR`), Caddy runs as a child
process ("process" host mode) and the dev Caddy binary is at `.dev/bin/caddy` (copy it into
`AppPaths.CaddyExe` if missing). `CM_UI_PORT=5081` is used for the dev backend.

## Config generation rules (Config module)

Managed mode produces one Caddy JSON document:

- `admin`: `{ "listen": CaddySettings.AdminListen, "config": { "persist": false } }`.
- `storage`: `{ "module": "file_system", "root": AppPaths.CaddyStorageDir }`.
- `logging.logs.default`: level from settings, writer `file` → `AppPaths.CaddyProcessLog` with roll (`roll_size_mb` 20, `roll_keep` 10). Per-host access logs (SiteHost.AccessLog) → named loggers writing `AppPaths.AccessLogDir\<firstDomain>.log`, JSON encoder, bound via `servers.*.logs.logger_names`.
- `apps.http.servers.srv0` listens on `:HttpsPort` (and BindAddresses), `srv1`-style HTTP listener on `:HttpPort` is Caddy's automatic one; set `http_port`/`https_port` at `apps.http`. HTTP/3 via `protocols` `["h1","h2","h3"]` when enabled, otherwise `["h1","h2"]`. `trusted_proxies` from settings (`static` source).
- Each enabled SiteHost → one route `{ match: [{ host: Domains }], handle: [{ handler: "subroute", routes: [...] }], terminal: true }`.
  - Order inside the subroute: access list (IP rules → `remote_ip` matchers + `static_response` 403; basic auth → `authentication` handler with `http_basic` provider and bcrypt accounts; honour SatisfyAny), block-exploits (403 on common probe patterns via `path_regexp`/`query` matchers), HSTS/response headers (`headers` handler), compression (`encode` gzip+zstd), AdvancedRoutesJson routes, then the kind handler:
    - Proxy: custom Locations first (`path` matcher `<path>*`, optional `rewrite` strip_path_prefix), then default `reverse_proxy` with `upstreams[].dial = host:port`, `transport.http.tls` (+ `insecure_skip_verify`) for https upstreams, `load_balancing.selection_policy`, `health_checks.active` (uri, interval, timeout, expect_status), request header ops (`headers.request`), Host header override (`{http.reverse_proxy.upstream.hostport}` when "{upstream}").
    - Redirect: `static_response` with `Location` = target (+ `{http.request.uri}` when PreservePath) and status code.
    - Static: `file_server` with root and browse; SpaFallback → `rewrite` via `try_files` matcher (`file` matcher `{path}`, `/index.html`).
    - Response: `static_response` with status, body, Content-Type header.
- TLS:
  - `Tls=None` → domains added to `automatic_https.skip` and served on an HTTP-only listener (`:HttpPort`), no redirect.
  - `ForceHttps=false` with TLS → add domains to `automatic_https.disable_redirects`-equivalent by generating explicit HTTP routes for those domains (serve on both).
  - `Tls=Acme` → automation policy with issuers from settings (`acme` with `ca` directory URL for LE/LE-staging/ZeroSSL/custom, `email`, `external_account` for EAB, challenge toggles; LE default also falls back to ZeroSSL only if configured).
  - `Tls=Internal` → automation policy `{ subjects: domains, issuers: [{ module: "internal" }] }`.
  - `Tls=Custom` → `apps.tls.certificates.load_files[] = { certificate, key, tags: ["cpm-<certId>"] }`, connection policy `{ match: { sni: domains }, certificate_selection: { any_tag: ["cpm-<certId>"] } }`, domains in `automatic_https.skip_certificates`. Put SNI-specific connection policies before a final catch-all `{}` policy.
- Default site (unknown Host): catch-all final route per `DefaultSite` (404 static_response / `abort` / redirect / Caddy welcome text).
- Streams (only if the installed binary contains module `layer4` — check `InstalledBinary.Modules`): `apps.layer4.servers.<id> = { listen: ["tcp/:port" | "udp/:port"], routes: [{ handle: [{ handler: "proxy", upstreams: [{ dial: ["tcp/host:port"] }] }] }] }`. Otherwise streams are skipped and an ApplyResult warning is returned.
- Caddyfile mode: POST the raw Caddyfile to admin `/adapt` (or `caddy adapt` via binary when Caddy is down), then load the JSON. Managed hosts are ignored in this mode.
- Apply: serialise with a SemaphoreSlim; if Caddy admin reachable → `POST /load` (Caddy validates & rolls back itself on failure); else validate with `caddy validate --config <tmp>` if the binary exists. On success write `AppPaths.CaddyConfigFile` atomically (tmp + move) and store a ConfigRevision (keep last 100). On failure raise an `IEventSink` event (category "config", alertRule "configFailure").
- Mutating endpoints for hosts/streams/access lists/certificates/caddy settings are **transactional**: persist → apply → if apply fails because Caddy rejected the config, restore the previous DB state and return **422** with Caddy's error as `detail`. If Caddy is simply not running/installed, keep the change and return 200 with `apply.writtenOnly = true` (+ warnings).
- File-path certificates: a FileSystemWatcher (plus 5-minute poll) re-reads metadata and re-applies config when cert files change.

## HTTP API

Base `/api`. JSON camelCase; enums are camelCase strings (e.g. `"proxy"`, `"letsEncryptStaging"`, `"startTls"`);
nulls omitted. Errors are RFC 7807 problem+json: `{ title, detail, status, errors?: { field: string[] } }`.
Auth: cookie `cpm_session` (HttpOnly, SameSite=Strict, Secure when HTTPS). **Every non-GET request
must include header `X-CPM-Request: 1`** (CSRF defence, enforced by Ops middleware for all
`/api` unsafe methods except `POST /api/auth/login` and `POST /api/setup`, which still send it from the UI).
401 = not signed in, 403 = role insufficient. Roles: viewer < operator < admin (policies in `Policies`).

**Settings wire shapes (all settings endpoints):** the settings class camelCased, *minus* every
`*Protected` property, *plus* an output-only boolean `has<Name>` and a write-only input `<name>`
(e.g. `SmtpPasswordProtected` → output `hasSmtpPassword`, input `smtpPassword`; `EabMacKeyProtected` →
`hasEabMacKey` / `eabMacKey`; `HttpsPfxPasswordProtected` → `hasHttpsPfxPassword` / `httpsPfxPassword`).
Input semantics: absent/null = unchanged, `""` = clear, other = set (protected via ISecretProtector).

Mutation responses for Config resources: `{ "item": <entity>, "apply": ApplyResult }`.
Delete responses: `{ "apply": ApplyResult }`.

### Setup & auth (Ops) — anonymous unless noted
| Method | Path | Body | Response |
|---|---|---|---|
| GET | /api/setup/status | | `{ needsSetup: bool, setupTokenPath: string }` |
| POST | /api/setup | `{ token, email, name, password }` | UserDto (and signs in). 403 if already set up, 400 bad token |
| POST | /api/auth/login | `{ email, password }` | UserDto; 401 `{title:"Invalid credentials"}`; rate-limited (429) |
| POST | /api/auth/logout | | 204 |
| GET | /api/auth/me | | UserDto (401 if anonymous) |
| POST | /api/auth/change-password | `{ currentPassword, newPassword }` (viewer) | 204 |

`UserDto = { id, email, name, role: "viewer"|"operator"|"admin", disabled, lastLoginAt?, createdAt }`.
Password policy: ≥ 12 chars. Setup token is generated at first start (when no users exist), written
to `AppPaths.SetupTokenFile`, logged, and deleted after setup.

### Users, audit, events (Ops)
| GET /api/users (admin) → UserDto[] | POST /api/users `{email,name,role,password}` → UserDto | PUT /api/users/{id} `{email,name,role,disabled,password?}` → UserDto | DELETE /api/users/{id} → 204 (cannot delete/disable/demote self or the last admin) |
| GET /api/audit?skip=0&take=50&q= (admin) → `{ items: AuditEntry[], total }` |
| GET /api/events?skip=0&take=50&severity= (viewer) → `{ items: EventEntry[], total }` |

### Dashboard (Ops) — viewer
`GET /api/dashboard` →
```
{ caddy: CaddyStatus, binary: BinaryOverview,
  counts: { proxy, redirect, static, response, streams, accessLists, certificates, certificatesExpiring, hostsDisabled },
  readiness: { ranAt?, pass, warn, fail } | null,
  upstreams: { total, unhealthy },
  recentEvents: EventEntry[] (10),
  system: { hostname, os, managerVersion, uptimeSeconds, dataDir } }
```

### Hosts (Config) — GET viewer, mutations operator
| GET /api/hosts?kind=proxy\|redirect\|static\|response → SiteHost[] |
| GET /api/hosts/{id} → SiteHost |
| POST /api/hosts (SiteHost without id) → `{item, apply}` |
| PUT /api/hosts/{id} → `{item, apply}` |
| DELETE /api/hosts/{id} → `{apply}` |
| POST /api/hosts/{id}/enable, /disable → `{item, apply}` |

Validation: ≥1 domain, valid hostnames (wildcard `*.` prefix allowed), a domain may belong to only one
enabled host (409 conflict naming the other host), Proxy needs ≥1 upstream with host + port 1–65535,
Redirect needs absolute http(s) target and code in 301/302/303/307/308, Static needs RootPath,
Custom TLS needs existing CertificateId, AdvancedRoutesJson must parse as a JSON array.
SiteHost JSON shape = the C# entity camelCased (see `src/CaddyManager.Core/Models/Hosts.cs`).

### Streams (Config): `GET/POST /api/streams`, `PUT/DELETE /api/streams/{id}` (StreamHost). `GET /api/streams/support` → `{ supported: bool, plugin: "github.com/mholt/caddy-l4" }`.

### Access lists (Config)
`GET/POST /api/access-lists`, `GET/PUT/DELETE /api/access-lists/{id}`.
Wire shape: `{ id, name, satisfyAny, passAuthToUpstream, rules: IpRule[], users: [{ username, password? }], usedBy: number, createdAt, updatedAt }`.
On output `users[].password` is never returned (`hasPassword: true` instead). On PUT a user with no
password keeps its existing hash (matched by username). Deleting a list in use → 409.

### Certificates (Config)
| GET /api/certificates → CertificateInfo[] (custom + ACME + internal, from ICertificateInventory) |
| POST /api/certificates/upload (multipart: `name`, and either `certFile`+`keyFile` (PEM) or `pfxFile`+`pfxPassword`) → `{item: Certificate, apply}` |
| POST /api/certificates/pem `{ name, certPem, keyPem }` → `{item, apply}` |
| POST /api/certificates/path `{ name, certPath, keyPath }` → `{item, apply}` (files must exist & parse; key must match cert) |
| PUT /api/certificates/{id} `{ name, notes }` | POST /api/certificates/{id}/replace (multipart like upload) → `{item, apply}` |
| DELETE /api/certificates/{id} → `{apply}` (409 if used by a host) |
| GET /api/certificates/internal-root → `application/x-pem-file` download of Caddy's local CA root (`<storage>/pki/authorities/local/root.crt`), 404 if not yet generated |

Uploaded certs are written as `fullchain.pem` + `privkey.pem` (PKCS#8, unencrypted) under
`<store>/<certId>/`; on Windows the directory ACL is restricted to SYSTEM and Administrators.
Validate: key matches certificate public key; cert not expired (warn only if expired).

### Caddy settings & config (Config) — GET viewer (secrets redacted), PUT admin
| GET/PUT /api/settings/caddy | CaddySettings wire shape (EAB MAC key write-only: input `eabMacKey` — null/absent = unchanged, "" = clear; output `hasEabMacKey`) → PUT returns `{item, apply}` |
| GET /api/config/preview → `{ json }` (pretty generated config) |
| GET /api/config/running → `{ json }` (from admin API; 503 if unreachable) |
| POST /api/config/apply (operator) → ApplyResult |
| GET /api/config/revisions?take=50 → `[{ id, createdAt, reason, appliedBy, success, error?, hash }]` |
| GET /api/config/revisions/{id} → ConfigRevision (with json) |
| POST /api/config/caddyfile/adapt (admin) `{ caddyfile }` → `{ json, warnings: string[] }` or 422 |
| GET /api/caddy/upstreams (viewer) → UpstreamHealth[] |

### Caddy service & binary (Platform)
| GET /api/caddy/status (viewer) → CaddyStatus |
| POST /api/caddy/start, /stop, /restart (operator) → CaddyStatus |
| POST /api/caddy/service/install, /service/uninstall (admin) → CaddyStatus |
| GET /api/caddy/binary (viewer) → BinaryOverview |
| POST /api/caddy/binary/check (operator) → BinaryOverview (forces GitHub check) |
| POST /api/caddy/binary/install `{ version?: "v2.11.4" }` (admin) → JobInfo (installs latest when omitted; uses desired plugins) |
| GET /api/caddy/plugins/catalog?q= (viewer) → PluginPackage[] (cached ~6h from `https://caddyserver.com/api/packages`, filtered, top 200) |
| PUT /api/caddy/plugins `{ plugins: string[] }` (admin) → BinaryOverview |
| GET/PUT /api/settings/binary (GET viewer, PUT admin) → BinarySettings |
| GET /api/jobs, GET /api/jobs/{id} (Core; viewer) → JobInfo |

Binary sources: official release zip `https://github.com/caddyserver/caddy/releases/download/<tag>/caddy_<ver>_windows_amd64.zip`
verified against `caddy_<ver>_checksums.txt` (SHA-512) when no plugins; with plugins
`https://caddyserver.com/api/download?os=windows&arch=amd64&p=<pkg>&p=<pkg>` (custom build of latest; no checksum
available → verify it runs `caddy version` and lists the requested packages). Update flow: download to
staging → `staging\caddy version` → `staging\caddy validate --config <current caddy.json>` → stop
Caddy → move current to `.previous` → move staged in → start → wait for admin API (30s) → on failure
restore `.previous`, start, raise event. Update check: background every `CheckIntervalHours`
(GitHub `releases/latest`), raising `IEventSink` event (category "update", key "update-available:<ver>",
alertRule "updateAvailable") once per new version; optional auto-install.

### Readiness (Platform)
| GET /api/readiness (viewer) → ReadinessReport (204 when never run) |
| POST /api/readiness/run (operator) → ReadinessReport |
| POST /api/readiness/fix/{checkId} (admin) → `{ message, report }` |
| GET /api/readiness/gpo-script (viewer) → text/plain PowerShell |

Checks (Windows; on other OS return Info/Skipped items so the UI still renders):
- System: OS edition/build (pass on build ≥ 17763 = Windows 10 1809 / Server 2019, any edition incl. Server Core), running as LocalSystem/service, free disk on DataDir (warn <5 GB, fail <1 GB), clock skew vs HTTP `Date` from `acme-v02.api.letsencrypt.org` (warn >30s, fail >5m), pending reboot (info).
- Firewall: Windows Defender Firewall service (mpssvc) running; each profile enabled; inbound allow rules for TCP HttpPort, TCP HttpsPort, UDP HttpsPort (HTTP/3, when enabled), TCP UI port — evaluated against the **ActiveStore** (includes GPO rules) and the active profile(s); detect GPO setting `AllowLocalFirewallRules = False` (local rules ignored → must deploy via GPO); Fixable → creates local rules named `Caddy Proxy Manager - HTTP (TCP-In)` etc. via `New-NetFirewallRule`.
- Network: connection profiles (Get-NetConnectionProfile). Domain-joined machine on Public/Private instead of DomainAuthenticated → warn (NLA couldn't reach a DC; remediation: check DNS points at DCs, `Restart-Service NlaSvc`). Non-domain machine on Public → warn, Fixable → `Set-NetConnectionProfile -NetworkCategory Private`.
- Domain: joined? domain name, computer DN (via ADSI/`[adsisearcher]`). If joined → Info check with the GPO recommendation and Script = BuildGpoScript() (creates/updates GPO "Caddy Proxy Manager - Firewall", adds the inbound rules to it via `New-NetFirewallRule -PolicyStore "<domain>\<GPO>"`, links it to the computer's OU, `gpupdate` hint). Also mention deploying the Caddy internal CA root to "Trusted Root Certification Authorities" via GPO when any host uses Internal TLS.
- Ports: who listens on TCP 80/443 and UDP 443 (Get-NetTCPConnection/Get-NetUDPEndpoint → process); PID 4/System → http.sys (IIS W3SVC, WinRM HTTPS, SSRS, ADFS...) with `netsh http show servicestate` hint; fail if owned by something other than caddy.exe; W3SVC running → warn.
- Connectivity: TCP 443 to `acme-v02.api.letsencrypt.org`, `api.github.com`, `caddyserver.com`; WinHTTP proxy (`netsh winhttp show proxy`).
- DNS: each enabled host domain (non-wildcard, TLS=Acme) resolves; list addresses; warn when none of them match a local IP or the public IP (public IP via `https://api.ipify.org`, best-effort).
- Caddy: binary installed, service installed + recovery configured + start type automatic, running, admin API reachable only on loopback.
All PowerShell is executed with `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command -` (script on stdin), JSON results via `ConvertTo-Json -Depth 5`, 60s timeout.

### System (Platform)
| GET /api/system/info (viewer) → `{ version, product, hostMode, dataDir, installDir, os, machineName, uptimeSeconds, isService }` |
| POST /api/system/restart (admin) → 202; restarts the manager service (exit code 1 → SCM recovery restarts it; in console mode just logs) |

### Notifications & UI settings (Ops) — admin
| GET/PUT /api/settings/notifications | NotificationSettings wire shape; `smtpPassword` write-only (null = unchanged, "" = clear), output `hasSmtpPassword` |
| POST /api/settings/notifications/test `{ }` → `{ ok: bool, errors: string[] }` (sends test via all enabled channels) |
| GET/PUT /api/settings/ui | UiSettings (+ write-only `httpsPfxPassword`); PUT returns `{ item, restartRequired: bool }` |

### Logs & backup (Ops)
| GET /api/logs/caddy?lines=500&q= (viewer) → `{ lines: string[], file }` (tail of caddy.log) |
| GET /api/logs/access?host=<domain>&lines=500 (viewer) → `{ lines, file, hosts: string[] }` |
| GET /api/logs/manager?lines=500&q= (admin) → `{ lines, file }` |
| GET /api/backup (admin) → zip download (`manager.db` copy via LiteDB checkpoint, `certificates/`, `caddy.json`, `manifest.json`) |
| POST /api/backup/restore (admin, multipart `file`) → `{ restartRequired: true }` (stages restore; applied on next start) |

### Health: GET /api/health (anonymous) → `{ status, version, product }`

## Monitoring & alerts (Ops)

`MonitorService` (BackgroundService, 30s tick, waits 60s after start):
- Caddy should be running (binary + service installed) but isn't / admin unreachable → event Error key `caddy-down` (alertRule `caddyDown`); if `AutoRestartCaddy` → `ICaddyHost.StartAsync()` (max 3 attempts / 10 min). When healthy again → Recovered event.
- Upstreams (`ICaddyAdminClient.GetUpstreamsAsync`) and active health-check failures → Warning per upstream key `upstream:<addr>` (alertRule `upstreamUnhealthy`), Recovered when healthy.
- Every 6h: certificates expiring within `CertificateExpiryDays` (from `ICertificateInventory`) → Warning key `cert-expiry:<id>` (alertRule `certificateExpiry`).
- Readiness: re-run daily (via `IReadinessService`); new failures → Warning key `readiness:<checkId>` (alertRule `readinessFailure`).
`EventSink.Raise` persists EventEntry (keep 90 days), applies per-key cooldown (`CooldownMinutes`),
sends notifications for enabled alert rules via INotifier, writes to Windows Event Log (source
`Caddy Proxy Manager`, when enabled and on Windows). E-mail: MailKit, plain-text + simple HTML body with
server name, event, time (UTC and local), details, link to the UI.

## Web UI (web/)

Stack: Vite + React 19 + TypeScript (strict) + react-router 7 + @tanstack/react-query 5 + Tailwind CSS v4
(`@tailwindcss/vite`) + lucide-react icons. Fonts bundled locally (servers may be offline):
`@fontsource-variable/inter` (UI) and `@fontsource-variable/jetbrains-mono` (domains, IPs, ports, paths, config, logs).
No external CDN requests at runtime (strict CSP `default-src 'self'`).

Design brief: professional, calm, dense, aimed at sysadmins — "straight to the point". Functionally: host
tables with status and add/edit dialogs with tabs, in a modern admin-console look:
- Left sidebar (collapsible) with grouped nav: **Overview** (Dashboard) · **Sites** (Proxy Hosts, Redirects, Static Sites, Custom Responses, Streams) · **Security** (Certificates, Access Lists) · **Caddy** (Service & Updates, Plugins, Configuration) · **Server** (Readiness, Logs, Events) · **Administration** (Notifications, Settings, Users, Audit Log).
- Top bar: page title + breadcrumb, global Caddy status pill (Running · v2.11.4 · "Update available"), user menu (change password, sign out), light/dark toggle (default: follow OS).
- Neutral slate palette, one accent (teal/emerald), status colours green/amber/red/grey used consistently with text labels (never colour alone). 13–14px base, tight tables, monospace for technical values, subtle borders, no gradients/emoji.
- Tables: search, status dot + enabled toggle, domains as mono chips, target, TLS badge (ACME / Internal / Custom: name / HTTP), access list, row action menu (Edit, Enable/Disable, Delete), empty states with a primary action.
- Host editor: modal/drawer with tabs **Details** (domains chip input, kind-specific fields, upstream rows with scheme/host/port, load balancing, health check) · **TLS** (mode radio, certificate picker, Force HTTPS, HSTS, HTTP/3 note) · **Access** (access list, block exploits) · **Headers** (request/response header ops) · **Locations** (proxy only) · **Advanced** (raw JSON routes with validation, access log toggle, notes).
- After every save show the apply result: success toast "Saved and applied", warning toast when `writtenOnly`, and an error dialog showing Caddy's error text for 422.
- Dashboard: status cards (Caddy service, version/update, hosts, certificates expiring, readiness pass/warn/fail, upstream health), recent events, quick actions.
- Readiness page: machine summary (hostname, OS, domain, network profiles), checks grouped by category with status icons, expandable details, copyable PowerShell scripts, "Fix" buttons (admin), "Run checks" button, GPO script panel with copy/download when domain joined.
- Caddy page: service state + Start/Stop/Restart/Install service; binary installed vs latest with release notes; "Update to vX" button → job progress log (poll `/api/jobs/{id}`); plugins page with catalog search, desired plugins list, "Rebuild with plugins" action; configuration page with generated/running JSON viewer, revisions list, apply button, Caddyfile mode editor.
- Certificates: table incl. ACME/internal/custom with expiry colour, "Add certificate" dialog (Upload PEM, Upload PFX, Paste PEM, Reference file path on disk/share), used-by hosts, download internal root CA.
- Settings: Caddy (ACME email/CA/EAB, ports, HTTP/3, default site, trusted proxies, log level, cert store path, config mode), Updates (auto check/install, interval, outbound proxy), UI (port, bind, HTTPS), Notifications (SMTP form + test button, webhook, alert rule toggles, cooldown).
- First run: Setup page (token from `C:\ProgramData\CaddyProxyManager\setup-token.txt`, admin email/name/password); Login page.
- API client: `fetch` wrapper with `credentials: 'same-origin'`, `X-CPM-Request: 1` on non-GET, problem+json error parsing, 401 → redirect to /login (or /setup when needsSetup).
- Role-aware UI: hide/disable mutations for viewers, admin-only sections for admins.
- Dev: `npm run dev` proxies `/api` to `http://localhost:5081`. `npm run build` outputs `web/dist` (embedded into the exe by the host csproj).

## Coding conventions

- .NET 10, nullable enabled, minimal APIs grouped with `MapGroup("/api/...").RequireAuthorization(Policies.X)`.
- Use `IHttpClientFactory` named client `"default"` (User-Agent set) for outbound calls.
- Never block the request thread on long work; long work → `IJobRunner`.
- Windows-only code guarded with `OperatingSystem.IsWindows()`; the app must build and run on macOS/Linux for development.
- Record audit entries (`IAuditLog.Record`) for every mutation; raise events via `IEventSink` for operational problems.
- Secrets always through `ISecretProtector`; never returned by the API.

---

## Round 2 additions (contract)

All new settings fields follow the settings wire-shape rule above. New Core fields: `SiteHost.UpstreamNtlm`;
`CertificateSource.PfxFile|WindowsStore` + `Certificate.SourcePath, PfxPasswordProtected, StoreLocation, StoreName,
StoreThumbprint, StoreSubject, LastSyncedAt, LastSyncError`; `CaddySettings.ExtraAppsJson, AcmeIssuerJsonProtected
(wire hasAcmeIssuerJson/acmeIssuerJson), TlsConnectionPolicyJson`; `BinarySettings.ProxyCaddyTraffic, NoProxy,
ManagerReleaseRepo`; `NotificationSettings.SmtpAuth (none|password|oAuth2ClientCredentials), OAuthTenantId,
OAuthClientId, OAuthClientSecretProtected (wire hasOAuthClientSecret/oAuthClientSecret), WebhookFormat
(generic|slack|teamsWorkflow)`; `UiSettings.RedirectHttpToHttps`; `BinaryOverview.CanRollback, PreviousVersion,
ManagerVersion, ManagerLatestVersion, ManagerLatestUrl, ManagerUpdateAvailable`; `User.ExternalSource, ExternalId`;
`ICaddyBinaryManager.StartInstallFromFile, StartRollback, CanRollback`.

### Privilege boundaries (Config)
- Operators may NOT: set/change `AdvancedRoutesJson` (admin only — 403 when an operator changes it), point Static
  `RootPath` at/inside DataDir, the Caddy storage dir, the certificate store, a drive root, %WINDIR%, %ProgramFiles%
  (400 for everyone; UNC roots admin-only), or target the Caddy admin endpoint / manager UI ports on loopback with
  an upstream or stream (400 for everyone).
- Certificate path-based sources (`/path`, `/pfx-path`, `/windows-store`, JSON re-point) are admin-only; only
  `.pem .crt .cer .key .pfx .p12` extensions; never inside DataDir except the configured certificate store; generic
  "cannot read" errors.
- Viewers get redacted configs: `/api/config/preview|running|revisions/{id}` replace ACME `external_account.mac_key`,
  DNS provider secrets from `acmeIssuerJson` and `http_basic` password hashes with `"***"` unless the caller is admin.
  `GET /api/settings/caddy` hides `rawCaddyfile`/`serverOptionsJson`/`extraAppsJson` values from non-admins.

### New endpoints
| Module | Method | Path | Notes |
|---|---|---|---|
| Config | POST | /api/certificates/pfx-path | admin; `{ name?, pfxPath, pfxPassword? }` → `{item, apply}`; PFX converted to PEM under `<store>/<id>/`, re-converted when the PFX changes (watcher) |
| Config | GET | /api/certificates/windows-store?location=LocalMachine&store=My | admin; `[{ thumbprint, subject, dnsNames[], issuer, notBefore, notAfter, hasPrivateKey, exportable, template? }]` (non-Windows: `[]`) |
| Config | POST | /api/certificates/windows-store | admin; `{ name?, storeLocation?, storeName?, thumbprint? , subject? }` (exactly one of thumbprint/subject) → `{item, apply}`; exported to PEM; re-synced every 15 min and on `/sync` (follows AD CS autoenrollment renewals when `subject` is used) |
| Config | POST | /api/certificates/{id}/sync | operator; re-read/re-export now → `{item, apply}` |
| Config | POST | /api/config/caddyfile/import | admin; `{ caddyfile }` → `{ drafts: SiteHost[], unmapped: string[], warnings: string[] }` (nothing saved) |
| Config | POST | /api/config/caddyfile/import/commit | admin; `{ hosts: SiteHost[] }` → `{ created: number, apply }` (transactional, 409 on domain conflicts) |
| Platform | POST | /api/caddy/binary/upload | admin; multipart `file` (caddy.exe or official release .zip), optional `sha512` → JobInfo (offline/air-gapped install through the verified swap pipeline) |
| Platform | POST | /api/caddy/binary/rollback | admin → JobInfo |
| Ops | GET/PUT | /api/settings/ldap | admin; LdapSettings (Ops-owned doc): `{ enabled, server, port, security: none|startTls|ldaps, allowInvalidCertificate, bindDn, hasBindPassword/bindPassword, baseDn, userFilter ("(&(objectClass=user)(|(sAMAccountName={0})(userPrincipalName={0})))" default), adminGroupDn, operatorGroupDn, viewerGroupDn, nestedGroups: bool }` |
| Ops | POST | /api/settings/ldap/test | admin; `{ username, password }` → `{ ok, role?, displayName?, email?, groups?: string[], error? }` |
| Ops | GET/PUT | /api/settings/backup | admin; BackupSettings (Ops-owned): `{ enabled, hourLocal (0-23), directory (local or UNC; default DataDir\backups), keep (1-365), hasPassword/password (AES-256 zip encryption) }` |
| Ops | GET | /api/backups | admin; `[{ name, size, createdAt }]` of scheduled backups |
| Ops | GET | /api/backups/{name} | admin; download |
| Ops | POST | /api/backups/run | admin; run a scheduled-style backup now → `{ name }` |

Login with LDAP enabled: the login `email` field also accepts `DOMAIN\user`, `user` or a UPN; local accounts are
tried first (break-glass), then LDAP; LDAP users are provisioned/updated as `User{ExternalSource="ldap"}` with the
role from group membership (no matching group → 403 "not authorised"); `UserDto` gains `externalSource?`.

### Monitoring additions (Ops)
- `cert-missing:<domain>`: an enabled Acme/Internal host (non-wildcard domain) has no covering certificate
  10 minutes after `UpdatedAt` → Warning (alertRule `certificateExpiry`) with the last `tls.obtain`/`tls.issuance`
  error lines from caddy.log; Recovered when issued.
- Manager behind Caddy: the host uses forwarded headers from loopback proxies only (X-Forwarded-For/Proto).
