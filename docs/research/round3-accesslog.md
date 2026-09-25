# Research: Caddy v2.11.4 access logs (source-verified, timberjack v1.4.2). B = https://github.com/caddyserver/caddy/blob/v2.11.4/

## 1. Entry fields
- Logger `http.log.access` (+ `.<name>` for named). Top-level: `level` (info; error when status>=500), `ts` (float unix seconds by default), `logger`, `msg` ("handled request", "NOP" when no handler matched).
- `request`: `remote_ip`, `remote_port` (TCP peer), `client_ip` (ALWAYS set; honours trusted_proxies → use for unique clients), `proto` (HTTP/1.1, HTTP/2.0, HTTP/3.0), `method`, `host` (raw Host header — MAY INCLUDE PORT; strip before grouping; lowercase), `uri`, `headers` (Cookie/Authorization redacted), `tls` {resumed, version, cipher_suite, proto, server_name, ech} on TLS only. Original (pre-rewrite) request.
- Then: `bytes_read` (request body bytes handlers actually read; 0 if none), `user_id`, `duration` (float seconds), `size` (response BODY bytes after encode = compressed; excludes headers), `status` (CAN BE 0 when nothing called WriteHeader, e.g. abort → treat 0 as "aborted"/count separately; Go sends 200 otherwise), `resp_headers`.
- WebSockets/hijacked: bytes after hijack are added to size/bytes_read.

## 2. Routing
- Access logging only if `servers.X.logs` is non-nil (`{}` suffices).
- `logger_names`: map host → array (string accepted). Lookup exact host, wildcard, then `[default_logger_name]`. Empty-string entry = base `http.log.access`. Keys without ports.
- `skip_unmapped_hosts`: unmapped hosts not logged at all (so do NOT use it if we want global stats).
- `vars` handler `access_logger_names` overrides per request; `log_skip` suppresses.
- RECOMMENDED fan-out at the sink: `logging.logs.<sink>.include` is a dot-boundary prefix match on logger names. `include: ["http.log.access"]` matches EVERY access logger (named or base). So: per-host sinks include `http.log.access.<name>`; one stats sink includes `http.log.access` → each request written once per matching sink.
- Default log: with no include/exclude it accepts everything → add `"exclude": ["http.log.access"]` to `logs.default` (respected since 2.10.1).

## 3. File writer (timberjack since 2.11.1)
Fields: `filename`, `mode` (octal string, default "0600"), `dir_mode`, `roll` (default true), `roll_size_mb` (default 100), `roll_interval` (Go Duration → integer NANOSECONDS in JSON), `roll_minutes`, `roll_at`, `roll_compression` (`none`|`gzip` default|`zstd`), `roll_local_time`, `roll_keep` (default 10), `roll_keep_days` (default 90; 0 = default), `backup_time_format` (default 2006-01-02T15-04-05.000). Website JSON reference is outdated.
- Backup name: `<name>-<ts>-<reason><ext>` (reason size|time) + `.gz`/`.zst` after async compression. Use `roll_compression: "none"` so a tailer can finish the old file.
- Rotation: active file closed, RENAMED to backup, new file created (O_TRUNC) at the same path.
- Windows: Go opens with FILE_SHARE_READ|FILE_SHARE_WRITE, no FILE_SHARE_DELETE. A reader must open with FileShare.ReadWrite | FileShare.Delete — otherwise Caddy's rename FAILS and the entry is lost. With share-delete, a held handle keeps reading the renamed file → tailer can drain the old handle to EOF, then reopen the path.

## 4. Net writer: synchronous, no write deadline → a slow receiver BLOCKS request completion. Don't use; tail a file.

## 5. Not logged: TLS handshake failures, malformed requests (Go auto 400/431), HTTP/2/QUIC protocol errors, layer4 streams, long-method rejects. Logged: abort (status 0, size 0), ACME HTTP-01 challenge requests, auto-HTTPS redirect server requests (copies logs config). Handler errors: access entry with final status + separate http.log.error.* entry.
