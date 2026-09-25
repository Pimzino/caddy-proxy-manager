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
