// Round 3 mock API routes. Owner: WEB-SETTINGS builder: /api/cluster*, /api/settings/caddy/dns-providers (and Round 3 settings fields in fixtures/plugin).
// Round 3b: POST /api/dns/delegation-check against a simulated DNS (see DNS_CNAMES).
//
// Environment switch: MOCK_CLUSTER_ROLE=standalone|primary|node (default primary). On a node, mutations of replicated
// resources answer 409 "Managed by the cluster primary" like the real server (see managedNodeGuard).
import type { CaddySettings, ClusterStatus, DelegationCheck, DelegationCheckResult, DnsProviderField, DnsProviderInfo, SiteHost } from '../src/api/types.ts';
import { iso, newId, type MockState } from './fixtures.ts';
import type { HttpError as HttpErrorClass, MockHelpers, MockRoute } from './plugin.ts';

// ---------------------------------------------------------------- DNS provider catalog (docs/research/round3-dns01.md §2)

type F = DnsProviderField;
const str = (name: string, label: string, extra: Partial<F> = {}): F => ({ name, label, secret: false, required: false, type: 'string', ...extra });
const secret = (name: string, label: string, extra: Partial<F> = {}): F => ({ name, label, secret: true, required: true, type: 'string', ...extra });

const PROVIDERS: Omit<DnsProviderInfo, 'package' | 'module' | 'docsUrl' | 'installed'>[] = [
  {
    name: 'cloudflare',
    label: 'Cloudflare',
    notes: 'Use a scoped API token with Zone:Zone:Read and Zone:DNS:Edit, not the global API key.',
    fields: [
      secret('api_token', 'API token', { help: 'Zone:Zone:Read + Zone:DNS:Edit on the zones Caddy issues certificates for.' }),
      secret('zone_token', 'Zone token', { required: false, help: 'Optional separate token with Zone:Zone:Read on all zones (then the API token needs only DNS:Edit).' }),
    ],
  },
  {
    name: 'route53',
    label: 'Amazon Route 53',
    notes: 'Credentials are optional: without them the AWS default chain (environment, shared profile, instance role) is used.',
    fields: [
      str('access_key_id', 'Access key ID', { placeholder: 'AKIA…' }),
      secret('secret_access_key', 'Secret access key', { required: false }),
      secret('session_token', 'Session token', { required: false, help: 'Only for temporary credentials.' }),
      str('region', 'Region', { placeholder: 'us-east-1' }),
      str('profile', 'Profile', { help: 'Named profile from the shared credentials file.' }),
      str('hosted_zone_id', 'Hosted zone ID', { help: 'Skips the zone lookup; needed for private or duplicate zones.' }),
      { name: 'max_retries', label: 'Max retries', secret: false, required: false, type: 'number' },
      { name: 'route53_max_wait', label: 'Max wait for sync', secret: false, required: false, type: 'duration', help: 'Seconds to wait for Route 53 to report INSYNC.' },
      { name: 'wait_for_route53_sync', label: 'Wait for Route 53 sync', secret: false, required: false, type: 'boolean' },
      { name: 'skip_route53_sync_on_delete', label: 'Skip sync on delete', secret: false, required: false, type: 'boolean' },
    ],
  },
  {
    name: 'azure',
    label: 'Azure DNS',
    notes: 'Leave tenant, client ID and secret empty to use the managed identity of the VM.',
    fields: [
      str('subscription_id', 'Subscription ID', { required: true }),
      str('resource_group_name', 'Resource group', { required: true }),
      str('tenant_id', 'Tenant ID'),
      str('client_id', 'Client ID', { help: 'App registration with the DNS Zone Contributor role on the zone.' }),
      secret('client_secret', 'Client secret', { required: false }),
    ],
  },
  { name: 'digitalocean', label: 'DigitalOcean', fields: [secret('auth_token', 'API token')] },
  {
    name: 'googleclouddns',
    label: 'Google Cloud DNS',
    fields: [
      str('gcp_project', 'Project ID', { required: true }),
      str('gcp_application_default', 'Service account JSON path', { placeholder: 'C:\\ProgramData\\gcp\\dns-sa.json', help: 'Leave empty to use application default credentials.' }),
    ],
  },
  {
    name: 'ovh',
    label: 'OVHcloud',
    fields: [
      str('endpoint', 'Endpoint', { required: true, placeholder: 'ovh-eu' }),
      str('application_key', 'Application key', { required: true }),
      secret('application_secret', 'Application secret'),
      secret('consumer_key', 'Consumer key'),
    ],
  },
  { name: 'porkbun', label: 'Porkbun', fields: [secret('api_key', 'API key'), secret('api_secret_key', 'Secret API key')] },
  {
    name: 'namecheap',
    label: 'Namecheap',
    notes: 'The server’s public IP must be whitelisted for API access in the Namecheap account.',
    fields: [
      secret('api_key', 'API key'),
      str('user', 'User name', { required: true }),
      str('api_endpoint', 'API endpoint', { placeholder: 'https://api.namecheap.com/xml.response' }),
      str('client_ip', 'Client IP', { help: 'Public IP sent to the API; detected when empty.' }),
    ],
  },
  { name: 'gandi', label: 'Gandi', fields: [secret('bearer_token', 'Personal access token')] },
  { name: 'godaddy', label: 'GoDaddy', fields: [secret('api_token', 'API key and secret', { placeholder: 'key:secret', help: 'Enter as <key>:<secret>.' })] },
  {
    name: 'duckdns',
    label: 'Duck DNS',
    fields: [secret('api_token', 'Token'), str('override_domain', 'Override domain', { placeholder: 'myname.duckdns.org' }), str('resolver', 'Resolver', { placeholder: '1.1.1.1:53' })],
  },
  { name: 'ionos', label: 'IONOS', fields: [secret('auth_api_token', 'API key', { placeholder: 'prefix.secret' })] },
  { name: 'desec', label: 'deSEC', fields: [secret('token', 'Token')] },
  { name: 'linode', label: 'Akamai (Linode)', fields: [secret('api_token', 'Personal access token'), str('api_url', 'API URL'), str('api_version', 'API version')] },
  { name: 'vultr', label: 'Vultr', fields: [secret('api_token', 'API key')] },
  { name: 'netlify', label: 'Netlify', fields: [secret('personal_access_token', 'Personal access token')] },
  { name: 'namesilo', label: 'NameSilo', fields: [secret('api_token', 'API key')] },
  { name: 'bunny', label: 'Bunny DNS', fields: [secret('access_key', 'API access key')] },
  {
    name: 'dnsimple',
    label: 'DNSimple',
    fields: [secret('api_access_token', 'API access token'), str('account_id', 'Account ID', { required: true, help: 'Required with a user token.' }), str('api_url', 'API URL')],
  },
  {
    name: 'alidns',
    label: 'Alibaba Cloud DNS',
    fields: [str('access_key_id', 'AccessKey ID', { required: true }), secret('access_key_secret', 'AccessKey secret'), str('region_id', 'Region ID'), secret('security_token', 'Security token', { required: false })],
  },
  {
    name: 'powerdns',
    label: 'PowerDNS',
    fields: [
      str('server_url', 'Server URL', { required: true, placeholder: 'http://pdns.corp.local:8081' }),
      secret('api_token', 'API key'),
      str('server_id', 'Server ID', { placeholder: 'localhost' }),
    ],
  },
  {
    name: 'rfc2136',
    label: 'RFC 2136 (BIND, Knot, PowerDNS, ...)',
    notes: 'Dynamic updates signed with a TSIG key. All four fields are needed.',
    fields: [
      str('server', 'Server', { required: true, placeholder: 'ns1.corp.local:53' }),
      str('key_name', 'Key name', { required: true, placeholder: 'caddy-key.' }),
      str('key_alg', 'Key algorithm', { required: true, placeholder: 'hmac-sha256' }),
      secret('key', 'Key (base64)'),
    ],
  },
  {
    name: 'acmedns',
    label: 'ACME-DNS',
    notes: 'Delegate _acme-challenge with a CNAME to your acme-dns server.',
    fields: [str('username', 'User name'), secret('password', 'Password', { required: false }), str('subdomain', 'Subdomain'), str('server_url', 'Server URL', { placeholder: 'https://auth.acme-dns.io' })],
  },
];

