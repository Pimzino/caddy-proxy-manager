import type { LucideIcon } from 'lucide-react';
import { ArrowLeftRight, CornerUpRight, FileCode2, FolderOpen } from 'lucide-react';
import type { CertificateInfo, HeaderOp, HostKind, SiteHost, SiteHostFields, Upstream } from '@/api/types';
import {
  isAbsoluteHttpUrl,
  isValidSiteDomain,
  isValidPort,
  isValidUpstreamHost,
  jsonArrayError,
  type FieldErrors,
} from '@/lib/validation';

export const HOST_KINDS: HostKind[] = ['proxy', 'redirect', 'static', 'response'];

export interface KindMeta {
  title: string;
  singular: string;
  description: string;
  icon: LucideIcon;
  emptyDescription: string;
}

export const kindMeta: Record<HostKind, KindMeta> = {
  proxy: {
    title: 'Proxy Hosts',
    singular: 'proxy host',
    description: 'Reverse proxy incoming requests for one or more domains to backend servers.',
    icon: ArrowLeftRight,
    emptyDescription: 'Forward a domain such as app.example.com to an internal server like http://10.0.0.20:8080.',
  },
  redirect: {
    title: 'Redirects',
    singular: 'redirect',
    description: 'Send visitors of a domain to another URL with an HTTP redirect.',
    icon: CornerUpRight,
    emptyDescription: 'Redirect old or alternative domains (for example example.org → https://example.com).',
  },
  static: {
    title: 'Static Sites',
    singular: 'static site',
    description: 'Serve files from a folder on this server or a network share.',
    icon: FolderOpen,
    emptyDescription: 'Publish a folder of HTML, CSS and JavaScript files, including single-page applications.',
  },
  response: {
    title: 'Custom Responses',
    singular: 'custom response',
    description: 'Answer with a fixed status code and body — maintenance pages, 404 hosts, health endpoints.',
    icon: FileCode2,
    emptyDescription: 'Return a fixed response such as a maintenance notice or a 410 Gone for retired domains.',
  },
};

export const REDIRECT_CODES: { value: number; label: string }[] = [
  { value: 301, label: '301 Moved Permanently' },
  { value: 302, label: '302 Found (temporary)' },
  { value: 303, label: '303 See Other' },
  { value: 307, label: '307 Temporary Redirect (keeps method)' },
  { value: 308, label: '308 Permanent Redirect (keeps method)' },
];

export const LOAD_BALANCING: { value: SiteHostFields['loadBalancing']; label: string }[] = [
  { value: 'roundRobin', label: 'Round robin' },
  { value: 'leastConn', label: 'Least connections' },
  { value: 'random', label: 'Random' },
  { value: 'first', label: 'First available (failover)' },
  { value: 'ipHash', label: 'Client IP hash (sticky)' },
  { value: 'cookie', label: 'Cookie (sticky)' },
  { value: 'uriHash', label: 'URI hash' },
];

export const HEADER_ACTIONS: { value: HeaderOp['action']; label: string }[] = [
  { value: 'set', label: 'Set' },
  { value: 'add', label: 'Add' },
  { value: 'delete', label: 'Delete' },
];

export function newUpstream(): Upstream {
  return { scheme: 'http', host: '', port: 80 };
}

export function newHost(kind: HostKind): SiteHostFields {
  return {
    kind,
    enabled: true,
    domains: [],
    notes: null,
    tls: 'acme',
    acmeChallenge: 'default',
    dnsDelegation: 'default',
    certificateId: null,
    forceHttps: true,
    hsts: false,
    hstsSubdomains: false,
    hstsMaxAgeSeconds: 31536000,
    compression: true,
    accessListId: null,
    blockExploits: false,
    accessLog: false,
    responseHeaders: [],
    upstreams: kind === 'proxy' ? [newUpstream()] : [],
    loadBalancing: 'roundRobin',
    healthCheck: { enabled: false, path: '/', intervalSeconds: 30, timeoutSeconds: 5, expectStatus: 0 },
    upstreamTlsInsecure: false,
    upstreamNtlm: false,
    upstreamHostHeader: null,
    requestHeaders: [],
    locations: [],
    redirectTarget: kind === 'redirect' ? '' : null,
    redirectCode: 301,
    preservePath: true,
    rootPath: kind === 'static' ? '' : null,
    browse: false,
    spaFallback: false,
    responseStatus: kind === 'response' ? 503 : 404,
    responseBody: kind === 'response' ? 'This site is temporarily down for maintenance.' : null,
    responseContentType: 'text/plain; charset=utf-8',
    advancedRoutesJson: null,
  };
}

/** Editable copy of an existing host (fills any members the server omitted because they were null). */
export function toFields(h: SiteHost): SiteHostFields {
  const { id: _id, createdAt: _c, updatedAt: _u, ...rest } = h;
  const base = newHost(h.kind);
  return {
    ...base,
    ...rest,
    healthCheck: { ...base.healthCheck, ...rest.healthCheck },
    upstreams: rest.upstreams ?? [],
    locations: (rest.locations ?? []).map((l) => ({ ...l, upstreams: l.upstreams ?? [] })),
    requestHeaders: rest.requestHeaders ?? [],
    responseHeaders: rest.responseHeaders ?? [],
    domains: rest.domains ?? [],
    acmeChallenge: rest.acmeChallenge ?? 'default',
  };
}

