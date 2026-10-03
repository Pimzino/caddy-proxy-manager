# Troubleshooting

This page lists common problems by symptom, with the cause and the fix. Most fixes in the console need the **Admin** role. The command-line fixes need an elevated PowerShell on the server.

## Where to look first

| What | Where |
|---|---|
| Alerts and their history | **Events** |
| Caddy's log | **Logs › Caddy**, file `C:\ProgramData\CaddyProxyManager\logs\caddy\caddy.log` |
| Access logs of a host | **Logs › Access logs**, files `C:\ProgramData\CaddyProxyManager\logs\access\<domain>.log` |
| Manager log (Admin) | **Logs › Manager**, files `C:\ProgramData\CaddyProxyManager\logs\manager\manager-yyyyMMdd.log`, kept for 14 days |
| Windows | Event Viewer › Windows Logs › Application, source *Caddy Proxy Manager*. Also `Get-Service CaddyProxyManager, Caddy`. |
| Configurations Caddy accepted or rejected | **Caddy › Configuration › Revisions** |
| Server checks | **Readiness** |

In the Windows Application log, manager warnings and errors appear under the *Caddy Proxy Manager* source. The first-run setup token is the exception: it is only in `setup-token.txt` and the manager log. Events from the **Events** page appear there too (unless **Write to the Windows Event Log** is turned off on **Notifications**), with IDs by category: Caddy 1000, configuration 1100, upstream 1200, certificate 1300, update 1400, readiness 1500, notification 1600, backup 1700, other 1900. Add 1 for a warning, 2 for an error and 3 for a recovery.

See [Logs](logs.md) and [Events](events.md).

## Locked out of the console

### The console is unreachable after changing its port, address or HTTPS

When the manager cannot listen on the configured address or port, it falls back to port `81` on all interfaces. It records the event "Management UI started with fallback settings" and logs the reason in the manager log. First try `http://<server>:81/`.

If that does not work, reset the listener from the command line. The command needs the service to be stopped, because it opens the database directly.

```powershell
Stop-Service CaddyProxyManager
& 'C:\Program Files\Caddy Proxy Manager\CaddyManager.exe' configure --reset-ui
Start-Service CaddyProxyManager
```

`--reset-ui` sets port `81` on all interfaces with HTTPS and the HTTPS redirect turned off. Make sure TCP `81` is allowed in Windows Firewall. See [Management UI](management-ui.md).

### Forgot the admin password

Reset the password of a local account from the command line, with the service stopped:

```powershell
Stop-Service CaddyProxyManager
& 'C:\Program Files\Caddy Proxy Manager\CaddyManager.exe' reset-password --email admin@example.com
Start-Service CaddyProxyManager
```

- Without `--password <password>`, a strong password is generated and printed.
- `--enable` also re-enables a disabled account.
- `list-users` shows all accounts with their role and status.

Resetting a password signs out that user's existing sessions. Directory (LDAP) accounts have no local password: reset them in the directory, or use a local admin account. See [Users](users.md) and [Command line](cli.md).

### "Too many attempts" when signing in

After 10 sign-in attempts within a minute from one address, the sign-in page shows "Too many sign-in attempts. Wait a minute before trying again." Wait a minute.

## The console says it cannot reach the management service

The message "The management service could not be reached. Check that the Caddy Proxy Manager service is running." means the browser got no answer from the manager.