function providerCatalog(s: MockState): DnsProviderInfo[] {
  const modules = new Set(s.binary.installed?.modules ?? []);
  const plugins = new Set(s.binary.installed?.plugins ?? []);
  return PROVIDERS.map((p) => {
    const pkg = `github.com/caddy-dns/${p.name}`;
    const module = `dns.providers.${p.name}`;
    return { ...p, package: pkg, module, docsUrl: `https://${pkg}`, installed: modules.has(module) || plugins.has(pkg) };
  });
}

// ---------------------------------------------------------------- PUT /api/settings/caddy: Round 3 write-only fields

type Round3Input = {
  dnsProviderSecrets?: Record<string, string> | null;
  dnsProviderSecretsClear?: boolean;
  redisPassword?: string | null;
  redisEncryptionKey?: string | null;
  storageJson?: string | Record<string, unknown> | null;
};

const ROUND3_WRITE_ONLY = ['dnsProviderSecrets', 'dnsProviderSecretsClear', 'redisPassword', 'redisEncryptionKey', 'storageJson'] as const;
const ROUND3_OUTPUT_ONLY = ['dnsProviderSecretFields', 'hasRedisPassword', 'hasRedisEncryptionKey', 'hasStorageJson'] as const;
const ROUND3_SECRETS = ['dnsProviderSecrets', 'redisPassword', 'redisEncryptionKey', 'storageJson'] as const;

