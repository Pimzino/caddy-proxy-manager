# VM test checklist

What CI cannot prove. The `windows-latest` runner already covers the MSI install / restart / repair / uninstall
(`installer/test-msi.ps1`), the Service Control Manager behaviour, firewall facts, the WinHTTP proxy, the Event Log
source and DPAPI (`Category=WindowsE2E`). This list is for real servers: domain, Group Policy, Microsoft 365, reboots,
localised Windows and hardened policies. Run it before a release on fresh VMs and record the result of every step.

**Lab**

| VM | Role |
|---|---|
| `DC01` | Windows Server 2025, new forest `lab.test`, AD CS Enterprise CA (for the LDAPS/StartTLS and store certificates) |
| `WEB01` | Windows Server 2022 or 2025, domain member, Desktop Experience — the main test server |
| `CORE01` | Windows Server 2025 Server Core, domain member |
| `OLD01` | Windows Server 2016 (build 14393), not joined — only for the OS check |
| `DE01` | Windows Server 2022 **de-DE** language image, workgroup — localisation checks |
| A client | Browser; a second machine on the same network for firewall tests |

Also: a Microsoft 365 test tenant with one licensed mailbox (`alerts@<tenant>`) and admin rights for Entra ID and
Exchange Online; a Teams channel; a file share `\\DC01\backups$`.

Take a checkpoint of every VM before starting so each section can start from a clean state.

## 1. Fresh MSI install

| # | Step | Expected result |
|---|---|---|
| 1.1 | On `WEB01`, double-click the MSI, accept defaults. | Wizard completes; the final page shows `http://WEB01:81/` and where the setup token is. |
| 1.2 | `Get-Service CaddyProxyManager, Caddy` | Both *Running* (Caddy may take up to a minute: the manager downloads it first). |
| 1.3 | `sc.exe qc CaddyProxyManager`; `sc.exe qfailure CaddyProxyManager`; `sc.exe qfailureflag CaddyProxyManager` | `AUTO_START (DELAYED)`, `LocalSystem`; RESTART 5000 / 10000 / 30000 ms, reset 86400 s; flag TRUE. |
| 1.4 | `icacls C:\ProgramData\CaddyProxyManager` | Only `NT AUTHORITY\SYSTEM` and `BUILTIN\Administrators`, inheritance disabled. |
| 1.5 | `Get-NetFirewallRule -Group 'Caddy Proxy Manager' \| Get-NetFirewallPortFilter` | UI rule on TCP 81. |
| 1.6 | Start menu → *Caddy Proxy Manager* | Opens `http://localhost:81/`. |
| 1.7 | From the client, browse to `http://WEB01:81/`, paste the token from `C:\ProgramData\CaddyProxyManager\setup-token.txt`, create the admin. | Signed in; the token file is gone. |
| 1.8 | On `CORE01`: `msiexec /i <msi> /qn UI_PORT=8081 /l*v C:\install.log` | Exit code 0; UI on 8081 from the client; `install.log` contains `WixQuietExec` lines for configure and configure-service. |
| 1.9 | On `OLD01` (Server 2016): run the MSI. | Refused before anything is installed: "requires … Windows Server 2019 or later (this is build 14393)". |
| 1.10 | Event Viewer › Application, source *Caddy Proxy Manager* | Events show their text (no "The description for Event ID … cannot be found"). |

## 2. Upgrade from the previous MSI

| # | Step | Expected result |
|---|---|---|
| 2.1 | Install the **previous release** MSI with `UI_PORT=8181`; set up an admin, add a host, a certificate and SMTP settings. | Working baseline. |
| 2.2 | Run the new MSI without properties (interactive, then repeat with `/qn` on a second VM). | Completes without a reboot prompt; one entry in *Apps*; UI still on 8181 (remembered). |
| 2.3 | Sign in; open hosts, certificates, notifications. | Everything kept; secrets still work (*Send test* succeeds without re-entering the password). |
| 2.4 | `sc.exe qfailure CaddyProxyManager`; `sc.exe qc CaddyProxyManager` | Same as 1.3. |
| 2.5 | Caddy › Service & Updates | Caddy kept its version; no unexpected Caddy restart except the manager's own. |

## 3. Reboot without anyone logging on

