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
  address, certificate store path and custom ACME root certificate path (`CaddySettings.NodeLocalProperties`). Secrets
  (EAB key, DNS provider credentials, Redis password/encryption key, custom storage JSON) travel inside the encrypted
  channel and are re-encrypted with the node's own DPAPI key;
- with a custom ACME CA, the **content** of its root certificate file: each node writes it to
  `C:\ProgramData\CaddyProxyManager\caddy\cluster-acme-root.pem` and uses that file, unless a node administrator set
  another root certificate path on the node (a node-local setting);
- the desired Caddy plugins. A node whose Caddy lacks a desired plugin rebuilds Caddy with the plugins first (a normal
  binary install job on the node) and stores and applies the configuration only when that has succeeded (see below).

What stays local on every server: users and sign-in settings, the management UI settings, notifications, backups,
readiness, logs, events, the Caddy binary/service actions and the node-local Caddy settings above.

The configuration is sent as one **bundle**; its revision is the SHA-256 of its canonical JSON (Servers page:
*desired* vs *applied* revision). A node applies a bundle completely or not at all: when its Caddy rejects the
configuration, the node restores its previous data and keeps running the previous configuration (Caddy never loads a
config it cannot provision). **Nodes keep serving their last applied configuration while the primary is unreachable.**

Consistency rules:

- The primary builds bundles only from **committed** configuration: while a change is being saved and applied (and
  possibly rolled back because its Caddy rejected it) a heartbeat reuses the last bundle, so nodes never receive a change
  the primary itself did not accept.
- A node stores and applies a bundle while holding the same lock as its own configuration changes, so a node-local
  settings change (e.g. its HTTPS port) and a replication never overwrite each other.
- A bundle that needs plugins the node's Caddy lacks is kept aside (*pending*) until Caddy has been rebuilt; the node's
  stored configuration is not touched before that. A newer revision arriving during the rebuild replaces the waiting
  one. When the rebuild fails, the node keeps its previous configuration and plugin list, reports the error once
  (`server-sync` alert) and retries that plugin set automatically only after 10 minutes, then 30 minutes, 1.5 hours ...
  (at most every 6 hours); **Sync now** retries at once.
- A certificate whose files cannot be read on the primary at the moment of a push (a renewal in progress, a share
  briefly unreachable) stays on the nodes unchanged (a warning is shown on the Servers page) instead of being deleted.
  Only certificates deleted on the primary disappear from the nodes.

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
| **Redis** | You already run a standalone Redis server | Plugin `github.com/pberkel/caddy-storage-redis` (added to the desired plugins; nodes rebuild automatically). One address (Redis Cluster and Sentinel are not supported). Optional TLS and value encryption. |
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

   A standalone server without nodes of its own can join. A node can join **its own primary** again with a new token
   (same primary name, or a token issued for its node id — e.g. after **Regenerate token**, or after the primary was
   restored) with `cluster join <token>` (or `POST /api/cluster/join`), without leaving first; a node of another
   primary must leave that cluster first. Joining replaces the
   node's hosts, certificates, access lists, streams and replicated settings at the first sync (its node-local settings,
   users etc. stay).
3. Within one heartbeat (15 s) the primary pushes its configuration. The Servers page shows the node *Online* with
   matching desired/applied revisions; the node shows the banner *Managed by &lt;primary&gt;*.
4. Configure shared storage (above) and point your load balancer/DNS at all servers.

After that, every change on the primary is pushed about 2 s after it is applied (and at the latest by the next
heartbeat, which also resyncs a node that reports another revision). **Sync now** pushes immediately.

