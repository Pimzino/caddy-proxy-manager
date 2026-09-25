// Round 3 mock API routes. Owner: WEB-SERVERS builder: /api/servers*.
//
// A local primary plus two nodes (WEB-PROXY02 online and in sync, WEB-PROXY03 offline with a stale revision).
// Resource samples and traffic are pure functions of (server, time): sin waves + hashed noise keyed by the
// 2-second sample slot or the traffic bucket, so repeated polls agree with each other and `?since=` polling
// returns a new point every 2 s. Environment switches:
//   MOCK_CLUSTER_ROLE=standalone → this server has no nodes yet (empty/standalone state)
//   MOCK_CLUSTER_ROLE=node       → this server is a managed node (only itself is listed; adding servers → 409)
import type {
  AddServerResult,
  AuditEntry,
  CaddyStatus,
  DiskUsage,
  JobInfo,
  ResourceSample,
  ServerInfo,
  ServerSummary,
  TrafficClientRow,
  TrafficHostRow,
  TrafficPoint,
  TrafficRange,
  TrafficReport,
  TrafficTotals,
} from '../src/api/types.ts';
import { iso, newId, type MockState } from './fixtures.ts';
import type { MockHelpers, MockRoute } from './plugin.ts';

const SEC = 1000;
const MIN = 60 * SEC;
const HOUR = 60 * MIN;
const DAY = 24 * HOUR;
const GiB = 1024 ** 3;
const SAMPLE_MS = 2 * SEC;
const HISTORY_MS = 10 * MIN;

// ---------------------------------------------------------------- deterministic noise

/** Integer hash → [0, 1). Same inputs always give the same value (so polls are consistent). */
function hash01(a: number, b: number): number {
  let h = (Math.imul(a | 0, 0x9e3779b1) ^ Math.imul((b | 0) + 0x7f4a7c15, 0x85ebca77)) >>> 0;
  h ^= h >>> 15;
  h = Math.imul(h, 0x2c1b3c6d);
  h ^= h >>> 12;
  h = Math.imul(h, 0x297a2d39);
  h ^= h >>> 15;
  return (h >>> 0) / 4294967296;
}

const clamp = (v: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, v));

/** Office-hours load curve for the (mock) server's local time: low at night, peak mid-afternoon. */
function diurnal(ms: number): number {
  const d = new Date(ms);
  const h = d.getHours() + d.getMinutes() / 60;
  const day = Math.max(0, Math.sin((Math.PI * (h - 6)) / 16)) ** 1.4;
  const weekend = d.getDay() === 0 || d.getDay() === 6 ? 0.45 : 1;
  return (0.12 + 0.88 * day) * weekend;
}

// ---------------------------------------------------------------- servers

interface MockServer {
  id: string;
  name: string;
  url?: string;
  seed: number;
  /** Relative load (requests) of this server. */
  load: number;
  status: ServerSummary['status'];
  addedAt?: string;
  lastSeenMs: number;
  lastError?: string;
  fingerprint?: string;
  /** Pending servers "join" after this time. */
  joinAtMs?: number;
  hostname: string;
  fqdn: string;
  os: string;
  ips: string[];
  cpus: number;
  memTotal: number;
  disks: { name: string; label: string; totalBytes: number; freeBase: number }[];
  caddyVersion: string;
  caddyStartedMs: number;
  bootMs: number;
  managerStartMs: number;
  appliedRevision?: string;
  lastSyncMs?: number;
  syncError?: string;
  syncWarnings: string[];
}

/** Caddy version placeholder: the node runs whatever the local (mock) server runs. */
const SAME_AS_LOCAL = '=';
const REV_CURRENT = 'e3b1c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855';
const REV_OLD = '9f2c7a1e05d34b8c6a0e1f7d2b93c4e5a6f708192a3b4c5d6e7f8091a2b3c4d5';

function makeToken(primary: string, nodeId: string): string {
  const secret = Buffer.from(Array.from({ length: 32 }, () => Math.floor(Math.random() * 256))).toString('base64');
  return `cpmj1.${Buffer.from(JSON.stringify({ v: 1, primary, nodeId, secret })).toString('base64url')}`;
}

function fakeFingerprint(seed: string): string {
  const bytes = Array.from({ length: 32 }, (_, i) => Math.floor(hash01(i + 1, seed.length * 7919 + seed.charCodeAt(i % seed.length)) * 256));
  return bytes.map((b) => b.toString(16).padStart(2, '0').toUpperCase()).join(':');
}

