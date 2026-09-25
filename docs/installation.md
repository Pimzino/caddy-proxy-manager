# Installation, upgrade and uninstall

## Requirements

- 64-bit Windows: Windows Server 2019, 2022 or 2025 — **Desktop Experience or Server Core** — or Windows 10 (1809+)
  / Windows 11. Nothing runs on the server's desktop: the UI is used from a browser on any machine, so Server Core
  works exactly like a full install (install with `msiexec … /qn` or `install.ps1`).
- Local administrator rights to install.
- Inbound TCP 80 and 443 reachable from clients (plus UDP 443 only if you turn on HTTP/3); TCP 81 (UI) from admin networks.
- Outbound HTTPS to `api.github.com`, `github.com`, `objects.githubusercontent.com` (Caddy downloads),
  `caddyserver.com` (plugin builds), and your ACME CA (e.g. `acme-v02.api.letsencrypt.org`). All optional: see
  *Offline servers* below.

Nothing else is required — the executable is self-contained (no .NET runtime to install).

## Install with the MSI

Interactive: double-click `CaddyProxyManager-<version>-x64.msi`. The final page shows the UI address and where the
setup token is.

Silent:

```powershell
msiexec /i CaddyProxyManager-1.0.0-x64.msi /qn
msiexec /i CaddyProxyManager-1.0.0-x64.msi /qn UI_PORT=8081 BIND=10.0.0.15
```

| Property | Meaning |
|---|---|
| `UI_PORT` | TCP port of the web UI (default 81). Remembered for upgrades. |
| `BIND` | IP address the UI listens on (default all addresses; `127.0.0.1` = local only). |

The MSI installs `CaddyManager.exe` into `C:\Program Files\Caddy Proxy Manager`, locks
`C:\ProgramData\CaddyProxyManager` down to SYSTEM and Administrators, registers the `CaddyProxyManager` service
(LocalSystem, automatic delayed start, restart on failure), adds a firewall exception for the UI port, adds a
Start-menu shortcut to the UI and starts the service.

## Install from the zip

Extract the zip, then in an **elevated** PowerShell in that folder:

```powershell
.\install.ps1                  # or: .\install.ps1 -UiPort 8081 -Bind 10.0.0.15
```

`install.ps1` wraps `CaddyManager.exe install` (see [cli.md](cli.md)). Re-running it upgrades in place.

## First run

1. The service starts, downloads the latest Caddy, registers the `Caddy` service and starts it with a minimal config.
2. Get the setup token (elevated PowerShell):
   ```powershell
   Get-Content C:\ProgramData\CaddyProxyManager\setup-token.txt
   ```
   It is also written to the manager log (`C:\ProgramData\CaddyProxyManager\logs\manager\`).
3. Browse to `http://<server>:81/` (or the Start-menu shortcut on the server), paste the token and create the first
   administrator (password: 12+ characters). The token file is deleted afterwards.
4. **Server › Readiness → Run checks.** Fix what is red/amber (firewall rules, network profile, port conflicts, DNS).
   On domain-joined servers use the generated GPO script — see [group-policy.md](group-policy.md).
5. **Settings › Caddy:** set the ACME e-mail address and CA.
6. **Administration › Notifications:** configure SMTP and send a test.

## Offline / air-gapped servers

- Download `caddy_<ver>_windows_amd64.zip` from https://github.com/caddyserver/caddy/releases (or a custom build from
  https://caddyserver.com/download) on another machine.
- In the UI: **Caddy › Service & Updates › Upload binary** — optionally paste the SHA-512 from the release's
  `checksums.txt`. The upload goes through the same pipeline as online updates (version check, config validation,
  swap, health check, automatic rollback).
- Behind a proxy instead: **Settings › Updates › Outbound proxy**; enable *Use the proxy for Caddy* so ACME works too.

## Upgrade

Run the newer MSI (or re-run `install.ps1` with the newer zip). Data, settings and certificates in
`C:\ProgramData\CaddyProxyManager` are kept. The UI shows *Manager vX available* when **Settings › Updates ›
Manager release repository** is set.

Caddy itself is updated from the UI (**Caddy › Service & Updates**); the previous binary is kept for one-click rollback.

## Ports

| Port | Used by | Notes |
|---|---|---|
| TCP 80 | Caddy | HTTP, ACME HTTP-01 challenge, redirects to HTTPS |
| TCP 443 | Caddy | HTTPS, ACME TLS-ALPN-01 challenge |
| UDP 443 | Caddy | HTTP/3 — optional, off by default (Settings › Caddy) |
| TCP 81 | Manager UI | configurable; optional HTTPS listener (default 8443) |
| 127.0.0.1:2019 | Caddy admin API | loopback only — see [security.md](security.md) |
| stream ports | Caddy (caddy-l4) | one per TCP/UDP stream |

## Uninstall

- MSI: *Settings › Apps* or `msiexec /x CaddyProxyManager-1.0.0-x64.msi /qn`. Removes both services, the firewall
  rules of the group *Caddy Proxy Manager* and the event log source. Data in `C:\ProgramData\CaddyProxyManager` is kept.
- Zip: `.\uninstall.ps1` (add `-Purge` to delete all data, including certificates and the internal CA).