Removing: Servers → **Remove** tells the node to leave (it becomes standalone and keeps its last configuration, now
editable). If the node is unreachable it is removed on the primary anyway, but it **still trusts its cluster key** (whoever
holds that key or the node's join token can still manage it): the primary raises a `server-removed:<nodeId>` warning, and
you run `CaddyManager.exe cluster leave` on the node with the service stopped (or Settings → Cluster → Leave there). A
node can also leave by itself (Settings → Cluster → Leave), after which the primary shows it as not joined (*pending*,
without alerts).

## Security model

- **Join token** `cpmj1.<base64url(json)>` = `{ v: 1, primary: <primary name>, nodeId, secret: <32 random bytes> }`. It is
  shown once; the secret in it is the node's cluster key, so anyone holding the token can manage the node (and make a
  server a node of your primary) — treat it like a password. Both sides store the secret encrypted with DPAPI.
- **Regenerate token** is a key rotation: the primary sends the new secret to the node over the encrypted channel (RPC
  `rekey`, sealed with the current key); the node switches at once and the old key and token stop working. The answer
  (`{ joinToken, rotated }`) says whether the node confirmed. When the node cannot be reached, the rotation is
  **pending** (Servers page: *key rotation pending*): the node **still accepts its previous key** until the primary reaches
  it — it retries at every heartbeat — or the node joins again with the new token or leaves. The new token is needed only
  to join the node again (e.g. after it left or its stored key became unusable).
- A node obeys one **primary instance**: every RPC carries the primary's random instance id and the node pins the first one
  it sees after joining. A copy of the primary running alongside the original (a cloned VM, a restored backup) is refused
  (`server-sync` alert on that copy: *obeys another instance of the primary*), so two primaries never take turns
  reconfiguring a node. A primary database that is started on a machine with another name becomes a new instance: after
  a restore on new hardware, regenerate each node's token there and join the nodes again with it (that also replaces
  keys that DPAPI cannot decrypt on the new machine).