/** Normalises the form before sending (trims, empty strings → null for optional values). */
export function toPayload(h: SiteHostFields): SiteHostFields {
  const trimOrNull = (s: string | null | undefined) => (s && s.trim() ? s.trim() : null);
  const cleanUpstreams = (list: Upstream[]) =>
    list.map((u) => ({ ...u, host: u.host.trim().replace(/^\[(.*)\]$/, '$1') }));
  const cleanHeaders = (list: HeaderOp[]) =>
    list.map((o) => ({ ...o, name: o.name.trim(), value: o.action === 'delete' ? '' : o.value }));
  return {
    ...h,
    domains: h.domains.map((d) => d.trim().toLowerCase()).filter(Boolean),
    notes: trimOrNull(h.notes),
    certificateId: h.tls === 'custom' ? trimOrNull(h.certificateId) : null,
    accessListId: trimOrNull(h.accessListId),
    upstreams: cleanUpstreams(h.upstreams),
    locations: h.locations.map((l) => ({ ...l, path: l.path.trim(), upstreams: cleanUpstreams(l.upstreams) })),
    requestHeaders: cleanHeaders(h.requestHeaders),
    responseHeaders: cleanHeaders(h.responseHeaders),
    upstreamHostHeader: trimOrNull(h.upstreamHostHeader),
    redirectTarget: trimOrNull(h.redirectTarget),
    rootPath: trimOrNull(h.rootPath),
    responseBody: h.responseBody ?? null,
    advancedRoutesJson: trimOrNull(h.advancedRoutesJson),
    upstreamNtlm: h.kind === 'proxy' ? !!h.upstreamNtlm : false,
    // The challenge only matters for ACME; other modes store the default so a later provider removal cannot invalidate them.
    acmeChallenge: h.tls === 'acme' ? h.acmeChallenge : 'default',
    forceHttps: h.tls === 'none' ? false : h.forceHttps,
    hsts: h.tls === 'none' ? false : h.hsts,
  };
}

function validateUpstreams(list: Upstream[], prefix: string, errors: FieldErrors) {
  if (list.length === 0) errors[prefix] = 'Add at least one upstream server.';
  list.forEach((u, i) => {
    if (!u.host.trim()) errors[`${prefix}.${i}.host`] = 'Enter the upstream host name or IP address.';
    else if (!isValidUpstreamHost(u.host)) errors[`${prefix}.${i}.host`] = `"${u.host}" is not a valid host name or IP address.`;
    if (!isValidPort(u.port)) errors[`${prefix}.${i}.port`] = 'Port must be between 1 and 65535.';
  });
}