| # | Step | Expected result |
|---|---|---|
| 3.1 | `Restart-Computer` on `WEB01`; do **not** log on. From the client, poll `http://WEB01:81/api/health` and a proxied site. | Both answer within ~2 minutes of boot (manager is *delayed start*; Caddy starts automatically before it). |
| 3.2 | Log on afterwards; Server › Events | No *Caddy down* alert for the boot; readiness *Pending reboot* is Pass (unless Windows Update queued one). |
| 3.3 | With UI HTTPS enabled (Settings › Management UI), reboot 3 times and use *Restart* from the UI 3 times. Count `C:\ProgramData\Microsoft\Crypto\Keys` and `\RSA\MachineKeys` before and after. | No growth (the UI certificate's key file is removed at exit). |
| 3.4 | Server › Restart manager | UI back within ~15 s; System log shows event 7031/7034 for *Caddy Proxy Manager* (expected: the restart uses the recovery actions). |
| 3.5 | After each boot of 3.3: `sc.exe queryex Caddy`, then Caddy › Service & Updates. | `RUNNING`, or `START_PENDING` with Caddy serving (caddy PR #8012); the UI shows *Running* in both cases and Events has no *Caddy down*. Note which one you saw. |
| 3.6 | If 3.5 showed `START_PENDING`: `sc.exe stop Caddy`; then Caddy › Service & Updates › *Restart*; then *Stop*, *Start*. | `sc stop` fails with 1061/1052 (the upstream bug); the UI restart/stop/start succeed within ~20 s; afterwards `sc.exe queryex Caddy` shows `RUNNING` and `sc.exe qfailure Caddy` still lists restart 5 s / 5 s / 30 s; the System log has no unexpected Caddy restart. |

## 4. Readiness checks and fixes

| # | Step | Expected result |
|---|---|---|
| 4.1 | `WEB01`: Server › Readiness › Run checks | Report completes; no *collect* errors; OS shows the right edition and build. |
| 4.2 | Remove the Caddy firewall rules (`Get-NetFirewallRule -Group 'Caddy Proxy Manager' \| Remove-NetFirewallRule`), run checks, click *Fix* on 80 and 443. | Fail → Pass; rules recreated in the group; the client reaches 80/443. |
| 4.3 | Set the Public profile to *Block all incoming connections* on a non-domain VM (`Set-NetFirewallProfile -Name Public -AllowInboundRules False`) with the NIC on Public. | 443 check Fail with the "blocks all incoming connections" remediation; no Fix button. Restore afterwards. |
| 4.4 | Same, but `-DefaultInboundAction Allow` as well. | 443 check Pass ("inbound rules are ignored … default inbound action is Allow"). Restore. |
| 4.5 | Install IIS on a test VM (`Install-WindowsFeature Web-Server`), run checks. | *Ports* names IIS / http.sys (PID 4) on 80 with the `Stop-Service W3SVC` remediation. |
| 4.6 | `netsh winhttp set proxy proxy-server="squid.lab.test:3128"`; run checks; then `netsh winhttp reset proxy`. | *WinHTTP proxy* Info naming the proxy and its use for revocation checks; after reset Pass. |
| 4.7 | On `DE01` (German): run checks, `CaddyManager.exe service-status`. | WinHTTP check correct (Pass when no proxy — no false "proxy configured"); service status shows state, PID, recovery 5s/10s/30s. |
| 4.8 | **Domain, GPO.** On `DC01`: GPO linked to the server's OU with *Windows Defender Firewall › Domain Profile › Settings › Apply local firewall rules: No*. On `WEB01`: `gpupdate /force`, run checks. | 80/443 Warn/Fail with "local rules are ignored by Group Policy" and the GPO remediation; no Fix. |
| 4.9 | Readiness › GPO script → download; on `DC01` (Windows PowerShell 5.1, GPMC) run with `-WhatIf`, then for real; run it a second time. | GPO *Caddy Proxy Manager - Firewall* created and linked; `Get-GPPermission` shows `WEB01$` GpoApply and Authenticated Users GpoRead; the second run changes nothing. |
| 4.10 | `WEB01`: `gpupdate /target:computer /force`; `Get-NetFirewallRule -PolicyStore ActiveStore -Group 'Caddy Proxy Manager'`; run checks. | Rules with `PolicyStoreSourceType GroupPolicy`; 80/443 Pass. |
| 4.11 | Network profile: on `WEB01` the NIC shows *DomainAuthenticated*. Point its DNS at a public resolver, `Restart-Service NlaSvc -Force`, run checks; revert. | Network check warns with the NLA/DNS remediation; back to Pass after revert. |
| 4.12 | Hardened PowerShell: on a lab VM, set the machine environment variable `__PSLockdownPolicy=4` (or deploy a WDAC policy in enforce mode), restart the manager, run checks; remove it afterwards. | Firewall/network/port checks report "Windows PowerShell runs in ConstrainedLanguage mode …" instead of an unreadable error; other checks unaffected. |

## 5. Custom certificate from the Windows store

| # | Step | Expected result |
|---|---|---|
| 5.1 | AD CS: duplicate the *Web Server* template, *Allow private key to be exported*, enrol `app.lab.test` into `LocalMachine\My` on `WEB01` (certlm.msc or `Get-Certificate`). | Certificate with an exportable key (CNG KSP). |
| 5.2 | Security › Certificates › Add › Windows certificate store; pick it, *Follow renewals by subject*. | Listed as exportable; the template name is shown; PEM files appear in the certificate store. |
| 5.3 | Host `app.lab.test` → Custom certificate; browse from a domain client. | Served with the AD CS certificate, trusted. |
| 5.4 | Renew the certificate in certlm.msc (same subject). Wait up to 15 minutes. | The new thumbprint is served without manual steps. |
| 5.5 | Enrol a certificate from a template **without** exportable key and try to add it. | Rejected with the "private key is not exportable" message. |

## 6. Active Directory sign-in

| # | Step | Expected result |
|---|---|---|
| 6.1 | Settings › Directory (LDAP): `DC01.lab.test`, StartTLS/389, bind account, base DN, groups `CPM-Admins` / `CPM-Operators` / `CPM-Viewers`. *Test sign-in* as a user in `CPM-Operators`. | Role Operator, groups listed. |
| 6.2 | Same with *Security: None* against the 2025 DC. | Fails with the "domain controller requires signing / use StartTLS or LDAPS" message. |
| 6.3 | LDAPS/636. | Success. |
| 6.4 | Nested group: user in `G1`, `G1` member of `CPM-Admins`; *Nested groups* on, then off. | Admin with it on; "not authorised" with it off. |
| 6.5 | Sign in with `LAB\user`, `user` and the UPN. | All three work; the user appears with a *Directory* badge; its external ID equals `(Get-ADUser user).ObjectGUID`. |
| 6.6 | Channel binding: on `DC01` set `HKLM\SYSTEM\CurrentControlSet\Services\NTDS\Parameters\LdapEnforceChannelBinding = 2`, restart NTDS, repeat 6.1 and 6.3. Check Directory Service events 3039-3041. | Record the result (sign-in works, or the exact error). |
| 6.7 | DC with a certificate `WEB01` does not trust; *Allow invalid certificate* on, StartTLS and LDAPS; then off. | Record whether StartTLS honours the option like LDAPS; with it off both fail with the TLS hint. |

## 7. Microsoft 365 e-mail and Teams

| # | Step | Expected result |
|---|---|---|
| 7.1 | Follow [notifications.md](notifications.md) (app registration, `SMTP.SendAsApp`, `New-ServicePrincipal` with the **Enterprise application** object ID, `Add-MailboxPermission`). Configure OAuth2, sender = the mailbox. *Send test*. | Mail arrives. |
| 7.2 | Change the sender to another address without SendAs. *Send test*. | Fails with `SendAsDenied` and the `Add-RecipientPermission … -AccessRights SendAs` hint. Grant it; after replication the test succeeds. |
| 7.3 | Use `New-ServicePrincipal` with the App registration's object ID instead (fresh app). | Token accepted by Entra but SMTP rejects it: the error explains the Enterprise application object ID. |
| 7.4 | Password mode against `smtp.office365.com`. | Readiness shows the *E-mail authentication (Microsoft 365)* warning with the December 2026 date; in a tenant with Basic SMTP AUTH disabled, *Send test* shows `535 5.7.139` plus the retirement hint. |
| 7.5 | Security *Auto*, port 587, password; point the host at a relay that does not offer STARTTLS. | Fails with "does not support the STARTTLS extension"; the relay log shows no AUTH. |
| 7.6 | Server behind a proxy only (block direct 443 outbound, Settings › Updates › proxy). Teams Workflows webhook (*Anyone* may trigger). *Send test*. | Card posted in the channel; the proxy log shows the request to `*.logic.azure.com` and to `login.microsoftonline.com` (OAuth). |
| 7.7 | Change the Workflows trigger to *Any user in my tenant*. *Send test*. | Rejected (401/403) — documented: the trigger must allow Anyone. |

## 8. Backup and restore

| # | Step | Expected result |
|---|---|---|
| 8.1 | Settings › Backups: directory `\\DC01\backups$\cpm`, no permissions for `WEB01$`. *Run backup now*. | Fails; the message names `LAB\WEB01$` (not `NT AUTHORITY`). |
| 8.2 | Grant `LAB\WEB01$` Modify on share and folder, set a backup password, run again. | Backup written; opens in 7-Zip only with the password. |
| 8.3 | Scheduled backup at the next hour; wait. | New archive; older ones pruned to *Keep*. |
| 8.4 | Restore the backup on `WEB01` (Settings › Backups › Restore, password, *Restart now*). | Hosts/certificates back; `pre-restore-<timestamp>` folder created. |
| 8.5 | Restore the same backup on `CORE01`. | Everything except secrets moves; SMTP/LDAP/PFX passwords must be re-entered (DPAPI of the original machine). |

## 9. Uninstall

| # | Step | Expected result |
|---|---|---|
| 9.1 | *Settings › Apps › Caddy Proxy Manager › Uninstall* on `WEB01`. | Both services gone (`Get-Service CaddyProxyManager, Caddy` fails), firewall group *Caddy Proxy Manager* gone, Start-menu shortcut gone, event log source removed. |
| 9.2 | `Test-Path C:\ProgramData\CaddyProxyManager` | True: data is kept for a reinstall. |
| 9.3 | GPO rules from 4.9 | Still present (domain policy is not touched by uninstall); remove the GPO manually if wanted. |
| 9.4 | Reinstall the MSI. | Existing data and admin account are used; no new setup token needed. |
