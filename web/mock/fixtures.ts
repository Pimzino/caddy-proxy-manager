// Realistic in-memory fixture data for `npm run dev:mock`. Development only — never bundled.
import type {
  AccessList,
  AuditEntry,
  BackupFile,
  BackupSettings,
  BinaryOverview,
  BinarySettings,
  CaddySettings,
  CaddyStatus,
  Certificate,
  CertificateInfo,
  ConfigRevision,
  EventEntry,
  JobInfo,
  LdapSettings,
  NotificationSettings,
  PluginPackage,
  ReadinessCheck,
  ReadinessReport,
  SiteHost,
  SiteHostFields,
  StreamHost,
  UiSettings,
  UserDto,
  WindowsStoreCertificate,
} from '../src/api/types.ts';

let seq = 1000;
export const newId = () => (seq++).toString(16).padStart(12, 'a').slice(-12);

const now = () => Date.now();
export const iso = (msAgo = 0) => new Date(now() - msAgo).toISOString();
const MIN = 60_000;
const HOUR = 60 * MIN;
const DAY = 24 * HOUR;

export interface MockUser extends UserDto {
  password: string;
}

export interface StoredAccessList extends Omit<AccessList, 'usedBy' | 'users'> {
  users: { username: string; passwordHash: string }[];
}

export interface CustomCert extends Certificate {
  usedBy?: never;
}

export interface MockJob extends JobInfo {
  script: string[];
  startedMs: number;
  durationMs: number;
  onDone?: () => void;
  fail?: string;
}

export interface MockState {
  needsSetup: boolean;
  sessionUserId: string | null;
  users: MockUser[];
  hosts: SiteHost[];
  streams: StreamHost[];
  accessLists: StoredAccessList[];
  certificates: Certificate[];
  managedCerts: CertificateInfo[];
  caddySettings: CaddySettings & {
    eabMacKey?: string;
    acmeIssuerJson?: string;
    /** Round 3 write-only secrets (never returned by GET). */
    dnsProviderSecrets?: Record<string, string>;
    redisPassword?: string;
    redisEncryptionKey?: string;
    storageJson?: string;
  };
  binarySettings: BinarySettings;
  notificationSettings: NotificationSettings & { smtpPassword?: string; oAuthClientSecret?: string };
  uiSettings: UiSettings & { httpsPfxPassword?: string };
  ldapSettings: LdapSettings & { bindPassword?: string };
  backupSettings: BackupSettings & { password?: string };
  backups: BackupFile[];
  windowsStore: Record<string, WindowsStoreCertificate[]>;
  status: CaddyStatus;
  binary: BinaryOverview;
  revisions: ConfigRevision[];
  runningJson: string | null;
  readiness: ReadinessReport | null;
  events: EventEntry[];
  audit: AuditEntry[];
  jobs: MockJob[];
  restartingUntil: number;
  startedAt: number;
}

const baseHost = (kind: SiteHostFields['kind']): SiteHostFields => ({
  kind,
  enabled: true,
  domains: [],
  acmeChallenge: 'default',
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
  responseStatus: 404,
  responseContentType: 'text/plain; charset=utf-8',
});

function host(kind: SiteHostFields['kind'], patch: Partial<SiteHost>, ageDays = 30): SiteHost {
  return { ...baseHost(kind), id: newId(), createdAt: iso(ageDays * DAY), updatedAt: iso((ageDays / 3) * DAY), ...patch } as SiteHost;
}

const RELEASE_NOTES = `## Highlights

This patch release fixes several bugs and hardens the reverse proxy.

- **reverseproxy:** retry idempotent requests after a dial timeout ([#7112](https://github.com/caddyserver/caddy/pull/7112))
- **tls:** fix OCSP stapling when the responder returns a stale response
- **caddyhttp:** \`encode\` no longer compresses \`206 Partial Content\` responses
- **logging:** the \`file\` writer honours \`roll_keep_for\` again on Windows
- **admin:** reject \`/load\` bodies larger than 10 MB with a clear error

## Upgrading

No configuration changes are required. See https://caddyserver.com/docs for details.
`;

const CATALOG: PluginPackage[] = [
  ['github.com/mholt/caddy-l4', 318_000, ['layer4', 'layer4.handlers.proxy', 'layer4.matchers.tls', 'layer4.matchers.http']],
  ['github.com/caddy-dns/cloudflare', 1_240_000, ['dns.providers.cloudflare']],
  ['github.com/caddy-dns/route53', 402_000, ['dns.providers.route53']],
  ['github.com/caddy-dns/azure', 88_000, ['dns.providers.azure']],
  ['github.com/caddy-dns/digitalocean', 120_500, ['dns.providers.digitalocean']],
  ['github.com/caddy-dns/rfc2136', 64_200, ['dns.providers.rfc2136']],
  ['github.com/caddy-dns/powerdns', 22_100, ['dns.providers.powerdns']],
  ['github.com/mholt/caddy-ratelimit', 356_000, ['http.handlers.rate_limit']],
  ['github.com/greenpau/caddy-security', 512_000, ['security', 'http.handlers.authenticator', 'http.authentication.providers.authorizer']],
  ['github.com/caddyserver/transform-encoder', 211_000, ['caddy.logging.encoders.transform']],
  ['github.com/porech/caddy-maxmind-geolocation', 97_000, ['http.matchers.maxmind_geolocation']],
  ['github.com/caddyserver/replace-response', 143_000, ['http.handlers.replace_response']],
  ['github.com/caddyserver/cache-handler', 131_000, ['http.handlers.cache']],
  ['github.com/mholt/caddy-webdav', 99_300, ['http.handlers.webdav']],
  ['github.com/hslatman/caddy-crowdsec-bouncer', 178_000, ['crowdsec', 'http.handlers.crowdsec']],
  ['github.com/corazawaf/coraza-caddy/v2', 154_000, ['http.handlers.waf']],
  ['github.com/lucaslorentz/caddy-docker-proxy/v2', 690_000, ['docker_proxy']],
  ['github.com/caddyserver/ntlm-transport', 41_000, ['http.reverse_proxy.transport.http_ntlm']],
  ['github.com/abiosoft/caddy-exec', 37_000, ['exec', 'http.handlers.exec']],
  ['github.com/sjtug/caddy2-filter', 12_000, ['http.handlers.filter']],
  ['github.com/tailscale/caddy-tailscale', 58_000, ['tailscale', 'http.authentication.providers.tailscale']],
  ['github.com/ueffel/caddy-brotli', 49_000, ['http.encoders.br']],
].map(([path, downloads, modules]) => ({
  path: path as string,
  repo: `https://${path as string}`.replace(/\/v2$/, ''),
  downloads: downloads as number,
  modules: modules as string[],
}));

