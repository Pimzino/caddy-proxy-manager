# Research: Caddy v2.11.4 clustering, storage, admin, metrics

## 1. Clustering = shared storage
- "Any Caddy instances that are configured to use the same storage will automatically share those resources and coordinate certificate management as a cluster" (https://caddyserver.com/docs/automatic-https#storage). Same storage is the sole criterion (certmagic README).
- Shared: certs/keys/OCSP, distributed locks (one instance obtains/renews), ACME account + challenge data, ECH configs.
- HTTP-01 AND TLS-ALPN-01 tokens are written to storage (certmagic distributedSolver) and any instance answers the CA → works behind load balancers. DNS-01 needs none of this.
- NOT shared: config. Each instance has its own config → push/pull per instance.
- file_system on network shares: README says mounting "the same shared folder" works. Lock = atomically-created lock file refreshed every 5s, stale after 10s. Maintainers blame NFS lock issues on NFS. No official statement about SMB/UNC. `file_system` has one field `root`. UNC root should work (Go os accepts UNC) — inference. LocalSystem reaches shares as DOMAIN\HOST$; mapped drive letters not visible to services.

## 2. Storage modules (JSON `"storage": {"module": "<name>", ...}`)
| Module | Go package | JSON fields | status |
|---|---|---|---|
| file_system | core | `root` | core |
| redis (use) | github.com/pberkel/caddy-storage-redis | `client_type` (simple/cluster/failover), `address[]`, `host[]`, `port[]` (ARRAYS), `db`, `username`, `password`, `timeout` (string e.g. "5"), `key_prefix` (default caddy), `encryption_key`, `compression`, `tls_enabled`, `tls_insecure`, `tls_server_certs_pem`, `tls_server_certs_path`, `master_name`, `route_by_latency`, `route_randomly` | active 2026-08 v1.8.2 |
| redis (old) | github.com/gamalan/caddy-tlsredis | — | archived 2024 (docs page shows its fields — trap) |
| postgres | github.com/yroc92/postgres-storage | `connection_string` or host/port/user/password/dbname/sslmode; `query_timeout`, `lock_timeout` | stale 2023 |
| consul | github.com/pteich/caddy-tlsconsul | `address`, `token`, `timeout`, `prefix`, `value_prefix`, `aes_key`, `tls_enabled`, `tls_insecure` | 2024-09 |
| s3 | github.com/ss098/certmagic-s3 | `host`, `bucket`, `access_id`, `secret_key`, `prefix`, `insecure` | 2025-09 |
All listed in https://caddyserver.com/api/packages.

## 3. Admin API remote management (experimental): `admin.remote` (mTLS, `access_control[].public_keys`), `admin.config.load` + `load_delay` with `caddy.config_loaders.http` (fields method,url,header,timeout,adapter,tls). Pushing with POST /load per instance is the stable alternative. → We push through our own manager agent on each node; Caddy admin stays on loopback.

## 4. Metrics
- `/metrics` on admin API on by default. Always: caddy_admin_http_requests_total, caddy_config_last_reload_successful, caddy_reverse_proxy_upstreams_healthy{upstream}, go_*, process_* (Linux+Windows only: process_cpu_seconds_total, process_resident_memory_bytes = working set, process_open_fds = handle count, process_start_time_seconds).
- HTTP metrics off unless `apps.http.metrics: {"per_host": true}` (moved from servers.* in 2.9; per-server is deprecated). Names: caddy_http_requests_in_flight, caddy_http_requests_total, caddy_http_request_errors_total, caddy_http_request_duration_seconds, caddy_http_request_size_bytes, caddy_http_response_size_bytes. Unconfigured hosts grouped as `_other`.
