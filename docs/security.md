# Security notes

## Design

- Both services run as **LocalSystem** (no passwords to manage, no logged-on user). `C:\ProgramData\CaddyProxyManager`
  is restricted to SYSTEM and Administrators; the manager re-applies this at every start.
- Secrets (SMTP/OAuth, ACME EAB and DNS credentials, LDAP bind, PFX passwords) are encrypted with DPAPI (machine
  scope) and never returned by the API. Viewers see redacted configurations.
- UI: session cookie (HttpOnly, SameSite=Strict), server-side revocation on sign-out/password change, CSRF header on
  all state-changing requests, rate-limited sign-in and setup, strict Content-Security-Policy, audit log of every
  change, fallback-deny authorization on every API route.
- First-run setup needs a one-time token that only administrators on the server can read.
- Operators cannot use raw Caddy routes, file/store certificate sources, or static roots inside system/app folders,
  and no host can proxy to the Caddy admin API or the manager itself.

## Hardening checklist

- [ ] Enable HTTPS for the UI (Settings › Management UI) with a certificate from your PKI, and *Redirect HTTP to
      HTTPS* — or publish the UI through Caddy itself (proxy host → `http://127.0.0.1:81`, access list restricted to
      admin subnets) and bind the UI to `127.0.0.1`.
- [ ] Restrict the UI port to admin networks (firewall rule scope).
- [ ] Use Active Directory sign-in with dedicated groups; keep one local break-glass admin with a long password.
- [ ] Set a password on scheduled backups and store them on a restricted share.
- [ ] Sign-in is not possible for users outside the mapped groups — review group membership regularly.

## Caddy admin API

Caddy's admin API listens on `127.0.0.1:2019` without authentication (Caddy's default). Any process on the server
can reconfigure Caddy, which runs as LocalSystem. On servers where untrusted users can log on interactively (RDS,
jump hosts), treat that as local privilege escalation risk: keep such servers admin-only, or change the admin listen
address in **Settings › Caddy** to a unix socket inside the protected data folder
(`unix//C:/ProgramData/CaddyProxyManager/caddy/admin.sock`) after testing it on your build.