/** Caddy modules provided by a plugin package (for the installed binary's module list). */
export function modulesOf(pkg: string): string[] {
  const known = CATALOG.find((p) => p.path === pkg)?.modules;
  if (known) return known;
  // Round 3: every github.com/caddy-dns/<name> package provides dns.providers.<name>; Redis storage for clustering.
  const dns = /^github\.com\/caddy-dns\/([a-z0-9]+)$/.exec(pkg);
  if (dns) return [`dns.providers.${dns[1]}`];
  if (pkg === 'github.com/pberkel/caddy-storage-redis') return ['caddy.storage.redis'];
  return [];
}

export const BASE_MODULES = ['http', 'tls', 'pki', 'http.handlers.reverse_proxy', 'http.handlers.file_server', 'http.reverse_proxy.transport.http'];

export function catalog(q: string): PluginPackage[] {
  const n = q.toLowerCase();
  return CATALOG.filter((p) => !n || p.path.toLowerCase().includes(n) || p.modules.some((m) => m.includes(n))).sort((a, b) => b.downloads - a.downloads);
}

function readinessChecks(): ReadinessCheck[] {
  const fw = (id: string, title: string, status: ReadinessCheck['status'], summary: string, extra: Partial<ReadinessCheck> = {}) => ({
    id,
    category: 'Firewall',
    title,
    status,
    summary,
    fixable: false,
    ...extra,
  });
  return [
    { id: 'system.os', category: 'System', title: 'Operating system', status: 'pass', summary: 'Windows Server 2025 Standard (build 26100.4061)', fixable: false },
    { id: 'system.identity', category: 'System', title: 'Service identity', status: 'pass', summary: 'Running as a Windows service under LocalSystem.', fixable: false },
    { id: 'system.disk', category: 'System', title: 'Free disk space', status: 'pass', summary: '84.2 GB free on C: (data folder).', fixable: false },
    { id: 'system.clock', category: 'System', title: 'Clock accuracy', status: 'pass', summary: 'Clock differs from acme-v02.api.letsencrypt.org by 0.4 s.', fixable: false },
    { id: 'system.reboot', category: 'System', title: 'Pending reboot', status: 'info', summary: 'A reboot is pending (Windows Update).', details: 'HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\WindowsUpdate\\Auto Update\\RebootRequired exists.', remediation: 'Schedule a reboot during the next maintenance window.', fixable: false },
    { id: 'caddy.binary', category: 'Caddy', title: 'Caddy binary', status: 'pass', summary: 'v2.11.3 installed at C:\\ProgramData\\CaddyProxyManager\\caddy\\bin\\caddy.exe', fixable: false },
    { id: 'caddy.service', category: 'Caddy', title: 'Caddy service', status: 'pass', summary: 'Service “Caddy” installed, automatic start, restarts on failure.', fixable: false },
    { id: 'caddy.running', category: 'Caddy', title: 'Caddy running', status: 'pass', summary: 'Running (PID 4412).', fixable: false },
    { id: 'caddy.admin', category: 'Caddy', title: 'Admin API exposure', status: 'pass', summary: 'Admin API listens on 127.0.0.1:2019 only.', fixable: false },
    fw('firewall.service', 'Windows Defender Firewall service', 'pass', 'mpssvc is running.'),
    fw('firewall.profiles', 'Firewall profiles', 'pass', 'Domain, Private and Public profiles are enabled.'),
    fw('firewall.tcp80', 'Inbound TCP 80 (HTTP)', 'pass', 'Allowed by “Caddy Proxy Manager - HTTP (TCP-In)” (GPO: Caddy Proxy Manager - Firewall).'),
    fw('firewall.tcp443', 'Inbound TCP 443 (HTTPS)', 'pass', 'Allowed by “Caddy Proxy Manager - HTTPS (TCP-In)” (GPO).'),
    fw('firewall.udp443', 'Inbound UDP 443 (HTTP/3)', 'warn', 'No inbound allow rule for UDP 443 in the active store. HTTP/3 clients fall back to TCP.', {
      details: 'Active profile: Domain\nPolicyStore: ActiveStore\nMatching rules: none',
      remediation: 'Create an inbound allow rule for UDP 443, or disable HTTP/3 in Settings.',
      script: "New-NetFirewallRule -DisplayName 'Caddy Proxy Manager - HTTP/3 (UDP-In)' -Direction Inbound -Protocol UDP -LocalPort 443 -Action Allow -Profile Any",
      fixable: true,
    }),
    fw('firewall.ui', 'Inbound TCP 81 (management UI)', 'fail', 'No rule allows TCP 81. The console is only reachable from this server.', {
      details: 'GPO setting AllowLocalFirewallRules = True (local rules are merged).',
      remediation: 'Allow TCP 81 from your admin network only.',
      script: "New-NetFirewallRule -DisplayName 'Caddy Proxy Manager - UI (TCP-In)' -Direction Inbound -Protocol TCP -LocalPort 81 -RemoteAddress 10.0.0.0/8 -Action Allow -Profile Domain",
      fixable: true,
    }),
    { id: 'network.profile', category: 'Network', title: 'Network profile', status: 'pass', summary: 'Ethernet0 is DomainAuthenticated (corp.example.com).', fixable: false },
    {
      id: 'domain.gpo',
      category: 'Domain',
      title: 'Deploy firewall rules via Group Policy',
      status: 'info',
      summary: 'Domain joined (corp.example.com). Deploy the inbound rules through a GPO so they survive policy refreshes.',
      remediation: 'Run the GPO script from the panel on the right as a Domain Admin. Also deploy the Caddy internal root CA to “Trusted Root Certification Authorities” because hosts use Internal TLS.',
      fixable: false,
    },
    { id: 'ports.tcp80', category: 'Ports', title: 'TCP 80 listener', status: 'pass', summary: 'Owned by caddy.exe (PID 4412).', fixable: false },
    { id: 'ports.tcp443', category: 'Ports', title: 'TCP 443 listener', status: 'pass', summary: 'Owned by caddy.exe (PID 4412).', fixable: false },
    {
      id: 'ports.iis',
      category: 'Ports',
      title: 'IIS (W3SVC)',
      status: 'warn',
      summary: 'World Wide Web Publishing Service is installed and set to Manual.',
      details: 'W3SVC status: Stopped, StartType: Manual\nhttp.sys URL reservations: http://+:80/Temporary_Listen_Addresses/',
      remediation: 'If IIS is not needed, disable it so it cannot take ports 80/443 after a reboot.',
      script: 'Stop-Service W3SVC -ErrorAction SilentlyContinue\nSet-Service W3SVC -StartupType Disabled',
      fixable: true,
    },
    { id: 'connectivity.acme', category: 'Connectivity', title: 'Let’s Encrypt', status: 'pass', summary: 'acme-v02.api.letsencrypt.org:443 reachable (38 ms).', fixable: false },
    { id: 'connectivity.github', category: 'Connectivity', title: 'GitHub (updates)', status: 'pass', summary: 'api.github.com:443 reachable (21 ms).', fixable: false },
    { id: 'connectivity.caddyserver', category: 'Connectivity', title: 'caddyserver.com (plugins)', status: 'pass', summary: 'caddyserver.com:443 reachable (95 ms).', fixable: false },
    { id: 'connectivity.proxy', category: 'Connectivity', title: 'WinHTTP proxy', status: 'info', summary: 'Direct access (no proxy server).', fixable: false },
    { id: 'dns.app.example.com', category: 'DNS', title: 'app.example.com', status: 'pass', summary: 'Resolves to 203.0.113.10 (this server’s public IP).', fixable: false },
    {
      id: 'dns.grafana.example.com',
      category: 'DNS',
      title: 'grafana.example.com',
      status: 'warn',
      summary: 'Resolves to 198.51.100.77, which is not an address of this server.',
      details: 'Local IPs: 10.0.0.15, fe80::1c2d:4ff:fe10:2a1\nPublic IP (api.ipify.org): 203.0.113.10',
      remediation: 'Point the DNS record at 203.0.113.10, or ACME validation will fail.',
      fixable: false,
    },
  ];
}