/** Removes stored Round 3 secrets from a GET /api/settings/caddy response (dnsProviderSecretFields/has* stay). */
export function stripRound3Secrets<T extends object>(out: T): T {
  const copy = { ...out } as Record<string, unknown>;
  for (const k of ROUND3_SECRETS) delete copy[k];
  return copy as T;
}

const STORAGE_PLUGINS: Record<string, string> = {
  redis: 'github.com/pberkel/caddy-storage-redis',
  consul: 'github.com/pteich/caddy-tlsconsul',
  s3: 'github.com/ss098/certmagic-s3',
  postgres: 'github.com/yroc92/postgres-storage',
};

/**
 * Validates and applies the Round 3 part of PUT /api/settings/caddy (SPEC "Settings wire shape additions"):
 * DNS provider secrets per field, provider change clearing foreign fields, Redis secrets, storage JSON, storage validation.
 * Returns the body without write-only/output-only keys, ready for Object.assign. Throws 400 like the server.
 */
export function applyRound3CaddySettings(s: MockState, body: Record<string, unknown>, HttpError: typeof HttpErrorClass): Record<string, unknown> {
  const b = body as Partial<CaddySettings> & Round3Input;
  const cs = s.caddySettings;
  const errors: Record<string, string[]> = {};
  const add = (k: string, m: string) => (errors[k] ??= []).push(m);
  const catalog = providerCatalog(s);
  const modules = s.binary.installed?.modules;

  // DNS provider and its fields.
  const providerName = b.dnsProvider === undefined ? cs.dnsProvider : b.dnsProvider || null;
  const provider = providerName ? catalog.find((p) => p.name === providerName) : undefined;
  if (providerName && !provider) add('dnsProvider', `Unknown DNS provider “${providerName}”.`);
  let secrets = { ...(cs.dnsProviderSecrets ?? {}) };
  let options = { ...(b.dnsProviderOptions ?? cs.dnsProviderOptions ?? {}) };
  if (providerName !== cs.dnsProvider) {
    const keep = new Set(provider?.fields.map((f) => f.name) ?? []);
    secrets = Object.fromEntries(Object.entries(secrets).filter(([k]) => keep.has(k)));
    options = Object.fromEntries(Object.entries(options).filter(([k]) => keep.has(k)));
  }
  if (b.dnsProviderSecretsClear) secrets = {};
  for (const [k, v] of Object.entries(b.dnsProviderSecrets ?? {})) {
    if (v === '') delete secrets[k];
    else if (typeof v === 'string') secrets[k] = v;
  }
  const challenge = b.defaultAcmeChallenge ?? cs.defaultAcmeChallenge;
  if (challenge === 'dns' && !providerName) add('dnsProvider', 'Choose a DNS provider, or use the HTTP challenge by default.');
  if (provider) {
    for (const f of provider.fields.filter((x) => x.required)) {
      const present = f.secret ? !!secrets[f.name] : !!options[f.name]?.trim();
      if (!present) add(f.secret ? `dnsProviderSecrets.${f.name}` : `dnsProviderOptions.${f.name}`, `${f.label} is required for ${provider.label}.`);
    }
  }
  if (typeof b.dnsOverrideDomain === 'string' && b.dnsOverrideDomain.trim() && !DELEGATION_NAME.test(b.dnsOverrideDomain.trim()))
    add('dnsOverrideDomain', 'The delegation name must be a valid DNS name without a wildcard, e.g. _acme-challenge.validation.example.net.');
  if (b.dnsPropagationTimeoutSeconds != null && b.dnsPropagationTimeoutSeconds !== -1 && b.dnsPropagationTimeoutSeconds < 1)
    add('dnsPropagationTimeoutSeconds', 'Use a positive number of seconds, or -1 to skip the propagation check.');

  // Storage backend (SPEC "Storage / clustering at the Caddy level").
  const backend = b.storageBackend ?? cs.storageBackend;
  let storageJson = cs.storageJson;
  if (b.storageJson !== undefined && b.storageJson !== null) storageJson = b.storageJson === '' ? undefined : typeof b.storageJson === 'string' ? b.storageJson : JSON.stringify(b.storageJson);
  if (backend === 'fileSystem') {
    const path = (b.storagePath ?? cs.storagePath ?? '').trim();
    if (!/^([A-Za-z]:\\|\\\\[^\\]+\\[^\\]+)/.test(path)) add('storagePath', 'Enter an absolute local path (D:\\CaddyStorage) or a UNC share (\\\\fs01\\caddy).');
    else if (/^\\\\offline\\/i.test(path)) add('storagePath', `The manager cannot write to ${path}: The network path was not found.`);
  }
  if (backend === 'redis') {
    const addrs = b.redisAddresses ?? cs.redisAddresses;
    if (!addrs.length) add('redisAddresses', 'Add at least one Redis server as host:port.');
    if (modules && !modules.includes('caddy.storage.redis'))
      add('storageBackend', `The installed Caddy has no caddy.storage.redis module. Add the plugin ${STORAGE_PLUGINS.redis} and rebuild Caddy first.`);
  }
  if (backend === 'custom') {
    let module: unknown;
    try {
      module = storageJson ? (JSON.parse(storageJson) as { module?: unknown }).module : undefined;
    } catch {
      add('storageJson', 'Invalid JSON.');
    }
    if (!storageJson) add('storageJson', 'Enter the storage module configuration as a JSON object.');
    else if (typeof module !== 'string' || !module) add('storageJson', 'The object needs a string "module", e.g. "consul".');
    else if (modules && module !== 'file_system' && !modules.includes(`caddy.storage.${module}`))
      add('storageJson', `The installed Caddy has no caddy.storage.${module} module.${STORAGE_PLUGINS[module] ? ` Add the plugin ${STORAGE_PLUGINS[module]} and rebuild Caddy first.` : ' Add its plugin and rebuild Caddy first.'}`);
  }
  if (Object.keys(errors).length) throw new HttpError(400, 'Invalid request', 'One or more fields are invalid.', errors);

  cs.dnsProviderSecrets = secrets;
  cs.dnsProviderSecretFields = Object.keys(secrets);
  if (typeof b.redisPassword === 'string') {
    if (b.redisPassword === '') delete cs.redisPassword;
    else cs.redisPassword = b.redisPassword;
  }
  if (typeof b.redisEncryptionKey === 'string') {
    if (b.redisEncryptionKey === '') delete cs.redisEncryptionKey;
    else cs.redisEncryptionKey = b.redisEncryptionKey;
  }
  cs.storageJson = storageJson;
  cs.hasRedisPassword = !!cs.redisPassword;
  cs.hasRedisEncryptionKey = !!cs.redisEncryptionKey;
  cs.hasStorageJson = !!cs.storageJson;

  const rest: Record<string, unknown> = { ...body, dnsProvider: providerName ?? undefined, dnsProviderOptions: options };
  if (!providerName) delete cs.dnsProvider;
  for (const k of [...ROUND3_WRITE_ONLY, ...ROUND3_OUTPUT_ONLY]) delete rest[k];
  return rest;
}

