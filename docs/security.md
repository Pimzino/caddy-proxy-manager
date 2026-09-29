# Security

This page describes how Caddy Proxy Manager protects the console, its data and Caddy, and what you should do to
harden a server. It is for administrators of the Windows server; the settings it mentions need the Admin role.

## Services and data folder

- Both Windows services, `CaddyProxyManager` (the manager and console) and `Caddy`, run as **LocalSystem**. There are
  no service passwords to manage.
- All data lives in `C:\ProgramData\CaddyProxyManager`: the database, the key ring that signs session cookies,
  certificate private keys, Caddy's storage, logs, backups and the first-run setup token.
- The installer restricts this folder to **SYSTEM** and **Administrators**, with inheritance from ProgramData removed.
  By default ProgramData lets every local user read and create files in its subfolders.
- At every start the manager checks the folder. If it still inherits permissions from ProgramData, the manager applies
  the same restriction. A folder that already has its own (protected) permissions is left as it is. If the manager
  cannot fix the permissions, it logs a start-up warning.
- **Server › Readiness** has a **Data directory permissions** check. It warns when the folder inherits permissions or
  grants access to Users, Authenticated Users, Everyone, Guests, INTERACTIVE or ANONYMOUS LOGON. An admin can click
  **Fix**, or run the `icacls` script shown there. See [Readiness](readiness.md).

## Secrets

Passwords and keys that the manager needs in clear text are encrypted before they are stored:

- SMTP password and OAuth client secret (notifications)
- ACME EAB key, ACME issuer JSON and DNS provider credentials
- Redis password and encryption key, and custom storage settings
- LDAP bind password
- Management UI PFX password and certificate PFX passwords
- Backup password
- Cluster keys

They are encrypted with Windows DPAPI in machine scope. The encryption is tied to this computer, not to a user
account: any process on the server that can read the database can decrypt them. The permissions of the data folder,
and of your backups, are what keep other local users away from them.

Because the encryption is tied to the computer, secrets in a backup cannot be decrypted on another server. After
restoring on a different server, enter them again. See [Backup and restore](backup-restore.md).

The console never sends a stored secret back to the browser. A stored secret shows as "Stored securely — not shown"
with **Change** and **Clear** buttons.

User passwords are not encrypted but stored as bcrypt hashes (work factor 12).

## What non-admins can see

- Viewers and operators see the generated and running Caddy configuration, configuration revisions and raw Caddy
  routes on hosts with secrets replaced by `***`.
- Some **Settings › Caddy** fields (the raw Caddyfile and the advanced JSON fields) are not shown to them at all.
- The notification, management UI, directory, backup settings, users, audit log and manager log are admin-only.