export function readinessReport(): ReadinessReport {
  const checks = readinessChecks();
  return summarize({
    ranAt: iso(2 * HOUR),
    machine: {
      hostname: 'WEB-PROXY01',
      fqdn: 'web-proxy01.corp.example.com',
      osDescription: 'Microsoft Windows Server 2025 Standard 10.0.26100',
      isWindows: true,
      domainJoined: true,
      domain: 'corp.example.com',
      computerDn: 'CN=WEB-PROXY01,OU=Web,OU=Servers,DC=corp,DC=example,DC=com',
      ipAddresses: ['10.0.0.15', 'fe80::1c2d:4ff:fe10:2a1'],
      networkProfiles: [{ interfaceAlias: 'Ethernet0', name: 'corp.example.com', category: 'DomainAuthenticated' }],
    },
    checks,
    pass: 0,
    warn: 0,
    fail: 0,
  });
}

export function summarize(r: ReadinessReport): ReadinessReport {
  return {
    ...r,
    pass: r.checks.filter((c) => c.status === 'pass').length,
    warn: r.checks.filter((c) => c.status === 'warn').length,
    fail: r.checks.filter((c) => c.status === 'fail').length,
  };
}

export const GPO_SCRIPT = `#Requires -Modules GroupPolicy
# Caddy Proxy Manager - firewall rules via Group Policy
# Run as a Domain Admin on a machine with RSAT Group Policy tools.
$ErrorActionPreference = 'Stop'
$Domain   = 'corp.example.com'
$GpoName  = 'Caddy Proxy Manager - Firewall'
$TargetOu = 'OU=Web,OU=Servers,DC=corp,DC=example,DC=com'

$gpo = Get-GPO -Name $GpoName -ErrorAction SilentlyContinue
if (-not $gpo) { $gpo = New-GPO -Name $GpoName -Comment 'Inbound rules for Caddy (managed by Caddy Proxy Manager)' }
$store = "$Domain\\$GpoName"

$rules = @(
  @{ Name = 'Caddy Proxy Manager - HTTP (TCP-In)';    Protocol = 'TCP'; Port = 80 },
  @{ Name = 'Caddy Proxy Manager - HTTPS (TCP-In)';   Protocol = 'TCP'; Port = 443 },
  @{ Name = 'Caddy Proxy Manager - HTTP/3 (UDP-In)';  Protocol = 'UDP'; Port = 443 },
  @{ Name = 'Caddy Proxy Manager - UI (TCP-In)';      Protocol = 'TCP'; Port = 81 }
)
foreach ($r in $rules) {
  Get-NetFirewallRule -PolicyStore $store -DisplayName $r.Name -ErrorAction SilentlyContinue | Remove-NetFirewallRule
  New-NetFirewallRule -PolicyStore $store -DisplayName $r.Name -Direction Inbound -Protocol $r.Protocol -LocalPort $r.Port -Action Allow -Profile Any | Out-Null
}

if (-not (Get-GPInheritance -Target $TargetOu).GpoLinks.DisplayName -contains $GpoName) {
  New-GPLink -Name $GpoName -Target $TargetOu -LinkEnabled Yes | Out-Null
}
Write-Host "GPO '$GpoName' updated and linked to $TargetOu. Run 'gpupdate /force' on WEB-PROXY01."
`;

export const INTERNAL_ROOT_PEM = `-----BEGIN CERTIFICATE-----
MIIBozCCAUqgAwIBAgIRAMcmockrootcertificateforcpmMAoGCCqGSM49BAMC
MDAxLjAsBgNVBAMTJUNhZGR5IExvY2FsIEF1dGhvcml0eSAtIDIwMjYgRUNDIFJv
b3QwHhcNMjYwMTAxMDAwMDAwWhcNMzUxMTEwMDAwMDAwWjAwMS4wLAYDVQQDEyVD
YWRkeSBMb2NhbCBBdXRob3JpdHkgLSAyMDI2IEVDQyBSb290MFkwEwYHKoZIzj0C
AQYIKoZIzj0DAQcDQgAEmockmockmockmockmockmockmockmockmockmockmock
-----END CERTIFICATE-----
`;