function validateHeaders(list: HeaderOp[], prefix: string, errors: FieldErrors) {
  list.forEach((o, i) => {
    if (!o.name.trim()) errors[`${prefix}.${i}.name`] = 'Enter a header name.';
    else if (o.name.includes('*'))
      errors[`${prefix}.${i}.name`] = `"${o.name}" is not allowed: Caddy treats "*" in a header name as a wildcard ("*" would delete every header).`;
    else if (!/^[A-Za-z0-9!#$%&'+.^_`|~-]+$/.test(o.name.trim()))
      errors[`${prefix}.${i}.name`] = `"${o.name}" is not a valid header name.`;
  });
}

/** Client-side mirror of the server's validation rules. */
export interface HostValidationContext {
  /** Settings › Caddy has a DNS provider (false = not configured; undefined = settings not loaded yet). */
  dnsProviderConfigured?: boolean;
}

export function validateHost(h: SiteHostFields, ctx: HostValidationContext = {}): FieldErrors {
  const e: FieldErrors = {};
  if (h.domains.length === 0) e.domains = 'Add at least one domain name.';
  const bad = h.domains.filter((d) => !isValidSiteDomain(d));
  if (bad.length) e.domains = `Invalid domain name: ${bad.join(', ')}`;

  if (h.kind === 'proxy') {
    validateUpstreams(h.upstreams, 'upstreams', e);
    if (h.healthCheck.enabled) {
      if (!h.healthCheck.path.startsWith('/')) e['healthCheck.path'] = 'The health check path must start with "/".';
      if (!(h.healthCheck.intervalSeconds >= 1)) e['healthCheck.intervalSeconds'] = 'Interval must be at least 1 second.';
      if (!(h.healthCheck.timeoutSeconds >= 1)) e['healthCheck.timeoutSeconds'] = 'Timeout must be at least 1 second.';
      const s = h.healthCheck.expectStatus;
      if (!((s >= 0 && s <= 5) || (s >= 100 && s <= 599))) e['healthCheck.expectStatus'] = 'Use 0 (any 2xx), a class 1–5 (e.g. 3 = any 3xx) or a status code 100–599.';
    }
    const paths = new Set<string>();
    h.locations.forEach((l, i) => {
      const p = l.path.trim();
      if (!p.startsWith('/')) e[`locations.${i}.path`] = 'The path must start with "/".';
      else if (paths.has(p)) e[`locations.${i}.path`] = `The path ${p} is used by another location.`;
      paths.add(p);
      validateUpstreams(l.upstreams, `locations.${i}.upstreams`, e);
    });
    validateHeaders(h.requestHeaders, 'requestHeaders', e);
  }
  if (h.kind === 'redirect') {
    if (!h.redirectTarget?.trim()) e.redirectTarget = 'Enter the URL to redirect to.';
    else if (!isAbsoluteHttpUrl(h.redirectTarget))
      e.redirectTarget = 'Enter an absolute URL starting with http:// or https://.';
    if (!REDIRECT_CODES.some((c) => c.value === h.redirectCode)) e.redirectCode = 'Choose 301, 302, 303, 307 or 308.';
  }
  if (h.kind === 'static' && !h.rootPath?.trim()) e.rootPath = 'Enter the folder to serve.';
  if (h.kind === 'response') {
    if (!(Number.isInteger(h.responseStatus) && h.responseStatus >= 200 && h.responseStatus <= 599))
      e.responseStatus = 'Status must be between 200 and 599 (1xx codes are informational).';
    if (!h.responseContentType.trim()) e.responseContentType = 'Enter a content type.';
  }
  if (h.tls === 'custom' && !h.certificateId) e.certificateId = 'Choose a certificate, or pick another TLS mode.';
  if (h.tls === 'acme' && h.acmeChallenge === 'dns' && ctx.dnsProviderConfigured === false)
    e.acmeChallenge = 'The DNS challenge needs a DNS provider. Configure one in Settings › Caddy, or choose another challenge.';
  if (h.tls !== 'none' && h.hsts && !(h.hstsMaxAgeSeconds >= 0)) e.hstsMaxAgeSeconds = 'Enter a max-age in seconds.';
  validateHeaders(h.responseHeaders, 'responseHeaders', e);
  const jsonErr = jsonArrayError(h.advancedRoutesJson);
  if (jsonErr) e.advancedRoutesJson = jsonErr;
  return e;
}

export type HostTab = 'details' | 'tls' | 'access' | 'headers' | 'locations' | 'advanced';

const TAB_OF_FIELD: Record<string, HostTab> = {
  tls: 'tls',
  certificateId: 'tls',
  acmeChallenge: 'tls',
  forceHttps: 'tls',
  hsts: 'tls',
  hstsSubdomains: 'tls',
  hstsMaxAgeSeconds: 'tls',
  accessListId: 'access',
  blockExploits: 'access',
  requestHeaders: 'headers',
  responseHeaders: 'headers',
  locations: 'locations',
  advancedRoutesJson: 'advanced',
  accessLog: 'advanced',
  notes: 'advanced',
};

const DETAIL_FIELDS = new Set([
  'domains', 'upstreams', 'loadBalancing', 'healthCheck', 'upstreamTlsInsecure', 'upstreamNtlm', 'upstreamHostHeader',
  'redirectTarget', 'redirectCode', 'preservePath', 'rootPath', 'browse', 'spaFallback', 'responseStatus',
  'responseBody', 'responseContentType', 'compression', 'enabled', 'kind',
]);

/** Which tab shows a (normalised) field key; null for keys no field displays. */
export function tabOfField(key: string): HostTab | null {
  const root = key.split('.')[0];
  if (TAB_OF_FIELD[root]) return TAB_OF_FIELD[root];
  if (DETAIL_FIELDS.has(root)) return 'details';
  return null;
}

/** Caddy module provided by github.com/caddyserver/ntlm-transport. */
export const NTLM_MODULE = 'http.reverse_proxy.transport.http_ntlm';
export const NTLM_PLUGIN = 'github.com/caddyserver/ntlm-transport';

/** True when the certificate subjects cover the domain (exact or single-label wildcard). */
export function certCovers(cert: CertificateInfo, domain: string): boolean {
  const d = domain.toLowerCase();
  return cert.subjects.some((s) => {
    const subj = s.toLowerCase();
    if (subj === d) return true;
    if (subj.startsWith('*.')) {
      const suffix = subj.slice(1);
      return d.endsWith(suffix) && !d.slice(0, -suffix.length).includes('.') && d.length > suffix.length;
    }
    return false;
  });
}

/** Parses "https://10.0.0.5:8443" pasted into a host field. */
export function parseUpstreamUrl(text: string): Upstream | null {
  const m = /^(https?):\/\/(\[[^\]]+\]|[^/:\s]+)(?::(\d{1,5}))?\/?$/i.exec(text.trim());
  if (!m) return null;
  const scheme = m[1].toLowerCase() as Upstream['scheme'];
  const port = m[3] ? Number(m[3]) : scheme === 'https' ? 443 : 80;
  return { scheme, host: m[2].replace(/^\[(.*)\]$/, '$1'), port };
}