1. Check the service: `Get-Service CaddyProxyManager`.
2. Start it if it is stopped: `Start-Service CaddyProxyManager`, or `CaddyManager.exe manager start`.
3. If it keeps stopping, read the newest file in `C:\ProgramData\CaddyProxyManager\logs\manager\` and the Application log.

Caddy keeps serving sites while the manager is stopped.

### After Restart now, Windows logs "terminated unexpectedly"

**Restart now** in a **Restart required** panel (Admin) ends the manager on purpose. The panel appears after you change the management UI listener or stage a restore. Windows then restarts the manager through the service recovery actions: after 5, 10 and 30 seconds. The System log entries 7031 or 7034 ("terminated unexpectedly") are expected.

If the manager does not come back, check the recovery actions with `sc.exe qfailure CaddyProxyManager`. Running the installer again, or `CaddyManager.exe install`, repairs them.

## Caddy does not start or keeps stopping

When Caddy is stopped or its admin API does not answer on two checks in a row, the **Events** page shows "Caddy is not running (state: …)" or "Caddy is running but its admin API is not reachable". With **Restart Caddy automatically** turned on under **Notifications**, the manager tries to start Caddy up to 3 times in 10 minutes. After that it raises "Automatic restart of Caddy gave up after 3 attempts in 10 minutes".

1. Read **Logs › Caddy** for the reason.
2. Open **Readiness** and look at the **Ports** section. A port that another program holds is the most common cause.
3. Start Caddy with **Start** or **Restart** on **Caddy › Service & Updates**.

If the Caddy service stays *Starting* for more than 2 minutes without its admin API answering, the console shows its state as *Unknown* and raises an alert. **Start** then ends the hung process and starts Caddy again.

See [Caddy service](caddy-service.md).

### Port 80 or 443 is already in use

**Readiness › Ports** names the program that holds the port.

- "… is held by http.sys (PID 4 'System')" means a Windows component registered a URL on that port. It is typically IIS (W3SVC), WinRM, SQL Server Reporting Services, ADFS, WSUS or Windows Admin Center. List the registrations with `netsh http show servicestate view=requestq`, then move that service to another port or stop it. For IIS: `Stop-Service W3SVC; Set-Service W3SVC -StartupType Disabled`.
- "… is already in use by *program* (PID …). Caddy cannot bind it." names another program. Stop or reconfigure it, or change Caddy's ports on **Settings › Caddy**.

### A Caddy update failed

Use **Roll back** on **Caddy › Service & Updates** to return to the previous Caddy version. The job log and **Events** show why the update failed. See [Caddy service](caddy-service.md).

## A certificate is not issued

When an enabled host with an ACME or internal certificate still has no certificate 10 minutes after its last change, the **Events** page shows "No certificate has been issued for *domain*". The event includes the last certificate errors from Caddy's log.

Check the usual causes:

- **DNS**: the domain's public DNS must point at this server, or at the load balancer in front of it. **Readiness** has a **DNS** check per domain.
- **Ports**: HTTP-01 needs TCP `80`, and TLS-ALPN-01 needs TCP `443`, reachable from the internet. Check the firewall and any NAT. If you cannot open them, use the DNS challenge (see [ACME](acme.md)).
- **Clock**: the **System clock** check on **Readiness** compares the server's time with Let's Encrypt. TLS and ACME fail when the clock is wrong.
- **Outbound HTTPS**: Caddy must reach the CA. The **Connectivity** checks on **Readiness** test outbound HTTPS. If your network needs a proxy, set it on **Settings › Updates** and turn on **Send Caddy’s own traffic through the proxy** (see [Updates](updates.md)).
- **Rate limits**: while testing, choose **Let’s Encrypt (staging — for testing, untrusted)** as the certificate authority.

In a cluster behind a load balancer, HTTP-01 and TLS-ALPN-01 need shared storage. See [Clustering](cluster.md#shared-caddy-storage).

## Caddy rejected the configuration

When you save a change that Caddy refuses, a dialog titled **Caddy rejected the configuration** appears. It says "The change was not saved and the previous configuration is still active" and shows Caddy's error. Nothing changed: Caddy keeps running the previous configuration.

- Read Caddy's error in the dialog. It names the part of the configuration it refused.
- **Caddy › Configuration › Revisions** lists recent attempts, with **Applied** or **Rejected**.
- If the error names a module Caddy does not have, add the plugin on **Caddy › Plugins** and rebuild Caddy. See [Plugins](plugins.md).

Two other outcomes look similar:

- **Configuration not applied**: the change was saved, but Caddy did not accept the resulting configuration. Read the error and correct the change.
- **Saved — Caddy is not running**: the configuration was written to disk. Caddy loads it when it starts.

See [Host options](host-options.md) and [Configuration](configuration.md).

## Streams are saved but not active

The **Streams** page shows "The installed Caddy binary does not include the layer4 plugin", and saving or enabling a stream reports "Stream saved but not active" or "Stream enabled but not active". Streams need the plugin `github.com/mholt/caddy-l4`.

1. Click **Open Plugins**, or go to **Caddy › Plugins**.
2. Add `github.com/mholt/caddy-l4`.
3. Click **Rebuild & install**.

The streams become active when Caddy runs with the plugin. See [Streams](streams.md).

## A backend is reported down or never alerts

The manager does not add passive health checks, so a single failing request never takes a backend out of rotation. The manager reports an upstream as unhealthy only when the host has an active health check. A backend without one is not monitored and never raises **Upstream unhealthy**. To be alerted when a backend goes down, turn on **Enable health checks** in the host's **Active health check** section. See [Host options](host-options.md).

## Clients get 431, or uploads and downloads are cut off

Since Caddy `v2.11.6`, Caddy limits requests more strictly than before. After an update of Caddy you may see:

- `431 Request Header Fields Too Large`: the request line and headers are larger than 16 KiB. This is common with Kerberos (Negotiate) sign-in for users in many groups, and with large cookies. Raise **Request header limit (KiB)**.
- An upload that ends after a pause, logged with status `499`: the client sent nothing for a minute. Raise **Upload idle timeout (seconds)**.
- A download that ends when the client stops reading for a minute. Raise **Download idle timeout (seconds)**.
- An application no longer receives a request header with a dot in its name, such as `X.Trace`. Add it to **Request headers to keep**.

All four are under **Settings › Caddy › Request limits and headers**. See [Caddy settings](caddy-settings.md#request-limits-and-headers).

## Clients are redirected to the wrong HTTPS port

If Caddy listens on a non-standard HTTPS port behind NAT or port forwarding, HTTP-to-HTTPS redirects use that port. Set **Public HTTPS port** on **Settings › Caddy** to the port clients use, for example `443`. See [Caddy settings](caddy-settings.md).

## Readiness says PowerShell runs in ConstrainedLanguage mode

A Windows Defender Application Control or AppLocker policy runs PowerShell in a restricted language mode, and the readiness scripts cannot run. Check the firewall, network profile and port settings by hand, for example with `Get-NetFirewallProfile`, `Get-NetConnectionProfile` and `Get-NetTCPConnection -State Listen`. Alternatively, allow the checks in the policy. Everything else works normally. See [Readiness](readiness.md).

## E-mail over TLS fails with a certificate revocation error

Windows could not download the revocation data of the mail server's certificate. It uses the WinHTTP proxy for that, not the manager's outbound proxy. The **WinHTTP proxy (certificate revocation checks)** check on **Readiness** shows the current setting. Set it with:

```powershell
netsh winhttp set proxy proxy-server="proxy.example.com:8080" bypass-list="<local>"
```

## Restore problems

| Message or symptom | Cause and fix |
|---|---|
| This backup is encrypted. Enter the backup password. | Enter the encryption password in **Backup password**. |
| The backup password is incorrect. | Use the password that was set when the backup was created. It cannot be recovered. |
| The archive has no manifest.json — it is not a Caddy Proxy Manager backup. | Upload a zip that the console created. |
| The backup format version … is newer than this version … supports. | Upgrade Caddy Proxy Manager on this server first. |
| The database in the backup has no enabled administrator account … | That backup would lock everyone out. Use another backup. |
| The restore was staged but nothing changed | The staged restore is applied only when the management service restarts. Click **Restart now**, or run `Restart-Service CaddyProxyManager`. |
| Manager log: "A backup restore is staged … but has not been applied" | Stop the service, run `CaddyManager.exe apply-restore`, then start the service. |
| Manager log: "Backup restore FAILED and the previous database was kept" | The previous database is back in use. The staged files are in `C:\ProgramData\CaddyProxyManager\restore-failed-<timestamp>`. Read the error in the log. |
| Messages such as "The stored EAB MAC key could not be decrypted (was the database restored from another server?)" | Secrets do not move between servers. Enter them again (see [Moving to another server](backup-restore.md#moving-to-another-server)). |

See [Backup and restore](backup-restore.md).

## Cluster problems

For nodes that are offline, out of sync, rejected or waiting to join, see [Cluster problems](cluster.md#cluster-problems).

## Related

- [Readiness](readiness.md)
- [Logs](logs.md)
- [Events](events.md)
- [Command line](cli.md)
- [Management UI](management-ui.md)
- [Backup and restore](backup-restore.md)
- [Clustering](cluster.md)
