# Readiness, firewall and Group Policy

**Server › Readiness** checks that the server can actually serve traffic and explains each finding with a fix.

| Category | Checks |
|---|---|
| System | OS build, service account, free disk, clock skew (ACME needs an accurate clock), pending reboot, data folder permissions |
| Firewall | Defender Firewall service, enabled profiles, inbound rules for 80/TCP, 443/TCP, 443/UDP, the UI port(s) and every stream port — evaluated against the **effective** policy (local + GPO) for the active profiles; detects GPOs that disable local rules |
| Network | Connection profile per interface (Domain / Private / Public) |
| Domain | Membership, computer OU, GPO recommendation |
| Ports | Who owns 80/443 — IIS, http.sys users (WinRM HTTPS, SSRS, ADFS…), other processes |
| Connectivity | Outbound to the ACME CA, GitHub and caddyserver.com; WinHTTP / outbound proxy |
| DNS | Each ACME host name resolves to this server |
| Caddy | Binary, service, recovery options, admin API on loopback |

*Fix* buttons (administrators) create local firewall rules in the group **Caddy Proxy Manager**, set a non-domain
Public network to Private, and repair the Caddy service.

## Domain-joined servers: deploy the rules with a GPO

On domain members, firewall policy is usually central and a GPO may set *Apply local firewall rules: No* — then
local rules are silently ignored. Readiness detects this and offers a **GPO script** (copy or download
`Deploy-CaddyProxyManagerFirewallGpo.ps1`). Run it on a domain controller or a management machine with GPMC
(`Install-WindowsFeature GPMC` / RSAT) in **Windows PowerShell 5.1** as a user who can create and link GPOs:

```powershell
.\Deploy-CaddyProxyManagerFirewallGpo.ps1 -WhatIf   # preview
.\Deploy-CaddyProxyManagerFirewallGpo.ps1
```

It is idempotent. It creates (or reuses) the GPO *Caddy Proxy Manager - Firewall*, writes the inbound rules into it,
restricts it to this computer via security filtering (`-RestrictToThisComputer $false` to target the whole OU) and
links it to the computer's OU. Then on the server: `gpupdate /target:computer /force` and re-run the checks.

## Network profile

A domain-joined server should show **DomainAuthenticated**. If it shows Public/Private, Network Location Awareness
could not reach a domain controller at boot: check that the NIC's DNS servers are the DCs, then
`Restart-Service NlaSvc -Force`. Non-domain servers on *Public* can be switched to *Private* with the Fix button.

## Distributing the internal root CA

If any host uses *Internal CA* TLS, clients must trust Caddy's root:

1. **Certificates › Internal root CA** → download `caddy-root.crt`.
2. GPMC → edit a GPO linked to the client OUs → *Computer Configuration › Policies › Windows Settings › Security
   Settings › Public Key Policies › Trusted Root Certification Authorities* → *Import* the file.

Or with PowerShell on a machine with GPMC:

```powershell
certutil -dspublish -f caddy-root.crt RootCA   # publish to the whole forest (Enterprise Admin), or use the GPO above
```