// ---------------------------------------------------------------- DNS challenge delegation check (SPEC round 3b)

export const DELEGATION_NAME = /^(?=.{1,253}$)([a-z0-9_]([a-z0-9_-]{0,61}[a-z0-9])?\.)+[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.?$/i;
const norm = (n: string) => n.trim().toLowerCase().replace(/\.$/, '');

/**
 * Simulated DNS: CNAME chains at _acme-challenge names as the OS resolvers see them. Names under corp.example.com exist only in
 * internal DNS (public resolvers answer NXDOMAIN); portal.example.net's authoritative servers time out; docs has a TXT record.
 * Names not listed get a stable pseudo-random answer so any typed domain shows a realistic mix.
 */
const DNS_CNAMES: Record<string, string[]> = {
  '_acme-challenge.app.example.com': ['_acme-challenge.validation.example.net'],
  '_acme-challenge.www.app.example.com': ['_acme-challenge.app.example.com', '_acme-challenge.validation.example.net'],
  '_acme-challenge.grafana.example.com': ['_acme-challenge.old-validation.example.net'],
  '_acme-challenge.shop.example.com': ['_acme-challenge.shop.validation.example.net'],
  '_acme-challenge.wiki.corp.example.com': ['_acme-challenge.validation.example.net'],
};
const DNS_MISSING = new Set(['_acme-challenge.api.example.com']);
const DNS_TXT_ONLY = new Set(['_acme-challenge.docs.example.com']);
const DNS_TIMEOUT = /(^|\.)portal\.example\.net$/;

function lookupDelegation(recordName: string, expected: string, publicDns: boolean): Omit<DelegationCheck, 'domain' | 'recordName' | 'expectedTarget'> {
  const base = recordName.replace(/^_acme-challenge\./, '');
  if (DNS_TIMEOUT.test(base)) return { status: 'error', found: [], detail: 'No answer within 5 s (the zone’s name servers did not respond).' };
  if (publicDns && /(^|\.)corp\.example\.com$/.test(base))
    return { status: 'missing', found: [], detail: 'Public DNS does not know this name (NXDOMAIN). It exists only in internal DNS, so a public CA cannot follow it.' };
  if (DNS_TXT_ONLY.has(recordName)) return { status: 'wrong', found: [], detail: 'A TXT record exists at this name instead of a CNAME (left over from an earlier challenge?). Delete it and add the CNAME.' };
  let chain = DNS_CNAMES[recordName];
  if (!chain && !DNS_MISSING.has(recordName)) {
    const h = [...recordName].reduce((a, c) => (a * 31 + c.charCodeAt(0)) >>> 0, 7) % 5;
    if (h === 3) return { status: 'error', found: [], detail: 'SERVFAIL from the resolver.' };
    chain = h === 0 || h === 1 ? [expected] : h === 2 ? [] : [`_acme-challenge.${base.split('.').slice(-2).join('.')}`];
  }
  if (!chain?.length) return { status: 'missing', found: [], detail: `No CNAME record at ${recordName}.` };
  if (chain.includes(expected)) return { status: 'ok', found: chain };
  return { status: 'wrong', found: chain, detail: `Points to ${chain.at(-1)} instead of ${expected}.` };
}

function effectiveDnsDomains(h: SiteHost, s: MockState): { domains: string[]; target: string | null } {
  const cs = s.caddySettings;
  if (h.tls !== 'acme' || !cs.dnsProvider) return { domains: [], target: null };
  const challenge = h.acmeChallenge === 'default' ? cs.defaultAcmeChallenge : h.acmeChallenge;
  const domains = challenge === 'dns' ? h.domains : h.domains.filter((d) => d.startsWith('*.'));
  const target = h.dnsDelegation === 'custom' ? h.dnsOverrideDomain : h.dnsDelegation === 'off' ? null : cs.dnsOverrideDomain;
  return { domains, target: target?.trim() ? norm(target) : null };
}

async function delegationCheck(body: unknown, s: MockState): Promise<DelegationCheckResult> {
  const { HttpError } = helpers!;
  const b = (body ?? {}) as { hostId?: string; domains?: string[]; target?: string; publicResolvers?: boolean; systemResolvers?: boolean };
  // Same order as the API: explicit public, explicit system, configured resolvers, else public DNS.
  const usePublic = !!b.publicResolvers || (!b.systemResolvers && s.caddySettings.dnsResolvers.length === 0);
  let domains: string[];
  let target: string | null;
  if (b.hostId) {
    const host = s.hosts.find((h) => h.id === b.hostId);
    if (!host) throw new HttpError(404, 'Not found', 'The host does not exist.');
    ({ domains, target } = effectiveDnsDomains(host, s));
    if (!domains.length) throw new HttpError(400, 'Invalid request', `${host.domains[0]} does not use the DNS challenge.`, { hostId: ['This host does not use the DNS challenge.'] });
    if (!target) throw new HttpError(400, 'Invalid request', `${host.domains[0]} does not delegate its DNS challenge.`, { hostId: ['This host has no challenge delegation.'] });
  } else {
    domains = (b.domains ?? []).map(norm).filter(Boolean);
    target = b.target?.trim() ? norm(b.target) : s.caddySettings.dnsOverrideDomain ? norm(s.caddySettings.dnsOverrideDomain) : null;
    const errors: Record<string, string[]> = {};
    if (!domains.length) errors.domains = ['Add at least one domain.'];
    const bad = domains.filter((d) => !DELEGATION_NAME.test(d.replace(/^\*\./, '')));
    if (bad.length) errors.domains = [`Not a valid domain name: ${bad.join(', ')}`];
    if (!target) errors.target = ['No delegation name: enter one, or set the default in Settings › Caddy.'];
    else if (!DELEGATION_NAME.test(target)) errors.target = ['The delegation name must be a valid DNS name without a wildcard.'];
    if (Object.keys(errors).length) throw new HttpError(400, 'Invalid request', 'One or more fields are invalid.', errors);
  }
  await new Promise((r) => setTimeout(r, 300 + Math.random() * 500));
  const expected = target!;
  const seen = new Set<string>();
  const checks: DelegationCheck[] = [];
  for (const domain of domains) {
    const recordName = `_acme-challenge.${norm(domain).replace(/^\*\./, '')}`;
    if (seen.has(recordName)) continue;
    seen.add(recordName);
    checks.push({ domain, recordName, expectedTarget: expected, ...lookupDelegation(recordName, expected, usePublic) });
  }
  const resolvers = usePublic ? ['1.1.1.1:53', '8.8.8.8:53'] : b.systemResolvers ? ['system'] : s.caddySettings.dnsResolvers;
  return { resolvers, checks, checkedAt: iso() };
}

// ---------------------------------------------------------------- Cluster

const envRole = process.env.MOCK_CLUSTER_ROLE;
const cluster: { role: ClusterStatus['role']; primaryName: string | null; nodeCount: number; appliedRevision: string | null; joinedAt: string | null } = {
  role: envRole === 'standalone' || envRole === 'node' ? envRole : 'primary',
  primaryName: envRole === 'node' ? 'WEB-PROXY01' : null,
  nodeCount: envRole === 'standalone' || envRole === 'node' ? 0 : 2,
  appliedRevision: envRole === 'node' ? '5f0c2a9e41d7b3c8a6e2f19d0b4c7a3e8f1d6b2c9a0e4f7d3b8c1a6e5d9f2b04' : null,
  joinedAt: envRole === 'node' ? iso(6 * 86_400_000) : null,
};

/** Cluster role of the mock server (for the other Round 3 mock modules). */
export function mockCluster() {
  return cluster;
}

function clusterStatus(s: MockState): ClusterStatus {
  // Local storage in a cluster is explained by the storage section of the Cluster tab (from storageBackend), not here.
  const warnings: string[] = [];
  return {
    role: cluster.role,
    serverName: cluster.role === 'node' ? 'WEB-PROXY02' : 'WEB-PROXY01',
    primaryName: cluster.role === 'node' ? cluster.primaryName : null,
    lastPrimaryContactAt: cluster.role === 'node' ? iso(9_000) : null,
    appliedRevision: cluster.role === 'node' ? cluster.appliedRevision : null,
    nodeCount: cluster.role === 'primary' ? cluster.nodeCount : 0,
    storageBackend: s.caddySettings.storageBackend,
    warnings,
  };
}

let helpers: MockHelpers | null = null;

const REPLICATED = [/^\/api\/hosts(\/|$)/, /^\/api\/streams(\/|$)/, /^\/api\/access-lists(\/|$)/, /^\/api\/certificates(\/|$)/, /^\/api\/config\/caddyfile\/import(\/|$)/];
/** CaddySettings.NodeLocalProperties (Core Models/Settings.cs). */
const NODE_LOCAL = ['httpPort', 'httpsPort', 'publicHttpsPort', 'bindAddresses', 'adminListen', 'certificateStorePath', 'customAcmeRootPath'];

/**
 * On a managed node, mutations of replicated resources answer 409 (ApiResults.ManagedByPrimary). PUT /api/settings/caddy
 * is accepted when only node-local fields change. Called by the mock dispatcher before every handler.
 */
export function managedNodeGuard(method: string, path: string, body: unknown, s: MockState) {
  if (cluster.role !== 'node' || method === 'GET' || !helpers) return;
  const reject = () => {
    throw new helpers!.HttpError(
      409,
      'Managed by the cluster primary',
      `This server is a node managed by '${cluster.primaryName ?? 'the primary'}'. Make this change on the primary.`,
    );
  };
  if (REPLICATED.some((re) => re.test(path))) reject();
  if (method === 'PUT' && path === '/api/caddy/plugins') reject();
  if (method === 'PUT' && path === '/api/settings/caddy') {
    const b = (body ?? {}) as Record<string, unknown>;
    const current = s.caddySettings as unknown as Record<string, unknown>;
    const writeOnlyChange = ROUND3_WRITE_ONLY.some((k) => b[k] !== undefined && b[k] !== null && b[k] !== false) || !!b.eabMacKey || !!b.acmeIssuerJson;
    const changed = Object.keys(b).filter(
      (k) => !NODE_LOCAL.includes(k) && !(ROUND3_WRITE_ONLY as readonly string[]).includes(k) && !(ROUND3_OUTPUT_ONLY as readonly string[]).includes(k) &&
        !['eabMacKey', 'acmeIssuerJson', 'hasEabMacKey', 'hasAcmeIssuerJson'].includes(k) && JSON.stringify(b[k] ?? null) !== JSON.stringify(current[k] ?? null),
    );
    if (writeOnlyChange || changed.length) reject();
  }
}

function sessionRole(s: MockState) {
  const u = s.users.find((x) => x.id === s.sessionUserId && !x.disabled);
  if (!u) throw new helpers!.HttpError(401, 'Not signed in');
  return u;
}

function requireAdmin(s: MockState) {
  const u = sessionRole(s);
  if (u.role !== 'admin') throw new helpers!.HttpError(403, 'Forbidden', 'This action requires the admin role.');
  return u;
}

function auditEntry(s: MockState, action: string, objectName: string, details?: string) {
  const u = s.users.find((x) => x.id === s.sessionUserId);
  s.audit.unshift({ id: newId(), createdAt: iso(), updatedAt: iso(), userId: u?.id, userName: u?.name ?? 'system', action, objectType: 'cluster', objectName, details, remoteIp: '127.0.0.1' });
}

/** Parses a join token cpmj1.<base64url(json {v, primary, nodeId, secret})> (SPEC round 3 "Security"). */
function parseJoinToken(token: string): { primary: string; nodeId: string } | null {
  const m = /^cpmj1\.([A-Za-z0-9_-]+)$/.exec(token.trim());
  if (!m) return null;
  try {
    const json = JSON.parse(Buffer.from(m[1], 'base64url').toString('utf8')) as { v?: number; primary?: string; nodeId?: string; secret?: string };
    if (json.v !== 1 || !json.primary || !json.nodeId || !json.secret) return null;
    return { primary: json.primary, nodeId: json.nodeId };
  } catch {
    return null;
  }
}

export function round3SettingsRoutes(h: MockHelpers): MockRoute[] {
  helpers = h;
  const { HttpError, ok } = h;
  return [
    ['GET', '/api/settings/caddy/dns-providers', (_c, s) => {
      sessionRole(s);
      return ok(providerCatalog(s));
    }],
    ['POST', '/api/dns/delegation-check', async (c, s) => {
      sessionRole(s);
      return ok(await delegationCheck(c.body, s));
    }],
    ['GET', '/api/cluster', (_c, s) => {
      sessionRole(s);
      return ok(clusterStatus(s));
    }],
    ['POST', '/api/cluster/join', (c, s) => {
      requireAdmin(s);
      if (cluster.role !== 'standalone')
        throw new HttpError(409, 'Conflict', cluster.role === 'node' ? `This server already belongs to ${cluster.primaryName}. Leave that cluster first.` : 'This server is a primary with nodes. Remove its nodes before it can join another cluster.');
      const token = String((c.body as { token?: unknown }).token ?? '');
      const parsed = parseJoinToken(token);
      if (!parsed) throw new HttpError(400, 'Invalid request', 'The join token is not valid.', { token: ['Paste the whole token shown on the primary (it starts with cpmj1.).'] });
      if (/fail/i.test(parsed.primary))
        throw new HttpError(502, 'Primary unreachable', `Could not reach ${parsed.primary}: the primary did not confirm the token (connection refused).`);
      cluster.role = 'node';
      cluster.primaryName = parsed.primary;
      cluster.appliedRevision = null;
      cluster.joinedAt = iso();
      auditEntry(s, 'joined', parsed.primary, `Node id ${parsed.nodeId}`);
      return ok(clusterStatus(s));
    }],
    ['POST', '/api/cluster/leave', (_c, s) => {
      requireAdmin(s);
      if (cluster.role !== 'node') throw new HttpError(409, 'Conflict', 'This server is not a cluster node.');
      auditEntry(s, 'left', cluster.primaryName ?? 'primary');
      cluster.role = 'standalone';
      cluster.primaryName = null;
      cluster.appliedRevision = null;
      return ok(clusterStatus(s));
    }],
  ];
}
