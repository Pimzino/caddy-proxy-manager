# Caddy Proxy Manager

[![Build & tests](https://github.com/Pimzino/caddy-proxy-manager/actions/workflows/build.yml/badge.svg?branch=main)](https://github.com/Pimzino/caddy-proxy-manager/actions/workflows/build.yml)
[![Latest release](https://img.shields.io/github/v/release/Pimzino/caddy-proxy-manager?sort=semver&display_name=tag&label=release)](https://github.com/Pimzino/caddy-proxy-manager/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/Pimzino/caddy-proxy-manager/total?label=downloads)](https://github.com/Pimzino/caddy-proxy-manager/releases)
[![Caddy](https://img.shields.io/github/v/release/caddyserver/caddy?label=caddy%20(latest)&color=1F88C0)](https://github.com/caddyserver/caddy/releases/latest)
[![Windows](https://img.shields.io/badge/Windows-Server%202019%2B%20%7C%2010%20%7C%2011-0078D4?logo=windows&logoColor=white)](docs/installation.md#requirements)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Last commit](https://img.shields.io/github/last-commit/Pimzino/caddy-proxy-manager)](https://github.com/Pimzino/caddy-proxy-manager/commits/main)
[![License: MIT](https://img.shields.io/github/license/Pimzino/caddy-proxy-manager)](LICENSE)
[![Open issues](https://img.shields.io/github/issues/Pimzino/caddy-proxy-manager)](https://github.com/Pimzino/caddy-proxy-manager/issues)

A web-based reverse proxy manager for **[Caddy](https://caddyserver.com)**, built to run natively on **Windows** as a
proper Windows service — Windows Server 2019, 2022 and 2025 (Desktop Experience or Server Core) and Windows 10/11,
64-bit.

One self-contained executable installs, updates and supervises Caddy, generates Caddy's configuration from a
clean admin console, manages certificates (automatic ACME, Caddy's internal CA, or your own), checks that the
server is actually ready to serve traffic, and e-mails you when something goes wrong.

- **No service account, no logged-on user.** Both the manager and Caddy run as Windows services under LocalSystem.
- **Caddy lifecycle.** Downloads the latest Caddy release (SHA-512 verified), shows when an update is available,
  updates with validation and automatic rollback, rebuilds Caddy with plugins, supports offline (air-gapped) installs
  and manual rollback.
- **Sites.** Proxy hosts (load balancing, health checks, custom locations, header rules, WebSockets, NTLM upstreams),
  redirects, static sites, custom responses, and TCP/UDP streams (with the `caddy-l4` plugin).
- **Certificates.** Automatic Let's Encrypt / ZeroSSL / private ACME CA (incl. EAB and DNS challenge via plugins),
  Caddy's internal CA (root exportable for GPO), and your own certificates — uploaded PEM/PFX, referenced PEM or PFX
  files on disk or a share (renewals picked up automatically), or the Windows certificate store (follows AD CS
  auto-enrolment renewals). Point any domain at any certificate.
- **Access lists.** IP allow/deny rules and basic auth, per host.
- **Server readiness.** Windows Firewall rules (evaluated against the effective policy incl. GPO), network
  connection profile, domain membership with a ready-to-run **GPO firewall script**, port conflicts (IIS/http.sys),
  outbound ACME connectivity, DNS, clock, disk — with one-click fixes.
- **Alerts.** SMTP (incl. Microsoft 365 OAuth2), Slack/Teams/generic webhooks and the Windows Event Log: Caddy down
  (with auto-restart), rejected configuration, unhealthy upstreams, certificates expiring or failing to issue,
  updates available, readiness regressions.
- **Operations.** Users with roles (viewer / operator / admin) and optional Active Directory (LDAP) sign-in, audit
  log, event history, log viewer (Caddy, per-host access logs, manager), configuration revisions, Caddyfile mode and
  Caddyfile import, scheduled and on-demand backups.

## Quick start

1. Download `CaddyProxyManager-<version>-x64.msi` (or the zip) from the build artifacts / releases.
2. On the server, run the MSI (or, from the extracted zip in an elevated PowerShell: `.\install.ps1`).
   Silent: `msiexec /i CaddyProxyManager-1.0.0-x64.msi /qn` — optional `UI_PORT=8081`.
3. Read the one-time setup token (elevated PowerShell):
   ```powershell
   Get-Content C:\ProgramData\CaddyProxyManager\setup-token.txt
   ```
4. Browse to `http://<server>:81/`, paste the token and create the first administrator.
5. Open **Server › Readiness**, run the checks and apply the suggested fixes (or deploy the GPO script).
6. Add your first proxy host.

The manager downloads the latest Caddy on first start. On servers without Internet access use
**Caddy › Service & Updates › Upload binary**.

## Documentation

| Topic | |
|---|---|
| [Installation, upgrade and uninstall](docs/installation.md) | MSI and zip installs, silent parameters, ports, first run |
| [Certificates](docs/certificates.md) | ACME, internal CA, own certificates, shares, Windows store, wildcards |
| [Group Policy and firewall](docs/group-policy.md) | Readiness checks, GPO firewall rules, distributing the internal root CA |
| [Notifications](docs/notifications.md) | SMTP, Microsoft 365, webhooks, alert rules |
| [Users and sign-in](docs/users.md) | Roles, Active Directory (LDAP), password reset |
| [Backup and restore](docs/backup-restore.md) | Scheduled backups, restore, moving to another server |
| [Command line](docs/cli.md) | `CaddyManager.exe` verbs |
| [Troubleshooting](docs/troubleshooting.md) | Logs, common failures |
| [Security notes](docs/security.md) | Threat model, hardening checklist |
| [Development](docs/development.md) | Building, testing, architecture |

## Layout on the server

| What | Where |
|---|---|
| Manager | `C:\Program Files\Caddy Proxy Manager\CaddyManager.exe` — service `CaddyProxyManager` |
| Caddy | `C:\ProgramData\CaddyProxyManager\caddy\bin\caddy.exe` — service `Caddy` |
| Data (DB, certificates, Caddy storage, logs, backups) | `C:\ProgramData\CaddyProxyManager` (SYSTEM + Administrators only) |
| Web UI | `http://<server>:81/` (configurable; optional HTTPS) |

## Building

See [docs/development.md](docs/development.md). In short, on Windows: `.\build.ps1` produces
`artifacts\CaddyProxyManager-<ver>-x64.msi` and a zip; CI (`.github/workflows/build.yml`) does the same on
`windows-latest`.

## License

Caddy Proxy Manager is released under the [MIT License](LICENSE). Bundled third-party components and their
licenses are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

[Caddy](https://caddyserver.com) itself is **not** bundled: the manager downloads the official Caddy binary
(Apache License 2.0) from Caddy's GitHub releases or caddyserver.com when you install or update it. Plugins you
choose are licensed by their respective authors.

This is an independent community project. It is not affiliated with, sponsored by or endorsed by the Caddy project
or its maintainers; "Caddy" is used only to describe what this software manages and remains a trademark of its owner.
