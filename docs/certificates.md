# Certificates

Every HTTPS site needs a certificate. Caddy Proxy Manager gets them automatically from a public or private ACME CA,
issues them from Caddy's own internal CA, or uses certificates you bring yourself. The **Certificates** page lists all
of them. Every role can view it; adding and changing certificates needs the Operator role, and some sources need Admin.

## Certificate types

Each host has a TLS mode, which you choose on the host's **TLS** tab (see [Host options](host-options.md#tls-tab)). The mode
decides where the host's certificate comes from.

| TLS mode | Use it for | Where the certificate comes from |
|---|---|---|
| Automatic (ACME) | Public names, or internal names served by a private ACME CA | Caddy obtains and renews it from the CA configured in **Settings › Caddy**. See [ACME certificates](acme.md). |
| Internal CA | Internal names such as `app.corp.local` | Caddy's local CA issues and renews it. Clients must trust the internal root (see [Internal CA root](#internal-ca-root)). |
| Custom certificate | Certificates from your own PKI or a commercial CA | A certificate you add on the **Certificates** page and select on the host. |
| None (HTTP only) | Trusted networks, or TLS terminated before Caddy | No certificate. The site is served over plain HTTP. |

On the **Certificates** page, ACME and internal certificates appear automatically once Caddy has obtained them. Custom
certificates appear when you add them.

## The Certificates page

Open **Security › Certificates**.

![The Certificates page listing ACME, internal and custom certificates with their expiry](images/certificates-list.webp)

- **Internal root CA** downloads the root certificate of Caddy's internal CA. Every role can use it.
- **Add certificate** opens the import dialog. It is shown to Operators and Admins.
- The search box matches the name, subjects, issuer, file path and notes.
- The filter buttons **All**, **Custom**, **ACME** and **Internal** limit the list to one type. The internal root CA
  row is shown under **All** only.

Rows that need attention come first: certificates with a problem, then expired ones, then those closest to expiry.
Internal certificates and the internal root are listed last.

### Columns

