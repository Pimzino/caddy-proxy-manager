// Vite dev-server plugin that emulates the Caddy Proxy Manager HTTP API with in-memory state.
// Enabled only by `npm run dev:mock` (vite --mode mock, serve). Never part of a production build.
//
// Environment switches: MOCK_SETUP=1 (first-run setup flow), MOCK_ANON=1 (start signed out),
// MOCK_ROLE=viewer|operator (start as a lower role), MOCK_LATENCY=ms (default 180),
// MOCK_CLUSTER_ROLE=standalone|primary|node (default primary; see round3-settings.ts).
import type { IncomingMessage, ServerResponse } from 'node:http';
import type { Plugin } from 'vite';
import type {
  AccessList,
  AccessListInput,
  ApplyResult,
  BackupSettings,
  CaddySettings,
  Certificate,
  CertificateInfo,
  LdapSettings,
  NotificationSettings,
  SiteHost,
  SiteHostFields,
  StreamHost,
  UiSettings,
  UserDto,
} from '../src/api/types.ts';
import {
  accessLog,
  caddyLog,
  catalog,
  createState,
  GPO_SCRIPT,
  INTERNAL_ROOT_PEM,
  iso,
  BASE_MODULES,
  JOB_STEPS,
  managerLog,
  modulesOf,
  newId,
  readinessReport,
  summarize,
  type MockJob,
  type MockState,
} from './fixtures.ts';
import { round3ServersRoutes } from './round3-servers.ts';
import { applyRound3CaddySettings, managedNodeGuard, round3SettingsRoutes, stripRound3Secrets } from './round3-settings.ts';

export class HttpError extends Error {
  readonly status: number;
  readonly title: string;
  readonly detail?: string;
  readonly errors?: Record<string, string[]>;
  constructor(status: number, title: string, detail?: string, errors?: Record<string, string[]>) {
    super(title);
    this.status = status;
    this.title = title;
    this.detail = detail;
    this.errors = errors;
  }
}

export interface Ctx {
  method: string;
  path: string;
  query: URLSearchParams;
  body: unknown;
  raw: Buffer;
  headers: IncomingMessage['headers'];
  params: Record<string, string>;
}

export type Result = { status?: number; json?: unknown; text?: string; contentType?: string; headers?: Record<string, string>; bytes?: Buffer } | undefined;
export type Handler = (ctx: Ctx, s: MockState) => Result | Promise<Result>;
export type MockRoute = [string, string, Handler];
/** Helpers handed to the Round 3 route modules (round3-settings.ts, round3-servers.ts). */
export interface MockHelpers {
  HttpError: typeof HttpError;
  ok: (json: unknown) => Result;
  noContent: () => Result;
}

const ok = (json: unknown): Result => ({ json });
const noContent = (): Result => ({ status: 204 });
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

function readBody(req: IncomingMessage): Promise<Buffer> {
  return new Promise((resolve, reject) => {
    const chunks: Buffer[] = [];
    req.on('data', (c: Buffer) => chunks.push(c));
    req.on('end', () => resolve(Buffer.concat(chunks)));
    req.on('error', reject);
  });
}

/** Minimal multipart/form-data parser (fields and file names only). */
function parseMultipart(raw: Buffer, contentType: string): Record<string, string> {
  const m = /boundary=(?:"([^"]+)"|([^;]+))/i.exec(contentType);
  if (!m) return {};
  const boundary = `--${m[1] ?? m[2]}`;
  const out: Record<string, string> = {};
  for (const part of raw.toString('latin1').split(boundary)) {
    const idx = part.indexOf('\r\n\r\n');
    if (idx < 0) continue;
    const head = part.slice(0, idx);
    const name = /name="([^"]+)"/.exec(head)?.[1];
    if (!name) continue;
    const filename = /filename="([^"]*)"/.exec(head)?.[1];
    out[name] = filename !== undefined ? `file:${filename}` : part.slice(idx + 4).replace(/\r\n$/, '');
  }
  return out;
}

// ---------------------------------------------------------------- helpers

function currentUser(s: MockState): UserDto & { password: string } {
  const u = s.users.find((x) => x.id === s.sessionUserId && !x.disabled);
  if (!u) throw new HttpError(401, 'Not signed in');
  return u;
}

const rank = { viewer: 0, operator: 1, admin: 2 } as const;
function requireRole(s: MockState, role: keyof typeof rank) {
  const u = currentUser(s);
  if (rank[u.role] < rank[role]) throw new HttpError(403, 'Forbidden', `This action requires the ${role} role.`);
  return u;
}

const isAdminSession = (s: MockState) => s.users.find((x) => x.id === s.sessionUserId)?.role === 'admin';

const publicUser = (u: UserDto & { password?: string }): UserDto => {
  const { password: _p, ...rest } = u;
  return rest;
};

function audit(s: MockState, action: string, objectType: string, objectName?: string, details?: string) {
  const u = s.users.find((x) => x.id === s.sessionUserId);
  s.audit.unshift({ id: newId(), createdAt: iso(), updatedAt: iso(), userId: u?.id, userName: u?.name ?? 'system', action, objectType, objectName, details, remoteIp: '127.0.0.1' });
}

function buildConfig(s: MockState): string {
  const cs = s.caddySettings;
  const enabled = s.hosts.filter((h) => h.enabled);
  const routes = enabled.map((h) => {
    const handlers: unknown[] = [];
    if (h.compression && h.kind !== 'redirect') handlers.push({ handler: 'encode', encodings: { gzip: {}, zstd: {} }, prefer: ['zstd', 'gzip'] });
    if (h.hsts && h.tls !== 'none')
      handlers.push({ handler: 'headers', response: { set: { 'Strict-Transport-Security': [`max-age=${h.hstsMaxAgeSeconds}${h.hstsSubdomains ? '; includeSubDomains' : ''}`] } } });
    if (h.kind === 'proxy')
      handlers.push({
        handler: 'reverse_proxy',
        upstreams: h.upstreams.map((u) => ({ dial: `${u.host}:${u.port}` })),
        ...(h.upstreams.length > 1 ? { load_balancing: { selection_policy: { policy: h.loadBalancing.replace(/[A-Z]/g, (c) => `_${c.toLowerCase()}`) } } } : {}),
        ...(h.upstreams.some((u) => u.scheme === 'https') ? { transport: { protocol: 'http', tls: h.upstreamTlsInsecure ? { insecure_skip_verify: true } : {} } } : {}),
        ...(h.healthCheck.enabled ? { health_checks: { active: { uri: h.healthCheck.path, interval: `${h.healthCheck.intervalSeconds}s`, timeout: `${h.healthCheck.timeoutSeconds}s` } } } : {}),
      });
    if (h.kind === 'redirect')
      handlers.push({ handler: 'static_response', status_code: h.redirectCode, headers: { Location: [`${h.redirectTarget}${h.preservePath ? '{http.request.uri}' : ''}`] } });
    if (h.kind === 'static') handlers.push({ handler: 'file_server', root: h.rootPath, ...(h.browse ? { browse: {} } : {}) });
    if (h.kind === 'response')
      handlers.push({ handler: 'static_response', status_code: h.responseStatus, headers: { 'Content-Type': [h.responseContentType] }, body: h.responseBody ?? '' });
    return { match: [{ host: h.domains }], handle: [{ handler: 'subroute', routes: [{ handle: handlers }] }], terminal: true };
  });
  const config = {
    admin: { listen: cs.adminListen, config: { persist: false } },
    storage: { module: 'file_system', root: 'C:\\ProgramData\\CaddyProxyManager\\caddy\\data' },
    logging: { logs: { default: { level: cs.logLevel.toUpperCase(), writer: { output: 'file', filename: 'C:\\ProgramData\\CaddyProxyManager\\logs\\caddy\\caddy.log', roll_size_mb: 20, roll_keep: 10 } } } },
    apps: {
      http: {
        http_port: cs.httpPort,
        https_port: cs.httpsPort,
        servers: {
          srv0: {
            listen: [`:${cs.httpsPort}`],
            protocols: cs.enableHttp3 ? ['h1', 'h2', 'h3'] : ['h1', 'h2'],
            ...(cs.tlsConnectionPolicyJson ? { tls_connection_policies: [{ ...(JSON.parse(cs.tlsConnectionPolicyJson) as object) }, {}] } : {}),
            routes: [...routes, { handle: [{ handler: 'static_response', status_code: 404 }] }],
            automatic_https: { skip: enabled.filter((h) => h.tls === 'none').flatMap((h) => h.domains) },
          },
        },
      },
      tls: {
        automation: {
          policies: [
            { subjects: enabled.filter((h) => h.tls === 'internal').flatMap((h) => h.domains), issuers: [{ module: 'internal' }] },
            { issuers: [{ module: 'acme', email: cs.acmeEmail, ...(cs.acmeIssuerJson ? redactIssuer(JSON.parse(cs.acmeIssuerJson) as Record<string, unknown>, isAdminSession(s)) : {}) }] },
          ],
        },
        certificates: {
          load_files: s.certificates.map((c) => ({ certificate: c.certPath, key: c.keyPath, tags: [`cpm-${c.id}`] })),
        },
      },
      ...(cs.extraAppsJson ? (JSON.parse(cs.extraAppsJson) as object) : {}),
      ...(s.streams.some((x) => x.enabled) && s.binary.installed?.plugins.includes('github.com/mholt/caddy-l4')
        ? {
            layer4: {
              servers: Object.fromEntries(
                s.streams
                  .filter((x) => x.enabled)
                  .map((x) => [x.id, { listen: [`${x.protocol}/:${x.listenPort}`], routes: [{ handle: [{ handler: 'proxy', upstreams: [{ dial: [`${x.protocol}/${x.upstreamHost}:${x.upstreamPort}`] }] }] }] }]),
              ),
            },
          }
        : {}),
    },
  };
  return JSON.stringify(config, null, 2);
}

/** Viewers/operators see DNS provider secrets as "***" (SPEC round 2: redacted configs). */
function redactIssuer(issuer: Record<string, unknown>, admin: boolean): Record<string, unknown> {
  if (admin) return issuer;
  return JSON.parse(JSON.stringify(issuer, (k, v: unknown) => (/token|secret|key|password/i.test(k) && typeof v === 'string' ? '***' : v))) as Record<string, unknown>;
}

const KNOWN_HANDLERS = ['subroute', 'reverse_proxy', 'static_response', 'file_server', 'headers', 'encode', 'rewrite', 'authentication', 'error', 'vars', 'map', 'request_body', 'templates', 'abort', 'copy_response', 'push', 'tracing', 'metrics', 'intercept', 'invoke', 'log_append', 'rate_limit'];

