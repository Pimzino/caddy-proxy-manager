# Caddy Proxy Manager documentation

Caddy Proxy Manager runs the [Caddy](https://caddyserver.com) web server on Windows and gives you a web console to
publish sites through it. You add proxy hosts, redirects, static sites and certificates in the console; the manager
turns them into Caddy's configuration, applies it, and keeps an eye on Caddy, your certificates and the server.

This documentation describes the product as it works today. You can read it without signing in, at `/docs` on any
server where the manager is installed. In the console, the **Help** button (question-mark icon) in the top bar opens
the page for the screen you are on.

## What it does

- **Runs Caddy as a Windows service.** The manager installs Caddy, starts and stops it, updates it (with automatic
  rollback if the new version fails to start) and rebuilds it with plugins. See [Service & updates](caddy-service.md).
- **Publishes sites.** [Proxy hosts](proxy-hosts.md) forward requests to backend servers, [redirects](redirects.md)
  send visitors elsewhere, [static sites](static-sites.md) serve files from a folder or share, and
  [custom responses](custom-responses.md) answer with a fixed status and body. [Streams](streams.md) forward raw TCP
  and UDP ports.
- **Handles certificates.** Automatic certificates from Let's Encrypt, ZeroSSL or your own ACME CA (including the DNS
  challenge for wildcards), Caddy's internal CA, or your own certificates from files, PFX files or the Windows
  certificate store. See [Certificates](certificates.md) and [ACME & DNS challenge](acme.md).
- **Controls access.** [Access lists](access-lists.md) combine IP rules and basic-authentication users.
- **Checks the server.** [Readiness checks](readiness.md) verify firewall rules, ports, DNS, outbound access and more,
  and can fix common problems for you.
- **Monitors and alerts.** The [dashboard](dashboard.md), [traffic statistics](traffic-statistics.md),
  [logs](logs.md) and [events](events.md) show what is happening; [notifications](notifications.md) send alerts by
  e-mail, webhook or the Windows Event Log.
- **Scales out.** A primary server can manage other servers as a [cluster](cluster.md) and push the same
  configuration to all of them.
- **Keeps you in control.** [Users and roles](users.md), optional [Active Directory sign-in](directory-sign-in.md), an
  [audit log](audit-log.md), configuration [revisions](configuration.md) and [backups](backup-restore.md).

## Where to start

1. [Install Caddy Proxy Manager](installation.md) on a Windows server.
2. [Sign in for the first time](getting-started.md) with the setup token and create the first administrator.
3. Run the [readiness checks](readiness.md) and fix what they report.
4. [Add your first proxy host](proxy-hosts.md).

## Roles at a glance

Every account has one of three roles. The pages in this documentation say which role an action needs.

| Role | Can do |
|---|---|
| Viewer | See everything except the admin-only pages. Cannot change anything. |
| Operator | Everything a viewer can, plus manage hosts, streams, certificates and access lists, and start, stop and restart Caddy. |
| Admin | Everything, including users, settings, notifications, updates, backups and readiness fixes. |

The full permission list is on [Users & roles](users.md).

## How the manager works

The manager keeps your hosts, certificates and settings in its own database. Every time you save a change, it
generates a complete Caddy configuration and loads it into Caddy through Caddy's local admin API. Caddy checks the
new configuration before it switches to it: if Caddy rejects it, the previous configuration stays active and the
console shows Caddy's error. Each attempt is recorded as a revision on the [Configuration](configuration.md) page.

The manager and Caddy are two separate Windows services, so the console stays available while Caddy is stopped or
misconfigured, and your sites keep running while the manager is restarted or upgraded.

## Related

- [Troubleshooting](troubleshooting.md)
- [Security & hardening](security.md)
- [Command line & tray icon](cli.md)
