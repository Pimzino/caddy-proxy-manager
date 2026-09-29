# Command line and tray icon

`CaddyManager.exe` has commands for installing, recovering and controlling Caddy Proxy Manager on the server without the web UI. This page lists every command, and describes the tray icon in the Windows notification area. You need to be a local administrator on the server.

## Run a command

Open PowerShell with **Run as administrator** and call the program by its full path:

```powershell
& 'C:\Program Files\Caddy Proxy Manager\CaddyManager.exe' <command> [options]
```

Run it elevated. `install`, `uninstall`, `caddy` and `manager` refuse to run otherwise, and the commands that open the database need access to `C:\ProgramData\CaddyProxyManager`, which only SYSTEM and Administrators have.

Without a command, `CaddyManager.exe` runs the manager itself. Windows does that when it starts the `CaddyProxyManager` service; you do not normally run it that way.

> [!IMPORTANT]
> `configure`, `reset-password`, `list-users`, `apply-restore` and the `cluster` commands open the database directly, which is only possible while the manager service is stopped. Stop it first with `Stop-Service CaddyProxyManager` and start it again afterwards. Caddy keeps serving sites while the manager is stopped.

## Commands

| Command | Purpose | Service |
|---|---|---|
| `install` | Install, repair or upgrade a zip installation | Stopped and started by the command |
| `configure` | Change the web UI listener; recover access to the UI | Must be stopped |
| `uninstall` | Remove the services, firewall rules and, optionally, all data | Removed by the command |
| `service-status` | Show the state of both services | Any |
| `caddy` | Start, stop or restart Caddy | Any |
| `manager` | Start, stop or restart the manager service | Any |
| `reset-password` | Reset a local account's password | Must be stopped |
| `list-users` | List the accounts | Must be stopped |
| `apply-restore` | Apply a backup restore staged in the web UI | Must be stopped |
| `cluster join`, `cluster leave`, `cluster status` | Manage this server's cluster membership | Must be stopped |
| `version` | Print the version | Any |
| `help` | Show the built-in help | Any |

### install

```text
install [--ui-port N] [--bind ADDR] [--no-start]
```

