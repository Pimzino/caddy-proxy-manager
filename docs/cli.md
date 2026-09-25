# Command line

`CaddyManager.exe` without arguments runs the manager (as the Windows service, or in the console for testing).
All other verbs are run from an **elevated** prompt, typically as
`& 'C:\Program Files\Caddy Proxy Manager\CaddyManager.exe' <verb>`.

| Verb | Purpose |
|---|---|
| `install [--ui-port N] [--bind ADDR] [--no-start]` | Install or repair: copy the exe to `%ProgramFiles%\Caddy Proxy Manager`, lock down the data folder, register the `CaddyProxyManager` service, open the UI port, start. Re-run to upgrade. |
| `uninstall [--purge]` | Remove both services and the *Caddy Proxy Manager* firewall rules; `--purge` also deletes `C:\ProgramData\CaddyProxyManager`. |
| `configure [--ui-port N] [--bind ADDR] [--ui-https on\|off] [--reset-ui]` | Change the UI listener without the UI (e.g. after locking yourself out). `--reset-ui` = port 81, all addresses, HTTP. Restart the service afterwards. |
| `service-status` | State of both services. |
| `reset-password --email <e> [--password <p>] [--enable]` | Reset (and optionally re-enable) a local user. Stop the service first. |
| `list-users` | List UI accounts. |
| `apply-restore` | Apply a restore staged in the UI (service stopped). |
| `version`, `help` | |

Development environment variables: `CM_DATA_DIR`, `CM_UI_PORT`, `CM_WEB_DIR`, `CM_CADDY_HOST=process`,
`CM_CADDY_AUTOSTART=0`, `CM_GITHUB_TOKEN`, `CM_DEV_AUTOLOGIN=1` (Debug builds only).

## Cluster verbs

Manage this server's membership in a cluster (see [cluster.md](cluster.md)). Like `reset-password` they open the
database directly: **stop the service first** (`net stop CaddyProxyManager`), otherwise they answer
*Stop the CaddyProxyManager service first*. All accept `--data-dir <dir>` (default: `C:\ProgramData\CaddyProxyManager`,
or `CM_DATA_DIR`).

| Verb | Purpose |
|---|---|
| `cluster join <token>` | Make this server a node of the primary that issued the token (Servers → Add server on the primary). Only a standalone server without nodes can join. Start the service afterwards: the primary pushes its configuration at its next heartbeat. Exit code 2 for an invalid token, 1 when this server is already a node or is a primary. |
| `cluster leave` | Leave the cluster (for example when the primary is gone). The last applied configuration stays and becomes editable. Remove the server on the primary too. |
| `cluster status` | Role, primary, node id, last contact from the primary, applied revision (nodes) or the node list (primary), and warnings such as *Local storage: each server obtains its own certificates*. |

```powershell
net stop CaddyProxyManager
& 'C:\Program Files\Caddy Proxy Manager\CaddyManager.exe' cluster join cpmj1.eyJ2IjoxLCJwcmltYXJ5Ijoi...
net start CaddyProxyManager
& 'C:\Program Files\Caddy Proxy Manager\CaddyManager.exe' cluster status
```

Joins and leaves from the command line are recorded in the audit log as `cli:<user>`.