- **Every RPC** is `POST /api/cluster/rpc` with `{ v, nodeId, ts, nonce, ct }`: AES-256-GCM with a key derived by
  HKDF-SHA256 from the secret (salt `cpm-cluster-v1`, info `aes-256-gcm`). The associated data binds direction, node id,
  timestamp and nonce (`cpm1|req|<nodeId>|<ts>|<nonce>`); the response additionally binds the request nonce
  (`cpm1|resp|<nodeId>|<ts>|<nonce>|<requestNonce>`), so a response cannot be replayed for another request. The node
  rejects timestamps outside ±300 s, nonces seen in the last 10 minutes, requests signed before its manager started (the
  nonce memory does not survive a restart; the primary's clock offset is remembered for this check, with 2 s tolerance),
  unknown node ids and anything that does not decrypt with **401 without detail**, and answers **404** when it is not a
  node. The endpoint is anonymous at the cookie level: possession of the key is the authentication.
- Rejections are logged in the node's manager log (and so in the Windows Application event log) at most once per remote
  address and minute (`Rejected cluster RPC from …`, the next one says how many were not logged); an address that
  causes 30 rejections within a minute gets **429** without its requests being read until the minute is over. Request
  bodies are limited to 64 MB (also when sent chunked): a larger configuration bundle is refused with **413**.
- The payload (configuration, certificates' private keys, DNS/Redis secrets) is always encrypted end to end, so the
  channel is confidential even over plain `http://`. Use **https** node URLs anyway: the TLS certificate is checked, and
  a self-signed/untrusted certificate is accepted only when its SHA-256 fingerprint matches the one **pinned** when the
  node was added or first contacted (trust on first use). After replacing a node's UI certificate, an admin re-pins it
  (Servers → Edit → Re-pin certificate). Node URLs never use the outbound proxy.
- Node administrators cannot change replicated resources (409 *Managed by the cluster primary*), but they remain
  administrators of that Windows server: they can leave the cluster, and anyone who controls a node controls what it
  serves. Only join servers that are managed by the same team.
- Every cluster action is audited: `server` (created, updated, tokenRegenerated, keyRotated, deleted, synced, restarted,
  caddyUpdate) on the primary; `cluster` (joined, left, keyRotated, synced, syncFailed) on the node; CLI actions as
  `cli:<user>`.

## Monitoring

| Event key | When | Alert rule |
|---|---|---|
| `server-offline:<nodeId>` | 3 consecutive heartbeats failed (Warning); *Recovered* when it answers again | Server offline |
| `server-sync:<nodeId>` | the node could not apply the configuration — Caddy's (secret-scrubbed) error, a failed Caddy rebuild, a bundle the node refuses (413), or the node obeys another primary instance; *Recovered* once the node runs the current configuration (a sync that is only pending — Caddy being rebuilt again — does not count) | Configuration failure |
| `server-removed:<nodeId>` | the server was removed on the primary but could not be told to leave: it still trusts its cluster key | — (Events page) |

A revision the node could not apply (rejected by its Caddy, failed rebuild, refused as too large) is not pushed again
automatically for 5 minutes (a new change or **Sync now** is pushed at once), so a persistent failure does not flood the
node's configuration history. A server that has not joined yet (or left) is shown as *pending*: configuration changes are
not pushed to it and raise no alert.

The Servers page shows each server's status (online / offline / pending / error), manager and Caddy versions, live
resources, sync state, and — per server — details, charts and traffic statistics (proxied from the node through the
same encrypted channel). Caddy on a node can be restarted and updated from the primary.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| *The server at … is not a cluster node* | The node has not joined yet or left the cluster: join it with the token (regenerate one if it was lost). |
| *The node rejected the request (authentication failed)* | The node left or joined another primary, its stored key cannot be decrypted (database restored on another machine), or the clocks differ by more than 5 minutes: regenerate the token and join the node again with it. The node's manager log names the reason (`Rejected cluster RPC from …`). |
| *Key rotation pending* | The node was not reachable when the token was regenerated; it still accepts its previous key. The rotation completes at the next successful heartbeat, when the node joins again with the new token, or when it leaves. |
| *This server obeys another instance of the primary …* | Two primaries with the same keys (a clone or restored copy running alongside the original). Shut the copy down; to move the node to this instance, regenerate its token here and join the node again with it. |
| *The node refused the request because it is too large (HTTP 413)* | The configuration bundle exceeds 64 MB (thousands of certificates). Remove unused certificates or hosts. |
| *The node is temporarily refusing requests from this address (HTTP 429)* | Many rejected cluster requests came from the primary's address within a minute (wrong key, or something else on that address probing the node). It clears after a minute; check the node's manager log. |
| *Its HTTPS certificate is not trusted and does not match the pinned fingerprint* | The node's UI certificate changed. Verify the new certificate, then Servers → Edit → Re-pin. |
| *The node redirects to https://…* | The node redirects HTTP to HTTPS for its UI: change the server URL to the https address. |
| Node *Offline* | Firewall/port/DNS between primary and node, or the node's manager service is stopped. The node keeps serving. |
| *Configuration sync to server … failed* | Caddy on the node rejected the configuration — typically a plugin/module only the primary has (add it to the desired plugins so nodes rebuild) or a node-local port that is already in use on the node. The node keeps its previous configuration. |
| Node *pending* with a Caddy rebuild | The node downloads Caddy with the desired plugins (needs access to caddyserver.com, or the outbound proxy in the node's Settings → Updates). The job log is on the node's Caddy page. The node keeps its previous configuration until the rebuild succeeded; after a failure it retries with growing intervals (10 min, 30 min, ... up to 6 h) — **Sync now** retries at once. |
| Every server requests its own certificates / HTTP-01 fails behind the load balancer | Storage backend is Local: configure shared storage. |
| Caddy log `permission denied` / `access is denied` under the storage path | The computer account lacks Modify on the share/folder, or the group membership is not effective yet (reboot / `klist -li 0x3e7 purge`). |
| `cluster join` says *Stop the CaddyProxyManager service first* | The CLI opens the database directly: `net stop CaddyProxyManager`, run the command, `net start CaddyProxyManager`. |
