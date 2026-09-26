# Traffic statistics and server telemetry

**Traffic** (`/traffic`) and the server pages show what Caddy served and how the server is doing. Everything is
collected on each server by the manager itself; no external monitoring system is needed.

## Where the numbers come from

**Traffic statistics** are built from Caddy's own access log. With **Settings › Caddy › Traffic statistics** on (the
default), the generated Caddy configuration adds a compact log (`cpm_stats`) that records one JSON line for every HTTP
request any site handled — including hosts without their own access log — to
`C:\ProgramData\CaddyProxyManager\logs\stats\requests.log`. Request and response headers and TLS details are removed
from these lines. Caddy rotates the file at 10 MB and keeps 5 rotated files (uncompressed).

The manager reads the file every second, following rotations (it keeps reading a rotated file to its end before
switching to the new one), and adds each request to per-minute, per-hour and per-day counters. The statistics have their
own database, `C:\ProgramData\CaddyProxyManager\db\telemetry.db`, separate from the configuration database
(`manager.db`), so they never slow down configuration changes and are not part of backups. The counters are saved at
least every 5 seconds together with the exact read position; the unique-client data and top clients, which are larger,
at least once a minute together with their own read position (reports always include what has not been saved yet).
After a restart — also after a crash, and also when Caddy rotated the file in the meantime — the manager reads the log
again from the older of the two positions and adds the lines in between to the unique-client data and top clients only,
so requests, unique clients and top clients are neither lost nor counted twice. If saving fails (for example a full
disk), the manager discards what it has not saved and reads it again from the log once saving works; an event is raised
after three failed attempts.

The statistics are only kept for the host names configured on enabled hosts. The Host header of a request is chosen by
the client, so requests for any other name are all counted under one row, **(other)** — a client sending random or
over-long Host headers cannot create new entries or make the statistics database grow.

**Server resources** (CPU, memory, disks, network, Caddy and manager processes, connections) are sampled every
2 seconds and kept in memory for the last 10 minutes. They are not stored.

## Definitions