/** Emulates POST /load: validates advanced routes like Caddy would, then "loads" the config. */
function apply(s: MockState, reason: string): ApplyResult {
  for (const h of s.hosts.filter((x) => x.enabled && x.advancedRoutesJson)) {
    const routes = JSON.parse(h.advancedRoutesJson as string) as { handle?: { handler?: string }[] }[];
    for (const [ri, r] of routes.entries())
      for (const [hi, hd] of (r.handle ?? []).entries())
        if (hd.handler && !KNOWN_HANDLERS.includes(hd.handler)) {
          const error = `loading new config: loading http app module: provision http: server srv0: setting up route handlers: route ${s.hosts.indexOf(h)}: loading handler modules: position 0: loading module 'subroute': provision http.handlers.subroute: setting up subroutes: route ${ri}: loading handler modules: position ${hi}: loading module '${hd.handler}': unknown module: http.handlers.${hd.handler}`;
          s.revisions.unshift({ id: newId(), createdAt: iso(), updatedAt: iso(), reason, appliedBy: currentUser(s).name, success: false, error, hash: 'sha256:rejected', json: buildConfig(s) });
          s.events.unshift({ id: newId(), createdAt: iso(), updatedAt: iso(), severity: 'error', category: 'config', message: `Caddy rejected the configuration (${reason})`, details: error, key: 'config-failure', notified: true });
          throw new HttpError(422, 'Caddy rejected the configuration', error);
        }
  }
  const json = buildConfig(s);
  const running = s.status.state === 'running';
  const warnings: string[] = [];
  if (s.streams.some((x) => x.enabled) && !s.binary.installed?.plugins.includes('github.com/mholt/caddy-l4'))
    warnings.push('Streams were skipped: the installed Caddy binary does not include the layer4 plugin (github.com/mholt/caddy-l4).');
  const id = newId();
  s.revisions.unshift({ id, createdAt: iso(), updatedAt: iso(), reason, appliedBy: s.users.find((u) => u.id === s.sessionUserId)?.name ?? 'system', success: true, hash: `sha256:${Math.abs(hash(json)).toString(16)}`, json });
  s.revisions = s.revisions.slice(0, 100);
  if (running) s.runningJson = json;
  else warnings.unshift('Caddy is not running. The configuration was validated and written to caddy.json only.');
  audit(s, 'applied', 'caddy', 'configuration', reason);
  return { success: true, revisionId: id, writtenOnly: !running, warnings };
}

function hash(str: string) {
  let h = 0;
  for (let i = 0; i < str.length; i++) h = (Math.imul(31, h) + str.charCodeAt(i)) | 0;
  return h;
}

/** Applies and rolls the state back when Caddy rejects the change (transactional endpoints). */
function transactional<T>(s: MockState, reason: string, mutate: () => T): { item: T; apply: ApplyResult } {
  const snapshot = JSON.stringify({ hosts: s.hosts, streams: s.streams, accessLists: s.accessLists, certificates: s.certificates, caddySettings: s.caddySettings });
  const item = mutate();
  try {
    return { item, apply: apply(s, reason) };
  } catch (err) {
    Object.assign(s, JSON.parse(snapshot));
    throw err;
  }
}