Installs or repairs a zip installation. `install.ps1` runs this command for you (see [Installation](installation.md#install-from-the-zip)). It:

1. Stops the running `CaddyProxyManager` service, if any.
2. Copies `CaddyManager.exe` to `C:\Program Files\Caddy Proxy Manager`, and the tray icon when it is next to it, and registers the tray icon to start at every sign-in.
3. Creates `C:\ProgramData\CaddyProxyManager` and restricts it to SYSTEM and Administrators.
4. Saves the UI port and address when given.
5. Registers or repairs the service: LocalSystem, Automatic (Delayed Start), restart on failure.
6. Creates the firewall rule for the UI port (and the UI HTTPS port when enabled), unless the UI listens on a loopback address.
7. Starts the service, unless you add `--no-start`.

It then prints the web UI address, the path of the setup token file when setup is still pending, and the log folder.

| Option | Description |
|---|---|
| `--ui-port N` | TCP port of the web UI, 1 to 65535. |
| `--bind ADDR` | Address the web UI listens on: `0.0.0.0` (all), a loopback address such as `127.0.0.1`, or an address of this server. Other addresses are refused. |
| `--no-start` | Do not start the service. |

Re-run `install` from a newer version to upgrade. Data is kept. For MSI installations, use the MSI instead.

### configure

```text
configure [--ui-port N] [--bind ADDR] [--ui-https on|off] [--reset-ui]
```

Changes the web UI listener stored in the database. Use it when a port, address or HTTPS change locked you out of the web UI.

| Option | Description |
|---|---|
| `--ui-port N` | New HTTP port of the web UI. |
| `--bind ADDR` | New listen address, with the same rules as for `install`. |
| `--ui-https on\|off` | Turn HTTPS for the web UI on or off. `off` also turns off the redirect from HTTP to HTTPS. |
| `--reset-ui` | Back to the defaults: port `81` on all addresses (`0.0.0.0`), HTTPS off. |

The command refuses an HTTP port that equals the HTTPS port. It prints the old and new listener settings. The change takes effect when the service starts.

#### Recover access to the web UI

1. Stop the manager service:

```powershell
Stop-Service CaddyProxyManager
```

2. Reset the listener:

```powershell
& 'C:\Program Files\Caddy Proxy Manager\CaddyManager.exe' configure --reset-ui
```

3. Start the service:

```powershell
Start-Service CaddyProxyManager
```

4. Open `http://localhost:81/` on the server.

If the service is still running, `configure` stops with "The database … is in use" and changes nothing. Make sure the port is allowed in the firewall (Readiness **Fix**, or re-run `install` on a zip installation).

The manager also never refuses to start because of bad listener settings: if it cannot use the configured address or port, it falls back to port 81 on all addresses and records a warning in the manager log and in **Events**.

### uninstall

```text
uninstall [--purge]
```

Stops and deletes the `CaddyProxyManager` and `Caddy` services, removes all firewall rules in the group **Caddy Proxy Manager**, removes the **Caddy Proxy Manager** event log source (earlier entries stay in the Application log) and removes the tray icon from sign-in. The program folder is not deleted; `uninstall.ps1` does that.

`--purge` also deletes `C:\ProgramData\CaddyProxyManager` with the database, certificates, Caddy storage, logs and local backups, so a later installation starts with first-run setup again. This cannot be undone.

For MSI installations, uninstall from **Settings › Apps** instead. See [Installation](installation.md#uninstall).

### service-status

Shows, for `CaddyProxyManager` and `Caddy`: display name, state and process ID, start type, account, command line and recovery actions, then the data folder and the installed Caddy version. The exit code is `0` when both services run, `3` otherwise.

### caddy

```text
caddy start|stop|restart [--message-file PATH]
```

Starts, stops or restarts Caddy. While the manager service runs, the command goes through the manager, the same as the buttons in the web UI: it is recorded in the audit log under your Windows account name, and a stop is respected by the monitor, so Caddy is not restarted automatically. When the manager is not running, the command controls the `Caddy` service directly and says so.

### manager

```text
manager start|stop|restart [--message-file PATH]
```

Starts, stops or restarts the `CaddyProxyManager` service. While it is stopped, the web UI is offline and Caddy keeps serving.

`--message-file` writes the one-line result to a file. The tray icon uses it to show the result as a notification.

### reset-password

```text
reset-password --email <address> [--password <password>] [--enable]
```

Resets the password of a local account and signs out its sessions. Without `--password`, a random 20-character password is generated and printed. A password you choose must have 12 to 256 characters and must not be the same as the e-mail address. `--enable` also re-enables a disabled account.

Directory (LDAP) accounts have no local password; reset them in Active Directory. The reset is recorded in the audit log as `cli:<your Windows user name>`.

```powershell
Stop-Service CaddyProxyManager
& 'C:\Program Files\Caddy Proxy Manager\CaddyManager.exe' reset-password --email admin@contoso.com
Start-Service CaddyProxyManager
```

### list-users

Lists every account with e-mail, name, role, status (enabled or disabled), source (local or ldap) and last sign-in in UTC.

### apply-restore

Applies a backup restore that was uploaded in **Settings › Backups** but not applied yet. Normally the manager applies a staged restore when it restarts; use this command if it did not. See [Backup and restore](backup-restore.md).

### cluster

```text
cluster join <token>
cluster leave
cluster status
```

`join` makes this server a node of the primary that issued the token, `leave` makes it standalone again and keeps its last configuration, and `status` shows its role and state. Joins and leaves are recorded in the audit log as `cli:<your Windows user name>`. See [Cluster](cluster.md).

### Common option: data folder

`reset-password`, `list-users`, `apply-restore` and the `cluster` commands accept `--data-dir <folder>` to work on a data folder other than `C:\ProgramData\CaddyProxyManager`, for example a copy.

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Success |
| `1` | The command failed: not elevated, database in use, account not found, or an error; the reason is printed |
| `2` | Wrong or missing options (the correct usage is printed), a password that breaks the password rules (`reset-password`), or HTTP and HTTPS ports that clash (`configure`) |
| `3` | `service-status` only: a service is not installed or not running |

## Tray icon

`CaddyManagerTray.exe` puts the Caddy Proxy Manager icon in the Windows notification area. It starts for every user who signs in to the server, and the MSI also starts it when you select **Finish**. It runs under the signed-in user's account without administrator rights.

The tooltip shows the state of Caddy and of the management UI (**running**, **stopped**, **starting**, **stopping**, **not installed** or **unknown**), refreshed every 5 seconds.

### Left-click

Opens the web UI in the default browser. When the management service is not running, a notification says so instead. The address is the one the manager recorded when it last started: `https://<server name>:<HTTPS port>/` when HTTPS with redirect is on for the web UI, otherwise `http://localhost:<port>/` (or the address the UI is bound to).

### Right-click menu

| Item | Available when | What it does |
|---|---|---|
| **Open management UI** | Always | Same as a left-click |
| **Caddy:** state | Always (information) | Shows Caddy's state |
| **Start Caddy** | Caddy is stopped | Starts Caddy |
| **Stop Caddy** | Caddy is running or starting | Stops Caddy |
| **Restart Caddy** | Caddy is running | Restarts Caddy |
| **Management UI:** state | Always (information) | Shows the manager service's state |
| **Start management UI** | The manager is stopped | Starts the `CaddyProxyManager` service |
| **Stop management UI** | The manager is running or starting | Stops it; Caddy keeps serving |
| **Restart management UI** | The manager is running | Restarts it |
| **Exit (hide this icon)** | Always | Closes the tray icon until the next sign-in |

The start, stop and restart items run `CaddyManager.exe caddy …` or `CaddyManager.exe manager …` and ask for administrator rights through User Account Control. While an action runs, the menu shows its progress and the other actions are unavailable. The result appears as a notification. If you cancel the User Account Control prompt, nothing happens.

Caddy actions from the tray go through the manager when it runs, so they are recorded in the audit log under your Windows account name. See [Audit log](audit-log.md).

## Related

- [Installation](installation.md)
- [Getting started](getting-started.md)
- [Management UI](management-ui.md)
- [Backup and restore](backup-restore.md)
- [Cluster](cluster.md)
- [Troubleshooting](troubleshooting.md)
