# Security notes

## Design

- Both services run as **LocalSystem** (no passwords to manage, no logged-on user). `C:\ProgramData\CaddyProxyManager`
  is restricted to SYSTEM and Administrators; the manager re-applies this at every start.
- Secrets (SMTP/OAuth, ACME EAB and DNS credentials, LDAP bind, PFX passwords) are encrypted with DPAPI (machine
  scope) and never returned by the API. Viewers see redacted configurations. Machine scope means "any process running
  on the computer can unprotect data"
  ([DataProtectionScope](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.dataprotectionscope))
  and the additional entropy is part of the (public) source code: the **file permissions** of
  `C:\ProgramData\CaddyProxyManager` and of the backups are what keeps a local non-admin user from reading them. A copy of
  `manager.db` or of an unencrypted backup that is readable by other users on this server exposes the secrets.
- UI: session cookie (HttpOnly, SameSite=Strict), server-side revocation on sign-out/password change, CSRF header on
  all state-changing requests, rate-limited sign-in and setup, strict Content-Security-Policy, audit log of every
  change, fallback-deny authorization on every API route.
- First-run setup needs a one-time token that only administrators on the server can read.
- Tray icon and `CaddyManager.exe caddy|manager ...`: the tray runs as the signed-in user and has no rights of its
  own. Start / stop / restart runs `CaddyManager.exe` through UAC, and Caddy control then reaches the manager over the
  local named pipe `CaddyProxyManager.Control`. The pipe's access list admits only SYSTEM and Administrators with an
  elevated token (a UAC-filtered token is refused), denies network clients, and only the manager can serve it: it holds
  the pipe as its first instance, and the command line talks to it only when the pipe server is the running
  `CaddyProxyManager` service process. Actions are audited under the Windows account name.
- Operators cannot use raw Caddy routes, file/store certificate sources, or static roots inside system/app folders,
  and no host can proxy to the Caddy admin API or the manager itself.

## Hardening checklist

- [ ] Enable HTTPS for the UI (Settings › Management UI) with a certificate from your PKI, and *Redirect HTTP to
      HTTPS* — or publish the UI through Caddy itself (proxy host → `http://127.0.0.1:81`, access list restricted to
      admin subnets) and bind the UI to `127.0.0.1`.
- [ ] Restrict the UI port to admin networks (firewall rule scope).
- [ ] Use Active Directory sign-in with dedicated groups; keep one local break-glass admin with a long password.
- [ ] Set a password on scheduled backups (always when the target is a network share: files written to a UNC path
      keep the share's permissions, the manager only locks down local backup folders) and store them on a restricted share.
- [ ] Sign-in is not possible for users outside the mapped groups — review group membership regularly.

## Caddy admin API

Caddy's admin API listens on `127.0.0.1:2019` without authentication (Caddy's default). Any process on the server
can reconfigure Caddy, which runs as LocalSystem. On servers where untrusted users can log on interactively (RDS,
jump hosts), treat that as local privilege escalation risk: keep such servers admin-only, or change the admin listen
address in **Settings › Caddy** to a unix socket inside the protected data folder
(`unix//C:/ProgramData/CaddyProxyManager/caddy/admin.sock`) after testing it on your build.
