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

**The HTTP→HTTPS redirect sends clients to the wrong port** (for example `https://site:8443/` from the Internet,
where only 443 is open). Caddy listens on a non-standard HTTPS port behind NAT or port forwarding. Set **Settings ›
Caddy › Public HTTPS port** to the port clients use (443 in that example); redirects then always use it. See
[installation.md › Other HTTP/HTTPS ports](installation.md#other-httphttps-ports-nat-and-port-forwarding).

**A proxy host answers `503` to everyone, or an *Upstream unhealthy* alert appears.** How Caddy decides that a
backend (upstream) is down depends on the host:

- **Several upstreams:** passive health checks. A request that fails at the proxy level (connection refused, reset
  or dropped, timeout; *not* an HTTP error status from the backend) takes that upstream out of rotation for 30 s.
  Requests are retried on the other upstreams for up to 5 s. When every upstream is out, the host answers 503 until
  one comes back.
- **One upstream:** no passive checks. A failing backend fails only the affected requests (502). It never takes the
  whole host offline, even when a client repeats a request that makes the backend drop the connection.
- **Active health check** (host › *Active health check*, any number of upstreams): Caddy requests the path every
  interval, with the same `Host` header as proxied requests. One failed check (wrong status, timeout, no connection)
  marks the upstream unhealthy until a check passes. With a single upstream the host answers **503 in the meantime**,
  so choose a path that answers reliably, and set *Expected status* when it redirects (for example 3 = any 3xx).
- **Alerts:** the monitor reports upstreams whose health Caddy tracks, meaning those with an active check or in a host
  with several upstreams. A single upstream without an active check is not monitored and never raises
  *Upstream unhealthy*. Turn on an active check to be alerted when it goes down.

Caddy's log (**Server › Logs › Caddy**) names the failing upstream and the error. The *Upstream unhealthy* alert names
its address.

**HTTP/3 clients get `425 Too Early`.** Early data (QUIC 0-RTT) reached a host with an IP access list. The manager
disables 0-RTT, so this comes from a configuration that the manager has not applied yet (for example an earlier
version, before the first save) or from custom JSON that sets `allow_0rtt`. Apply the configuration once (**Caddy ›
Configuration**, or save any host). See [installation.md › HTTP/3 and 0-RTT](installation.md#http3-and-0-rtt).

**Port 80/443 already in use.** Readiness › Ports names the owner. PID 4 (*System*) means http.sys: IIS
(`Stop-Service W3SVC; Set-Service W3SVC -StartupType Disabled`), WinRM HTTPS listeners, SSRS, ADFS/WAP.
`netsh http show servicestate` lists http.sys registrations.

**E-mail over TLS fails with a certificate revocation error.** Windows could not download the CRL/OCSP data of the
mail server's certificate. It uses the WinHTTP proxy for that, not the manager's outbound proxy: set it with
`netsh winhttp set proxy proxy-server="<host:port>" bypass-list="<local>"` (Readiness › Connectivity › *WinHTTP proxy*).

**Microsoft 365 e-mail stops with `535 5.7.139`.** Basic authentication for SMTP AUTH is disabled for the tenant:
switch to *Microsoft 365 OAuth2* ([notifications.md](notifications.md)).

**Readiness says "Windows PowerShell runs in ConstrainedLanguage mode".** A Windows Defender Application Control /
AppLocker policy puts PowerShell into Constrained Language mode, which blocks the scripts the firewall, network and
port checks use. Check those settings manually (`Get-NetFirewallProfile`, `Get-NetConnectionProfile`,
`Get-NetTCPConnection -State Listen`) or allow the checks in the policy; everything else works normally.

**Restart from the UI.** *Restart* ends the manager process with exit code 1 without reporting "stopped" to Windows;
the Service Control Manager logs event 7031/7034 (*terminated unexpectedly*) and restarts it after 5 seconds through
the recovery actions. Those events are expected. If the manager does not come back, check
`sc.exe qfailure CaddyProxyManager` (restart 5 s / 10 s / 30 s) — re-running the MSI or `CaddyManager.exe install`
repairs it.

**`sc query Caddy` shows `START_PENDING` although Caddy serves, and `sc stop Caddy` / `net stop Caddy` fail with
error 1061 ("cannot accept control messages") or 1052.** A known issue of Caddy v2.11.4 as a Windows service: when
Caddy loads its config before Windows has finished starting the service (small config, fast machine), its "running"
report is lost and the service stays *Starting* ([caddy PR #8012](https://github.com/caddyserver/caddy/pull/8012), not
released yet). Caddy itself works normally. The manager handles it:

- While the service is *Starting* but Caddy's admin API answers, the UI shows Caddy as *Running* and no *Caddy down*
  alert is raised.
- *Start*/*Restart* and every stop from the manager (UI, binary updates) first re-send the running configuration unchanged
  to Caddy's admin API (`POST /load` without forcing a reload, so nothing restarts); Caddy then reports *Running* and
  stops normally. If the service still refuses, the manager asks Caddy to exit through its admin API (`/stop`) and,
  as the last resort, ends the Caddy process. While it does that it switches the service's recovery actions off, so
  Windows does not restart Caddy a few seconds later, and restores them afterwards (the manager's next start restores
  them too). The manager log names the path taken ("stopped (Fallback)" / "stopped (Killed)"). Uninstall
  (`CaddyManager.exe uninstall`, `uninstall-caddy-service`) retries the stop for 10 s and then ends the process the
  same way.
- A service that stays *Starting* for more than 2 minutes **without** the admin API answering is reported as *Unknown*
  with an explanation (the monitor alerts); *Start* ends that hung process and starts Caddy again.

To get the service out of that state by hand, use *Restart* on **Caddy › Service & Updates**. Without the UI:
`sc.exe queryex Caddy` for the PID, then `Stop-Process -Id <PID> -Force`; Windows counts that as a failure and starts
Caddy again after 5 s through the recovery actions (and it may end up in the same state again).

**Local firewall rules have no effect.** A GPO disables local rules — deploy the GPO script (see
[group-policy.md](group-policy.md)).

**Backends using Windows authentication (IIS/SharePoint) prompt repeatedly.** Enable *Upstream uses Windows
authentication (NTLM)* on the host and add the plugin `github.com/caddyserver/ntlm-transport`.

**Caddy update failed.** The previous binary is restored automatically; details are in Events and the job log. Use
*Roll back* on Caddy › Service & Updates to return to the previous version at any time.