function initialServers(now: number): MockServer[] {
  const mode = process.env.MOCK_CLUSTER_ROLE;
  const local: MockServer = {
    id: 'local',
    name: 'WEB-PROXY01',
    seed: 11,
    load: 1,
    status: 'online',
    lastSeenMs: now,
    hostname: 'WEB-PROXY01',
    fqdn: 'web-proxy01.corp.example.com',
    os: 'Microsoft Windows Server 2025 Standard 10.0.26100',
    ips: ['10.0.10.51', 'fe80::4c1d:9aff:fe21:7b10'],
    cpus: 8,
    memTotal: 16 * GiB,
    disks: [
      { name: 'C:\\', label: 'System', totalBytes: 127 * GiB, freeBase: 41.6 * GiB },
      { name: 'D:\\', label: 'Data', totalBytes: 512 * GiB, freeBase: 318 * GiB },
    ],
    caddyVersion: 'v2.11.4',
    caddyStartedMs: now - 3 * DAY - 4 * HOUR,
    bootMs: now - 23 * DAY,
    managerStartMs: now - 3 * DAY - 4 * HOUR - 40 * SEC,
    syncWarnings: [],
  };
  if (mode === 'standalone' || mode === 'node') return [local];
  return [
    local,
    {
      id: 'n-7f3a91c2e4b5',
      name: 'WEB-PROXY02',
      url: 'https://web-proxy02.corp.example.com:8443',
      seed: 23,
      load: 0.78,
      status: 'online',
      addedAt: iso(41 * DAY),
      lastSeenMs: now - 6 * SEC,
      fingerprint: fakeFingerprint('web-proxy02'),
      hostname: 'WEB-PROXY02',
      fqdn: 'web-proxy02.corp.example.com',
      os: 'Microsoft Windows Server 2022 Standard 10.0.20348',
      ips: ['10.0.10.52'],
      cpus: 4,
      memTotal: 8 * GiB,
      disks: [
        { name: 'C:\\', label: 'System', totalBytes: 96 * GiB, freeBase: 22.4 * GiB },
        { name: 'E:\\', label: 'Data', totalBytes: 200 * GiB, freeBase: 151 * GiB },
      ],
      caddyVersion: SAME_AS_LOCAL,
      caddyStartedMs: now - 3 * DAY - 2 * HOUR,
      bootMs: now - 17 * DAY,
      managerStartMs: now - 17 * DAY + 3 * MIN,
      appliedRevision: REV_CURRENT,
      lastSyncMs: now - 3 * HOUR - 12 * MIN,
      syncWarnings: [],
    },
    {
      id: 'n-2b8e4d6f1a09',
      name: 'WEB-PROXY03',
      url: 'http://10.0.10.53:8080',
      seed: 37,
      load: 0.6,
      status: 'offline',
      addedAt: iso(12 * DAY),
      lastSeenMs: now - 27 * MIN,
      lastError: 'No connection could be made because the target machine actively refused it. (10.0.10.53:8080)',
      hostname: 'WEB-PROXY03',
      fqdn: 'web-proxy03.corp.example.com',
      os: 'Microsoft Windows Server 2019 Standard 10.0.17763',
      ips: ['10.0.10.53'],
      cpus: 4,
      memTotal: 8 * GiB,
      disks: [{ name: 'C:\\', label: 'System', totalBytes: 80 * GiB, freeBase: 9.1 * GiB }],
      caddyVersion: 'v2.10.2',
      caddyStartedMs: now - 9 * DAY,
      bootMs: now - 9 * DAY - 20 * MIN,
      managerStartMs: now - 9 * DAY - 18 * MIN,
      appliedRevision: REV_OLD,
      lastSyncMs: now - 2 * DAY - 5 * HOUR,
      syncError: 'Node did not answer the sync request within 30 s.',
      syncWarnings: ['Caddy on this node is v2.10.2; the primary runs v2.11.4.'],
    },
  ];
}

// ---------------------------------------------------------------- resource samples