export function createState(): MockState {
  const users: MockUser[] = [
    { id: 'u-admin00001', email: 'admin@example.com', name: 'Alex Morgan', role: 'admin', disabled: false, lastLoginAt: iso(5 * MIN), createdAt: iso(120 * DAY), password: 'x' },
    { id: 'u-oper000002', email: 'operator@example.com', name: 'Sam Patel', role: 'operator', disabled: false, lastLoginAt: iso(2 * DAY), createdAt: iso(90 * DAY), password: 'x' },
    { id: 'u-view000003', email: 'viewer@example.com', name: 'Jordan Lee', role: 'viewer', disabled: false, lastLoginAt: iso(9 * DAY), createdAt: iso(60 * DAY), password: 'x' },
    { id: 'u-old0000004', email: 'svc-monitoring@example.com', name: 'Monitoring (old)', role: 'viewer', disabled: true, createdAt: iso(300 * DAY), password: 'x' },
    { id: 'u-ldap000005', email: 'priya.shah@corp.example.com', name: 'Priya Shah', role: 'operator', disabled: false, lastLoginAt: iso(3 * HOUR), createdAt: iso(14 * DAY), password: '', externalSource: 'ldap' },
  ];

  const officeList: StoredAccessList = {
    id: 'al-office001',
    name: 'Office networks',
    satisfyAny: false,
    passAuthToUpstream: false,
    rules: [
      { action: 'allow', cidr: '10.0.0.0/8' },
      { action: 'allow', cidr: '192.168.0.0/16' },
      { action: 'deny', cidr: 'all' },
    ],
    users: [],
    createdAt: iso(80 * DAY),
    updatedAt: iso(12 * DAY),
  };
  const contractors: StoredAccessList = {
    id: 'al-contr0002',
    name: 'Contractors (VPN or login)',
    satisfyAny: true,
    passAuthToUpstream: false,
    rules: [
      { action: 'allow', cidr: '203.0.113.0/24' },
      { action: 'deny', cidr: 'all' },
    ],
    users: [
      { username: 'contractor-a', passwordHash: '$2a$12$mock' },
      { username: 'contractor-b', passwordHash: '$2a$12$mock' },
    ],
    createdAt: iso(40 * DAY),
    updatedAt: iso(3 * DAY),
  };

  const wildcard: Certificate = {
    id: 'crt-wild0001',
    name: 'Wildcard corp.example.com (2026)',
    source: 'uploaded',
    certPath: 'C:\\ProgramData\\CaddyProxyManager\\certificates\\crt-wild0001\\fullchain.pem',
    keyPath: 'C:\\ProgramData\\CaddyProxyManager\\certificates\\crt-wild0001\\privkey.pem',
    subjects: ['*.corp.example.com', 'corp.example.com'],
    issuer: 'CN=Example Corp Issuing CA 01, DC=corp, DC=example, DC=com',
    notBefore: iso(245 * DAY),
    notAfter: iso(-120 * DAY),
    thumbprint: '3F1A9C0D5E7B2A4C6D8E0F1A2B3C4D5E6F708192',
    createdAt: iso(240 * DAY),
    updatedAt: iso(240 * DAY),
  };
  const legacy: Certificate = {
    id: 'crt-legacy02',
    name: 'Legacy intranet (Sectigo)',
    source: 'uploaded',
    certPath: 'C:\\ProgramData\\CaddyProxyManager\\certificates\\crt-legacy02\\fullchain.pem',
    keyPath: 'C:\\ProgramData\\CaddyProxyManager\\certificates\\crt-legacy02\\privkey.pem',
    subjects: ['legacy.example.com'],
    issuer: 'CN=Sectigo RSA Domain Validation Secure Server CA',
    notBefore: iso(356 * DAY),
    notAfter: iso(-9 * DAY),
    thumbprint: '9A8B7C6D5E4F3A2B1C0D9E8F7A6B5C4D3E2F1A0B',
    createdAt: iso(350 * DAY),
    updatedAt: iso(350 * DAY),
  };
  const pki: Certificate = {
    id: 'crt-pki00003',
    name: 'ERP (auto-renewed by PKI)',
    source: 'filePath',
    certPath: '\\\\fs01.corp.example.com\\pki$\\web-proxy01\\erp\\fullchain.pem',
    keyPath: '\\\\fs01.corp.example.com\\pki$\\web-proxy01\\erp\\privkey.pem',
    subjects: ['erp.corp.example.com'],
    issuer: 'CN=Example Corp Issuing CA 01, DC=corp, DC=example, DC=com',
    notBefore: iso(20 * DAY),
    notAfter: iso(-345 * DAY),
    thumbprint: '11223344556677889900AABBCCDDEEFF00112233',
    createdAt: iso(20 * DAY),
    updatedAt: iso(20 * DAY),
  };

  const pfx: Certificate = {
    id: 'crt-pfx00004',
    name: 'Shop (win-acme PFX)',
    source: 'pfxFile',
    sourcePath: 'C:\\ProgramData\\win-acme\\certificates\\shop.example.com-chain.pfx',
    certPath: 'C:\\ProgramData\\CaddyProxyManager\\certificates\\crt-pfx00004\\fullchain.pem',
    keyPath: 'C:\\ProgramData\\CaddyProxyManager\\certificates\\crt-pfx00004\\privkey.pem',
    subjects: ['shop.example.com', 'www.shop.example.com'],
    issuer: 'CN=R11, O=Let’s Encrypt, C=US',
    notBefore: iso(25 * DAY),
    notAfter: iso(-65 * DAY),
    thumbprint: '5566778899AABBCCDDEEFF001122334455667788',
    lastSyncedAt: iso(12 * MIN),
    createdAt: iso(60 * DAY),
    updatedAt: iso(25 * DAY),
  };
  const store: Certificate = {
    id: 'crt-store005',
    name: 'Exchange OWA (AD CS autoenrollment)',
    source: 'windowsStore',
    storeLocation: 'LocalMachine',
    storeName: 'My',
    storeSubject: 'mail.corp.example.com',
    certPath: 'C:\\ProgramData\\CaddyProxyManager\\certificates\\crt-store005\\fullchain.pem',
    keyPath: 'C:\\ProgramData\\CaddyProxyManager\\certificates\\crt-store005\\privkey.pem',
    subjects: ['mail.corp.example.com', 'autodiscover.corp.example.com'],
    issuer: 'CN=Example Corp Issuing CA 01, DC=corp, DC=example, DC=com',
    notBefore: iso(40 * DAY),
    notAfter: iso(-325 * DAY),
    thumbprint: 'A1B2C3D4E5F60718293A4B5C6D7E8F9012345678',
    lastSyncedAt: iso(6 * MIN),
    lastSyncError: 'Windows store sync: no currently valid certificate with a private key matches “mail.corp.example.com” in LocalMachine\\My; keeping the last exported certificate.',
    createdAt: iso(40 * DAY),
    updatedAt: iso(6 * MIN),
  };

  const hosts: SiteHost[] = [
    host('proxy', { domains: ['app.example.com', 'www.app.example.com'], upstreams: [{ scheme: 'http', host: '10.0.10.21', port: 8080 }], hsts: true, blockExploits: true, accessLog: true, notes: 'Customer portal (IIS on APP01).' }),
    host('proxy', {
      domains: ['grafana.example.com'],
      upstreams: [
        { scheme: 'http', host: '10.0.10.31', port: 3000 },
        { scheme: 'http', host: '10.0.10.32', port: 3000 },
      ],
      loadBalancing: 'leastConn',
      healthCheck: { enabled: true, path: '/api/health', intervalSeconds: 15, timeoutSeconds: 3, expectStatus: 200 },
      accessLog: true,
    }),
    host('proxy', {
      domains: ['intranet.corp.example.com'],
      tls: 'internal',
      upstreams: [{ scheme: 'https', host: 'sp01.corp.example.com', port: 443 }],
      upstreamTlsInsecure: true,
      upstreamNtlm: true,
      notes: 'SharePoint 2019 (Windows authentication).',
      upstreamHostHeader: '{upstream}',
      accessListId: officeList.id,
    }),
    host('proxy', {
      domains: ['wiki.corp.example.com'],
      tls: 'custom',
      certificateId: wildcard.id,
      upstreams: [{ scheme: 'http', host: '10.0.20.40', port: 8090 }],
      accessListId: contractors.id,
      responseHeaders: [
        { action: 'set', name: 'X-Frame-Options', value: 'SAMEORIGIN' },
        { action: 'delete', name: 'Server', value: '' },
      ],
    }),
    host('proxy', {
      domains: ['api.example.com'],
      upstreams: [{ scheme: 'http', host: '10.0.10.50', port: 5000 }],
      locations: [
        { path: '/v2', upstreams: [{ scheme: 'http', host: '10.0.10.51', port: 5000 }], stripPrefix: false, upstreamTlsInsecure: false },
        { path: '/files', upstreams: [{ scheme: 'http', host: '10.0.10.60', port: 9000 }], stripPrefix: true, upstreamTlsInsecure: false },
      ],
      requestHeaders: [{ action: 'set', name: 'X-Real-IP', value: '{http.request.remote.host}' }],
    }),
    host('proxy', { domains: ['erp.corp.example.com'], tls: 'custom', certificateId: pki.id, upstreams: [{ scheme: 'https', host: '10.0.20.80', port: 44300 }], accessListId: officeList.id }),
    host('proxy', { domains: ['legacy.example.com'], enabled: false, tls: 'custom', certificateId: legacy.id, upstreams: [{ scheme: 'http', host: '10.0.99.9', port: 80 }], notes: 'Decommission after migration.' }),
    host('redirect', { domains: ['example.org', 'www.example.org'], redirectTarget: 'https://www.example.com', redirectCode: 301 }),
    host('redirect', { domains: ['old-shop.example.com'], redirectTarget: 'https://shop.example.com', redirectCode: 308, preservePath: true }),
    host('redirect', { domains: ['portal.example.net'], redirectTarget: 'https://app.example.com/login', redirectCode: 302, preservePath: false, tls: 'acme' }),
    host('static', { domains: ['docs.example.com'], rootPath: 'D:\\Sites\\docs\\dist', spaFallback: true, hsts: true }),
    host('static', { domains: ['downloads.corp.example.com'], rootPath: '\\\\fs01.corp.example.com\\public$\\downloads', browse: true, tls: 'internal', accessListId: officeList.id }),
    host('response', { domains: ['maintenance.example.com'], responseStatus: 503, responseBody: 'We are performing scheduled maintenance. Back soon.', responseContentType: 'text/plain; charset=utf-8' }),
    host('response', { domains: ['retired.example.net'], responseStatus: 410, responseBody: '<h1>410 Gone</h1><p>This service has been retired.</p>', responseContentType: 'text/html; charset=utf-8', tls: 'none', forceHttps: false }),
  ];

  const managedCerts: CertificateInfo[] = [
    { id: 'acme/app.example.com', kind: 'acme', name: 'app.example.com', subjects: ['app.example.com'], issuer: 'CN=R11, O=Let’s Encrypt, C=US', notBefore: iso(30 * DAY), notAfter: iso(-60 * DAY), daysRemaining: 60, source: 'acme-v02.api.letsencrypt.org-directory', usedByHostIds: [hosts[0].id] },
    { id: 'acme/www.app.example.com', kind: 'acme', name: 'www.app.example.com', subjects: ['www.app.example.com'], issuer: 'CN=R11, O=Let’s Encrypt, C=US', notBefore: iso(30 * DAY), notAfter: iso(-60 * DAY), daysRemaining: 60, source: 'acme-v02.api.letsencrypt.org-directory', usedByHostIds: [hosts[0].id] },
    { id: 'acme/grafana.example.com', kind: 'acme', name: 'grafana.example.com', subjects: ['grafana.example.com'], issuer: 'CN=E6, O=Let’s Encrypt, C=US', notBefore: iso(78 * DAY), notAfter: iso(-12 * DAY), daysRemaining: 12, source: 'acme-v02.api.letsencrypt.org-directory', usedByHostIds: [hosts[1].id], error: 'Renewal failed: DNS record points to 198.51.100.77 (see Readiness › DNS).' },
    { id: 'acme/docs.example.com', kind: 'acme', name: 'docs.example.com', subjects: ['docs.example.com'], issuer: 'CN=R10, O=Let’s Encrypt, C=US', notBefore: iso(10 * DAY), notAfter: iso(-80 * DAY), daysRemaining: 80, source: 'acme-v02.api.letsencrypt.org-directory', usedByHostIds: [hosts[10].id] },
    { id: 'local/intranet.corp.example.com', kind: 'internal', name: 'intranet.corp.example.com', subjects: ['intranet.corp.example.com'], issuer: 'CN=Caddy Local Authority - ECC Intermediate', notBefore: iso(2 * DAY), notAfter: iso(-5 * DAY), daysRemaining: 5, source: 'local', usedByHostIds: [hosts[2].id] },
    { id: 'local/root', kind: 'internalRoot', name: 'Caddy Local Authority - 2026 ECC Root', subjects: ['Caddy Local Authority - 2026 ECC Root'], issuer: 'CN=Caddy Local Authority - 2026 ECC Root', notBefore: iso(200 * DAY), notAfter: iso(-3450 * DAY), daysRemaining: 3450, source: 'local', usedByHostIds: [] },
  ];

  const revisions: ConfigRevision[] = [];
  const reasons: [string, string, boolean, string?][] = [
    ['Host updated: api.example.com', 'Sam Patel', true],
    ['Host created: docs.example.com', 'Alex Morgan', true],
    ['Host updated: wiki.corp.example.com', 'Alex Morgan', false, 'loading new config: loading http app module: provision http: server srv0: setting up route handlers: route 3: loading handler modules: position 0: loading module \'subroute\': provision http.handlers.subroute: setting up subroutes: route 1: loading handler modules: position 0: loading module \'headers\': unknown module: http.handlers.headerz'],
    ['Settings changed: Caddy', 'Alex Morgan', true],
    ['Certificate replaced: Wildcard corp.example.com (2026)', 'Alex Morgan', true],
    ['Caddy started', 'system', true],
    ['Access list updated: Office networks', 'Sam Patel', true],
    ['Startup', 'system', true],
  ];
  reasons.forEach(([reason, by, success, error], i) =>
    revisions.push({
      id: newId(),
      createdAt: iso((i * 7 + 1) * HOUR),
      updatedAt: iso((i * 7 + 1) * HOUR),
      reason,
      appliedBy: by,
      success,
      error,
      hash: `sha256:${(0x9f3a1c2e7b + i * 7919).toString(16)}d41d8cd98f00b204e9800998ecf8427e`,
      json: '{}',
    }),
  );

  const events: EventEntry[] = [];
  const ev = (ago: number, severity: EventEntry['severity'], category: string, message: string, details?: string, key?: string, notified = true) =>
    events.push({ id: newId(), createdAt: iso(ago), updatedAt: iso(ago), severity, category, message, details, key, notified });
  ev(25 * MIN, 'warning', 'certificate', 'Certificate for grafana.example.com expires in 12 days', 'Issuer: Let’s Encrypt E6\nRenewal attempts are failing: the DNS record points to 198.51.100.77.', 'cert-expiry:acme/grafana.example.com');
  ev(3 * HOUR, 'recovered', 'upstream', 'Upstream 10.0.10.32:3000 is healthy again', undefined, 'upstream:10.0.10.32:3000');
  ev(3 * HOUR + 12 * MIN, 'warning', 'upstream', 'Upstream 10.0.10.32:3000 failed health checks', 'GET http://10.0.10.32:3000/api/health → connection refused (3 consecutive failures)', 'upstream:10.0.10.32:3000');
  ev(9 * HOUR, 'info', 'update', 'Caddy v2.11.4 is available (installed v2.11.3)', 'https://github.com/caddyserver/caddy/releases/tag/v2.11.4', 'update-available:v2.11.4');
  ev(22 * HOUR, 'error', 'config', 'Caddy rejected the configuration for wiki.corp.example.com', reasons[2][3], 'config-failure', true);
  ev(26 * HOUR, 'warning', 'readiness', 'Readiness: inbound TCP 81 (management UI) is not allowed', 'No firewall rule allows TCP 81.', 'readiness:firewall.ui');
  ev(2 * DAY, 'recovered', 'caddy', 'Caddy is running again', 'Restarted automatically by the monitor (attempt 1 of 3).', 'caddy-down');
  ev(2 * DAY + 2 * MIN, 'error', 'caddy', 'Caddy is not running', 'Service “Caddy” stopped unexpectedly (exit code 1).\nLast log line: {"level":"error","msg":"listen tcp :443: bind: An attempt was made to access a socket in a way forbidden by its access permissions."}', 'caddy-down');
  for (let i = 0; i < 30; i++) ev((3 + i) * DAY, i % 3 === 0 ? 'warning' : 'info', i % 2 ? 'upstream' : 'certificate', i % 3 === 0 ? `Upstream 10.0.10.${20 + (i % 9)}:8080 responded slowly` : `Certificate renewed for host${i}.example.com`, undefined, undefined, i % 3 === 0);

  const audit: AuditEntry[] = [];
  const actions: [string, string, string][] = [
    ['updated', 'host', 'api.example.com'],
    ['login', 'user', 'Sam Patel'],
    ['created', 'host', 'docs.example.com'],
    ['applied', 'caddy', 'configuration'],
    ['updated', 'accessList', 'Office networks'],
    ['replaced', 'certificate', 'Wildcard corp.example.com (2026)'],
    ['updated', 'settings', 'Caddy'],
    ['disabled', 'host', 'legacy.example.com'],
    ['restarted', 'caddy', 'service'],
    ['created', 'user', 'Jordan Lee'],
  ];
  for (let i = 0; i < 137; i++) {
    const [action, type, name] = actions[i % actions.length];
    const u = users[i % 3];
    audit.push({ id: newId(), createdAt: iso(i * 5 * HOUR + 11 * MIN), updatedAt: iso(i * 5 * HOUR), userId: u.id, userName: u.name, action, objectType: type, objectName: name, objectId: newId(), details: action === 'updated' && type === 'host' ? 'Changed: upstreams, tls' : undefined, remoteIp: i % 4 === 0 ? '10.0.5.23' : '10.0.5.41' });
  }

  return {
    needsSetup: process.env.MOCK_SETUP === '1',
    sessionUserId: process.env.MOCK_SETUP === '1' || process.env.MOCK_ANON === '1' ? null : process.env.MOCK_ROLE === 'viewer' ? users[2].id : process.env.MOCK_ROLE === 'operator' ? users[1].id : users[0].id,
    users,
    hosts,
    streams: [
      { id: newId(), createdAt: iso(50 * DAY), updatedAt: iso(50 * DAY), enabled: true, protocol: 'tcp', listenPort: 3389, upstreamHost: '10.0.30.10', upstreamPort: 3389, notes: 'RDP gateway' },
      { id: newId(), createdAt: iso(40 * DAY), updatedAt: iso(40 * DAY), enabled: true, protocol: 'udp', listenPort: 1194, upstreamHost: '10.0.30.20', upstreamPort: 1194, notes: 'OpenVPN' },
      { id: newId(), createdAt: iso(10 * DAY), updatedAt: iso(10 * DAY), enabled: false, protocol: 'tcp', listenPort: 5432, upstreamHost: 'pg01.corp.example.com', upstreamPort: 5432 },
    ],
    accessLists: [officeList, contractors],
    certificates: [wildcard, legacy, pki, pfx, store],
    managedCerts,
    caddySettings: {
      mode: 'managed',
      rawCaddyfile: '# Used only in Caddyfile mode\nexample.com {\n\treverse_proxy 10.0.10.21:8080\n}\n',
      acmeEmail: 'hostmaster@example.com',
      acmeCa: 'letsEncrypt',
      hasEabMacKey: false,
      hasAcmeIssuerJson: false,
      tlsConnectionPolicyJson: '{\n  "protocol_min": "tls1.2"\n}',
      disableHttpChallenge: false,
      disableTlsAlpnChallenge: false,
      httpPort: 80,
      httpsPort: 443,
      enableHttp3: false,
      bindAddresses: [],
      defaultSite: 'notFound',
      trustedProxies: [],
      logLevel: 'info',
      adminListen: '127.0.0.1:2019',
      defaultAcmeChallenge: 'http',
      dnsProvider: 'cloudflare',
      dnsProviderOptions: {},
      dnsProviderSecretFields: ['api_token'],
      dnsProviderSecrets: { api_token: 'mockCloudflareToken_0123456789abcdefXYZ' },
      dnsResolvers: [],
      storageBackend: 'local',
      redisAddresses: [],
      redisDb: 0,
      hasRedisPassword: false,
      redisTls: false,
      redisTlsInsecure: false,
      redisKeyPrefix: 'caddy',
      hasRedisEncryptionKey: false,
      hasStorageJson: false,
      trafficStatsEnabled: true,
    },
    binarySettings: {
      plugins: ['github.com/caddy-dns/cloudflare'],
      autoCheckUpdates: true,
      checkIntervalHours: 12,
      autoInstallUpdates: false,
      lastCheckedAt: iso(47 * MIN),
      latestKnownVersion: 'v2.11.4',
      proxyCaddyTraffic: false,
      noProxy: 'localhost,127.0.0.1,::1,10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,.local',
      managerReleaseRepo: 'contoso/caddy-proxy-manager',
    },
    notificationSettings: {
      smtpEnabled: true,
      smtpHost: 'smtp.office365.com',
      smtpPort: 587,
      smtpSecurity: 'startTls',
      smtpAuth: 'oAuth2ClientCredentials',
      smtpUsername: 'caddy-alerts@example.com',
      hasSmtpPassword: true,
      smtpPassword: 'secret',
      oAuthTenantId: 'example.onmicrosoft.com',
      oAuthClientId: '3f2a1b0c-9d8e-4f7a-8b6c-5d4e3f2a1b0c',
      hasOAuthClientSecret: true,
      oAuthClientSecret: 'secret',
      smtpFrom: 'caddy-alerts@example.com',
      recipients: ['it-ops@example.com', 'alex.morgan@example.com'],
      allowInvalidCertificate: false,
      webhookEnabled: false,
      webhookFormat: 'teamsWorkflow',
      writeWindowsEventLog: true,
      alertCaddyDown: true,
      alertConfigFailure: true,
      alertUpstreamUnhealthy: true,
      alertCertificateExpiry: true,
      certificateExpiryDays: 14,
      alertUpdateAvailable: true,
      alertReadinessFailure: true,
      alertServerOffline: true,
      autoRestartCaddy: true,
      cooldownMinutes: 30,
      sendRecoveryNotices: true,
    },
    uiSettings: { port: 81, bindAddress: '0.0.0.0', httpsEnabled: false, redirectHttpToHttps: false, httpsPort: 8443, hasHttpsPfxPassword: false, sessionHours: 12, displayName: 'DMZ proxy – London' },
    ldapSettings: {
      enabled: true,
      server: 'corp.example.com',
      port: 636,
      security: 'ldaps',
      allowInvalidCertificate: false,
      bindDn: 'svc-cpm@corp.example.com',
      hasBindPassword: true,
      bindPassword: 'secret',
      baseDn: 'DC=corp,DC=example,DC=com',
      userFilter: '(&(objectClass=user)(|(sAMAccountName={0})(userPrincipalName={0})))',
      adminGroupDn: 'CN=CPM Admins,OU=Groups,DC=corp,DC=example,DC=com',
      operatorGroupDn: 'CN=CPM Operators,OU=Groups,DC=corp,DC=example,DC=com',
      viewerGroupDn: 'CN=IT Staff,OU=Groups,DC=corp,DC=example,DC=com',
      nestedGroups: true,
    },
    backupSettings: { enabled: true, hourLocal: 2, directory: '\\\\nas01.corp.example.com\\backups\\web-proxy01', keep: 14, hasPassword: true, password: 'secret' },
    backups: Array.from({ length: 6 }, (_, i) => {
      const d = new Date(now() - (i + 1) * DAY);
      d.setHours(2, 0, 0, 0);
      return { name: `cpm-backup-WEB-PROXY01-${d.toISOString().slice(0, 10).replace(/-/g, '')}-0200.zip`, size: 2_400_000 + i * 18_000, createdAt: d.toISOString() };
    }),
    windowsStore: {
      'LocalMachine/My': [
        { thumbprint: 'A1B2C3D4E5F60718293A4B5C6D7E8F9012345678', subject: 'CN=mail.corp.example.com', dnsNames: ['mail.corp.example.com', 'autodiscover.corp.example.com'], issuer: 'CN=Example Corp Issuing CA 01, DC=corp, DC=example, DC=com', notBefore: iso(40 * DAY), notAfter: iso(-325 * DAY), hasPrivateKey: true, exportable: true, template: 'WebServer-Exportable' },
        { thumbprint: '0F1E2D3C4B5A69788796A5B4C3D2E1F00F1E2D3C', subject: 'CN=erp.corp.example.com', dnsNames: ['erp.corp.example.com'], issuer: 'CN=Example Corp Issuing CA 01, DC=corp, DC=example, DC=com', notBefore: iso(20 * DAY), notAfter: iso(-345 * DAY), hasPrivateKey: true, exportable: true, template: 'WebServer-Exportable' },
        { thumbprint: '9988776655443322110099887766554433221100', subject: 'CN=WEB-PROXY01.corp.example.com', dnsNames: ['WEB-PROXY01.corp.example.com'], issuer: 'CN=Example Corp Issuing CA 01, DC=corp, DC=example, DC=com', notBefore: iso(100 * DAY), notAfter: iso(-265 * DAY), hasPrivateKey: true, exportable: false, template: 'Machine' },
        { thumbprint: '1234ABCD1234ABCD1234ABCD1234ABCD1234ABCD', subject: 'CN=old-owa.corp.example.com', dnsNames: ['old-owa.corp.example.com'], issuer: 'CN=Example Corp Issuing CA 01, DC=corp, DC=example, DC=com', notBefore: iso(420 * DAY), notAfter: iso(55 * DAY), hasPrivateKey: true, exportable: true },
        { thumbprint: 'FEDCBA9876543210FEDCBA9876543210FEDCBA98', subject: 'CN=WMSvc-SHA2-WEB-PROXY01', dnsNames: [], issuer: 'CN=WMSvc-SHA2-WEB-PROXY01', notBefore: iso(700 * DAY), notAfter: iso(-3000 * DAY), hasPrivateKey: false, exportable: false },
      ],
      'LocalMachine/WebHosting': [],
    },
    status: {
      binaryInstalled: true,
      serviceInstalled: true,
      state: 'running',
      processId: 4412,
      version: 'v2.11.3',
      adminReachable: true,
      startedAt: iso(2 * DAY - 5 * MIN),
      binaryPath: 'C:\\ProgramData\\CaddyProxyManager\\caddy\\bin\\caddy.exe',
      configPath: 'C:\\ProgramData\\CaddyProxyManager\\caddy\\caddy.json',
      hostMode: 'windows-service',
      serviceStartType: 'Automatic',
    },
    binary: {
      installed: {
        version: 'v2.11.3',
        path: 'C:\\ProgramData\\CaddyProxyManager\\caddy\\bin\\caddy.exe',
        installedAt: iso(34 * DAY),
        plugins: ['github.com/caddy-dns/cloudflare'],
        modules: [...BASE_MODULES, 'dns.providers.cloudflare'],
      },
      latest: { version: 'v2.11.4', publishedAt: iso(3 * DAY), url: 'https://github.com/caddyserver/caddy/releases/tag/v2.11.4', notes: RELEASE_NOTES },
      updateAvailable: true,
      lastCheckedAt: iso(47 * MIN),
      desiredPlugins: ['github.com/caddy-dns/cloudflare'],
      pluginsOutOfSync: false,
      platform: 'windows/amd64',
      canRollback: true,
      previousVersion: 'v2.11.2',
      managerVersion: '1.0.0',
      managerLatestVersion: '1.1.0',
      managerLatestUrl: 'https://github.com/contoso/caddy-proxy-manager/releases/tag/v1.1.0',
      managerUpdateAvailable: true,
    },
    revisions,
    runningJson: null,
    readiness: readinessReport(),
    events,
    audit,
    jobs: [],
    restartingUntil: 0,
    startedAt: now() - 6 * DAY - 3 * HOUR,
  };
}

