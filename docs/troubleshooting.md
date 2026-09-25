# Troubleshooting

## Where to look

| What | Where |
|---|---|
| Alerts and history | **Server › Events** |
| Caddy log | **Server › Logs › Caddy**, file `C:\ProgramData\CaddyProxyManager\logs\caddy\caddy.log` |
| Access logs (per host, when enabled on the host's Advanced tab) | **Server › Logs › Access**, `...\logs\access\<domain>.log` |
| Manager log | **Server › Logs › Manager**, `...\logs\manager\manager-yyyyMMdd.log` |
| Windows | Event Viewer › Application, source *Caddy Proxy Manager*; `Get-Service CaddyProxyManager, Caddy` |
| Applied configs | **Caddy › Configuration › Revisions** |

## Common problems

**The UI is unreachable after changing its port/bind/HTTPS.** Run `CaddyManager.exe configure --reset-ui`
elevated, then `Restart-Service CaddyProxyManager`. The manager also falls back to defaults automatically when a
listener cannot start (see the manager log).

**"Caddy rejected the configuration".** Nothing was changed — the previous config keeps running. The dialog shows
Caddy's error; typical causes are raw JSON routes, a plugin module missing from the binary, or a certificate file
that cannot be read.

**Certificates are not issued (ACME).** Readiness shows the usual causes: port 80/443 blocked (firewall/NAT), DNS
not pointing at the server, clock skew, outbound HTTPS blocked or a proxy required (Settings › Updates › proxy for
Caddy), CAA records. The *certificate missing* alert includes Caddy's ACME error. Use *Let's Encrypt staging* while
testing to avoid rate limits.

**Port 80/443 already in use.** Readiness › Ports names the owner. PID 4 (*System*) means http.sys: IIS
(`Stop-Service W3SVC; Set-Service W3SVC -StartupType Disabled`), WinRM HTTPS listeners, SSRS, ADFS/WAP.
`netsh http show servicestate` lists http.sys registrations.

**Local firewall rules have no effect.** A GPO disables local rules — deploy the GPO script (see
[group-policy.md](group-policy.md)).

**Backends using Windows authentication (IIS/SharePoint) prompt repeatedly.** Enable *Upstream uses Windows
authentication (NTLM)* on the host and add the plugin `github.com/caddyserver/ntlm-transport`.

**Caddy update failed.** The previous binary is restored automatically; details are in Events and the job log. Use
*Roll back* on Caddy › Service & Updates to return to the previous version at any time.