function sampleAt(s: MockServer, t: number, caddyRunning: boolean): ResourceSample {
  const slot = Math.floor(t / SAMPLE_MS);
  const n = (k: number) => hash01(slot, s.seed * 31 + k);
  const wave = Math.sin((2 * Math.PI * t) / (7 * MIN) + s.seed) * 0.5 + Math.sin((2 * Math.PI * t) / (53 * SEC) + s.seed * 2) * 0.25;
  const load = diurnal(t) * s.load;
  const spike = n(9) > 0.985 ? 30 + n(10) * 25 : 0;
  const cpu = clamp(6 + 26 * load + 9 * wave + (n(1) - 0.5) * 7 + spike, 0.5, 99.5);
  const memFrac = clamp(0.46 + 0.06 * Math.sin((2 * Math.PI * t) / (23 * MIN) + s.seed) + (n(2) - 0.5) * 0.015, 0.2, 0.97);
  const rps = Math.max(0, (22 * load + 6) * (1 + 0.35 * wave + (n(3) - 0.5) * 0.4));
  const disks: DiskUsage[] = s.disks.map((d, i) => ({
    name: d.name,
    label: d.label,
    totalBytes: d.totalBytes,
    freeBytes: Math.round(d.freeBase - ((t / MIN) % 600) * 1024 * 1024 * (i + 1) * 0.3),
  }));
  return {
    at: new Date(slot * SAMPLE_MS).toISOString(),
    cpuPercent: Math.round(cpu * 10) / 10,
    memoryUsedBytes: Math.round(s.memTotal * memFrac),
    memoryTotalBytes: s.memTotal,
    disks,
    networkRxBytesPerSec: Math.round(rps * (1400 + n(4) * 900) + 2500 * n(5)),
    networkTxBytesPerSec: Math.round(rps * (34_000 + n(6) * 22_000) + 4000 * n(7)),
    caddyCpuPercent: caddyRunning ? Math.round(clamp(cpu * 0.42 + (n(8) - 0.5) * 2, 0.1, 99) * 10) / 10 : null,
    caddyMemoryBytes: caddyRunning ? Math.round((58 + 14 * load + 6 * Math.sin((2 * Math.PI * t) / (31 * MIN))) * 1024 * 1024) : null,
    managerCpuPercent: Math.round((0.4 + n(11) * 1.4) * 10) / 10,
    managerMemoryBytes: Math.round((96 + 8 * Math.sin((2 * Math.PI * t) / (13 * MIN))) * 1024 * 1024),
    activeConnections: caddyRunning ? Math.round(rps * (2.6 + n(12)) + 4) : null,
    requestsPerSecond: caddyRunning ? Math.round(rps * 10) / 10 : 0,
  };
}

// ---------------------------------------------------------------- traffic

const RANGES: Record<TrafficRange, { bucket: number; count: number; bucketSize: TrafficReport['bucketSize'] }> = {
  hour: { bucket: MIN, count: 60, bucketSize: 'minute' },
  day: { bucket: HOUR, count: 24, bucketSize: 'hour' },
  week: { bucket: HOUR, count: 168, bucketSize: 'hour' },
  month: { bucket: DAY, count: 30, bucketSize: 'day' },
};

/** Distinct clients seen in one bucket ≈ requests / this. */
const CLIENT_DIVISOR: Record<TrafficReport['bucketSize'], number> = { minute: 7, hour: 38, day: 260 };

interface HostWeight {
  host: string;
  weight: number;
  err4: number;
  err5: number;
}

function hostWeights(s: MockState): HostWeight[] {
  const domains = s.hosts.filter((h) => h.enabled).flatMap((h) => h.domains.slice(0, 1));
  const list = domains.length ? domains : ['app.example.com'];
  return list.map((host, i) => ({
    host,
    weight: 1 / (i + 1) ** 1.15,
    err4: 0.012 + hash01(i, 77) * 0.05,
    err5: host.startsWith('maintenance') ? 0.4 : hash01(i, 78) * 0.006,
  }));
}

/** Requests in the bucket starting at `start` (full bucket; the caller scales the current partial one). */
function bucketRequests(srv: MockServer, start: number, bucket: number): number {
  let total = 0;
  const step = Math.min(bucket, HOUR);
  // Integrate the load curve at (at most) hourly resolution so day buckets follow the diurnal pattern.
  for (let t = start; t < start + bucket; t += step) total += diurnal(t + step / 2) * (step / MIN);
  const noise = 0.82 + hash01(Math.floor(start / MIN), srv.seed) * 0.36;
  return total * 1250 * srv.load * noise;
}