// ---------------------------------------------------------------- Logs

const LOGGERS = ['http.log', 'tls.obtain', 'tls.cache.maintenance', 'http.handlers.reverse_proxy.health_checker.active', 'admin.api', 'http'];

export function caddyLog(count: number): string[] {
  const out: string[] = [];
  const base = now() / 1000 - count * 7;
  for (let i = 0; i < count; i++) {
    const ts = base + i * 7;
    const r = (i * 7919) % 100;
    if (r < 3)
      out.push(JSON.stringify({ level: 'error', ts, logger: 'tls.obtain', msg: 'could not get certificate from issuer', identifier: 'grafana.example.com', issuer: 'acme-v02.api.letsencrypt.org-directory', error: 'HTTP 403 urn:ietf:params:acme:error:unauthorized - Invalid response from http://grafana.example.com/.well-known/acme-challenge/x: 404' }));
    else if (r < 9)
      out.push(JSON.stringify({ level: 'warn', ts, logger: 'http.handlers.reverse_proxy.health_checker.active', msg: 'unhealthy upstream', host: '10.0.10.32:3000', error: 'dial tcp 10.0.10.32:3000: connectex: No connection could be made because the target machine actively refused it.' }));
    else if (r < 15) out.push(JSON.stringify({ level: 'debug', ts, logger: 'http.handlers.reverse_proxy', msg: 'selected upstream', dial: '10.0.10.21:8080', total_upstreams: 1 }));
    else if (r < 22) out.push(JSON.stringify({ level: 'info', ts, logger: 'tls.cache.maintenance', msg: 'certificate is valid', identifiers: ['app.example.com'], remaining: 5184000 }));
    else if (r < 26) out.push(JSON.stringify({ level: 'info', ts, logger: 'admin.api', msg: 'received request', method: 'GET', host: '127.0.0.1:2019', uri: '/reverse_proxy/upstreams', remote_ip: '127.0.0.1' }));
    else out.push(JSON.stringify({ level: 'info', ts, logger: LOGGERS[i % LOGGERS.length], msg: i % 2 ? 'server running' : 'serving initial configuration', name: 'srv0', protocols: ['h1', 'h2', 'h3'] }));
  }
  return out;
}