| Column | What it shows |
|---|---|
| Name | The certificate's name, its issuer (for example `R11 (Let's Encrypt)`), your notes, and any problem in red. |
| Type | **Custom**, **ACME**, **Internal** or **Internal root CA**. Certificates from any ACME CA, including a private one, show as **ACME**. |
| Source | For custom certificates: **Uploaded**, **File**, **PFX file** or **Windows store**. Sources the manager re-reads by itself also show **Auto-sync**, or **Sync failed** when the last re-read failed. ACME certificates show **ACME (Caddy)**; internal ones show **Caddy local CA**. |
| Subjects | The DNS names and IP addresses the certificate covers. The first two are shown; hover **+N** for the rest. |
| Expires | The time left, coloured by urgency, and the expiry date. |
| Used by | The hosts that use the certificate. Custom certificates that no host uses show **Not used**. |

### Expiry colours

| Type | Green | Amber | Red |
|---|---|---|---|
| Custom | More than 30 days left | 8 to 30 days left | 7 days or less, or expired |
| ACME | **Auto-renews** with more than 7 days left | 7 days or less (**renewal overdue**) | Expired |
| Internal | Always grey (**Auto-renews**) | — | Expired |
| Internal root CA | Always grey | — | Expired |

Caddy renews ACME certificates well before they expire, so an ACME certificate with a week or less left means renewal
is failing. Check **Server › Logs** for ACME errors (see [Logs](logs.md)).

> [!NOTE]
> When Caddy keeps its data in Redis or a custom storage module (see [Cluster](cluster.md)), ACME and internal
> certificates are stored outside the server and the page lists only your own certificates.

## Add your own certificate

You need the Operator role. Three sources that read files on the server or the Windows certificate store are
available to Admins only, because the services run as LocalSystem and can read almost any file.

| Tab | Role | Use it for | How renewals reach Caddy |
|---|---|---|---|
| Upload PEM | Operator | Certificate and key files from any CA | Upload the renewed certificate with **Replace…** |
| Upload PFX | Operator | A `.pfx` / `.p12` export from Windows or a CA portal | Upload the renewed certificate with **Replace…** |
| Paste PEM | Operator | Certificate and key text copied from elsewhere | Upload the renewed certificate with **Replace…** |
| File path | Admin | PEM files that another tool renews in place (a script, your PKI tooling) | Detected automatically |
| PFX on disk/share | Admin | A PFX that another tool renews in place (for example win-acme) | Re-converted automatically |
| Windows store | Admin | Certificates enrolled into the Windows certificate store (AD CS autoenrolment, `certreq`, IIS) | Re-exported automatically when you follow renewals by subject |

To add a certificate:

1. Open **Security › Certificates** and select **Add certificate**.
2. Choose the tab for your source.
3. Optionally enter a **Name**. It is shown in the host editor. Leave it empty to use the certificate's first domain
   name.
4. Fill in the source fields described below.
5. Select **Add certificate**.

If the server refuses the certificate, the dialog shows **The certificate was not accepted** with the reason. A
certificate that has expired, or is not valid yet, is still added, with a warning.

### Upload PEM

| Field | Default | Description |
|---|---|---|
| Certificate file | — | PEM file (`.pem`, `.crt`, `.cer`). Put the intermediate certificates after the server certificate. |
| Private key file | — | Unencrypted PEM key (`.key`, `.pem`). |

### Upload PFX

| Field | Default | Description |
|---|---|---|
| PFX / PKCS#12 file | — | A `.pfx` or `.p12` file. When you export from Windows, choose *Include all certificates in the certification path*. |
| PFX password | Empty | Used only to open the file. It is not stored. |

The private key in the PFX must be exportable. If it is not, re-export the PFX with *Mark this key as exportable*.

### Paste PEM

| Field | Default | Description |
|---|---|---|
| Certificate (PEM) | — | Text starting with `-----BEGIN CERTIFICATE-----`, with any intermediates after the server certificate. |
| Private key (PEM) | — | Text starting with `-----BEGIN PRIVATE KEY-----` (or `RSA` / `EC PRIVATE KEY`). Encrypted keys are not accepted. |

### File path (Admin)

The files are referenced, not copied. Renew them in place and the manager picks up the change.

| Field | Default | Description |
|---|---|---|
| Certificate path | — | Full path of the PEM certificate chain, for example `\\fileserver\pki\web01\fullchain.pem`. |
| Private key path | — | Full path of the unencrypted PEM key, for example `\\fileserver\pki\web01\privkey.pem`. |

- Paths must be absolute: a local path or a UNC share.
- Allowed extensions are `.pem`, `.crt`, `.cer` and `.key`.
- On a share, grant read access to the server's computer account (`DOMAIN\SERVER$`).
- Files inside the manager's data folder (`C:\ProgramData\CaddyProxyManager`) are refused, except in the certificate
  store.

### PFX on disk/share (Admin)

For tools that renew a PFX in place. The manager converts the PFX to PEM in the certificate store and converts it again
whenever the file changes.

| Field | Default | Description |
|---|---|---|
| PFX path | — | Local path or UNC share of a `.pfx` or `.p12` file, for example `C:\ProgramData\win-acme\certificates\app.example.com.pfx`. |
| PFX password | Empty | Leave empty when the file has no password. It is stored encrypted so renewals can be read unattended. |

### Windows store (Admin)

![The Add certificate dialog on the Windows store tab, listing certificates from the Personal store](images/add-certificate.webp)

| Field | Default | Description |
|---|---|---|
| Store location | Local computer (LocalMachine) | Or **Service account (CurrentUser)**. |
| Store | Personal (My) | Or **Web Hosting (WebHosting)**. |
| Which certificate to use | This certificate (thumbprint) | Pin one certificate, or **Follow renewals by subject**. |
| Subject or SAN to follow | — | Shown when you follow renewals. The host name the certificate is issued for, for example `app.corp.example.com`. |

The dialog lists the certificates in the selected store. Usable certificates (with a private key, not expired) come
first. Badges show **Expired**, **No private key** (cannot be selected), **Key not exportable** and the AD CS
**Template**. Certificates enrolled for the computer are usually in **Local computer › Personal**.

- **This certificate (thumbprint)** pins the certificate you select. Renewals are not picked up: when the certificate
  is renewed, add the new one or switch to following renewals.
- **Follow renewals by subject** uses the newest currently valid certificate that has a private key, allows Server
  Authentication, and whose subject CN or a DNS name matches. A wildcard name that covers the host name also matches.
  This follows AD CS autoenrolment and `certreq` renewals.

> [!IMPORTANT]
> Caddy reads certificates as PEM files, so the manager has to export the private key. Windows refuses this for
> non-exportable keys. Issue the certificate from a template with *Allow private key to be exported* enabled, or import
> a PFX instead.

### Supported formats and limits

- RSA and ECDSA keys in PKCS#1, SEC1 or PKCS#8 format.
- The private key must belong to the certificate. The import fails if the public keys differ.
- Uploaded files can be up to 2 MB each.
- The manager stores the chain server certificate first, followed by its issuers.

## Use a certificate on a host

On the host's **TLS** tab, choose **Custom certificate** and select the certificate (see
[Host options](host-options.md#tls-tab)). The host editor warns when the certificate does not cover all of the host's
domains, and when it has expired.

If a custom certificate's files go missing or cannot be read, the hosts that use it are left out of the configuration
until the files are back, and the configuration result says so.

## Renewal and synchronisation

The manager re-reads certificates from external sources by itself:

| Source | How changes are detected |
|---|---|
| File | A file watcher on the folder, plus a check every 5 minutes (covers shares and missed events). Files changed while the service was stopped are picked up by the first check after it starts. |
| PFX file | The same watcher and 5-minute check. The PFX is converted to PEM again when it changes. |
| Windows store | Every 15 minutes the store is read again. With **Follow renewals by subject**, a newer certificate is exported. |

When a certificate in use by an enabled host changes, the manager applies the configuration again so Caddy loads it.

To re-read a certificate straight away, open its row menu and select **Sync now**. This is available for File, PFX
file and Windows store certificates (Operator).

If a re-read fails, the row shows **Sync failed** and the error, and the manager raises a warning event. When a later
re-read succeeds, a recovery event follows. See [Events](events.md).

## Edit, replace or delete a certificate

The row menu of a custom certificate (Operator) offers:

- **Edit name & notes**: change the **Name** and add **Notes**, for example where the certificate came from and who
  renews it.
- **Sync now**: re-read the source now (File, PFX file and Windows store only).
- **Replace…**: upload a renewed certificate and key, as PEM or PFX (Uploaded certificates only). Hosts that use the
  certificate pick it up immediately.
- **Delete**: remove the certificate. It shows as **Delete (in use)** and is disabled while any host uses it, including
  disabled hosts. Change those hosts' TLS settings first.

What **Delete** removes depends on the source:

| Source | Deleted | Kept |
|---|---|---|
| Uploaded | The certificate and its private key in the certificate store. This cannot be undone. | — |
| File | The entry in the manager | The files on disk |
| PFX file | The entry and its converted PEM copy | The PFX file |
| Windows store | The entry and its exported PEM copy | The certificate in the Windows store |

ACME and internal certificates have no row menu. Caddy manages them.

## Where certificates are stored

The manager writes uploaded certificates, and the PEM copies of PFX and Windows-store certificates, to the
**certificate store**: one folder per certificate with `fullchain.pem` and `privkey.pem`.

- The default is `C:\ProgramData\CaddyProxyManager\certificates`. On a local folder, the manager restricts access to
  SYSTEM and Administrators.
- To use another folder or a share, set **Certificate store path** in **Settings › Caddy** (see
  [Caddy settings](caddy-settings.md)). The manager writes to it, so on a share grant the computer account
  (`DOMAIN\SERVER$`) modify rights on the share and folder. Share permissions are not changed by the manager.

On a server managed by a cluster primary, certificates are replicated from the primary and the page is read-only. See
[Cluster](cluster.md).

## Internal CA root

Hosts with **Internal CA** TLS get certificates from Caddy's local CA. Browsers trust them only when the client trusts
the CA's root certificate.

1. Open **Security › Certificates** and select **Internal root CA**. The file `caddy-local-root.crt` is downloaded.
2. Deploy it to the clients' *Trusted Root Certification Authorities* store, for example with Group Policy. See
   [Group Policy](group-policy.md).

Caddy creates the root the first time a host uses Internal TLS. Before that, the download fails with a message saying
the internal CA has not been created yet. The manager never installs the root into the server's own trust stores.

The root's row in the list shows every host that uses Internal TLS under **Used by**.

## Expiry and missing-certificate alerts

The manager watches certificates and raises events. Notifications for them follow the **Certificate expiring** alert
rule on the **Notifications** page (Admin; see [Notifications](notifications.md)). Events are recorded either way.

- **Expiring certificates**: every 6 hours, custom and ACME certificates are checked. A warning is raised when a
  certificate expires within the number of days set in **Warn this many days before expiry** (default 14), and an
  error once it has expired. Internal certificates are not included, because Caddy renews them continuously.
- **Unreadable certificates**: a custom certificate whose files cannot be read raises a warning.
- **Certificates not issued**: every 5 minutes, each domain of an enabled host with Automatic (ACME) or Internal CA TLS
  must have a current certificate. If a domain still has none 10 minutes after the host was last changed, a warning
  **No certificate has been issued for** the domain is raised, with the last certificate errors from Caddy's log.
  Wildcard names are not checked. The check runs only while Caddy is running and the configuration mode is
  **Managed**.
- **Sync failures** of File, PFX file and Windows store certificates raise warnings (see
  [Renewal and synchronisation](#renewal-and-synchronisation)).

Each alert is followed by a recovery event when the problem is resolved.

## Related

- [ACME certificates](acme.md)
- [Host options](host-options.md)
- [Group Policy](group-policy.md)
- [Notifications](notifications.md)
- [Cluster](cluster.md)