function trafficReport(s: MockState, srv: MockServer, range: TrafficRange, host: string | null, now: number): TrafficReport {
  const { bucket, count, bucketSize } = RANGES[range];
  // Buckets align to local time for day buckets (like the backend's day buckets) and to UTC multiples otherwise.
  const tz = new Date(now).getTimezoneOffset() * MIN;
  const currentStart = bucket === DAY ? Math.floor((now - tz) / DAY) * DAY + tz : Math.floor(now / bucket) * bucket;
  const firstStart = currentStart - (count - 1) * bucket;
  const enabled = s.caddySettings.trafficStatsEnabled !== false;
  const hosts = hostWeights(s);
  const weightSum = hosts.reduce((a, h) => a + h.weight, 0);
  const selected = host ? hosts.find((h) => h.host === host.toLowerCase()) : undefined;
  const share = host ? (selected ? selected.weight / weightSum : 0) : 1;
  const err4 = selected ? selected.err4 : hosts.reduce((a, h) => a + h.err4 * h.weight, 0) / weightSum;
  const err5 = selected ? selected.err5 : hosts.reduce((a, h) => a + h.err5 * h.weight, 0) / weightSum;

  const series: TrafficPoint[] = [];
  for (let i = 0; i < count; i++) {
    const start = firstStart + i * bucket;
    const fraction = start === currentStart ? clamp((now - start) / bucket, 0, 1) : 1;
    const noise = hash01(Math.floor(start / MIN), srv.seed + 5);
    const incident = noise > 0.965 ? 7 : 1; // occasional burst of upstream errors
    const requests = enabled ? Math.round(bucketRequests(srv, start, bucket) * share * fraction) : 0;
    const s5 = Math.round(requests * Math.min(0.95, err5 * incident + (incident > 1 ? 0.02 : 0)));
    const s4 = Math.round(requests * err4 * (0.8 + noise * 0.4));
    series.push({
      at: new Date(start).toISOString(),
      requests,
      bytesIn: Math.round(requests * (1150 + noise * 600)),
      bytesOut: Math.round(requests * (31_000 + noise * 19_000)),
      uniqueClients: Math.min(requests, Math.round(requests / (CLIENT_DIVISOR[bucketSize] * (0.8 + noise * 0.4)))),
      status4xx: s4,
      status5xx: s5,
    });
  }

  const sum = (f: (p: TrafficPoint) => number) => series.reduce((a, p) => a + f(p), 0);
  const requests = sum((p) => p.requests);
  const s4 = sum((p) => p.status4xx);
  const s5 = sum((p) => p.status5xx);
  const s3 = Math.round(requests * 0.061);
  const other = Math.round(requests * 0.0006);
  const totals: TrafficTotals = {
    requests,
    bytesIn: sum((p) => p.bytesIn),
    bytesOut: sum((p) => p.bytesOut),
    // Clients return across buckets: the range total grows with √requests, never above the bucket sum.
    uniqueClients: Math.min(sum((p) => p.uniqueClients), Math.round(5 * Math.sqrt(requests))),
    status2xx: Math.max(0, requests - s3 - s4 - s5 - other),
    status3xx: s3,
    status4xx: s4,
    status5xx: s5,
    statusOther: other,
    avgDurationMs: requests ? Math.round((64 + hash01(srv.seed, range.length) * 70 + (s5 / requests) * 900) * 10) / 10 : 0,
  };

  const topHosts: TrafficHostRow[] = (selected ? [selected] : host ? [] : hosts).map((h) => {
    const f = selected ? 1 : h.weight / weightSum;
    const r = Math.round(requests * f);
    return {
      host: h.host,
      requests: r,
      bytesIn: Math.round(totals.bytesIn * f),
      bytesOut: Math.round(totals.bytesOut * f * (0.6 + hash01(h.host.length, 3) * 0.8)),
      uniqueClients: Math.max(r ? 1 : 0, Math.round(totals.uniqueClients * Math.sqrt(f))),
      status4xx: Math.round(r * h.err4),
      status5xx: Math.round(r * h.err5),
    };
  }).filter((h) => h.requests > 0).sort((a, b) => b.requests - a.requests);

  const ips = [
    '10.0.20.114', '10.0.20.37', '203.0.113.24', '10.0.31.9', '198.51.100.77', '10.0.20.201', '192.0.2.145',
    '10.0.44.12', '203.0.113.180', '2001:db8:4c2::1a', '10.0.20.66', '198.51.100.3', '10.0.31.140', '192.0.2.19',
  ];
  let remaining = requests;
  const topClients: TrafficClientRow[] = requests
    ? ips.map((ip, i) => {
        const r = Math.min(remaining, Math.round((requests * 0.16) / (i + 1) ** 1.05));
        remaining -= r;
        return {
          ip,
          requests: r,
          bytesOut: Math.round(r * (18_000 + hash01(i, 9) * 60_000)),
          lastSeen: new Date(now - Math.round(hash01(i, 10) * (range === 'hour' ? 50 * MIN : range === 'day' ? 5 * HOUR : 2 * DAY)) - i * 7 * SEC).toISOString(),
        };
      }).filter((c) => c.requests > 0)
    : [];

  const split = (n: number, parts: [number, number][]) => parts.map(([code, f]) => ({ code, count: Math.round(n * f) }));
  const statusCodes = [
    ...split(totals.status2xx, [[200, 0.93], [204, 0.035], [206, 0.035]]),
    ...split(totals.status3xx, [[304, 0.62], [301, 0.21], [302, 0.17]]),
    ...split(totals.status4xx, [[404, 0.58], [401, 0.19], [403, 0.14], [400, 0.06], [429, 0.03]]),
    ...split(totals.status5xx, [[502, 0.47], [503, 0.33], [504, 0.14], [500, 0.06]]),
    { code: 0, count: totals.statusOther },
  ]
    .filter((c) => c.count > 0)
    .sort((a, b) => b.count - a.count);

  return {
    range,
    from: new Date(firstStart).toISOString(),
    to: new Date(currentStart + bucket).toISOString(),
    bucketSize,
    host: host ?? undefined,
    enabled,
    lastIngestAt: enabled ? new Date(now - 1200).toISOString() : undefined,
    totals,
    series: enabled ? series : [],
    topHosts: enabled ? topHosts : [],
    topClients: enabled ? topClients : [],
    statusCodes: enabled ? statusCodes : [],
    notes: enabled
      ? [
          'Streams (layer 4) and connections rejected before HTTP parsing (TLS handshake failures, malformed requests) are not counted.',
          'Unique clients are exact up to 1,024 addresses per bucket and estimated (HyperLogLog) above that; totals across buckets are estimates.',
          'Status 0 means the client aborted before a response was written.',
        ]
      : ['Traffic statistics are disabled in Settings > Caddy.'],
  };
}