export function accessLog(hostName: string, count: number): string[] {
  const paths = ['/', '/login', '/api/v1/orders?page=2', '/static/app.8f3c1.js', '/favicon.ico', '/api/v1/session', '/.env', '/wp-login.php'];
  const out: string[] = [];
  const base = now() / 1000 - count * 3;
  for (let i = 0; i < count; i++) {
    const p = paths[(i * 31) % paths.length];
    const status = p === '/.env' || p === '/wp-login.php' ? 403 : i % 23 === 0 ? 502 : i % 11 === 0 ? 304 : 200;
    out.push(
      JSON.stringify({
        level: status >= 500 ? 'error' : 'info',
        ts: base + i * 3,
        logger: 'http.log.access.log0',
        msg: 'handled request',
        request: { remote_ip: `198.51.100.${(i * 13) % 250}`, proto: 'HTTP/2.0', method: i % 9 === 0 ? 'POST' : 'GET', host: hostName, uri: p, headers: { 'User-Agent': ['Mozilla/5.0'] } },
        bytes_read: 0,
        duration: Number((0.002 + ((i * 17) % 90) / 1000).toFixed(4)),
        size: status === 200 ? 1024 + ((i * 97) % 40000) : 0,
        status,
      }),
    );
  }
  return out;
}

export function managerLog(count: number): string[] {
  const msgs = [
    '[INF] CaddyManager.Ops.MonitorService: Monitor tick: caddy running, 5 upstreams healthy',
    '[INF] CaddyManager.Config.CaddyConfigService: Applied configuration (reason: Host updated: api.example.com) revision 1f3a',
    '[WRN] CaddyManager.Ops.MonitorService: Certificate acme/grafana.example.com expires in 12 days',
    '[INF] CaddyManager.Platform.UpdateChecker: Latest Caddy release v2.11.4 (installed v2.11.3)',
    '[INF] CaddyManager.Ops.Auth: User admin@example.com signed in from 10.0.5.23',
    '[ERR] CaddyManager.Ops.Notifier: Webhook delivery failed: 404 Not Found',
    '[DBG] CaddyManager.Config.CaddyAdminClient: GET http://127.0.0.1:2019/reverse_proxy/upstreams 200 (3 ms)',
  ];
  const out: string[] = [];
  for (let i = 0; i < count; i++) {
    const d = new Date(now() - (count - i) * 45_000);
    out.push(`${d.toISOString().replace('T', ' ').slice(0, 23)} ${msgs[(i * 5) % msgs.length]}`);
  }
  return out;
}

export const JOB_STEPS = (version: string, plugins: string[]) => [
  plugins.length
    ? `Requesting custom build from caddyserver.com with ${plugins.length} plugin(s): ${plugins.join(', ')}`
    : `Downloading https://github.com/caddyserver/caddy/releases/download/${version}/caddy_${version.slice(1)}_windows_amd64.zip`,
  plugins.length ? 'Downloaded 48.7 MB (custom build — no checksum published, verifying by running it)' : 'Downloaded 16.2 MB',
  plugins.length ? 'caddy list-modules --packages: all requested packages present' : 'SHA-512 checksum verified against caddy_checksums.txt',
  `staging\\caddy.exe version → ${version} h1:mock`,
  'staging\\caddy.exe validate --config caddy.json → Valid configuration',
  'Stopping service Caddy…',
  'Moved caddy.exe → caddy.exe.previous',
  'Installed new caddy.exe',
  'Starting service Caddy…',
  'Admin API responded after 1.4 s',
  `Caddy ${version} is running`,
];