const HOSTNAME = /^(\*\.)?([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)*[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$/i;

const FORBIDDEN_ROOTS = [/^[a-z]:\\?$/i, /^c:\\windows(\\|$)/i, /^c:\\program files( \(x86\))?(\\|$)/i, /^c:\\programdata\\caddyproxymanager(\\|$)/i];

function validateHost(s: MockState, h: SiteHostFields, id?: string) {
  const errors: Record<string, string[]> = {};
  const add = (k: string, m: string) => (errors[k] ??= []).push(m);
  const me = s.users.find((x) => x.id === s.sessionUserId);
  const before = id ? s.hosts.find((x) => x.id === id) : undefined;
  if (me?.role !== 'admin' && (h.advancedRoutesJson ?? null) !== (before?.advancedRoutesJson ?? null))
    throw new HttpError(403, 'Forbidden', 'Only administrators can change custom Caddy routes (advancedRoutesJson).');
  if (h.kind === 'static' && h.rootPath) {
    const root = h.rootPath.trim().replace(/\//g, '\\');
    if (FORBIDDEN_ROOTS.some((re) => re.test(root)))
      add('RootPath', `Serving ${root} is not allowed: drive roots, the Windows and Program Files folders and the manager's data folder (including Caddy storage and the certificate store) are protected.`);
    else if (root.startsWith('\\\\') && me?.role !== 'admin' && before?.rootPath !== h.rootPath) add('RootPath', 'Only administrators can serve files from a UNC path.');
  }
  (h.upstreams ?? []).forEach((u, i) => {
    if (['127.0.0.1', 'localhost', '::1'].includes(u.host.toLowerCase()) && [2019, s.uiSettings.port, s.uiSettings.httpsPort].includes(u.port))
      add(`Upstreams[${i}].Port`, `${u.host}:${u.port} is the Caddy admin endpoint or the management UI and cannot be used as an upstream.`);
  });
  if (!h.domains?.length) add('Domains', 'At least one domain is required.');
  for (const d of h.domains ?? []) if (!HOSTNAME.test(d)) add('Domains', `'${d}' is not a valid host name.`);
  if (h.kind === 'proxy' && !h.upstreams?.length) add('Upstreams', 'At least one upstream is required.');
  if (h.kind === 'redirect' && !/^https?:\/\//.test(h.redirectTarget ?? '')) add('RedirectTarget', 'Target must be an absolute http(s) URL.');
  if (h.kind === 'static' && !h.rootPath) add('RootPath', 'Root path is required.');
  if (h.tls === 'custom' && !s.certificates.some((c) => c.id === h.certificateId)) add('CertificateId', 'Certificate not found.');
  if (h.tls === 'acme' && h.acmeChallenge === 'dns' && !s.caddySettings.dnsProvider)
    add('AcmeChallenge', 'The DNS challenge needs a DNS provider. Configure one in Settings › Caddy first.');
  if (h.advancedRoutesJson) {
    try {
      if (!Array.isArray(JSON.parse(h.advancedRoutesJson))) add('AdvancedRoutesJson', 'Must be a JSON array.');
    } catch (e) {
      add('AdvancedRoutesJson', `Invalid JSON: ${(e as Error).message}`);
    }
  }
  if (Object.keys(errors).length) throw new HttpError(400, 'Invalid request', 'One or more fields are invalid.', errors);
  if (h.enabled)
    for (const d of h.domains) {
      const other = s.hosts.find((o) => o.id !== id && o.enabled && o.domains.some((x) => x.toLowerCase() === d.toLowerCase()));
      if (other) throw new HttpError(409, 'Conflict', `The domain ${d} is already used by the enabled host "${other.domains[0]}".`);
    }
}

function certInfos(s: MockState): CertificateInfo[] {
  const custom: CertificateInfo[] = s.certificates.map((c) => ({
    id: c.id,
    kind: 'custom',
    name: c.name,
    subjects: c.subjects,
    issuer: c.issuer,
    notBefore: c.notBefore,
    notAfter: c.notAfter,
    daysRemaining: Math.floor((Date.parse(c.notAfter) - Date.now()) / 86_400_000),
    certPath: c.certPath,
    keyPath: c.keyPath,
    source: c.source,
    notes: c.notes,
    error: c.lastSyncError,
    usedByHostIds: s.hosts.filter((h) => h.tls === 'custom' && h.certificateId === c.id).map((h) => h.id),
  }));
  return [...custom, ...s.managedCerts];
}

/** Creates a certificate transactionally (shared by the path-based sources). */
function addCert(s: MockState, patch: Partial<Certificate> & Pick<Certificate, 'name' | 'source' | 'subjects'>) {
  const id = newId();
  const res = transactional(s, `Certificate added: ${patch.name}`, () => {
    const cert: Certificate = {
      id,
      certPath: `C:\\ProgramData\\CaddyProxyManager\\certificates\\${id}\\fullchain.pem`,
      keyPath: `C:\\ProgramData\\CaddyProxyManager\\certificates\\${id}\\privkey.pem`,
      issuer: 'CN=Example Corp Issuing CA 01',
      notBefore: iso(),
      notAfter: iso(-365 * 86_400_000),
      thumbprint: 'ABCDEF0123456789ABCDEF0123456789ABCDEF01',
      createdAt: iso(),
      updatedAt: iso(),
      ...patch,
    };
    s.certificates.push(cert);
    return cert;
  });
  audit(s, 'created', 'certificate', patch.name, `Source: ${patch.source}`);
  return res;
}

type BinarySettingsBody = MockState['binarySettings'];

/** GET /api/settings/caddy: secrets never returned; raw config values hidden from non-admins (SPEC round 2). */
function caddySettingsOut(s: MockState) {
  const out = stripRound3Secrets(stripSecret(stripSecret(s.caddySettings, 'eabMacKey'), 'acmeIssuerJson')) as Partial<CaddySettings>;
  if (!isAdminSession(s)) {
    delete out.rawCaddyfile;
    delete out.serverOptionsJson;
    delete out.extraAppsJson;
  }
  return out;
}

/** Very small Caddyfile → host drafts converter for the mock (site blocks with one main directive). */
function importCaddyfile(text: string) {
  const drafts: SiteHostFields[] = [];
  const unmapped: string[] = [];
  const warnings: string[] = [];
  const blocks = [...text.matchAll(/^([^\s#{][^{\n]*)\{([\s\S]*?)^\}/gm)];
  const globalOpts = /^\s*\{[\s\S]*?^\}/m.exec(text);
  if (globalOpts && text.trimStart().startsWith('{')) unmapped.push(globalOpts[0].trim());
  for (const m of blocks) {
    const domains = m[1].trim().split(/[\s,]+/).map((d) => d.replace(/^https?:\/\//, '').replace(/:\d+$/, '')).filter(Boolean);
    const body = m[2];
    const base = { ...BLANK_HOST, domains };
    const lines = body.split('\n').map((l) => l.trim()).filter((l) => l && !l.startsWith('#'));
    const rp = lines.find((l) => l.startsWith('reverse_proxy'));
    const redir = lines.find((l) => l.startsWith('redir'));
    const fs = lines.find((l) => l.startsWith('file_server'));
    const respond = lines.find((l) => l.startsWith('respond'));
    const root = lines.find((l) => l.startsWith('root'));
    const known = [rp, redir, fs, respond, root].filter(Boolean);
    const extra = lines.filter((l) => !known.includes(l) && !l.startsWith('encode') && l !== '}' && !/^tls internal$/.test(l));
    if (lines.some((l) => l === 'tls internal')) base.tls = 'internal';
    if (rp) {
      const ups = rp.split(/\s+/).slice(1).filter((x) => x !== '{');
      drafts.push({
        ...base,
        kind: 'proxy',
        upstreams: ups.map((u) => {
          const https = u.startsWith('https://');
          const hp = u.replace(/^https?:\/\//, '');
          const [host, port] = hp.includes(':') ? [hp.slice(0, hp.lastIndexOf(':')), Number(hp.slice(hp.lastIndexOf(':') + 1))] : [hp, https ? 443 : 80];
          return { scheme: https ? 'https' : 'http', host, port } as const;
        }),
      });
    } else if (redir) {
      const [, target = '', code = 'permanent'] = redir.split(/\s+/);
      drafts.push({ ...base, kind: 'redirect', redirectTarget: target.replace('{uri}', ''), preservePath: target.includes('{uri}'), redirectCode: code === 'permanent' ? 301 : code === 'temporary' ? 302 : Number(code) || 302 });
    } else if (fs) {
      drafts.push({ ...base, kind: 'static', rootPath: root?.split(/\s+/).pop() ?? '', browse: fs.includes('browse') });
    } else if (respond) {
      const mm = /respond\s+"([^"]*)"\s*(\d{3})?/.exec(respond);
      drafts.push({ ...base, kind: 'response', responseBody: mm?.[1] ?? '', responseStatus: Number(mm?.[2] ?? 200) });
    } else {
      unmapped.push(m[0].trim());
      continue;
    }
    if (extra.length) {
      warnings.push(`${domains[0]}: ${extra.length} directive(s) not converted (${extra.map((l) => l.split(/\s+/)[0]).join(', ')}).`);
      unmapped.push(`${m[1].trim()} {\n\t${extra.join('\n\t')}\n}`);
    }
  }
  if (drafts.length === 0 && unmapped.length === 0) warnings.push('No site blocks were found. Each site must look like: example.com { … }');
  return { drafts, unmapped, warnings };
}

const BLANK_HOST: SiteHostFields = {
  kind: 'proxy',
  enabled: true,
  domains: [],
  acmeChallenge: 'default',
  dnsDelegation: 'default',
  tls: 'acme',
  forceHttps: true,
  hsts: false,
  hstsSubdomains: false,
  hstsMaxAgeSeconds: 31536000,
  compression: true,
  blockExploits: false,
  accessLog: false,
  responseHeaders: [],
  upstreams: [],
  loadBalancing: 'roundRobin',
  healthCheck: { enabled: false, path: '/', intervalSeconds: 30, timeoutSeconds: 5, expectStatus: 0 },
  upstreamTlsInsecure: false,
  upstreamNtlm: false,
  requestHeaders: [],
  locations: [],
  redirectCode: 301,
  preservePath: true,
  browse: false,
  spaFallback: false,
  responseStatus: 200,
  responseContentType: 'text/plain; charset=utf-8',
};

const accessListOut = (s: MockState, l: MockState['accessLists'][number]): AccessList => ({
  ...l,
  users: l.users.map((u) => ({ username: u.username, hasPassword: true })),
  usedBy: s.hosts.filter((h) => h.accessListId === l.id).length,
});

function secret<T extends object>(target: T, key: string, value: unknown, hasKey: string) {
  const t = target as Record<string, unknown>;
  if (value === undefined || value === null) return;
  if (value === '') {
    delete t[key];
    t[hasKey] = false;
  } else {
    t[key] = value;
    t[hasKey] = true;
  }
}

function stripSecret<T extends object>(o: T, key: string): T {
  const copy = { ...o } as Record<string, unknown>;
  delete copy[key];
  return copy as T;
}

function startJob(s: MockState, kind: string, title: string, steps: string[], onDone: () => void): MockJob {
  const job: MockJob = { id: newId(), kind, title, state: 'running', startedAt: iso(), log: [], script: steps, startedMs: Date.now(), durationMs: steps.length * 650, onDone };
  s.jobs.unshift(job);
  return job;
}

function jobSnapshot(j: MockJob) {
  const elapsed = Date.now() - j.startedMs;
  const n = Math.min(j.script.length, Math.floor(elapsed / 650) + 1);
  const stamp = (i: number) => new Date(j.startedMs + i * 650).toISOString().slice(11, 19);
  j.log = j.script.slice(0, n).map((l, i) => `${stamp(i)} ${l}`);
  if (elapsed >= j.durationMs && j.state === 'running') {
    j.state = 'succeeded';
    j.finishedAt = iso();
    j.onDone?.();
  }
  const { script: _s, startedMs: _m, durationMs: _d, onDone: _o, fail: _f, ...info } = j;
  return info;
}

// ---------------------------------------------------------------- routes

const routes: [string, string, Handler][] = [
  ['GET', '/api/health', (_c, s) => {
    if (Date.now() < s.restartingUntil) throw new HttpError(503, 'Service unavailable', 'Restarting');
    return ok({ status: 'ok', version: '1.0.0', product: 'Caddy Proxy Manager' });
  }],

  // ---- setup & auth
  ['GET', '/api/setup/status', (_c, s) => ok({ needsSetup: s.needsSetup, setupTokenPath: 'C:\\ProgramData\\CaddyProxyManager\\setup-token.txt' })],
  ['POST', '/api/setup', (c, s) => {
    if (!s.needsSetup) throw new HttpError(403, 'Already set up');
    const b = c.body as { token: string; email: string; name: string; password: string };
    if (b.token !== 'mock-token') throw new HttpError(400, 'Invalid setup token', 'The setup token is not valid. (Mock token: mock-token)');
    const u = { id: newId(), email: b.email, name: b.name, role: 'admin' as const, disabled: false, createdAt: iso(), lastLoginAt: iso(), password: b.password };
    s.users = [u, ...s.users.filter((x) => x.email !== b.email)];
    s.needsSetup = false;
    s.sessionUserId = u.id;
    return ok(publicUser(u));
  }],
  ['POST', '/api/auth/login', (c, s) => {
    const b = c.body as { email: string; password: string };
    let u = s.users.find((x) => x.email.toLowerCase() === (b.email ?? '').toLowerCase() && !x.disabled);
    // Directory sign-in (mock): "CORP\\jdoe", "jdoe" or "jdoe@corp.example.com"; "nogroup" is in no mapped group.
    if (!u && s.ldapSettings.enabled && b.password && b.password !== 'wrong') {
      const name = (b.email ?? '').replace(/^.*\\/, '').replace(/@.*$/, '').toLowerCase();
      if (name === 'nogroup') throw new HttpError(403, 'Not authorised', `The directory account ${b.email} is not a member of a group mapped to a role in Caddy Proxy Manager.`);
      if (/^[a-z][a-z0-9._-]{1,30}$/.test(name)) {
        const email = `${name}@corp.example.com`;
        u = s.users.find((x) => x.email === email);
        if (u?.disabled) throw new HttpError(401, 'Invalid credentials');
        if (!u) {
          u = { id: newId(), email, name: name.replace(/(^|[._-])(\w)/g, (_m, sep: string, ch: string) => (sep ? ' ' : '') + ch.toUpperCase()), role: 'viewer', disabled: false, createdAt: iso(), password: '', externalSource: 'ldap' };
          s.users.push(u);
          audit(s, 'provisioned', 'user', u.name, 'Directory account created at first sign-in');
        }
      }
    }
    if (!u || !b.password || b.password === 'wrong') throw new HttpError(401, 'Invalid credentials');
    s.sessionUserId = u.id;
    u.lastLoginAt = iso();
    audit(s, 'login', 'user', u.name);
    return ok(publicUser(u));
  }],
  ['POST', '/api/auth/logout', (_c, s) => {
    s.sessionUserId = null;
    return noContent();
  }],
  ['GET', '/api/auth/me', (_c, s) => ok(publicUser(currentUser(s)))],
  ['POST', '/api/auth/change-password', (c, s) => {
    const u = currentUser(s);
    const b = c.body as { currentPassword: string; newPassword: string };
    if (b.currentPassword === 'wrong') throw new HttpError(400, 'Invalid request', 'The current password is incorrect.', { CurrentPassword: ['The current password is incorrect.'] });
    if ((b.newPassword ?? '').length < 12) throw new HttpError(400, 'Invalid request', 'Password too short.', { NewPassword: ['Use at least 12 characters.'] });
    u.password = b.newPassword;
    audit(s, 'changed password', 'user', u.name);
    return noContent();
  }],

  // ---- users / audit / events
  ['GET', '/api/users', (_c, s) => {
    requireRole(s, 'admin');
    return ok(s.users.map(publicUser));
  }],
  ['POST', '/api/users', (c, s) => {
    requireRole(s, 'admin');
    const b = c.body as UserDto & { password: string };
    if (s.users.some((u) => u.email.toLowerCase() === b.email.toLowerCase())) throw new HttpError(409, 'Conflict', `A user with e-mail ${b.email} already exists.`);
    const u = { id: newId(), email: b.email, name: b.name, role: b.role, disabled: false, createdAt: iso(), password: b.password };
    s.users.push(u);
    audit(s, 'created', 'user', u.name);
    return ok(publicUser(u));
  }],
  ['PUT', '/api/users/:id', (c, s) => {
    const me = requireRole(s, 'admin');
    const u = s.users.find((x) => x.id === c.params.id);
    if (!u) throw new HttpError(404, 'Not found', 'User was not found.');
    const b = c.body as UserDto & { password?: string };
    if (u.id === me.id && (b.role !== u.role || b.disabled)) throw new HttpError(400, 'Invalid request', 'You cannot demote or disable yourself.');
    if (u.externalSource && b.password) throw new HttpError(400, 'Invalid request', 'Directory accounts have no local password.', { Password: ['Directory accounts sign in with their directory password.'] });
    Object.assign(u, { email: b.email, name: b.name, role: b.role, disabled: b.disabled }, b.password ? { password: b.password } : {});
    audit(s, 'updated', 'user', u.name);
    return ok(publicUser(u));
  }],
  ['DELETE', '/api/users/:id', (c, s) => {
    const me = requireRole(s, 'admin');
    if (c.params.id === me.id) throw new HttpError(400, 'Invalid request', 'You cannot delete yourself.');
    const u = s.users.find((x) => x.id === c.params.id);
    s.users = s.users.filter((x) => x.id !== c.params.id);
    audit(s, 'deleted', 'user', u?.name);
    return noContent();
  }],
  ['GET', '/api/audit', (c, s) => {
    requireRole(s, 'admin');
    const q = (c.query.get('q') ?? '').toLowerCase();
    const skip = Number(c.query.get('skip') ?? 0);
    const take = Number(c.query.get('take') ?? 50);
    const items = s.audit.filter((a) => !q || [a.userName, a.action, a.objectType, a.objectName, a.details].join(' ').toLowerCase().includes(q));
    return ok({ items: items.slice(skip, skip + take), total: items.length });
  }],
  ['GET', '/api/events', (c, s) => {
    currentUser(s);
    const sev = c.query.get('severity');
    const skip = Number(c.query.get('skip') ?? 0);
    const take = Number(c.query.get('take') ?? 50);
    const items = s.events.filter((e) => !sev || e.severity === sev);
    return ok({ items: items.slice(skip, skip + take), total: items.length });
  }],

  // ---- dashboard & system
  ['GET', '/api/dashboard', (_c, s) => {
    currentUser(s);
    const certs = certInfos(s);
    const count = (k: string) => s.hosts.filter((h) => h.kind === k).length;
    return ok({
      caddy: s.status,
      binary: s.binary,
      counts: {
        proxy: count('proxy'),
        redirect: count('redirect'),
        static: count('static'),
        response: count('response'),
        streams: s.streams.length,
        accessLists: s.accessLists.length,
        certificates: certs.filter((c) => c.kind !== 'internalRoot').length,
        certificatesExpiring: certs.filter((c) => c.kind !== 'internalRoot' && c.daysRemaining <= s.notificationSettings.certificateExpiryDays).length,
        hostsDisabled: s.hosts.filter((h) => !h.enabled).length,
      },
      readiness: s.readiness ? { ranAt: s.readiness.ranAt, pass: s.readiness.pass, warn: s.readiness.warn, fail: s.readiness.fail } : null,
      upstreams: { total: 7, unhealthy: s.status.state === 'running' ? 1 : 0 },
      recentEvents: s.events.slice(0, 10),
      system: { hostname: 'WEB-PROXY01', os: 'Microsoft Windows Server 2025 Standard 10.0.26100', managerVersion: '1.0.0', uptimeSeconds: Math.floor((Date.now() - s.startedAt) / 1000), dataDir: 'C:\\ProgramData\\CaddyProxyManager' },
    });
  }],
  ['GET', '/api/system/info', (_c, s) => {
    currentUser(s);
    return ok({ version: '1.0.0', product: 'Caddy Proxy Manager', hostMode: 'windows-service', dataDir: 'C:\\ProgramData\\CaddyProxyManager', installDir: 'C:\\Program Files\\Caddy Proxy Manager', os: 'Microsoft Windows Server 2025 Standard 10.0.26100', machineName: 'WEB-PROXY01', uptimeSeconds: Math.floor((Date.now() - s.startedAt) / 1000), isService: true });
  }],
  ['POST', '/api/system/restart', (_c, s) => {
    requireRole(s, 'admin');
    s.restartingUntil = Date.now() + 4000;
    return { status: 202, json: {} };
  }],

  // ---- hosts
  ['GET', '/api/hosts', (c, s) => {
    currentUser(s);
    const kind = c.query.get('kind');
    return ok(s.hosts.filter((h) => !kind || h.kind === kind));
  }],
  ['GET', '/api/hosts/:id', (c, s) => {
    currentUser(s);
    const h = s.hosts.find((x) => x.id === c.params.id);
    if (!h) throw new HttpError(404, 'Not found', 'Host was not found.');
    return ok(h);
  }],
  ['POST', '/api/hosts', (c, s) => {
    requireRole(s, 'operator');
    const b = c.body as SiteHostFields;
    validateHost(s, b);
    const res = transactional(s, `Host created: ${b.domains[0]}`, () => {
      const h = { ...b, id: newId(), createdAt: iso(), updatedAt: iso() } as SiteHost;
      s.hosts.push(h);
      return h;
    });
    audit(s, 'created', 'host', b.domains[0]);
    return ok(res);
  }],
  ['PUT', '/api/hosts/:id', (c, s) => {
    requireRole(s, 'operator');
    const b = c.body as SiteHostFields;
    const existing = s.hosts.find((x) => x.id === c.params.id);
    if (!existing) throw new HttpError(404, 'Not found', 'Host was not found.');
    validateHost(s, b, existing.id);
    const res = transactional(s, `Host updated: ${b.domains[0]}`, () => {
      const h = { ...existing, ...b, id: existing.id, createdAt: existing.createdAt, updatedAt: iso() } as SiteHost;
      s.hosts = s.hosts.map((x) => (x.id === h.id ? h : x));
      return h;
    });
    audit(s, 'updated', 'host', b.domains[0]);
    return ok(res);
  }],
  ['DELETE', '/api/hosts/:id', (c, s) => {
    requireRole(s, 'operator');
    const h = s.hosts.find((x) => x.id === c.params.id);
    if (!h) throw new HttpError(404, 'Not found', 'Host was not found.');
    const res = transactional(s, `Host deleted: ${h.domains[0]}`, () => {
      s.hosts = s.hosts.filter((x) => x.id !== h.id);
    });
    audit(s, 'deleted', 'host', h.domains[0]);
    return ok({ apply: res.apply });
  }],
  ['POST', '/api/hosts/:id/:action', (c, s) => {
    requireRole(s, 'operator');
    const h = s.hosts.find((x) => x.id === c.params.id);
    if (!h) throw new HttpError(404, 'Not found', 'Host was not found.');
    const enable = c.params.action === 'enable';
    if (enable) validateHost(s, { ...h, enabled: true }, h.id);
    const res = transactional(s, `Host ${enable ? 'enabled' : 'disabled'}: ${h.domains[0]}`, () => {
      h.enabled = enable;
      h.updatedAt = iso();
      return h;
    });
    audit(s, enable ? 'enabled' : 'disabled', 'host', h.domains[0]);
    return ok(res);
  }],

  // ---- streams
  ['GET', '/api/streams/support', (_c, s) => {
    currentUser(s);
    return ok({ supported: !!s.binary.installed?.plugins.includes('github.com/mholt/caddy-l4'), plugin: 'github.com/mholt/caddy-l4' });
  }],
  ['GET', '/api/streams', (_c, s) => {
    currentUser(s);
    return ok(s.streams);
  }],
  ['POST', '/api/streams', (c, s) => {
    requireRole(s, 'operator');
    const b = c.body as StreamHost;
    const res = transactional(s, `Stream created: ${b.protocol}/${b.listenPort}`, () => {
      const st = { ...b, id: newId(), createdAt: iso(), updatedAt: iso() };
      s.streams.push(st);
      return st;
    });
    return ok(res);
  }],
  ['PUT', '/api/streams/:id', (c, s) => {
    requireRole(s, 'operator');
    const b = c.body as StreamHost;
    const res = transactional(s, `Stream updated: ${b.protocol}/${b.listenPort}`, () => {
      const st = { ...s.streams.find((x) => x.id === c.params.id), ...b, id: c.params.id, updatedAt: iso() } as StreamHost;
      s.streams = s.streams.map((x) => (x.id === st.id ? st : x));
      return st;
    });
    return ok(res);
  }],
  ['DELETE', '/api/streams/:id', (c, s) => {
    requireRole(s, 'operator');
    const res = transactional(s, 'Stream deleted', () => {
      s.streams = s.streams.filter((x) => x.id !== c.params.id);
    });
    return ok({ apply: res.apply });
  }],

  // ---- access lists
  ['GET', '/api/access-lists', (_c, s) => {
    currentUser(s);
    return ok(s.accessLists.map((l) => accessListOut(s, l)));
  }],
  ['GET', '/api/access-lists/:id', (c, s) => {
    currentUser(s);
    const l = s.accessLists.find((x) => x.id === c.params.id);
    if (!l) throw new HttpError(404, 'Not found', 'Access list was not found.');
    return ok(accessListOut(s, l));
  }],
  ['POST', '/api/access-lists', (c, s) => {
    requireRole(s, 'operator');
    const b = c.body as AccessListInput;
    const res = transactional(s, `Access list created: ${b.name}`, () => {
      const l = { id: newId(), createdAt: iso(), updatedAt: iso(), name: b.name, satisfyAny: b.satisfyAny, passAuthToUpstream: b.passAuthToUpstream, rules: b.rules, users: b.users.map((u) => ({ username: u.username, passwordHash: '$2a$12$mock' })) };
      s.accessLists.push(l);
      return accessListOut(s, l);
    });
    audit(s, 'created', 'accessList', b.name);
    return ok(res);
  }],
  ['PUT', '/api/access-lists/:id', (c, s) => {
    requireRole(s, 'operator');
    const b = c.body as AccessListInput;
    const l = s.accessLists.find((x) => x.id === c.params.id);
    if (!l) throw new HttpError(404, 'Not found', 'Access list was not found.');
    for (const u of b.users) if (!u.password && !l.users.some((x) => x.username === u.username)) throw new HttpError(400, 'Invalid request', `User ${u.username} needs a password.`, { Users: [`User ${u.username} needs a password.`] });
    const res = transactional(s, `Access list updated: ${b.name}`, () => {
      Object.assign(l, { name: b.name, satisfyAny: b.satisfyAny, passAuthToUpstream: b.passAuthToUpstream, rules: b.rules, updatedAt: iso(), users: b.users.map((u) => l.users.find((x) => x.username === u.username && !u.password) ?? { username: u.username, passwordHash: '$2a$12$mock' }) });
      return accessListOut(s, l);
    });
    audit(s, 'updated', 'accessList', b.name);
    return ok(res);
  }],
  ['DELETE', '/api/access-lists/:id', (c, s) => {
    requireRole(s, 'operator');
    const l = s.accessLists.find((x) => x.id === c.params.id);
    const used = s.hosts.filter((h) => h.accessListId === c.params.id);
    if (used.length) throw new HttpError(409, 'Conflict', `The access list is used by ${used.map((h) => h.domains[0]).join(', ')}.`);
    const res = transactional(s, `Access list deleted: ${l?.name}`, () => {
      s.accessLists = s.accessLists.filter((x) => x.id !== c.params.id);
    });
    return ok({ apply: res.apply });
  }],

  // ---- certificates
  ['GET', '/api/certificates', (_c, s) => {
    currentUser(s);
    return ok(certInfos(s));
  }],
  ['GET', '/api/certificates/internal-root', (_c, s) => {
    currentUser(s);
    return { text: INTERNAL_ROOT_PEM, contentType: 'application/x-pem-file', headers: { 'Content-Disposition': 'attachment; filename="caddy-local-root.crt"' } };
  }],
  ['GET', '/api/certificates/windows-store', (c, s) => {
    requireRole(s, 'admin');
    const key = `${c.query.get('location') ?? 'LocalMachine'}/${c.query.get('store') ?? 'My'}`;
    return ok(s.windowsStore[key] ?? []);
  }],
  ['POST', '/api/certificates/pfx-path', (c, s) => {
    requireRole(s, 'admin');
    const b = c.body as { name?: string; pfxPath: string; pfxPassword?: string };
    const path = (b.pfxPath ?? '').trim();
    if (!/\.(pfx|p12)$/i.test(path)) throw new HttpError(400, 'Invalid request', 'Only .pfx and .p12 files can be referenced.', { PfxPath: ['Only .pfx and .p12 files can be referenced.'] });
    if (/programdata\\caddyproxymanager/i.test(path)) throw new HttpError(400, 'Invalid request', 'Files inside the manager data folder cannot be referenced.', { PfxPath: ['Files inside the manager data folder cannot be referenced.'] });
    if (/missing/i.test(path)) throw new HttpError(400, 'Invalid request', `Cannot read ${path}.`, { PfxPath: [`Cannot read ${path}. Check the path and that this computer's account has read access.`] });
    if (b.pfxPassword === 'wrong') throw new HttpError(400, 'Invalid request', 'The PFX password is incorrect.', { PfxPassword: ['The PFX password is incorrect or the file is damaged.'] });
    const file = path.split(/[\\/]/).pop() ?? path;
    const subject = file.replace(/(-chain)?\.(pfx|p12)$/i, '');
    return ok(addCert(s, { name: b.name?.trim() || subject, source: 'pfxFile', sourcePath: path, subjects: [subject], lastSyncedAt: iso() }));
  }],
  ['POST', '/api/certificates/windows-store', (c, s) => {
    requireRole(s, 'admin');
    const b = c.body as { name?: string; storeLocation?: string; storeName?: string; thumbprint?: string; subject?: string };
    if (!!b.thumbprint === !!b.subject) throw new HttpError(400, 'Invalid request', 'Specify exactly one of thumbprint or subject.', { Thumbprint: ['Specify exactly one of thumbprint or subject.'] });
    const location = b.storeLocation ?? 'LocalMachine';
    const storeName = b.storeName ?? 'My';
    const list = s.windowsStore[`${location}/${storeName}`] ?? [];
    const now = Date.now();
    const match = b.thumbprint
      ? list.find((x) => x.thumbprint === b.thumbprint)
      : list
          .filter((x) => x.hasPrivateKey && Date.parse(x.notAfter) > now && [x.subject.replace(/^CN=/, ''), ...x.dnsNames].some((n) => n.toLowerCase() === b.subject!.trim().toLowerCase()))
          .sort((x, y) => Date.parse(y.notAfter) - Date.parse(x.notAfter))[0];
    if (!match)
      throw new HttpError(400, 'Invalid request', b.thumbprint ? `No certificate with thumbprint ${b.thumbprint} in ${location}\\${storeName}.` : `No currently valid certificate with a private key matches “${b.subject}” in ${location}\\${storeName}.`, b.thumbprint ? { Thumbprint: ['Certificate not found in the store.'] } : { Subject: [`No currently valid certificate with a private key matches “${b.subject}”.`] });
    if (!match.hasPrivateKey) throw new HttpError(400, 'Invalid request', 'The certificate has no private key.', { Thumbprint: ['The certificate has no private key.'] });
    if (!match.exportable) throw new HttpError(400, 'Invalid request', `The private key of ${match.subject} is not exportable. Re-issue it from a template that allows exporting the private key.`, { Thumbprint: ['The private key is not exportable.'] });
    return ok(addCert(s, {
      name: b.name?.trim() || match.dnsNames[0] || match.subject.replace(/^CN=/, ''),
      source: 'windowsStore',
      storeLocation: location,
      storeName,
      storeThumbprint: b.thumbprint,
      storeSubject: b.subject?.trim(),
      subjects: match.dnsNames.length ? match.dnsNames : [match.subject.replace(/^CN=/, '')],
      issuer: match.issuer,
      notBefore: match.notBefore,
      notAfter: match.notAfter,
      thumbprint: match.thumbprint,
      lastSyncedAt: iso(),
    }));
  }],
  ['POST', '/api/certificates/:id/sync', (c, s) => {
    requireRole(s, 'operator');
    const cert = s.certificates.find((x) => x.id === c.params.id);
    if (!cert) throw new HttpError(404, 'Not found', 'Certificate was not found.');
    if (cert.source === 'uploaded') throw new HttpError(400, 'Invalid request', 'Uploaded certificates have no source to sync from. Use Replace instead.');
    const res = transactional(s, `Certificate synced: ${cert.name}`, () => {
      cert.lastSyncedAt = iso();
      cert.lastSyncError = cert.source === 'windowsStore' && cert.storeSubject?.startsWith('mail.') ? cert.lastSyncError : undefined;
      cert.updatedAt = iso();
      return cert;
    });
    audit(s, 'synced', 'certificate', cert.name);
    return ok(res);
  }],
  ['POST', '/api/certificates/:method', (c, s) => {
    requireRole(s, 'operator');
    let name = '';
    let subjects = ['imported.example.com'];
    let source: 'uploaded' | 'filePath' = 'uploaded';
    let certPath = '';
    let keyPath = '';
    if (c.params.method === 'upload') {
      const f = parseMultipart(c.raw, String(c.headers['content-type'] ?? ''));
      name = f.name ?? '';
      if (f.pfxFile && f.pfxPassword === 'wrong') throw new HttpError(400, 'Invalid request', 'The PFX password is incorrect or the file is damaged.', { PfxPassword: ['The PFX password is incorrect.'] });
      const file = (f.certFile ?? f.pfxFile ?? '').replace(/^file:/, '');
      subjects = [file.replace(/\.(pem|crt|cer|pfx|p12)$/i, '') || 'upload.example.com'];
    } else if (c.params.method === 'pem') {
      const b = c.body as { name: string; certPem: string; keyPem: string };
      name = b.name;
      if (b.keyPem.includes('MISMATCH')) throw new HttpError(400, 'Invalid request', 'The private key does not match the certificate.', { KeyPem: ['The private key does not match the certificate.'] });
    } else if (c.params.method === 'path') {
      requireRole(s, 'admin');
      const b = c.body as { name: string; certPath: string; keyPath: string };
      name = b.name;
      source = 'filePath';
      certPath = b.certPath;
      keyPath = b.keyPath;
      if (!/\.(pem|crt|cer)$/i.test(b.certPath)) throw new HttpError(400, 'Invalid request', `Cannot read ${b.certPath}: file not found or not a PEM certificate.`, { CertPath: ['File not found or not PEM.'] });
    } else throw new HttpError(404, 'Not found');
    const id = newId();
    const res = transactional(s, `Certificate added: ${name}`, () => {
      const cert = {
        id,
        name,
        source,
        certPath: certPath || `C:\\ProgramData\\CaddyProxyManager\\certificates\\${id}\\fullchain.pem`,
        keyPath: keyPath || `C:\\ProgramData\\CaddyProxyManager\\certificates\\${id}\\privkey.pem`,
        subjects,
        issuer: 'CN=Example Corp Issuing CA 01',
        notBefore: iso(),
        notAfter: iso(-365 * 86_400_000),
        thumbprint: 'ABCDEF0123456789ABCDEF0123456789ABCDEF01',
        createdAt: iso(),
        updatedAt: iso(),
      };
      s.certificates.push(cert);
      return cert;
    });
    audit(s, 'created', 'certificate', name);
    return ok(res);
  }],
  ['POST', '/api/certificates/:id/replace', (c, s) => {
    requireRole(s, 'operator');
    const cert = s.certificates.find((x) => x.id === c.params.id);
    if (!cert) throw new HttpError(404, 'Not found', 'Certificate was not found.');
    const res = transactional(s, `Certificate replaced: ${cert.name}`, () => {
      cert.notBefore = iso();
      cert.notAfter = iso(-365 * 86_400_000);
      cert.updatedAt = iso();
      return cert;
    });
    audit(s, 'replaced', 'certificate', cert.name);
    return ok(res);
  }],
  ['PUT', '/api/certificates/:id', (c, s) => {
    requireRole(s, 'operator');
    const cert = s.certificates.find((x) => x.id === c.params.id);
    if (!cert) throw new HttpError(404, 'Not found', 'Certificate was not found.');
    const b = c.body as { name: string; notes: string };
    cert.name = b.name;
    cert.notes = b.notes;
    return ok({ item: cert, apply: { success: true, writtenOnly: false, warnings: [] } });
  }],
  ['DELETE', '/api/certificates/:id', (c, s) => {
    requireRole(s, 'operator');
    if (s.hosts.some((h) => h.certificateId === c.params.id && h.tls === 'custom')) throw new HttpError(409, 'Conflict', 'The certificate is used by a host.');
    const res = transactional(s, 'Certificate deleted', () => {
      s.certificates = s.certificates.filter((x) => x.id !== c.params.id);
    });
    return ok({ apply: res.apply });
  }],

  // ---- settings
  ['GET', '/api/settings/caddy', (_c, s) => {
    currentUser(s);
    return ok(caddySettingsOut(s));
  }],
  ['PUT', '/api/settings/caddy', (c, s) => {
    requireRole(s, 'admin');
    const b = c.body as CaddySettings & { eabMacKey?: string | null; acmeIssuerJson?: string | null };
    const { eabMacKey, acmeIssuerJson, hasEabMacKey: _h, hasAcmeIssuerJson: _a, ...rest } = b;
    const errors: Record<string, string[]> = {};
    for (const [k, v] of [['ExtraAppsJson', rest.extraAppsJson], ['TlsConnectionPolicyJson', rest.tlsConnectionPolicyJson], ['AcmeIssuerJson', acmeIssuerJson]] as const) {
      if (!v) continue;
      try {
        const o: unknown = JSON.parse(v);
        if (!o || typeof o !== 'object' || Array.isArray(o)) errors[k] = ['Must be a JSON object.'];
        else if (k === 'ExtraAppsJson') {
          const reserved = Object.keys(o).filter((a) => ['http', 'tls', 'pki', 'layer4'].includes(a));
          if (reserved.length) errors[k] = [`The app(s) ${reserved.join(', ')} are generated by the manager and cannot be overridden.`];
        }
      } catch (e) {
        errors[k] = [`Invalid JSON: ${(e as Error).message}`];
      }
    }
    if (Object.keys(errors).length) throw new HttpError(400, 'Invalid request', 'One or more fields are invalid.', errors);
    const provider = acmeIssuerJson ? (/"name"\s*:\s*"([^"]+)"/.exec(acmeIssuerJson)?.[1] ?? null) : null;
    if (provider && !(s.binary.installed?.modules ?? []).includes(`dns.providers.${provider}`))
      throw new HttpError(422, 'Caddy rejected the configuration', `loading new config: loading tls app module: provision tls: provisioning automation policy 1: loading TLS automation management module: position 0: loading module 'acme': provision tls.issuance.acme: loading DNS provider module: loading module '${provider}': unknown module: dns.providers.${provider}`);
    const snapshot = JSON.stringify(s.caddySettings);
    let round3Rest: Record<string, unknown>;
    try {
      round3Rest = applyRound3CaddySettings(s, rest as unknown as Record<string, unknown>, HttpError);
    } catch (err) {
      s.caddySettings = JSON.parse(snapshot) as MockState['caddySettings'];
      throw err;
    }
    let res: { item: Partial<CaddySettings>; apply: ApplyResult };
    try {
      res = transactional(s, 'Settings changed: Caddy', () => {
        Object.assign(s.caddySettings, round3Rest);
        secret(s.caddySettings, 'eabMacKey', eabMacKey, 'hasEabMacKey');
        secret(s.caddySettings, 'acmeIssuerJson', acmeIssuerJson, 'hasAcmeIssuerJson');
        return caddySettingsOut(s);
      });
    } catch (err) {
      s.caddySettings = JSON.parse(snapshot) as MockState['caddySettings'];
      throw err;
    }
    // SPEC round 3: a DNS provider missing from the installed binary is a warning (certificates using DNS-01 fail until Caddy is rebuilt).
    const dnsProvider = s.caddySettings.dnsProvider;
    if (dnsProvider && !(s.binary.installed?.modules ?? []).includes(`dns.providers.${dnsProvider}`))
      res.apply.warnings = [...(res.apply.warnings ?? []), `DNS provider module dns.providers.${dnsProvider} is not in the installed Caddy. Add github.com/caddy-dns/${dnsProvider} on the Plugins page and rebuild Caddy.`];
    audit(s, 'updated', 'settings', 'Caddy');
    return ok(res);
  }],
  ['GET', '/api/settings/binary', (_c, s) => {
    currentUser(s);
    return ok(s.binarySettings);
  }],
  ['PUT', '/api/settings/binary', (c, s) => {
    requireRole(s, 'admin');
    const b = c.body as BinarySettingsBody;
    if (b.managerReleaseRepo && !/^[\w.-]+\/[\w.-]+$/.test(b.managerReleaseRepo))
      throw new HttpError(400, 'Invalid request', 'Use owner/repository.', { ManagerReleaseRepo: ['Use the GitHub owner/repository form.'] });
    Object.assign(s.binarySettings, b);
    s.binary.managerUpdateAvailable = !!b.managerReleaseRepo;
    if (!b.managerReleaseRepo) {
      delete s.binary.managerLatestVersion;
      delete s.binary.managerLatestUrl;
    } else {
      s.binary.managerLatestVersion = '1.1.0';
      s.binary.managerLatestUrl = `https://github.com/${b.managerReleaseRepo}/releases/tag/v1.1.0`;
    }
    audit(s, 'updated', 'settings', 'Updates');
    return ok(s.binarySettings);
  }],
  ['GET', '/api/settings/notifications', (_c, s) => {
    requireRole(s, 'admin');
    return ok(stripSecret(stripSecret(s.notificationSettings, 'smtpPassword'), 'oAuthClientSecret'));
  }],
  ['PUT', '/api/settings/notifications', (c, s) => {
    requireRole(s, 'admin');
    const b = c.body as NotificationSettings & { smtpPassword?: string | null; oAuthClientSecret?: string | null };
    const { smtpPassword, oAuthClientSecret, hasSmtpPassword: _h, hasOAuthClientSecret: _o, ...rest } = b;
    if (rest.smtpAuth === 'oAuth2ClientCredentials' && rest.oAuthClientId && !/^[0-9a-f-]{36}$/i.test(rest.oAuthClientId))
      throw new HttpError(400, 'Invalid request', 'The client ID must be a GUID.', { OAuthClientId: ['The Application (client) ID must be a GUID.'] });
    Object.assign(s.notificationSettings, rest);
    secret(s.notificationSettings, 'smtpPassword', smtpPassword, 'hasSmtpPassword');
    secret(s.notificationSettings, 'oAuthClientSecret', oAuthClientSecret, 'hasOAuthClientSecret');
    audit(s, 'updated', 'settings', 'Notifications');
    return ok(stripSecret(stripSecret(s.notificationSettings, 'smtpPassword'), 'oAuthClientSecret'));
  }],
  ['POST', '/api/settings/notifications/test', (_c, s) => {
    requireRole(s, 'admin');
    const n = s.notificationSettings;
    const errors: string[] = [];
    if (n.webhookEnabled && (n.webhookUrl ?? '').includes('fail')) errors.push(`Webhook: POST ${n.webhookUrl} returned 404 Not Found.`);
    if (n.smtpEnabled && n.smtpHost.includes('invalid')) errors.push(`SMTP: could not connect to ${n.smtpHost}:${n.smtpPort} (No such host is known).`);
    if (n.smtpEnabled && n.smtpAuth === 'oAuth2ClientCredentials' && (n.oAuthTenantId ?? '').includes('fail'))
      errors.push(`SMTP: Microsoft Entra ID token request failed: AADSTS700016: Application with identifier '${n.oAuthClientId}' was not found in the directory '${n.oAuthTenantId}'.`);
    return ok({ ok: errors.length === 0, errors });
  }],
  ['GET', '/api/settings/ui', (_c, s) => {
    requireRole(s, 'admin');
    return ok(stripSecret(s.uiSettings, 'httpsPfxPassword'));
  }],
  ['PUT', '/api/settings/ui', (c, s) => {
    requireRole(s, 'admin');
    const b = c.body as UiSettings & { httpsPfxPassword?: string | null };
    const before = JSON.stringify([s.uiSettings.port, s.uiSettings.bindAddress, s.uiSettings.httpsEnabled, s.uiSettings.httpsPort, s.uiSettings.httpsPfxPath]);
    const { httpsPfxPassword, hasHttpsPfxPassword: _h, ...rest } = b;
    Object.assign(s.uiSettings, rest);
    secret(s.uiSettings, 'httpsPfxPassword', httpsPfxPassword, 'hasHttpsPfxPassword');
    const after = JSON.stringify([s.uiSettings.port, s.uiSettings.bindAddress, s.uiSettings.httpsEnabled, s.uiSettings.httpsPort, s.uiSettings.httpsPfxPath]);
    audit(s, 'updated', 'settings', 'Management UI');
    return ok({ item: stripSecret(s.uiSettings, 'httpsPfxPassword'), restartRequired: before !== after || httpsPfxPassword !== undefined });
  }],

  // ---- directory (LDAP)
  ['GET', '/api/settings/ldap', (_c, s) => {
    requireRole(s, 'admin');
    return ok(stripSecret(s.ldapSettings, 'bindPassword'));
  }],
  ['PUT', '/api/settings/ldap', (c, s) => {
    requireRole(s, 'admin');
    const b = c.body as LdapSettings & { bindPassword?: string | null };
    const { bindPassword, hasBindPassword: _h, ...rest } = b;
    if (rest.enabled && !rest.userFilter.includes('{0}')) throw new HttpError(400, 'Invalid request', 'The user filter must contain {0}.', { UserFilter: ['The user filter must contain {0}.'] });
    Object.assign(s.ldapSettings, rest);
    secret(s.ldapSettings, 'bindPassword', bindPassword, 'hasBindPassword');
    audit(s, 'updated', 'settings', 'Directory (LDAP)');
    return ok(stripSecret(s.ldapSettings, 'bindPassword'));
  }],
  ['POST', '/api/settings/ldap/test', async (c, s) => {
    requireRole(s, 'admin');
    await sleep(700);
    const { username, password } = c.body as { username: string; password: string };
    const l = s.ldapSettings;
    if (!l.enabled) return ok({ ok: false, error: 'Directory sign-in is disabled.' });
    if (l.server.includes('unreachable')) return ok({ ok: false, error: `Cannot connect to ${l.server}:${l.port} (${l.security}): The LDAP server is unavailable.` });
    const name = username.replace(/^.*\\/, '').replace(/@.*$/, '').toLowerCase();
    if (password === 'wrong') return ok({ ok: false, error: `Bind as ${username} failed: 49 — invalid credentials (AcceptSecurityContext error, data 52e).` });
    const groups = name === 'nogroup' ? ['CN=Domain Users,CN=Users,DC=corp,DC=example,DC=com'] : [l.adminGroupDn, 'CN=Domain Users,CN=Users,DC=corp,DC=example,DC=com', 'CN=VPN Users,OU=Groups,DC=corp,DC=example,DC=com'].filter(Boolean) as string[];
    return ok({
      ok: true,
      role: name === 'nogroup' ? undefined : 'admin',
      displayName: name.replace(/(^|[._-])(\w)/g, (_m, sep: string, ch: string) => (sep ? ' ' : '') + ch.toUpperCase()),
      email: `${name}@corp.example.com`,
      groups,
    });
  }],

  // ---- scheduled backups
  ['GET', '/api/settings/backup', (_c, s) => {
    requireRole(s, 'admin');
    return ok(stripSecret(s.backupSettings, 'password'));
  }],
  ['PUT', '/api/settings/backup', (c, s) => {
    requireRole(s, 'admin');
    const b = c.body as BackupSettings & { password?: string | null };
    const { password, hasPassword: _h, ...rest } = b;
    if (rest.directory && /programdata\\caddyproxymanager\\db/i.test(rest.directory)) throw new HttpError(400, 'Invalid request', 'Choose a folder outside the database folder.', { Directory: ['Choose a folder outside the database folder.'] });
    Object.assign(s.backupSettings, rest);
    secret(s.backupSettings, 'password', password, 'hasPassword');
    audit(s, 'updated', 'settings', 'Backups');
    return ok(stripSecret(s.backupSettings, 'password'));
  }],
  ['GET', '/api/backups', (_c, s) => {
    requireRole(s, 'admin');
    return ok(s.backups);
  }],
  ['POST', '/api/backups/run', async (_c, s) => {
    requireRole(s, 'admin');
    await sleep(900);
    const d = new Date();
    const pad = (n: number) => String(n).padStart(2, '0');
    const name = `cpm-backup-WEB-PROXY01-${d.getFullYear()}${pad(d.getMonth() + 1)}${pad(d.getDate())}-${pad(d.getHours())}${pad(d.getMinutes())}${pad(d.getSeconds())}.zip`;
    s.backups.unshift({ name, size: 2_512_345, createdAt: iso() });
    s.backups = s.backups.slice(0, s.backupSettings.keep);
    audit(s, 'backup', 'system', name);
    return ok({ name });
  }],
  ['GET', '/api/backups/:name', (c, s) => {
    requireRole(s, 'admin');
    if (!s.backups.some((b) => b.name === c.params.name)) throw new HttpError(404, 'Not found', `Backup ${c.params.name} was not found.`);
    return { bytes: Buffer.from('PK\u0005\u0006' + '\u0000'.repeat(18), 'latin1'), contentType: 'application/zip', headers: { 'Content-Disposition': `attachment; filename="${c.params.name}"` } };
  }],

  // ---- config
  ['GET', '/api/config/preview', (_c, s) => {
    currentUser(s);
    return ok({ json: buildConfig(s) });
  }],
  ['GET', '/api/config/running', (_c, s) => {
    currentUser(s);
    if (s.status.state !== 'running') throw new HttpError(503, 'Caddy admin API unreachable', 'Could not connect to http://127.0.0.1:2019.');
    s.runningJson ??= buildConfig(s);
    return ok({ json: s.runningJson });
  }],
  ['POST', '/api/config/apply', (_c, s) => {
    requireRole(s, 'operator');
    return ok(apply(s, 'Manual apply'));
  }],
  ['GET', '/api/config/revisions', (c, s) => {
    currentUser(s);
    const take = Number(c.query.get('take') ?? 50);
    return ok(s.revisions.slice(0, take).map(({ json: _j, updatedAt: _u, ...r }) => r));
  }],
  ['GET', '/api/config/revisions/:id', (c, s) => {
    currentUser(s);
    const r = s.revisions.find((x) => x.id === c.params.id);
    if (!r) throw new HttpError(404, 'Not found', 'Revision was not found.');
    return ok({ ...r, json: r.json === '{}' ? buildConfig(s) : r.json });
  }],
  ['POST', '/api/config/caddyfile/adapt', (c, s) => {
    requireRole(s, 'admin');
    const { caddyfile } = c.body as { caddyfile: string };
    const open = (caddyfile.match(/\{/g) ?? []).length;
    const close = (caddyfile.match(/\}/g) ?? []).length;
    if (open !== close) throw new HttpError(422, 'Adapt failed', `Caddyfile:${caddyfile.split('\n').length}: unexpected EOF — ${open > close ? 'missing closing brace' : 'unexpected closing brace'}`);
    const sites = [...caddyfile.matchAll(/^([^\s#{][^{]*)\{/gm)].map((m) => m[1].trim());
    const warnings = /\t/.test(caddyfile) ? [] : ['Caddyfile input is not formatted; run `caddy fmt --overwrite` to fix inconsistencies.'];
    return ok({ json: JSON.stringify({ apps: { http: { servers: { srv0: { listen: [':443'], routes: sites.map((site) => ({ match: [{ host: site.split(/[\s,]+/).filter(Boolean) }], handle: [{ handler: 'subroute', routes: [] }], terminal: true })) } } } } }, null, 2), warnings });
  }],

  ['POST', '/api/config/caddyfile/import', (c, s) => {
    requireRole(s, 'admin');
    const { caddyfile } = c.body as { caddyfile: string };
    return ok(importCaddyfile(caddyfile ?? ''));
  }],
  ['POST', '/api/config/caddyfile/import/commit', (c, s) => {
    requireRole(s, 'admin');
    const { hosts } = c.body as { hosts: SiteHostFields[] };
    if (!Array.isArray(hosts) || hosts.length === 0) throw new HttpError(400, 'Invalid request', 'Select at least one host to import.');
    for (const h of hosts) validateHost(s, h);
    const res = transactional(s, `Caddyfile import: ${hosts.length} host(s)`, () => {
      for (const h of hosts) s.hosts.push({ ...h, id: newId(), createdAt: iso(), updatedAt: iso() } as SiteHost);
    });
    audit(s, 'imported', 'host', `${hosts.length} host(s)`, 'Imported from a Caddyfile');
    return ok({ created: hosts.length, apply: res.apply });
  }],

  // ---- caddy runtime
  ['GET', '/api/caddy/status', (_c, s) => {
    currentUser(s);
    return ok(s.status);
  }],
  ['POST', '/api/caddy/:action', (c, s) => {
    const a = c.params.action;
    if (!['start', 'stop', 'restart'].includes(a)) throw new HttpError(404, 'Not found');
    requireRole(s, 'operator');
    if (a === 'stop') s.status = { ...s.status, state: 'stopped', processId: undefined, adminReachable: false, startedAt: undefined };
    else s.status = { ...s.status, state: 'running', processId: 4000 + Math.floor(Math.random() * 999), adminReachable: true, startedAt: iso(), lastError: undefined };
    s.events.unshift({ id: newId(), createdAt: iso(), updatedAt: iso(), severity: a === 'stop' ? 'warning' : 'recovered', category: 'caddy', message: a === 'stop' ? `Caddy stopped by ${currentUser(s).name}` : 'Caddy is running', key: 'caddy-down', notified: false });
    audit(s, a === 'stop' ? 'stopped' : a === 'start' ? 'started' : 'restarted', 'caddy', 'service');
    return ok(s.status);
  }],
  ['POST', '/api/caddy/service/:action', (c, s) => {
    requireRole(s, 'admin');
    if (c.params.action === 'uninstall') s.status = { ...s.status, serviceInstalled: false, state: 'notInstalled', adminReachable: false, processId: undefined, serviceStartType: undefined };
    else s.status = { ...s.status, serviceInstalled: true, serviceStartType: 'Automatic', state: s.status.state === 'notInstalled' ? 'stopped' : s.status.state };
    return ok(s.status);
  }],
  ['GET', '/api/caddy/binary', (_c, s) => {
    currentUser(s);
    return ok(s.binary);
  }],
  ['POST', '/api/caddy/binary/check', (_c, s) => {
    requireRole(s, 'operator');
    s.binary.lastCheckedAt = iso();
    s.binarySettings.lastCheckedAt = iso();
    return ok(s.binary);
  }],
  ['POST', '/api/caddy/binary/install', (c, s) => {
    requireRole(s, 'admin');
    if (s.jobs.some((j) => j.kind === 'caddy-install' && j.state === 'running')) throw new HttpError(409, 'Conflict', 'An installation is already running.');
    const version = (c.body as { version?: string }).version ?? s.binary.latest?.version ?? 'v2.11.4';
    const plugins = [...s.binarySettings.plugins];
    const job = startJob(s, 'caddy-install', `Install Caddy ${version}`, JOB_STEPS(version, plugins), () => {
      s.binary.previousVersion = s.binary.installed?.version;
      s.binary.canRollback = !!s.binary.installed;
      s.binary.installed = { ...(s.binary.installed ?? { path: s.status.binaryPath }), version, installedAt: iso(), plugins, modules: [...BASE_MODULES, ...plugins.flatMap(modulesOf)] };
      s.binary.updateAvailable = version !== s.binary.latest?.version;
      s.binary.pluginsOutOfSync = false;
      s.status = { ...s.status, binaryInstalled: true, version, state: 'running', adminReachable: true, startedAt: iso() };
      audit(s, 'installed', 'caddy', version);
    });
    return ok(jobSnapshot(job));
  }],
  ['POST', '/api/caddy/binary/upload', (c, s) => {
    requireRole(s, 'admin');
    const f = parseMultipart(c.raw, String(c.headers['content-type'] ?? ''));
    const file = (f.file ?? '').replace(/^file:/, '');
    if (!file) throw new HttpError(400, 'Invalid request', "Upload caddy.exe or a release archive in the form field 'file'.", { file: ['Choose caddy.exe or a release archive.'] });
    if (!/\.(exe|zip|tar\.gz)$/i.test(file)) throw new HttpError(400, 'Invalid request', `${file} is not caddy.exe or a release archive.`, { file: ['Upload caddy.exe, a .zip or a .tar.gz release archive.'] });
    if (f.sha512 && f.sha512.startsWith('00')) throw new HttpError(400, 'Invalid request', 'Checksum mismatch.', { sha512: [`SHA-512 of ${file} does not match the value you entered.`] });
    if (s.jobs.some((j) => j.kind.startsWith('caddy-') && j.state === 'running')) throw new HttpError(409, 'Conflict', 'Another Caddy installation job is running.');
    const version = /v?(\d+\.\d+\.\d+)/.exec(file)?.[1] ? `v${/v?(\d+\.\d+\.\d+)/.exec(file)![1]}` : 'v2.11.4';
    const job = startJob(s, 'caddy-upload', `Install uploaded ${file}`, [
      `Received ${file} (uploaded by ${currentUser(s).name})`,
      f.sha512 ? 'SHA-512 checksum matches the value provided' : 'No checksum provided — skipping checksum verification',
      /\.zip$|\.tar\.gz$/i.test(file) ? `Extracted caddy.exe from ${file}` : 'Using the uploaded executable',
      ...JOB_STEPS(version, []).slice(3),
    ], () => {
      s.binary.previousVersion = s.binary.installed?.version;
      s.binary.canRollback = true;
      s.binary.installed = { ...(s.binary.installed ?? { path: s.status.binaryPath, modules: BASE_MODULES, plugins: [] }), version, installedAt: iso() };
      s.binary.updateAvailable = version !== s.binary.latest?.version;
      s.status = { ...s.status, binaryInstalled: true, version, state: 'running', adminReachable: true, startedAt: iso() };
      audit(s, 'installed', 'caddy', version, `Offline upload: ${file}`);
    });
    return ok(jobSnapshot(job));
  }],
  ['POST', '/api/caddy/binary/rollback', (_c, s) => {
    requireRole(s, 'admin');
    if (!s.binary.canRollback || !s.binary.previousVersion) throw new HttpError(409, 'Conflict', 'There is no previous Caddy binary to roll back to.');
    const target = s.binary.previousVersion;
    const current = s.binary.installed?.version;
    const job = startJob(s, 'caddy-rollback', `Roll back Caddy to ${target}`, [
      `caddy.exe.previous version → ${target}`,
      'caddy.exe.previous validate --config caddy.json → Valid configuration',
      'Stopping service Caddy…',
      `Swapped caddy.exe (${current}) and caddy.exe.previous (${target})`,
      'Starting service Caddy…',
      'Admin API responded after 1.2 s',
      `Caddy ${target} is running`,
    ], () => {
      s.binary.previousVersion = current;
      s.binary.installed = { ...(s.binary.installed ?? { path: s.status.binaryPath, modules: BASE_MODULES, plugins: [] }), version: target, installedAt: iso() };
      s.binary.updateAvailable = target !== s.binary.latest?.version;
      s.status = { ...s.status, version: target, state: 'running', adminReachable: true, startedAt: iso() };
      audit(s, 'rolledBack', 'caddy', target);
    });
    return ok(jobSnapshot(job));
  }],
  ['GET', '/api/caddy/plugins/catalog', (c, s) => {
    currentUser(s);
    return ok(catalog(c.query.get('q') ?? ''));
  }],
  ['PUT', '/api/caddy/plugins', (c, s) => {
    requireRole(s, 'admin');
    const { plugins } = c.body as { plugins: string[] };
    s.binarySettings.plugins = plugins;
    s.binary.desiredPlugins = plugins;
    s.binary.pluginsOutOfSync = plugins.slice().sort().join('|') !== (s.binary.installed?.plugins ?? []).slice().sort().join('|');
    audit(s, 'updated', 'plugins', plugins.join(', ') || 'none');
    return ok(s.binary);
  }],
  ['GET', '/api/caddy/upstreams', (_c, s) => {
    currentUser(s);
    if (s.status.state !== 'running') throw new HttpError(503, 'Caddy admin API unreachable');
    return ok([
      { address: '10.0.10.21:8080', numRequests: 3, fails: 0, healthy: true, monitored: true },
      { address: '10.0.10.31:3000', numRequests: 1, fails: 0, healthy: true, monitored: true },
      { address: '10.0.10.32:3000', numRequests: 0, fails: 4, healthy: false, monitored: true },
      { address: 'sp01.corp.example.com:443', numRequests: 0, fails: 0, healthy: true, monitored: false },
      { address: '10.0.20.40:8090', numRequests: 2, fails: 0, healthy: true, monitored: false },
      { address: '10.0.10.50:5000', numRequests: 7, fails: 1, healthy: true },
      { address: '10.0.20.80:44300', numRequests: 0, fails: 0, healthy: true },
    ]);
  }],
  ['GET', '/api/jobs', (_c, s) => {
    currentUser(s);
    return ok(s.jobs.map(jobSnapshot));
  }],
  ['GET', '/api/jobs/:id', (c, s) => {
    currentUser(s);
    const j = s.jobs.find((x) => x.id === c.params.id);
    if (!j) throw new HttpError(404, 'Not found', 'Job was not found.');
    return ok(jobSnapshot(j));
  }],

  // ---- readiness
  ['GET', '/api/readiness', (_c, s) => {
    currentUser(s);
    return s.readiness ? ok(s.readiness) : noContent();
  }],
  ['POST', '/api/readiness/run', async (_c, s) => {
    requireRole(s, 'operator');
    await sleep(1500);
    s.readiness = summarize({ ...(s.readiness ?? readinessReport()), ranAt: iso() });
    return ok(s.readiness);
  }],
  ['POST', '/api/readiness/fix/:checkId', (c, s) => {
    requireRole(s, 'admin');
    const r = s.readiness;
    const check = r?.checks.find((x) => x.id === c.params.checkId);
    if (!r || !check) throw new HttpError(404, 'Not found', 'Check was not found.');
    if (!check.fixable) throw new HttpError(400, 'Invalid request', 'This check cannot be fixed automatically.');
    s.readiness = summarize({ ...r, checks: r.checks.map((x) => (x.id === check.id ? { ...x, status: 'pass', summary: 'Fixed by the manager.', fixable: false } : x)) });
    audit(s, 'fixed', 'readiness', check.title);
    return ok({ message: `${check.title}: remediation applied.`, report: s.readiness });
  }],
  ['GET', '/api/readiness/gpo-script', (_c, s) => {
    currentUser(s);
    return { text: GPO_SCRIPT, contentType: 'text/plain; charset=utf-8' };
  }],

  // ---- logs & backup
  ['GET', '/api/logs/caddy', (c, s) => {
    currentUser(s);
    const q = (c.query.get('q') ?? '').toLowerCase();
    const lines = caddyLog(Number(c.query.get('lines') ?? 500)).filter((l) => !q || l.toLowerCase().includes(q));
    return ok({ lines, file: 'C:\\ProgramData\\CaddyProxyManager\\logs\\caddy\\caddy.log' });
  }],
  ['GET', '/api/logs/access', (c, s) => {
    currentUser(s);
    const hosts = s.hosts.filter((h) => h.accessLog).map((h) => h.domains[0]);
    const host = c.query.get('host') || hosts[0] || '';
    return ok({ lines: host ? accessLog(host, Number(c.query.get('lines') ?? 500)) : [], file: host ? `C:\\ProgramData\\CaddyProxyManager\\logs\\access\\${host}.log` : '', hosts });
  }],
  ['GET', '/api/logs/manager', (c, s) => {
    requireRole(s, 'admin');
    const q = (c.query.get('q') ?? '').toLowerCase();
    const lines = managerLog(Number(c.query.get('lines') ?? 500)).filter((l) => !q || l.toLowerCase().includes(q));
    return ok({ lines, file: 'C:\\ProgramData\\CaddyProxyManager\\logs\\manager\\manager-20260925.log' });
  }],
  ['GET', '/api/backup', (_c, s) => {
    requireRole(s, 'admin');
    return { bytes: Buffer.from('PK\u0005\u0006' + '\u0000'.repeat(18), 'latin1'), contentType: 'application/zip', headers: { 'Content-Disposition': 'attachment; filename="caddy-proxy-manager-backup.zip"' } };
  }],
  ['POST', '/api/backup/restore', (c, s) => {
    requireRole(s, 'admin');
    const f = parseMultipart(c.raw, String(c.headers['content-type'] ?? ''));
    const file = (f.file ?? '').replace(/^file:/, '');
    if (/encrypted|cpm-backup/i.test(file) && !f.password)
      throw new HttpError(400, 'Invalid request', 'This backup is encrypted. Enter the backup password.', { password: ['This backup is encrypted. Enter the backup password.'] });
    if (f.password === 'wrong') throw new HttpError(400, 'Invalid request', 'The backup password is incorrect.', { password: ['The backup password is incorrect.'] });
    return ok({ restartRequired: true, message: 'The backup was validated and will be applied when the Caddy Proxy Manager service restarts.' });
  }],
];

const helpers: MockHelpers = { HttpError, ok, noContent };
routes.push(...round3SettingsRoutes(helpers), ...round3ServersRoutes(helpers));

const compiled = routes.map(([method, pattern, handler]) => {
  const keys: string[] = [];
  const re = new RegExp(`^${pattern.replace(/:(\w+)/g, (_m, k: string) => (keys.push(k), '([^/]+)'))}/?$`);
  return { method, re, keys, handler, literal: !pattern.includes(':') };
});
// Literal routes win over parameterised ones (e.g. /api/caddy/binary/check vs /api/caddy/:action).
compiled.sort((a, b) => Number(b.literal) - Number(a.literal));

function send(res: ServerResponse, r: NonNullable<Result>) {
  res.statusCode = r.status ?? 200;
  for (const [k, v] of Object.entries(r.headers ?? {})) res.setHeader(k, v);
  if (r.status === 204) return res.end();
  if (r.bytes) {
    res.setHeader('Content-Type', r.contentType ?? 'application/octet-stream');
    return res.end(r.bytes);
  }
  if (r.text !== undefined) {
    res.setHeader('Content-Type', r.contentType ?? 'text/plain; charset=utf-8');
    return res.end(r.text);
  }
  res.setHeader('Content-Type', 'application/json; charset=utf-8');
  res.end(JSON.stringify(r.json ?? null, (_k, v: unknown) => (v === null ? undefined : v)));
}

export function mockApi(): Plugin {
  const state = createState();
  const latency = Number(process.env.MOCK_LATENCY ?? 180);
  return {
    name: 'cpm-mock-api',
    apply: 'serve',
    configureServer(server) {
      server.config.logger.info('\n  Caddy Proxy Manager mock API enabled (in-memory data). Sign in with any fixture e-mail, e.g. admin@example.com / any password.\n');
      server.middlewares.use(async (req, res, next) => {
        const url = new URL(req.url ?? '/', 'http://localhost');
        if (!url.pathname.startsWith('/api/')) return next();
        const method = (req.method ?? 'GET').toUpperCase();
        try {
          const raw = await readBody(req);
          if (method !== 'GET' && method !== 'HEAD' && req.headers['x-cpm-request'] !== '1')
            throw new HttpError(400, 'Missing X-CPM-Request header', 'State-changing requests must include the header X-CPM-Request: 1.');
          const type = String(req.headers['content-type'] ?? '');
          let body: unknown = {};
          if (raw.length && type.includes('json')) {
            try {
              body = JSON.parse(raw.toString('utf8'));
            } catch {
              throw new HttpError(400, 'Invalid JSON body');
            }
          }
          const route = compiled.find((r) => r.method === method && r.re.test(url.pathname));
          if (!route) throw new HttpError(404, 'Not found', `${method} ${url.pathname} is not implemented by the mock API.`);
          const m = route.re.exec(url.pathname) as RegExpExecArray;
          const params = Object.fromEntries(route.keys.map((k, i) => [k, decodeURIComponent(m[i + 1])]));
          if (latency > 0) await sleep(latency * (0.6 + Math.random() * 0.8));
          managedNodeGuard(method, url.pathname, body, state);
          const result = await route.handler({ method, path: url.pathname, query: url.searchParams, body, raw, headers: req.headers, params }, state);
          send(res, result ?? { status: 204 });
        } catch (err) {
          const e = err instanceof HttpError ? err : new HttpError(500, 'Internal Server Error', (err as Error).message);
          if (!(err instanceof HttpError)) console.error('[mock-api]', err);
          res.statusCode = e.status;
          res.setHeader('Content-Type', 'application/problem+json; charset=utf-8');
          res.end(JSON.stringify({ title: e.title, detail: e.detail, status: e.status, errors: e.errors }));
        }
      });
    },
  };
}