// ---------------------------------------------------------------- routes

interface ServerJob extends JobInfo {
  steps: string[];
  startedMs: number;
  onDone?: () => void;
}

export function round3ServersRoutes(h: MockHelpers): MockRoute[] {
  const { HttpError, ok, noContent } = h;
  const servers = initialServers(Date.now());
  const jobs = new Map<string, ServerJob>();
  const isNode = process.env.MOCK_CLUSTER_ROLE === 'node';

  const role = (s: MockState) => {
    const u = s.users.find((x) => x.id === s.sessionUserId && !x.disabled);
    if (!u) throw new HttpError(401, 'Not signed in');
    return u;
  };
  const rank = { viewer: 0, operator: 1, admin: 2 } as const;
  const requireRole = (s: MockState, r: keyof typeof rank) => {
    const u = role(s);
    if (rank[u.role] < rank[r]) throw new HttpError(403, 'Forbidden', `This action requires the ${r} role.`);
    return u;
  };
  const audit = (s: MockState, action: string, objectName: string, details?: string) => {
    const u = s.users.find((x) => x.id === s.sessionUserId);
    const entry: AuditEntry = { id: newId(), createdAt: iso(), updatedAt: iso(), userId: u?.id, userName: u?.name ?? 'system', action, objectType: 'server', objectName, details, remoteIp: '127.0.0.1' };
    s.audit.unshift(entry);
  };

  const find = (id: string) => {
    const srv = servers.find((x) => x.id === id);
    if (!srv) throw new HttpError(404, 'Server not found', `No server with id "${id}".`);
    return srv;
  };

  /** Advances simulated state: pending servers join after a while, online nodes heartbeat. */
  const tick = (now: number) => {
    for (const srv of servers) {
      if (srv.status === 'pending' && srv.joinAtMs && now >= srv.joinAtMs) {
        srv.status = 'online';
        srv.lastSeenMs = now;
        srv.appliedRevision = REV_CURRENT;
        srv.lastSyncMs = now;
      }
      if (srv.status === 'online') srv.lastSeenMs = srv.id === 'local' ? now : now - (Math.floor(now / 1000) % 15) * SEC;
    }
  };

  const caddyRunning = (s: MockState, srv: MockServer) => (srv.id === 'local' ? s.status.state === 'running' : srv.status === 'online');
  const caddyVersionOf = (s: MockState, srv: MockServer) =>
    srv.id === 'local' || srv.caddyVersion === SAME_AS_LOCAL
      ? (s.status.version ?? s.binary.installed?.version ?? null)
      : srv.status === 'pending'
        ? null
        : srv.caddyVersion;

  const summary = (s: MockState, srv: MockServer, now: number): ServerSummary => {
    const reachable = srv.status === 'online';
    const seen = srv.status === 'pending' ? null : srv.lastSeenMs;
    const info: ServerInfo | null =
      srv.status === 'pending'
        ? null
        : {
            hostname: srv.hostname,
            fqdn: srv.fqdn,
            os: srv.os,
            isWindows: true,
            architecture: 'X64',
            managerVersion: srv.id === 'n-2b8e4d6f1a09' ? '0.9.3' : '1.0.0',
            caddyVersion: caddyVersionOf(s, srv),
            caddyState: srv.id === 'local' ? s.status.state : reachable ? 'running' : 'unknown',
            caddyStartedAt: srv.id === 'local' ? (s.status.startedAt ?? null) : new Date(srv.caddyStartedMs).toISOString(),
            caddyPlugins: srv.id === 'local' ? (s.binary.installed?.plugins ?? []) : s.binarySettings.plugins,
            processorCount: srv.cpus,
            totalMemoryBytes: srv.memTotal,
            systemUptimeSeconds: Math.floor(((reachable ? now : srv.lastSeenMs) - srv.bootMs) / 1000),
            managerUptimeSeconds: Math.floor(((reachable ? now : srv.lastSeenMs) - srv.managerStartMs) / 1000),
            dataDir: srv.disks.length > 1 ? `${srv.disks[1].name}CaddyProxyManager` : 'C:\\ProgramData\\CaddyProxyManager',
            ipAddresses: srv.ips,
            domain: 'corp.example.com',
            collectedAt: new Date(seen ?? now).toISOString(),
          };
    return {
      id: srv.id,
      name: srv.name,
      isLocal: srv.id === 'local',
      url: srv.url,
      status: srv.status,
      lastSeenAt: seen ? new Date(seen).toISOString() : null,
      lastError: srv.status === 'pending' ? 'Waiting for the node to join (run the join command on it).' : srv.lastError,
      info,
      latest: srv.status === 'pending' ? null : sampleAt(srv, reachable ? now : srv.lastSeenMs, caddyRunning(s, srv)),
      sync:
        srv.id === 'local'
          ? null
          : {
              desiredRevision: REV_CURRENT,
              appliedRevision: srv.appliedRevision ?? null,
              inSync: srv.appliedRevision === REV_CURRENT,
              lastSyncAt: srv.lastSyncMs ? new Date(srv.lastSyncMs).toISOString() : null,
              lastError: srv.syncError ?? null,
              warnings: srv.syncWarnings,
            },
      addedAt: srv.addedAt,
    };
  };

  const requireReachable = (srv: MockServer) => {
    if (srv.status === 'pending') throw new HttpError(409, 'Server has not joined yet', `${srv.name} has not joined the cluster yet. Run the join command on it first.`);
    if (srv.status !== 'online') throw new HttpError(502, 'Server unreachable', `${srv.name} did not answer: ${srv.lastError ?? 'no response'}`);
  };

  const validate = (b: { name?: unknown; url?: unknown }, selfId?: string) => {
    const errors: Record<string, string[]> = {};
    const name = typeof b.name === 'string' ? b.name.trim() : '';
    const url = typeof b.url === 'string' ? b.url.trim().replace(/\/+$/, '') : '';
    if (!name) errors.Name = ['Enter a name for the server.'];
    else if (name.length > 64) errors.Name = ['Use at most 64 characters.'];
    else if (servers.some((x) => x.id !== selfId && x.name.toLowerCase() === name.toLowerCase())) errors.Name = ['Another server already uses this name.'];
    const parsed = URL.canParse(url) ? new URL(url) : null;
    if (!url) errors.Url = ['Enter the address of the node’s management UI, e.g. https://web-proxy02:8443.'];
    else if (!parsed || !/^https?:$/.test(parsed.protocol) || !parsed.hostname) errors.Url = ['Use an http:// or https:// URL of the node’s management UI.'];
    else if (servers.some((x) => x.id !== selfId && x.url?.toLowerCase() === url.toLowerCase())) errors.Url = ['Another server already uses this URL.'];
    if (Object.keys(errors).length) throw new HttpError(400, 'Invalid request', 'One or more fields are invalid.', errors);
    return { name, url, https: parsed?.protocol === 'https:', host: parsed?.hostname ?? '' };
  };

  const jobSnapshot = (j: ServerJob): JobInfo => {
    const elapsed = Date.now() - j.startedMs;
    const n = Math.min(j.steps.length, Math.floor(elapsed / 700) + 1);
    j.log = j.steps.slice(0, n).map((l, i) => `${new Date(j.startedMs + i * 700).toISOString().slice(11, 19)} ${l}`);
    if (n >= j.steps.length && j.state === 'running') {
      j.state = 'succeeded';
      j.finishedAt = iso();
      j.onDone?.();
    }
    const { steps: _s, startedMs: _m, onDone: _o, ...info } = j;
    return info;
  };

  return [
    ['GET', '/api/servers', (_c, s) => {
      role(s);
      const now = Date.now();
      tick(now);
      return ok(servers.map((srv) => summary(s, srv, now)));
    }],

    ['GET', '/api/servers/:id', (c, s) => {
      role(s);
      const now = Date.now();
      tick(now);
      return ok(summary(s, find(c.params.id), now));
    }],

    ['GET', '/api/servers/:id/samples', (c, s) => {
      role(s);
      const srv = find(c.params.id);
      const now = Date.now();
      tick(now);
      if (srv.status === 'pending') return ok([]);
      requireReachable(srv);
      const sinceRaw = c.query.get('since');
      const since = sinceRaw ? Date.parse(sinceRaw) : NaN;
      const from = Number.isFinite(since) ? Math.max(since + 1, now - HISTORY_MS) : now - HISTORY_MS;
      const out: ResourceSample[] = [];
      for (let t = Math.ceil(from / SAMPLE_MS) * SAMPLE_MS; t <= now; t += SAMPLE_MS) out.push(sampleAt(srv, t, caddyRunning(s, srv)));
      return ok(out);
    }],

    ['GET', '/api/servers/:id/traffic', (c, s) => {
      role(s);
      const srv = find(c.params.id);
      requireReachable(srv);
      const r = (c.query.get('range') ?? 'day') as TrafficRange;
      if (!(r in RANGES)) throw new HttpError(400, 'Invalid request', 'range must be hour, day, week or month.', { Range: ['Use hour, day, week or month.'] });
      const host = c.query.get('host')?.trim() || null;
      return ok(trafficReport(s, srv, r, host, Date.now()));
    }],

    ['POST', '/api/servers', (c, s) => {
      requireRole(s, 'admin');
      if (isNode) throw new HttpError(409, 'Managed by the primary', 'This server is a cluster node. Add servers on the primary.');
      const v = validate(c.body as { name?: unknown; url?: unknown });
      const id = `n-${newId()}`;
      const now = Date.now();
      const srv: MockServer = {
        id,
        name: v.name,
        url: v.url,
        seed: 40 + servers.length * 13,
        load: 0.5,
        status: 'pending',
        addedAt: new Date(now).toISOString(),
        lastSeenMs: now,
        joinAtMs: now + 25 * SEC,
        fingerprint: v.https ? fakeFingerprint(v.host) : undefined,
        hostname: v.name.toUpperCase().replace(/[^A-Z0-9-]/g, '-').slice(0, 15),
        fqdn: v.host,
        os: 'Microsoft Windows Server 2025 Standard 10.0.26100',
        ips: [/^\d+\.\d+\.\d+\.\d+$/.test(v.host) ? v.host : '10.0.10.60'],
        cpus: 4,
        memTotal: 8 * GiB,
        disks: [{ name: 'C:\\', label: 'System', totalBytes: 127 * GiB, freeBase: 88 * GiB }],
        caddyVersion: 'v2.11.4',
        caddyStartedMs: now + 25 * SEC,
        bootMs: now - 4 * DAY,
        managerStartMs: now - 4 * DAY,
        syncWarnings: [],
      };
      servers.push(srv);
      audit(s, 'server.add', srv.name, srv.url);
      const result: AddServerResult = { server: summary(s, srv, now), joinToken: makeToken(servers[0].name, id), fingerprint: srv.fingerprint };
      return ok(result);
    }],

    ['PUT', '/api/servers/:id', (c, s) => {
      requireRole(s, 'admin');
      const srv = find(c.params.id);
      if (srv.id === 'local') throw new HttpError(400, 'Invalid request', 'This server’s name is its computer name; it cannot be edited here.');
      const b = c.body as { name?: unknown; url?: unknown; repin?: unknown };
      const v = validate(b, srv.id);
      srv.name = v.name;
      if (srv.url !== v.url) {
        srv.url = v.url;
        srv.fingerprint = v.https ? fakeFingerprint(v.host) : undefined;
      } else if (b.repin === true && v.https) {
        srv.fingerprint = fakeFingerprint(`${v.host}-${Date.now()}`);
      }
      audit(s, 'server.update', srv.name, b.repin === true ? 'Certificate fingerprint re-pinned' : undefined);
      return ok(summary(s, srv, Date.now()));
    }],

    ['POST', '/api/servers/:id/token', (c, s) => {
      requireRole(s, 'admin');
      const srv = find(c.params.id);
      if (srv.id === 'local') throw new HttpError(400, 'Invalid request', 'Join tokens belong to nodes.');
      audit(s, 'server.token', srv.name, 'Join token regenerated (the previous one no longer works)');
      return ok({ joinToken: makeToken(servers[0].name, srv.id) });
    }],

    ['DELETE', '/api/servers/:id', (c, s) => {
      requireRole(s, 'admin');
      const srv = find(c.params.id);
      if (srv.id === 'local') throw new HttpError(400, 'Invalid request', 'This server cannot remove itself.');
      servers.splice(servers.indexOf(srv), 1);
      audit(s, 'server.remove', srv.name);
      return noContent();
    }],

    ['POST', '/api/servers/:id/sync', (c, s) => {
      requireRole(s, 'operator');
      const srv = find(c.params.id);
      if (srv.id === 'local') throw new HttpError(400, 'Invalid request', 'The primary does not sync with itself.');
      const now = Date.now();
      if (srv.status !== 'online') {
        srv.syncError = srv.status === 'pending' ? 'The node has not joined yet.' : `Sync failed: ${srv.lastError ?? 'node unreachable'}`;
        requireReachable(srv);
      }
      srv.appliedRevision = REV_CURRENT;
      srv.lastSyncMs = now;
      srv.syncError = undefined;
      audit(s, 'server.sync', srv.name);
      return ok(summary(s, srv, now));
    }],

    ['POST', '/api/servers/:id/caddy/restart', (c, s) => {
      requireRole(s, 'operator');
      const srv = find(c.params.id);
      audit(s, 'server.caddy.restart', srv.name);
      if (srv.id === 'local') {
        s.status = { ...s.status, state: 'running', startedAt: iso(), lastError: undefined };
        return ok(s.status);
      }
      requireReachable(srv);
      srv.caddyStartedMs = Date.now();
      const status: CaddyStatus = {
        binaryInstalled: true,
        serviceInstalled: true,
        state: 'running',
        processId: 4000 + srv.seed,
        version: caddyVersionOf(s, srv) ?? undefined,
        adminReachable: true,
        startedAt: iso(),
        binaryPath: 'C:\\Program Files\\Caddy Proxy Manager\\caddy\\caddy.exe',
        configPath: 'C:\\ProgramData\\CaddyProxyManager\\caddy\\config.json',
        hostMode: 'windows-service',
      };
      return ok(status);
    }],

    ['POST', '/api/servers/:id/caddy/update', (c, s) => {
      requireRole(s, 'admin');
      const srv = find(c.params.id);
      if (srv.id !== 'local') requireReachable(srv);
      const version = (c.body as { version?: string }).version ?? s.binary.latest?.version ?? 'v2.11.4';
      const job: ServerJob = {
        id: newId(),
        kind: 'caddy-install',
        title: `Update Caddy on ${srv.name} to ${version}`,
        state: 'running',
        startedAt: iso(),
        log: [],
        startedMs: Date.now(),
        steps: [
          `Resolving Caddy ${version} for windows/amd64`,
          `Downloading caddy_${version.replace(/^v/, '')}_windows_amd64.zip`,
          'Verifying SHA-512 checksum',
          'Validating the configuration with the new binary',
          'Stopping the Caddy service',
          'Replacing caddy.exe (previous binary kept for rollback)',
          'Starting the Caddy service',
          `Caddy ${version} is running`,
        ],
      };
      job.onDone = () => {
        if (srv.id !== 'local') {
          srv.caddyVersion = version;
          srv.caddyStartedMs = Date.now();
        }
      };
      jobs.set(job.id, job);
      audit(s, 'server.caddy.update', srv.name, version);
      return ok(jobSnapshot(job));
    }],

    ['GET', '/api/servers/:id/jobs/:jobId', (c, s) => {
      role(s);
      find(c.params.id);
      const job = jobs.get(c.params.jobId);
      if (!job) throw new HttpError(404, 'Job not found');
      return ok(jobSnapshot(job));
    }],
  ];
}
