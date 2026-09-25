# Clustering (several Caddy servers, one configuration)

Caddy Proxy Manager can manage several servers as one: you configure hosts, certificates and settings on one server
(the **primary**) and it pushes that configuration to the others (**nodes**). Put the servers behind a load balancer,
DNS round robin or an anycast/VRRP address and they serve the same sites.

## Concepts

| Role | Meaning |
|---|---|
| **Standalone** | The default: a single server. |
| **Primary** | A server that has at least one node (Servers → Add server). It becomes standalone again when its last node is removed. |
| **Node** | A server that joined a primary with a join token. Its replicated configuration is read-only; it is changed on the primary. |

What the primary replicates to every node:

- proxy hosts, redirects, static sites, custom responses, streams, access lists;
- certificates (the PEM chain and key are sent; nodes store them as *Uploaded* in their own certificate store under the
  same id — also certificates that are file-path, PFX or Windows-store based on the primary);
- Caddy settings **except** the node-local ones: HTTP port, HTTPS port, public HTTPS port, bind addresses, admin API
  address and certificate store path (`CaddySettings.NodeLocalProperties`). Secrets (EAB key, DNS provider credentials,
  Redis password/encryption key, custom storage JSON) travel inside the encrypted channel and are re-encrypted with the
  node's own DPAPI key;
- the desired Caddy plugins. A node whose Caddy lacks a desired plugin rebuilds Caddy with the plugins first (a normal
  binary install job on the node) and applies the configuration when that has finished.

What stays local on every server: users and sign-in settings, the management UI settings, notifications, backups,
readiness, logs, events, the Caddy binary/service actions and the node-local Caddy settings above.

The configuration is sent as one **bundle**; its revision is the SHA-256 of its canonical JSON (Servers page:
*desired* vs *applied* revision). A node applies a bundle completely or not at all: when its Caddy rejects the
configuration, the node restores its previous data and keeps running the previous configuration (Caddy never loads a
config it cannot provision). **Nodes keep serving their last applied configuration while the primary is unreachable.**

Caddy's admin API stays on loopback on every server: the primary talks to the nodes' **manager**
(`POST /api/cluster/rpc` on the management UI port), which applies the configuration to its local Caddy.

### Clustering at the Caddy level: shared storage

The configuration push makes the servers serve the same sites. Certificates are a second matter: Caddy instances
"configured to use the same storage will automatically share those resources and coordinate certificate management as
a cluster" ([Caddy docs: storage](https://caddyserver.com/docs/automatic-https#storage)). With shared storage:

- one server obtains/renews each ACME certificate (distributed locks) and all others use it;
- HTTP-01 and TLS-ALPN-01 challenge data is in the storage, so **any** server can answer the CA — the load balancer may
  send the validation request anywhere;
- the ACME account and Caddy's internal CA (Tls = *Internal*) are the same everywhere, so one root certificate covers
  all servers (deploy it once via GPO).

Without shared storage (backend *Local*, the default) every server obtains its own certificates: this works for DNS-01
but HTTP-01/TLS-ALPN-01 validation fails whenever the CA reaches a server other than the one that asked, and every
server has its own internal CA. The Servers page and `cluster status` warn about this.

Configure it on the primary under **Settings → Cluster → Shared storage** (it is a replicated setting):

| Backend | Use when | Notes |
|---|---|---|
| **File system** | A Windows file share (UNC path) or a local path mounted everywhere | Built into Caddy. See the share guidance below. |
| **Redis** | You already run Redis (ideally replicated) | Plugin `github.com/pberkel/caddy-storage-redis` (added to the desired plugins; nodes rebuild automatically). Optional TLS and value encryption. |
| **Custom** | Consul, S3, Postgres, ... | Paste the storage JSON (`{"module":"...", ...}`) and add the module's plugin. |

Switching from Local to File system copies the existing `certificates/`, `acme/`, `pki/` and `ocsp/` folders to the new
root when they do not exist there yet, so issued certificates and the internal CA root are kept.

#### SMB / UNC shares (Windows)

Both services run as LocalSystem, which reaches network shares as the **computer account** `DOMAIN\HOSTNAME$`:

1. Create an AD group, e.g. `Caddy Servers`, and add the computer accounts of all cluster servers.
2. Create the share (e.g. `\\files01\caddy$`) and give the group **Change** on the share and **Modify** on the
   folder (NTFS). Remove *Everyone*/*Users* access: the folder holds private keys and the ACME account key.
3. On each server, make the new group membership effective: reboot, or run `klist -li 0x3e7 purge` from an elevated
   prompt (purges the LocalSystem Kerberos tickets), then restart both services.
4. Use the **UNC path** (`\\files01\caddy$\storage`). Mapped drive letters belong to a user's logon session and are
   not visible to services.
5. Test from each server: `PsExec -s cmd /c dir \\files01\caddy$` (as SYSTEM). The manager also writes and removes a
   test file when you save the setting and refuses paths it cannot write.

Keep the file server highly available (DFS-N with a replicated target, a clustered file server): Caddy needs the storage
to obtain/renew certificates and when it starts. Certificates already loaded keep being served while the share is
briefly unavailable. Caddy's `file_system` storage coordinates with lock files that are refreshed every few seconds and
considered stale after about ten; clocks of all servers must be in sync (domain time service).

Caddy does not document SMB explicitly; the `file_system` module only needs ordinary file operations (atomic create,
rename), which SMB provides. Avoid DFS-R multi-master replication of the storage folder itself (two writable copies break
the locks) — point every server at the same single target.

#### NFS caveats

Caddy maintainers attribute lock problems reported on NFS to NFS itself. If you must use NFS (e.g. a Linux file server
exported to Windows NFS clients): use NFSv4, disable attribute caching for the mount so lock files and their timestamps
are seen promptly, keep clocks synchronised, and prefer SMB or Redis where possible.

## Setup walkthrough

Prerequisites

- The same version of Caddy Proxy Manager on every server (install/upgrade the nodes first).
- The primary can reach each node's management UI port (default 81, or the UI HTTPS port). Restrict that port on the
  nodes to the primary and your admin network (firewall rule scope / GPO).
- Clocks within 5 minutes of each other (requests outside ±300 s are rejected; domain time sync is enough).
- A node exposes its management UI directly. Do not publish a *node's* UI through a proxy host on that node: proxy
  hosts are replicated from the primary and would replace it.

Steps

1. **Primary:** Servers → **Add server**: a name and the node's management URL, e.g. `https://proxy2.corp.local:8443` or
   `http://proxy2.corp.local:81`. The dialog shows the **join token once** (copy it) and, for https URLs, the pinned
   certificate fingerprint. The primary becomes *Primary*.
2. **Node:** either sign in on the node and paste the token under **Settings → Cluster → Join cluster**, or on the node
   (elevated prompt):

   ```powershell
   net stop CaddyProxyManager
   & 'C:\Program Files\Caddy Proxy Manager\CaddyManager.exe' cluster join <token>
   net start CaddyProxyManager
   ```

   Only a standalone server without nodes of its own can join. Joining replaces the node's hosts, certificates, access
   lists, streams and replicated settings at the first sync (its node-local settings, users etc. stay).
3. Within one heartbeat (15 s) the primary pushes its configuration. The Servers page shows the node *Online* with
   matching desired/applied revisions; the node shows the banner *Managed by &lt;primary&gt;*.
4. Configure shared storage (above) and point your load balancer/DNS at all servers.

After that, every change on the primary is pushed about 2 s after it is applied (and at the latest by the next
heartbeat, which also resyncs a node that reports another revision). **Sync now** pushes immediately.

Removing: Servers → **Remove** tells the node to leave (it becomes standalone and keeps its last configuration, now
editable). If the node is unreachable, run `CaddyManager.exe cluster leave` on it with the service stopped. A node can
also leave by itself (Settings → Cluster → Leave), after which the primary shows it as not joined.

## Security model

- **Join token** `cpmj1.<base64url(json)>` = `{ v: 1, primary: <primary name>, nodeId, secret: <32 random bytes> }`. It is
  shown once; anyone holding it can make a server a node of your primary — treat it like a password. **Regenerate
  token** issues a new secret; the old token and the node's current key stop working immediately (the node shows as
  *Pending* until it leaves and joins again with the new token). Both sides store the secret encrypted with DPAPI.
- **Every RPC** is `POST /api/cluster/rpc` with `{ v, nodeId, ts, nonce, ct }`: AES-256-GCM with a key derived by
  HKDF-SHA256 from the secret (salt `cpm-cluster-v1`, info `aes-256-gcm`). The associated data binds direction, node id,
  timestamp and nonce (`cpm1|req|<nodeId>|<ts>|<nonce>`); the response additionally binds the request nonce
  (`cpm1|resp|<nodeId>|<ts>|<nonce>|<requestNonce>`), so a response cannot be replayed for another request. The node
  rejects timestamps outside ±300 s, nonces seen in the last 10 minutes, unknown node ids and anything that does not
  decrypt with **401 without detail** (the reason is in the node's manager log), and answers **404** when it is not a
  node. The endpoint is anonymous at the cookie level: possession of the key is the authentication.
- The payload (configuration, certificates' private keys, DNS/Redis secrets) is always encrypted end to end, so the
  channel is confidential even over plain `http://`. Use **https** node URLs anyway: the TLS certificate is checked, and
  a self-signed/untrusted certificate is accepted only when its SHA-256 fingerprint matches the one **pinned** when the
  node was added or first contacted (trust on first use). After replacing a node's UI certificate, an admin re-pins it
  (Servers → Edit → Re-pin certificate). Node URLs never use the outbound proxy.
- Node administrators cannot change replicated resources (409 *Managed by the cluster primary*), but they remain
  administrators of that Windows server: they can leave the cluster, and anyone who controls a node controls what it
  serves. Only join servers that are managed by the same team.
- Every cluster action is audited: `server` (created, updated, tokenRegenerated, deleted, synced, restarted, caddyUpdate)
  on the primary; `cluster` (joined, left, synced, syncFailed) on the node; CLI actions as `cli:<user>`.

## Monitoring

| Event key | When | Alert rule |
|---|---|---|
| `server-offline:<nodeId>` | 3 consecutive heartbeats failed (Warning); *Recovered* when it answers again | Server offline |
| `server-sync:<nodeId>` | the node could not apply the configuration — Caddy's (secret-scrubbed) error; *Recovered* after the next successful sync or when the node runs the current configuration again | Configuration failure |

A revision the node rejected is not pushed again automatically for 5 minutes (a new change or **Sync now** is pushed at
once), so a persistent failure does not flood the node's configuration history.

The Servers page shows each server's status (online / offline / pending / error), manager and Caddy versions, live
resources, sync state, and — per server — details, charts and traffic statistics (proxied from the node through the
same encrypted channel). Caddy on a node can be restarted and updated from the primary.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| *The server at … is not a cluster node* | The node has not joined yet or left the cluster: join it with the token (regenerate one if it was lost). |
| *The node rejected the request (authentication failed)* | The token was regenerated (join again with the new one), the node left/joined another primary, or the clocks differ by more than 5 minutes. The node's manager log names the exact reason (`Rejected cluster RPC from …`). |
| *Its HTTPS certificate is not trusted and does not match the pinned fingerprint* | The node's UI certificate changed. Verify the new certificate, then Servers → Edit → Re-pin. |
| *The node redirects to https://…* | The node redirects HTTP to HTTPS for its UI: change the server URL to the https address. |
| Node *Offline* | Firewall/port/DNS between primary and node, or the node's manager service is stopped. The node keeps serving. |
| *Configuration sync to server … failed* | Caddy on the node rejected the configuration — typically a plugin/module only the primary has (add it to the desired plugins so nodes rebuild) or a node-local port that is already in use on the node. The node keeps its previous configuration. |
| Node *pending* with a Caddy rebuild | The node downloads Caddy with the desired plugins (needs access to caddyserver.com, or the outbound proxy in the node's Settings → Updates). The job log is on the node's Caddy page. |
| Every server requests its own certificates / HTTP-01 fails behind the load balancer | Storage backend is Local: configure shared storage. |
| Caddy log `permission denied` / `access is denied` under the storage path | The computer account lacks Modify on the share/folder, or the group membership is not effective yet (reboot / `klist -li 0x3e7 purge`). |
| `cluster join` says *Stop the CaddyProxyManager service first* | The CLI opens the database directly: `net stop CaddyProxyManager`, run the command, `net start CaddyProxyManager`. |