See [Users and roles](users.md#permission-matrix) for the full list.

## Secrets in logs and alerts

Caddy's own output can contain credentials, for example a DNS provider's API token in an error message. Before the
manager shows or sends such text, it replaces every configured Caddy secret with `***`. It also masks the values of
credential-like URL parameters such as `token=`, `key=`, `apikey=`, `password=` and `secret=`. This applies to:

- Caddy log lines in **Server › Logs**;
- Caddy errors shown when a configuration is rejected or Caddy fails to start;
- certificate alerts that include Caddy's error lines;
- messages from cluster nodes.

## Console sign-in and sessions

- Local accounts are checked before the directory, so a local admin can sign in when the directory is unavailable.
- Each client address gets 10 sign-in attempts per minute (per /64 for IPv6). The same limit covers first-run setup and
  the directory test. Accounts are not locked.
- The session cookie cannot be read by scripts and is not sent with requests that start on other sites. It is marked
  secure when the console is used over HTTPS.
- Signing out, changing or resetting a password, and disabling or deleting a user end sessions on the server; a copied
  cookie stops working.
- First-run setup needs a one-time token. It is written to `C:\ProgramData\CaddyProxyManager\setup-token.txt` and to
  the manager log in `logs\manager\`, both in the data folder that only SYSTEM and Administrators can read. It is never
  written to the Windows Application log, which any user who signs in to the server can read: the start-up warning there
  only says where the token is. The file is deleted when setup completes.

See [Users and roles](users.md#sessions) and [Directory sign-in](directory-sign-in.md).

## Request protection

- **Sign-in required**: every API request needs a signed-in user unless it is explicitly public. The public ones are
  the health check (`GET /api/health`), sign-in, sign-out, first-run setup, and the endpoint that cluster servers use
  to talk to each other, which uses its own encrypted and signed messages.
- **Roles**: each action checks the role on the server, not only in the browser.
- **Cross-site request forgery**: every request that changes something must carry the header `X-CPM-Request: 1`, which
  a page on another site cannot add. Sign-in and first-run setup are exempt; the session cookie is not sent with
  cross-site requests anyway.
- **Response headers** on every response:
  - `Content-Security-Policy: default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self'; font-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'`
  - `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: same-origin`
  - `Cross-Origin-Opener-Policy: same-origin`, `Cross-Origin-Resource-Policy: same-origin`
  - `Permissions-Policy: camera=(), microphone=(), geolocation=(), payment=(), usb=()`
  - `Cache-Control: no-store` on API responses, so configuration, user and audit data stay out of caches.
- The console does not send an HSTS header. Use **Redirect HTTP to HTTPS**, or publish the console through Caddy, to
  keep sessions off plain HTTP.

## Forwarded headers

The console trusts `X-Forwarded-For` and `X-Forwarded-Proto` only from a proxy that connects from the loopback address
(`127.0.0.1` or `::1`), that is Caddy on the same server. From any other address these headers are ignored, so a
client cannot fake its address in the audit log or get around the sign-in limit. See
[Publish the console through Caddy](management-ui.md#publish-the-console-through-caddy).

## Caddy admin API

Caddy Proxy Manager controls Caddy through Caddy's admin API, by default on `127.0.0.1:2019`. The admin API has no
authentication: any process on the server that can connect to it can reconfigure Caddy, which runs as LocalSystem.

- **Settings › Caddy › Admin API listen address** accepts only a loopback address or `localhost` with a port, for
  example `127.0.0.1:2019`. Other addresses are refused.
- **Server › Readiness** fails the **Caddy admin API** check if the admin API is not on a loopback address.
- No proxy host, stream or raw route may forward to the admin API port on this server, for any role.
- On servers where people who are not administrators can sign in (Remote Desktop hosts, jump hosts), a local user
  could use the admin API to take over Caddy. Allow only administrators to sign in to the proxy server.

## Forwarding to the console itself

- Only admins can create a proxy host that forwards to the console's own ports, to publish it through Caddy. For
  operators and viewers this target is refused.
- Streams can never forward to the console's ports.
- "This server" includes `localhost`, loopback addresses, every address of the server's network adapters and its host
  names.

## Local control from Windows

The tray icon and `CaddyManager.exe caddy start|stop|restart` reach the manager over a local named pipe,
`CaddyProxyManager.Control`, which only accepts start, stop and restart for Caddy.

- Only SYSTEM and Administrators with an elevated token can use it. A standard (UAC-filtered) token is refused, so the
  tray asks for elevation.
- Connections from the network are denied.
- Only the manager service can serve the pipe, and the command line checks that the pipe belongs to the running
  service before using it.
- Actions are recorded in the audit log under the Windows account, for example `CORP\alice (Windows)`.

See [Command line](cli.md).

## Cluster connections

A primary sends its nodes requests on their management UI port. Each request and answer is encrypted and authenticated
with that node's own key, also over `http://`. With an `https` node URL, the primary also pins the node's HTTPS
certificate: it accepts only the certificate with the pinned SHA-256 fingerprint, even when Windows trusts another
certificate for that address. After you replace or renew a node's certificate, an admin re-pins it on the primary. See
[Clustering](cluster.md#replacing-a-nodes-https-certificate).

## Hardening checklist

- Encrypt console traffic (see [Management UI settings](management-ui.md)). Either:
  - turn on **Enable HTTPS** in **Settings › Management UI** with a PFX from your PKI, and **Redirect HTTP to HTTPS**;
  - or publish the console through a Caddy proxy host with an access list, and set **Listen on** to
    **This server only (127.0.0.1)**. Do not do this on cluster nodes.
- Limit the console's firewall rule to your admin networks (and, on cluster nodes, the primary server).
- In a cluster, use `https` node URLs so the primary pins each node's certificate.
- Use [directory sign-in](directory-sign-in.md) with dedicated groups, and keep one local admin with a long password as
  break-glass account.
- Review the mapped groups regularly. When someone must lose access at once, disable their account on the **Users**
  page: an open session of a directory user keeps its role until the next sign-in.
- Keep the Caddy admin API on `127.0.0.1` and allow only administrators to sign in to the server.
- Run **Server › Readiness** and resolve the **Data directory permissions** check.
- Protect backups: set a backup password and store backups where only administrators can read them. See
  [Backup and restore](backup-restore.md).
- Review the [audit log](audit-log.md) for failed sign-ins and unexpected changes.

## Related

- [Users and roles](users.md)
- [Directory sign-in](directory-sign-in.md)
- [Management UI settings](management-ui.md)
- [Audit log](audit-log.md)
- [Readiness](readiness.md)
- [Installation](installation.md)
