# Installation, upgrade and uninstall

This page explains how to install Caddy Proxy Manager on a Windows server, what the installer puts on the server, and how to upgrade, repair and remove it. You need to be a local administrator on the server.

## Requirements

- 64-bit Windows 10 version 1809 or later, Windows 11, or Windows Server 2019 or later. Desktop Experience and Server Core are both supported: the services need no desktop (only the optional tray icon uses it), and you use the web UI from a browser on any machine.
- Windows build 17763 or later. The installer reads the real build number and refuses older systems such as Windows Server 2016.
- A local administrator account to install.
- No .NET runtime or other prerequisite. `CaddyManager.exe` is self-contained.
- Caddy itself is not bundled. The manager downloads the official Caddy release the first time it starts, or you upload it yourself on an offline server (see [First start and the Caddy download](#first-start-and-the-caddy-download)).

### Outbound access

The server works without Internet access, but some features need outbound HTTPS (TCP 443):

| Host | Used for |
|---|---|
| `api.github.com` | Checking for new Caddy releases and new Caddy Proxy Manager releases |
| `github.com` | Downloading official Caddy releases and their checksum files |
| `caddyserver.com` | The plugin catalogue and Caddy builds with plugins |
| `acme-v02.api.letsencrypt.org` (or your ACME CA) | Certificates from Let's Encrypt; also the Readiness clock check |
| `api.ipify.org` | Optional: the Readiness DNS check uses it to learn the server's public address |

If the server reaches the Internet through a proxy, set it in **Settings › Updates** (see [Updates](updates.md)).

## Download

Two packages are published for each release:

- `CaddyProxyManager-<version>-x64.msi`: the Windows Installer package. Use it unless you have a reason not to.
- `CaddyProxyManager-<version>-win-x64.zip`: the same program with `install.ps1` and `uninstall.ps1` scripts, for environments where MSI packages are not wanted.

Do not mix the two methods on one server.

## Install with the MSI

1. Copy the MSI to the server and double-click it.
2. On the welcome page, select **Next**.
3. On the ready page, select **Install** and accept the User Account Control prompt.
4. Wait for the progress page to finish. The installer registers and starts the services.
5. On the finish page, leave **Open the web UI in my browser now** ticked if you want to open the console straight away, then select **Finish**.

**Finish** also starts the tray icon for your account (see [Tray icon](cli.md#tray-icon)).

### What the finish page tells you

The finish page adapts to what the installer did:

| Situation | Title | What to do next |
|---|---|---|
| New installation on a server that was never set up | Caddy Proxy Manager is installed | Open the web UI and create the first administrator with the setup token |
| New installation over kept data from an earlier installation | Caddy Proxy Manager is installed | Sign in with your existing accounts |
| Upgrade of a server that is in use | Caddy Proxy Manager is updated | Sign in as before |
| Upgrade of a server where setup was never finished | Caddy Proxy Manager is updated | Finish the first-run setup with the setup token |
| Repair | Caddy Proxy Manager is repaired | Sign in as before |
| Removal | Caddy Proxy Manager was removed | Your data is kept in `C:\ProgramData\CaddyProxyManager` |

When setup is still needed, the page shows where the one-time setup token is: `C:\ProgramData\CaddyProxyManager\setup-token.txt`. You can read that file only from an elevated prompt. See [Getting started](getting-started.md).

### Silent installation

Use `msiexec` with `/qn` for unattended installs, for example on Server Core:

```powershell
msiexec /i CaddyProxyManager-1.2.0-x64.msi /qn
msiexec /i CaddyProxyManager-1.2.0-x64.msi /qn UI_PORT=8081 BIND=10.0.0.15
msiexec /i CaddyProxyManager-1.2.0-x64.msi /qn /l*v install.log
```

A silent install shows no pages, does not open a browser and does not start the tray icon; the tray icon appears at the next sign-in.

| Property | Default | Description |
|---|---|---|
| UI_PORT | `81` on a new install; the current port on an upgrade | TCP port of the web UI. |
| BIND | `0.0.0.0` (all addresses) on a new install; the current address on an upgrade | IP address the web UI listens on. `127.0.0.1` makes the UI reachable from this server only. Any other value must be an address of this server, otherwise the installation fails and rolls back. |

An invalid `UI_PORT` (outside 1 to 65535) or `BIND` value makes the installation roll back. The reason is in the MSI log (`/l*v`).

## Install from the zip

1. Extract the whole zip to a folder on the server.
2. Open PowerShell with **Run as administrator** and change to that folder.
3. Run the install script:

```powershell
.\install.ps1
.\install.ps1 -UiPort 8081 -Bind 10.0.0.15
```

| Parameter | Description |
|---|---|
| `-UiPort` | TCP port of the web UI (1 to 65535). Default: keep the current port, `81` on a new install. |
| `-Bind` | IP address the web UI listens on: `0.0.0.0`, a loopback address, or an address of this server. |
| `-NoStart` | Register the service but do not start it. |

The script removes the "downloaded from the Internet" mark from the extracted files, then runs `CaddyManager.exe install` (see [Command line](cli.md#install)). It prints the web UI address, the setup token file and the log folder when it finishes. Re-running it with a newer zip upgrades in place and keeps your data.

The zip install registers the tray icon to start at every sign-in. It appears at your next sign-in.

## What gets installed

| Item | Location or name |
|---|---|
| Program | `C:\Program Files\Caddy Proxy Manager\CaddyManager.exe` |
| Tray icon | `C:\Program Files\Caddy Proxy Manager\CaddyManagerTray.exe`, started for every user at sign-in |
| Data | `C:\ProgramData\CaddyProxyManager` (see [Data folder](#data-folder)) |
| Caddy | `C:\ProgramData\CaddyProxyManager\caddy\bin\caddy.exe`, downloaded at first start |
| Start menu | **Caddy Proxy Manager** shortcut that opens the web UI (MSI only) |
| Event log | Source **Caddy Proxy Manager** in the Windows **Application** log |

### Windows services

| | Manager | Caddy |
|---|---|---|
| Service name | `CaddyProxyManager` | `Caddy` |
| Display name | Caddy Proxy Manager | Caddy (managed by Caddy Proxy Manager) |
| Account | LocalSystem | LocalSystem |
| Startup type | Automatic (Delayed Start) | Automatic |
| Recovery | Restart after 5 s, 10 s, then 30 s; failure count resets after 1 day | Restart after 5 s, 5 s, then 30 s; failure count resets after 1 day |
| Registered by | The installer | The manager, at its first start |

The two services are independent: Caddy keeps serving your sites while the manager is stopped, restarted or upgraded. The Caddy service runs `caddy.exe run --config C:\ProgramData\CaddyProxyManager\caddy\caddy.json`. Do not edit `caddy.json` by hand; the manager generates it.

### Data folder

Everything the product stores lives in `C:\ProgramData\CaddyProxyManager`. The installer restricts the folder to **SYSTEM** and **Administrators** because it holds the database, keys, private keys and the setup token. When the folder still inherits the default ProgramData permissions, the manager restricts it again at start-up. The Readiness check **Data directory permissions** reports the current state.

| Path | Contents |
|---|---|
| `db\` | Database (`manager.db`), secrets key, traffic statistics database |
| `keys\` | Keys that protect sign-in sessions |
| `setup-token.txt` | One-time setup token (only until the first administrator exists) |
| `caddy\bin\` | `caddy.exe`, plus `caddy.exe.previous` for rollback after an update |
| `caddy\caddy.json` | The configuration Caddy starts with |
| `caddy\data\` | Caddy storage: ACME certificates and accounts, the internal CA |
| `certificates\` | Default store for certificates you upload |
| `logs\caddy\caddy.log` | Caddy log |
| `logs\access\` | Per-host access logs |
| `logs\manager\` | Manager log, one file per day (`manager-yyyyMMdd.log`), kept for 14 days |
| `backups\` | Backups |
| `readiness.json` | The last Readiness report |
| `ui-selfsigned.pfx` | Self-signed certificate for the web UI, when HTTPS is on without your own certificate |

## Ports

| Port | Used by | Default and notes |
|---|---|---|
| TCP 80 | Caddy | HTTP, ACME HTTP-01 challenge, redirects to HTTPS. Configurable in **Settings › Caddy**. |
| TCP 443 | Caddy | HTTPS, ACME TLS-ALPN-01 challenge. Configurable in **Settings › Caddy**. |
| UDP 443 | Caddy | HTTP/3, off by default. |
| TCP 81 | Manager web UI | Configurable in **Settings › Management UI**. |
| TCP 8443 | Manager web UI over HTTPS | Only when HTTPS is enabled for the web UI. |
| `127.0.0.1:2019` | Caddy admin API | Loopback only. See [Security](security.md). |
| Stream ports | Caddy | One per TCP or UDP stream. See [Streams](streams.md). |

### Other HTTP and HTTPS ports, NAT and port forwarding

**Settings › Caddy** has three ports. **HTTP port** and **HTTPS port** are the ports Caddy listens on, on this server. **Public HTTPS port** is the port clients use for HTTPS. Set it only when a router or firewall forwards a different public port to the server, for example public 443 forwarded to 8443 on the server. Leave it empty when clients connect to the HTTPS port directly.

**Public HTTPS port** changes only where HTTP-to-HTTPS redirects send clients. It does not change what Caddy listens on, the firewall rules or the UI port.

Where **Force HTTPS** sends a client:

| Settings | Redirect target |
|---|---|
| **Public HTTPS port** set | `https://<host>/` when it is 443, otherwise `https://<host>:<public port>/` |
| **Public HTTPS port** empty, **HTTPS port** 443 | `https://<host>/` |
| **Public HTTPS port** empty, another **HTTPS port** | `https://<host>:<HTTPS port>/` |

Certificates from a public CA such as Let's Encrypt need public TCP 80 forwarded to the HTTP port (HTTP-01 challenge) or public TCP 443 forwarded to the HTTPS port (TLS-ALPN-01). The CA always connects to 80 and 443 (https://letsencrypt.org/docs/challenge-types/). The DNS challenge needs no inbound port. See [ACME](acme.md).

## Firewall rules

All firewall rules the product creates are in the Windows Defender Firewall group **Caddy Proxy Manager**, so you can find and remove them together.

| When | Rule |
|---|---|
| MSI install, upgrade or repair | **Caddy Proxy Manager - Management UI (TCP-In)** for the UI port, all profiles, any remote address |
| `CaddyManager.exe install` or `install.ps1` | **Caddy Proxy Manager - Management UI (TCP-In)**, plus **Caddy Proxy Manager - Management UI HTTPS (TCP-In)** when HTTPS is enabled for the UI. Skipped when the UI listens on a loopback address. |
| **Fix** on the Readiness page | One rule per missing port: **Caddy Proxy Manager - HTTP (TCP-In)**, **Caddy Proxy Manager - HTTPS (TCP-In)**, **Caddy Proxy Manager - HTTP/3 (UDP-In)**, the UI rules, and **Caddy Proxy Manager - Stream TCP 3389 (TCP-In)** style rules for streams |

> [!IMPORTANT]
> The installer does not open ports 80 and 443 for Caddy. After installing, run the checks on the **Readiness** page and use **Fix**, or deploy the rules through Group Policy on domain-joined servers. See [Readiness](readiness.md) and [Group Policy](group-policy.md).

The UI rule allows any remote address. To limit the UI to admin networks, change the rule's scope in Windows Defender Firewall.

The MSI remembers the UI port it last used for its firewall rule and Start-menu shortcut. If you change the UI port later in **Settings › Management UI**, the manager does not change firewall rules, and a later MSI upgrade or repair without `UI_PORT` recreates the MSI rule and shortcut on the old port. Pass `UI_PORT=<new port>` when you upgrade, and use Readiness **Fix** for the new port.

## First start and the Caddy download

When the manager service starts for the first time, it works in the background and the web UI is available straight away:

1. It creates a one-time setup token (see [Getting started](getting-started.md)).
2. It downloads the latest official Caddy release from GitHub and checks it against the SHA-512 checksum published with the release.
3. It writes a minimal Caddy configuration, registers the `Caddy` Windows service and starts it.
4. It applies the current configuration.

The manager repeats steps 3 and 4 at every start, so it also repairs the Caddy service registration if someone changed it.

### Offline servers

If the download fails, the manager raises the alert **Caddy is not installed and the automatic installation failed**. To install Caddy without Internet access:

1. On a machine with Internet access, download `caddy_<version>_windows_amd64.zip` (or `caddy.exe`) from https://github.com/caddyserver/caddy/releases. Note the SHA-512 value for that file from `caddy_<version>_checksums.txt` on the same page.
2. Sign in to the web UI as an Admin and open **Caddy › Service & Updates**.
3. Open the actions menu of the **Caddy binary** card and select **Upload binary (offline)…**.
4. Choose the file, optionally paste the checksum into **SHA-512 checksum**, and select **Upload and install**.

The manager then registers the Caddy service and applies the configuration, the same as after a download. See [Caddy service](caddy-service.md).

If the server reaches the Internet only through a proxy, set **Proxy URL** in **Settings › Updates** instead, then retry the installation on **Caddy › Service & Updates**. See [Updates](updates.md).

## Upgrade

Run the newer MSI on the server, or re-run `install.ps1` from the newer zip. Hosts, certificates, settings and accounts in `C:\ProgramData\CaddyProxyManager` are kept, and you sign in with your existing accounts. Caddy keeps serving while the manager restarts.

The console tells you when a newer release is available. See [Updates](updates.md). You cannot install an older MSI over a newer one: the installer stops with "A newer version of Caddy Proxy Manager is already installed. Uninstall it first to downgrade."

Caddy itself is updated separately, from **Caddy › Service & Updates**. See [Caddy service](caddy-service.md).

## Repair

If program files, the manager service or the MSI firewall rule were damaged or deleted, run the same MSI version again and choose **Repair** in the maintenance wizard. Your configuration is not changed.

For a zip install, re-run `install.ps1`; it repairs the service registration and firewall rule the same way.

If you are locked out of the web UI after changing its port, address or HTTPS settings, see [Command line](cli.md#recover-access-to-the-web-ui).

## Uninstall

### MSI

Remove **Caddy Proxy Manager** in **Settings › Apps**, or run:

```powershell
msiexec /x CaddyProxyManager-1.2.0-x64.msi /qn
```

This removes:

- the `CaddyProxyManager` and `Caddy` services
- all firewall rules in the **Caddy Proxy Manager** group, including those created by Readiness fixes
- the **Caddy Proxy Manager** event log source (earlier entries stay in the Application log)
- the Start-menu shortcut, the tray icon and its sign-in entry
- `C:\Program Files\Caddy Proxy Manager`

The data folder `C:\ProgramData\CaddyProxyManager` is kept, including the database, certificates, Caddy storage and backups. If you install again later, the server picks up where it left off and you sign in with the same accounts. To remove everything, delete the folder after uninstalling. Take a backup first if you might need it (see [Backup and restore](backup-restore.md)).

### Zip

In an elevated PowerShell, in the extracted folder:

```powershell
.\uninstall.ps1
.\uninstall.ps1 -Purge
```

The script asks for confirmation. It removes both services, the firewall rules of the **Caddy Proxy Manager** group, the event log source, the tray icon and `C:\Program Files\Caddy Proxy Manager`. Data is kept unless you add `-Purge`.

> [!CAUTION]
> `-Purge` deletes `C:\ProgramData\CaddyProxyManager` permanently: the database, certificates, Caddy storage including ACME accounts and the internal CA, logs and local backups. It cannot be undone.

`uninstall.ps1` refuses to run on a server where the product was installed with the MSI; use **Settings › Apps** there.

## Related

- [Getting started](getting-started.md)
- [Readiness](readiness.md)
- [Command line](cli.md)
- [Group Policy](group-policy.md)
- [Management UI settings](management-ui.md)
- [Updates](updates.md)