| Number | Meaning |
|---|---|
| Requests | HTTP requests Caddy completed and logged (one per request, including redirects to HTTPS and ACME HTTP-01 challenge requests). |
| Data in | Request **body** bytes Caddy read (Caddy's `bytes_read`). Headers are not included; a request whose body the handler never read counts 0. |
| Data out | Response **body** bytes sent, after compression (Caddy's `size`). Headers and TLS overhead are not included. |
| Status 2xx/3xx/4xx/5xx | By the final status code. **Other** = requests without a status (the client or Caddy aborted before a response was written; Caddy logs status 0) and 1xx. |
| Status codes | Every individual code with its count (0 = aborted). |
| Unique clients | Distinct client IP addresses. The client IP is Caddy's `client_ip`: the connecting address, or — when the connection comes from a configured **trusted proxy** — the address that proxy reported in `X-Forwarded-For`. |
| Avg duration | Mean time from Caddy receiving the request to finishing the response (Caddy's `duration`), in milliseconds. |
| Top hosts | Hosts by request count in the window (max 20). A host is a name configured on an enabled host, matched against the request's `Host` header (lower-case, without port) the way Caddy matches it: an exact name is its own row (`[::1]` for IPv6 literals); a wildcard host is one row under its wildcard name (`*.example.com` collects `a.example.com`, `b.example.com`, ... — as in Caddy, `*` is exactly one label). Every other name, and requests without a Host, are counted as **(other)**. Filtering on a name covered by a wildcard shows the wildcard's row. The configured names are re-read after every configuration change; a host added later counts from then on, a host removed keeps its old statistics. |
| Top clients | The busiest client IPs (max 20) — see accuracy below. |
| Requests/s (live) | Requests per second by the time Caddy logged them, averaged over the sample interval and lagging about 2 seconds behind real time (the log is read once a second). |
| CPU | Whole-machine CPU use, 0–100 %. |
| Memory used | Physical memory in use (total − available). |
| Caddy / manager CPU | Share of the **whole machine** used by that process (so 100 % = every core busy). Caddy's values are empty while Caddy is not running. |
| Caddy / manager memory | Working set of the process. |
| Network in/out | Bytes per second over all connected, non-loopback network interfaces (all traffic, not only Caddy's), from each interface's own counters: an interface that comes up (link regained, VPN connected, virtual switch adapter re-created) counts from its second sample, so it does not show its whole since-boot total as one spike. Traffic that passes through a VPN or a Hyper-V virtual switch adapter and a physical adapter is counted on both. |
| Connections | Established TCP connections to Caddy's HTTP and HTTPS ports (HTTP/3 over UDP is not included). |
| Disks | The volume that holds the data folder and the system volume (one row when they are the same). |

## Windows (time ranges)

| Range | Points | Bucket |
|---|---|---|
| 1 hour | 60 | 1 minute |
| 24 hours | 24 | 1 hour |
| 7 days | 168 | 1 hour |
| 30 days | 30 | 1 day |

Buckets are UTC. The newest point is the bucket that is still filling, so "24 hours" covers the current hour and the
23 before it. Buckets without traffic show as zero.

How the charts show the time: minute and hour buckets in the browser's time zone (named next to the range selector,
e.g. *Times in your time zone (Europe/London, UTC+01:00)*); day buckets as **UTC calendar days** — each 30-day point
covers 00:00–24:00 UTC and is labelled with its UTC date, e.g. *Fri, Sep 25 (UTC)*, and the 30-day axis ticks are at
UTC midnight. West of UTC a day bucket therefore starts on the previous local evening (00:00 UTC = 20:00 EDT).

## Accuracy

- Requests, data in/out, status counts and durations are **exact**.
- Unique clients are **exact up to 1,024 different IPs** per bucket. Above that a HyperLogLog sketch (4,096 registers)
  estimates the count with a typical error of **±1.6 %** (within ±3.2 % for 95 % of buckets). Unique clients over a
  window are computed by merging the buckets' sketches, so a client that appears in many buckets is still counted once;
  the per-point values in a chart are unique clients *of that bucket*.
- Top clients use a heavy-hitter summary of 200 clients per UTC day. Any client that made more than 1/200 of the day's
  requests is guaranteed to be listed; its count can be over-estimated (never under-estimated) when many different
  clients were seen. Over several days the daily summaries are merged. For the 1-hour view, top clients cover the whole
  UTC day(s) of that hour.
- The 1-hour view per host uses per-host minute buckets that are kept for 2 hours only.

## Retention

| Bucket | Kept | Content |
|---|---|---|
| Minute | 48 hours (per host: 2 hours) | Server total; per host |
| Hour | 35 days | Server total and per host |
| Day | 400 days | Server total and per host, with the top-clients summary |

Expired buckets are deleted every hour. The raw `requests.log` files are kept by Caddy (5 × 10 MB) and deleted by
Caddy as it rotates.

## What is not counted

- **Layer-4 streams** (TCP/UDP proxying) — they are not HTTP requests.
- Connections that never became an HTTP request: **TLS handshake failures**, malformed requests that Go rejects before
  Caddy sees them (for example 400/431 for bad or huge headers), HTTP/2 and HTTP/3 protocol errors.
- Requests handled while **Traffic statistics** was turned off (Caddy does not write the log then), and while the
  configuration is in Caddyfile mode (the generated `cpm_stats` log is part of the managed configuration). In
  Caddyfile mode the Traffic page says that statistics are not collected instead of showing zeros.
- Log lines that cannot be read are skipped and counted; the report notes how many. If the manager is stopped long
  enough for Caddy to rotate more than 5 files (50 MB of log), the oldest are deleted before they are read: the report
  notes this and an event is raised.

## Privacy

Client IP addresses are personal data in many jurisdictions (for example under the GDPR).

- The stats log contains, per request, the time, client IP, host, method, URI (with query string), protocol, status,
  sizes and duration — no headers, cookies or authorization. It lives in the manager's data folder, readable only by
  SYSTEM and Administrators, and Caddy deletes it through rotation (at most 50 MB).
- The counters store **no IP addresses** for unique clients — only keyed 64-bit hashes (HMAC-SHA256 with a random key
  of this installation; exact mode) or HyperLogLog registers. The key is kept in `db\telemetry.key`, outside the
  statistics database and encrypted with Windows DPAPI for this machine, so a copy of `telemetry.db` cannot be turned
  back into addresses by hashing every possible IPv4 address. (If the key is lost — the file deleted, or the data folder
  moved to another machine — a new one is created; clients seen before and after that moment are not recognised as the
  same.)
- The **top-clients summary stores up to 200 IP addresses per day** with their request counts and last-seen time,
  for 400 days. Everyone with the **viewer** role can see them on the Traffic page.
- The statistics database is not part of backups (`manager.db` is), and restoring a backup leaves it unchanged.
- Turn **Traffic statistics** off if you must not keep client IPs; existing statistics stay until they expire.
