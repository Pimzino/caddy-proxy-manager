# Users and sign-in

## Roles

| Role | Can |
|---|---|
| Viewer | See everything except secrets, users, audit log and manager logs |
| Operator | Manage hosts, streams, access lists and uploaded certificates; start/stop/restart Caddy; apply config; run readiness checks |
| Admin | Everything: users, settings, notifications, Caddy binary/plugins, readiness fixes, raw Caddy routes, file/store certificate sources, backups |

## Active Directory (LDAP) sign-in

**Settings › Directory (LDAP)**:

- Server `dc01.corp.local` (or the domain name), port 636 with *LDAPS* (recommended) or 389 with *StartTLS*.
- Bind account: a read-only service user (`CN=svc-cpm,OU=Service Accounts,DC=corp,DC=local`) and its password.
- Base DN `DC=corp,DC=local`; the default user filter matches `sAMAccountName` or `userPrincipalName`.
- Group DNs for the Admin / Operator / Viewer roles (nested groups supported). Users in none of them cannot sign in.

Use **Test sign-in** to see the resolved role and groups. Notes: use the DC's FQDN (it must match its LDAPS
certificate); Server 2025 domain controllers require signing/encryption, so use *StartTLS* or *LDAPS*; the primary
group (*Domain Users*) never appears in `memberOf`, so don't map a role to it. Group membership is evaluated at
sign-in; disabling a user in the manager blocks them immediately. Users then sign in with `DOMAIN\user`, `user` or their UPN;
their account appears in **Users** with a *Directory* badge and their role is refreshed at every sign-in. Local
accounts keep working (keep one local admin as break-glass).

## Forgotten password

On the server, in an elevated prompt, stop the service and reset:

```powershell
Stop-Service CaddyProxyManager
& 'C:\Program Files\Caddy Proxy Manager\CaddyManager.exe' reset-password --email admin@contoso.com
Start-Service CaddyProxyManager
```

A strong password is generated and printed (or pass `--password`). `list-users` shows all accounts.
