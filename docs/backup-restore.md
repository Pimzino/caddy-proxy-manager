# Backup and restore

A backup is a zip containing the database (hosts, certificates metadata, users, settings, audit/events), the
certificate store, Caddy's storage (ACME certificates and the **internal CA including its private key**) and the
current `caddy.json`. Treat backups as secrets.

## Scheduled backups

**Settings › Backups**: enable, hour of day, directory (local or UNC), number to keep, and an optional password
(WinZip AES-256 encrypted zip; open with 7-Zip, WinZip, WinRAR or libarchive's `bsdtar --passphrase` — the classic
Windows Explorer ZIP folder cannot open AES zips).

UNC targets: the service runs as LocalSystem, which reaches shares as the **computer account** (`DOMAIN\SERVER$`,
[LocalSystem account](https://learn.microsoft.com/en-us/windows/win32/services/localsystem-account)). Grant that
account *Modify* on the share and the folder; a failed backup names the exact account. A server that is not
domain-joined has no network identity another server can grant access to. Files on a share keep the share's
permissions (the manager only restricts local backup folders), so **set a backup password for UNC targets**: backups
contain the database with the DPAPI-protected secrets (see [security.md](security.md)). Failures raise an alert (under the *Configuration failure* alert rule). *Run backup now* creates one immediately.

## On demand

**Settings › Backups › Download backup**.

## Restore

**Settings › Backups › Restore** — upload a backup (and its password if encrypted). The backup is validated and
staged; click **Restart now** in the panel that appears (or `Restart-Service CaddyProxyManager`) to apply it. The
replaced files are kept under `C:\ProgramData\CaddyProxyManager\backups\pre-restore-<timestamp>` for rollback.

If the service cannot start, stop it and run `CaddyManager.exe apply-restore` from an elevated prompt.

## Moving to another server

Secrets (SMTP password, ACME EAB key, DNS provider credentials, LDAP bind password, PFX passwords) are encrypted with
DPAPI for the original machine and cannot be decrypted elsewhere: re-enter them after restoring on a new server.
Everything else — including certificates and the internal CA — moves with the backup.
