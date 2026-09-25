// Wire types derived from src/CaddyManager.Core (Models/*.cs, Contracts/Dtos.cs).
// System.Text.Json: camelCase properties, camelCase string enums, null properties omitted.
// Nullable C# members are therefore optional here. Dates are ISO-8601 strings (UTC).

export type IsoDate = string;

// ---------------------------------------------------------------- Enums

export type HostKind = 'proxy' | 'redirect' | 'static' | 'response';
export type TlsMode = 'none' | 'acme' | 'internal' | 'custom';
export type LoadBalancingPolicy = 'roundRobin' | 'random' | 'leastConn' | 'ipHash' | 'first' | 'cookie' | 'uriHash';
export type UpstreamScheme = 'http' | 'https';
export type HeaderAction = 'set' | 'add' | 'delete';
export type StreamProtocol = 'tcp' | 'udp';
export type IpRuleAction = 'allow' | 'deny';
export type CertificateSource = 'uploaded' | 'filePath' | 'pfxFile' | 'windowsStore';
export type AcmeCa = 'letsEncrypt' | 'letsEncryptStaging' | 'zeroSsl' | 'custom';
export type DefaultSiteBehavior = 'notFound' | 'closeConnection' | 'redirect' | 'caddyWelcome';
export type ConfigMode = 'managed' | 'caddyfile';
export type SmtpSecurity = 'none' | 'startTls' | 'sslOnConnect' | 'auto';
export type SmtpAuthMode = 'none' | 'password' | 'oAuth2ClientCredentials';
export type WebhookFormat = 'generic' | 'slack' | 'teamsWorkflow';
export type LdapSecurity = 'none' | 'startTls' | 'ldaps';
export type UserRole = 'viewer' | 'operator' | 'admin';
export type EventSeverity = 'info' | 'warning' | 'error' | 'recovered';
export type CaddyRunState = 'notInstalled' | 'stopped' | 'starting' | 'running' | 'stopping' | 'unknown';
export type CertificateKind = 'custom' | 'acme' | 'internal' | 'internalRoot';
export type CheckStatus = 'pass' | 'warn' | 'fail' | 'info' | 'skipped';
export type JobState = 'running' | 'succeeded' | 'failed';
export type AcmeChallengeType = 'http' | 'dns';
export type HostAcmeChallenge = 'default' | 'http' | 'dns';
export type StorageBackend = 'local' | 'fileSystem' | 'redis' | 'custom';
export type ClusterRole = 'standalone' | 'primary' | 'node';
export type ServerStatus = 'online' | 'offline' | 'pending' | 'error';
export type TrafficRange = 'hour' | 'day' | 'week' | 'month';

// ---------------------------------------------------------------- Entities (Models/Hosts.cs)

export interface Entity {
  id: string;
  createdAt: IsoDate;
  updatedAt: IsoDate;
}

export interface Upstream {
  scheme: UpstreamScheme;
  host: string;
  port: number;
}

export interface HeaderOp {
  action: HeaderAction;
  name: string;
  value: string;
}

export interface HealthCheck {
  enabled: boolean;
  path: string;
  intervalSeconds: number;
  timeoutSeconds: number;
  /** Expected status code, 0 = any 2xx. */
  expectStatus: number;
}

export interface ProxyLocation {
  path: string;
  upstreams: Upstream[];
  stripPrefix: boolean;
  upstreamTlsInsecure: boolean;
}

export interface SiteHostFields {
  kind: HostKind;
  enabled: boolean;
  domains: string[];
  notes?: string | null;

  tls: TlsMode;
  certificateId?: string | null;
  /** ACME challenge for this host (tls = acme only). "default" = CaddySettings.defaultAcmeChallenge. */
  acmeChallenge: HostAcmeChallenge;
  forceHttps: boolean;
  hsts: boolean;
  hstsSubdomains: boolean;
  hstsMaxAgeSeconds: number;
  compression: boolean;
  accessListId?: string | null;
  blockExploits: boolean;
  accessLog: boolean;
  responseHeaders: HeaderOp[];

  upstreams: Upstream[];
  loadBalancing: LoadBalancingPolicy;
  healthCheck: HealthCheck;
  upstreamTlsInsecure: boolean;
  /** Upstream uses Windows Integrated Authentication (NTLM/Negotiate); needs plugin github.com/caddyserver/ntlm-transport. */
  upstreamNtlm: boolean;
  /** null/"" = keep client Host; "{upstream}" = upstream host:port; otherwise literal. */
  upstreamHostHeader?: string | null;
  requestHeaders: HeaderOp[];
  locations: ProxyLocation[];

  redirectTarget?: string | null;
  redirectCode: number;
  preservePath: boolean;

  rootPath?: string | null;
  browse: boolean;
  spaFallback: boolean;

  responseStatus: number;
  responseBody?: string | null;
  responseContentType: string;

  /** Raw Caddy JSON array of route objects. */
  advancedRoutesJson?: string | null;
}

export interface SiteHost extends Entity, SiteHostFields {}

export interface StreamHostFields {
  enabled: boolean;
  protocol: StreamProtocol;
  listenPort: number;
  upstreamHost: string;
  upstreamPort: number;
  notes?: string | null;
}

export interface StreamHost extends Entity, StreamHostFields {}

export interface IpRule {
  action: IpRuleAction;
  /** IP or CIDR, or "all". */
  cidr: string;
}

/** Access list as returned by the API (password hashes never returned). */
export interface AccessList extends Entity {
  name: string;
  satisfyAny: boolean;
  passAuthToUpstream: boolean;
  rules: IpRule[];
  users: { username: string; hasPassword?: boolean }[];
  usedBy: number;
}

/** Access list create/update body. A user without password keeps its existing hash on PUT. */
export interface AccessListInput {
  name: string;
  satisfyAny: boolean;
  passAuthToUpstream: boolean;
  rules: IpRule[];
  users: { username: string; password?: string }[];
}

export interface Certificate extends Entity {
  name: string;
  source: CertificateSource;
  certPath: string;
  keyPath: string;
  subjects: string[];
  issuer: string;
  notBefore: IsoDate;
  notAfter: IsoDate;
  thumbprint: string;
  notes?: string;
  /** PfxFile source: the referenced .pfx/.p12 (certPath/keyPath point at the converted PEMs in the store). */
  sourcePath?: string;
  /** WindowsStore source: "LocalMachine" or "CurrentUser". */
  storeLocation?: string;
  /** WindowsStore source: store name, e.g. "My". */
  storeName?: string;
  /** WindowsStore source pinned to one certificate. */
  storeThumbprint?: string;
  /** WindowsStore source following renewals by subject/SAN. */
  storeSubject?: string;
  lastSyncedAt?: IsoDate;
  lastSyncError?: string;
}

// ---------------------------------------------------------------- Ops models (Models/Ops.cs)

export interface UserDto {
  id: string;
  email: string;
  name: string;
  role: UserRole;
  disabled: boolean;
  lastLoginAt?: IsoDate;
  createdAt: IsoDate;
  /** Absent = local account; "ldap" = directory account (no local password, role from group mapping). */
  externalSource?: string;
}

export interface AuditEntry extends Entity {
  userId?: string;
  userName: string;
  action: string;
  objectType: string;
  objectId?: string;
  objectName?: string;
  details?: string;
  remoteIp?: string;
}

export interface EventEntry extends Entity {
  severity: EventSeverity;
  category: string;
  message: string;
  details?: string;
  key?: string;
  notified: boolean;
}

export interface ConfigRevision extends Entity {
  json: string;
  hash: string;
  reason: string;
  appliedBy: string;
  success: boolean;
  error?: string;
}

export interface ConfigRevisionSummary {
  id: string;
  createdAt: IsoDate;
  reason: string;
  appliedBy: string;
  success: boolean;
  error?: string;
  hash: string;
}

// ---------------------------------------------------------------- Settings wire shapes
// Rule: class camelCased, minus *Protected, plus output has<Name>, plus write-only input <name>
// (absent/null = unchanged, "" = clear, other = set).

export interface CaddySettings {
  mode: ConfigMode;
  /** Hidden (absent) for non-admins. */
  rawCaddyfile?: string;
  acmeEmail: string;
  acmeCa: AcmeCa;
  customAcmeDirectory?: string | null;
  eabKeyId?: string | null;
  hasEabMacKey: boolean;
  customAcmeRootPath?: string | null;
  disableHttpChallenge: boolean;
  disableTlsAlpnChallenge: boolean;
  httpPort: number;
  httpsPort: number;
  /** HTTPS port clients reach when NAT/port forwarding maps it to httpsPort (e.g. 443 → 8443); null/absent = httpsPort. Used for “Force HTTPS” redirects. */
  publicHttpsPort?: number | null;
  enableHttp3: boolean;
  bindAddresses: string[];
  defaultSite: DefaultSiteBehavior;
  defaultRedirectUrl?: string | null;
  trustedProxies: string[];
  logLevel: string;
  adminListen: string;
  certificateStorePath?: string | null;
  /** Hidden (absent) for non-admins. */
  serverOptionsJson?: string | null;
  /** JSON object of extra top-level Caddy apps for plugins (hidden for non-admins). */
  extraAppsJson?: string | null;
  /** A secret ACME issuer JSON object is stored (e.g. DNS challenge provider credentials). */
  hasAcmeIssuerJson: boolean;
  /** JSON object merged into every TLS connection policy. */
  tlsConnectionPolicyJson?: string | null;

  // ---- Round 3: DNS-01
  defaultAcmeChallenge: AcmeChallengeType;
  /** caddy-dns provider name, e.g. "cloudflare"; absent = none. */
  dnsProvider?: string | null;
  /** Non-secret provider fields by JSON field name. */
  dnsProviderOptions: Record<string, string>;
  /** Names of secret provider fields that have a stored value (values are never returned). */
  dnsProviderSecretFields: string[];
  dnsPropagationDelaySeconds?: number | null;
  /** null = Caddy default (2 min); -1 = skip the propagation check. */
  dnsPropagationTimeoutSeconds?: number | null;
  dnsTtlSeconds?: number | null;
  dnsResolvers: string[];
  dnsOverrideDomain?: string | null;

  // ---- Round 3: storage (clustering)
  storageBackend: StorageBackend;
  storagePath?: string | null;
  redisAddresses: string[];
  redisDb: number;
  redisUsername?: string | null;
  hasRedisPassword: boolean;
  redisTls: boolean;
  redisTlsInsecure: boolean;
  redisKeyPrefix: string;
  hasRedisEncryptionKey: boolean;
  hasStorageJson: boolean;

  // ---- Round 3: traffic statistics
  trafficStatsEnabled: boolean;
}

export type CaddySettingsInput = Omit<
  CaddySettings,
  'hasEabMacKey' | 'hasAcmeIssuerJson' | 'dnsProviderSecretFields' | 'hasRedisPassword' | 'hasRedisEncryptionKey' | 'hasStorageJson'
> & {
  eabMacKey?: string | null;
  /** Write-only: absent/null = unchanged, "" = clear, other = set. */
  acmeIssuerJson?: string | null;
  /** Write-only per field: non-empty = set, "" = remove that field, absent keys unchanged. */
  dnsProviderSecrets?: Record<string, string> | null;
  /** Remove every stored secret field (applied before dnsProviderSecrets). */
  dnsProviderSecretsClear?: boolean;
  redisPassword?: string | null;
  redisEncryptionKey?: string | null;
  /** Custom storage module JSON object (text or object). absent/null = unchanged, "" = clear. */
  storageJson?: string | null;
};

/** Field of a caddy-dns provider (GET /api/settings/caddy/dns-providers). "duration" = seconds in the UI. */
export interface DnsProviderField {
  name: string;
  label: string;
  secret: boolean;
  required: boolean;
  type: 'string' | 'number' | 'boolean' | 'duration';
  placeholder?: string | null;
  help?: string | null;
}

export interface DnsProviderInfo {
  name: string;
  label: string;
  /** Go package for the Caddy build, e.g. github.com/caddy-dns/cloudflare. */
  package: string;
  /** Caddy module id, e.g. dns.providers.cloudflare. */
  module: string;
  docsUrl: string;
  /** The module is compiled into the installed Caddy binary. */
  installed: boolean;
  notes?: string | null;
  fields: DnsProviderField[];
}

export interface BinarySettings {
  plugins: string[];
  autoCheckUpdates: boolean;
  checkIntervalHours: number;
  autoInstallUpdates: boolean;
  lastCheckedAt?: IsoDate | null;
  latestKnownVersion?: string | null;
  outboundProxy?: string | null;
  /** Also give Caddy the proxy (HTTPS_PROXY/HTTP_PROXY) so ACME works behind a corporate proxy. */
  proxyCaddyTraffic: boolean;
  /** NO_PROXY for Caddy when proxyCaddyTraffic is on. */
  noProxy: string;
  /** GitHub "owner/repo" checked for new manager versions; empty = disabled. */
  managerReleaseRepo?: string | null;
}

export interface NotificationSettings {
  smtpEnabled: boolean;
  smtpHost: string;
  smtpPort: number;
  smtpSecurity: SmtpSecurity;
  smtpAuth: SmtpAuthMode;
  smtpUsername?: string | null;
  hasSmtpPassword: boolean;
  oAuthTenantId?: string | null;
  oAuthClientId?: string | null;
  hasOAuthClientSecret: boolean;
  smtpFrom: string;
  recipients: string[];
  allowInvalidCertificate: boolean;
  webhookEnabled: boolean;
  webhookUrl?: string | null;
  webhookFormat: WebhookFormat;
  writeWindowsEventLog: boolean;
  alertCaddyDown: boolean;
  alertConfigFailure: boolean;
  alertUpstreamUnhealthy: boolean;
  alertCertificateExpiry: boolean;
  certificateExpiryDays: number;
  alertUpdateAvailable: boolean;
  alertReadinessFailure: boolean;
  /** A cluster node stopped answering the primary. */
  alertServerOffline: boolean;
  autoRestartCaddy: boolean;
  cooldownMinutes: number;
  sendRecoveryNotices: boolean;
}

export type NotificationSettingsInput = Omit<NotificationSettings, 'hasSmtpPassword' | 'hasOAuthClientSecret'> & {
  smtpPassword?: string | null;
  oAuthClientSecret?: string | null;
};

export interface UiSettings {
  port: number;
  bindAddress: string;
  httpsEnabled: boolean;
  /** When HTTPS is enabled, redirect plain-HTTP UI requests to HTTPS. */
  redirectHttpToHttps: boolean;
  httpsPort: number;
  httpsPfxPath?: string | null;
  hasHttpsPfxPassword: boolean;
  sessionHours: number;
  displayName?: string | null;
}

export type UiSettingsInput = Omit<UiSettings, 'hasHttpsPfxPassword'> & { httpsPfxPassword?: string | null };

/** Directory sign-in (GET/PUT /api/settings/ldap, Ops-owned document). */
export interface LdapSettings {
  enabled: boolean;
  server: string;
  port: number;
  security: LdapSecurity;
  allowInvalidCertificate: boolean;
  bindDn?: string | null;
  hasBindPassword: boolean;
  baseDn: string;
  /** "{0}" is replaced with the escaped user name. */
  userFilter: string;
  adminGroupDn?: string | null;
  operatorGroupDn?: string | null;
  viewerGroupDn?: string | null;
  nestedGroups: boolean;
}

export type LdapSettingsInput = Omit<LdapSettings, 'hasBindPassword'> & { bindPassword?: string | null };

export interface LdapTestResult {
  ok: boolean;
  role?: UserRole;
  displayName?: string;
  email?: string;
  groups?: string[];
  error?: string;
}

/** Scheduled backups (GET/PUT /api/settings/backup, Ops-owned document). */
export interface BackupSettings {
  enabled: boolean;
  /** Local hour of day 0–23. */
  hourLocal: number;
  /** Local path or UNC share; empty = DataDir\backups. */
  directory?: string | null;
  /** Number of backups kept, 1–365. */
  keep: number;
  /** An AES-256 zip password is stored. */
  hasPassword: boolean;
}

export type BackupSettingsInput = Omit<BackupSettings, 'hasPassword'> & { password?: string | null };

export interface BackupFile {
  name: string;
  size: number;
  createdAt: IsoDate;
}

// ---------------------------------------------------------------- Contracts/Dtos.cs

export interface CaddyStatus {
  binaryInstalled: boolean;
  serviceInstalled: boolean;
  state: CaddyRunState;
  processId?: number;
  version?: string;
  adminReachable: boolean;
  startedAt?: IsoDate;
  binaryPath: string;
  configPath: string;
  /** "windows-service" or "process" (dev). */
  hostMode: string;
  serviceStartType?: string;
  lastError?: string;
}

export interface InstalledBinary {
  version: string;
  path: string;
  installedAt?: IsoDate;
  plugins: string[];
  modules: string[];
}

export interface ReleaseInfo {
  version: string;
  publishedAt?: IsoDate;
  url: string;
  notes?: string;
}

export interface BinaryOverview {
  installed?: InstalledBinary;
  latest?: ReleaseInfo;
  updateAvailable: boolean;
  lastCheckedAt?: IsoDate;
  desiredPlugins: string[];
  pluginsOutOfSync: boolean;
  platform: string;
  /** A previous binary exists and POST /api/caddy/binary/rollback is possible. */
  canRollback: boolean;
  previousVersion?: string;
  /** Version of Caddy Proxy Manager itself. */
  managerVersion?: string;
  managerLatestVersion?: string;
  managerLatestUrl?: string;
  managerUpdateAvailable: boolean;
}

export interface PluginPackage {
  path: string;
  repo?: string;
  downloads: number;
  modules: string[];
}

export interface ApplyResult {
  success: boolean;
  error?: string;
  revisionId?: string;
  writtenOnly: boolean;
  warnings: string[];
}

export interface UpstreamHealth {
  address: string;
  numRequests: number;
  fails: number;
  /** Only meaningful when monitored: Caddy reports an unchecked upstream as healthy whatever its state. */
  healthy: boolean;
  /** A health check measures this upstream (an active check, or passive checks on a host with several upstreams). */
  monitored?: boolean;
}

export interface CertificateInfo {
  id: string;
  kind: CertificateKind;
  name: string;
  subjects: string[];
  issuer: string;
  notBefore: IsoDate;
  notAfter: IsoDate;
  daysRemaining: number;
  certPath?: string;
  keyPath?: string;
  /** "uploaded" | "filePath" | "pfxFile" | "windowsStore" for custom certificates; issuer directory name otherwise. */
  source?: string;
  usedByHostIds: string[];
  error?: string;
  notes?: string;
}

export interface ReadinessCheck {
  id: string;
  category: string;
  title: string;
  status: CheckStatus;
  summary: string;
  details?: string;
  remediation?: string;
  script?: string;
  fixable: boolean;
}

export interface NetworkProfileInfo {
  interfaceAlias: string;
  name: string;
  category: string;
}

export interface MachineInfo {
  hostname: string;
  fqdn?: string;
  osDescription: string;
  isWindows: boolean;
  domainJoined: boolean;
  domain?: string;
  computerDn?: string;
  ipAddresses: string[];
  networkProfiles: NetworkProfileInfo[];
}

export interface ReadinessReport {
  ranAt: IsoDate;
  machine: MachineInfo;
  checks: ReadinessCheck[];
  pass: number;
  warn: number;
  fail: number;
}

export interface JobInfo {
  id: string;
  kind: string;
  title: string;
  state: JobState;
  startedAt: IsoDate;
  finishedAt?: IsoDate;
  log: string[];
  error?: string;
}

export interface Page<T> {
  items: T[];
  total: number;
}

// ---------------------------------------------------------------- Endpoint-specific shapes (SPEC "HTTP API")

export interface SetupStatus {
  needsSetup: boolean;
  setupTokenPath: string;
}

export interface MutationResult<T> {
  item: T;
  apply: ApplyResult;
}

export interface DeleteResult {
  apply: ApplyResult;
}

export interface StreamSupport {
  supported: boolean;
  plugin: string;
}

export interface Dashboard {
  caddy: CaddyStatus;
  binary: BinaryOverview;
  counts: {
    proxy: number;
    redirect: number;
    static: number;
    response: number;
    streams: number;
    accessLists: number;
    certificates: number;
    certificatesExpiring: number;
    hostsDisabled: number;
  };
  readiness?: { ranAt?: IsoDate; pass: number; warn: number; fail: number } | null;
  upstreams: { total: number; unhealthy: number };
  recentEvents: EventEntry[];
  system: { hostname: string; os: string; managerVersion: string; uptimeSeconds: number; dataDir: string };
  /** Parts that could not be loaded (the rest still renders). */
  warnings?: string[];
}

export interface SystemInfo {
  version: string;
  product: string;
  hostMode: string;
  dataDir: string;
  installDir: string;
  os: string;
  machineName: string;
  uptimeSeconds: number;
  isService: boolean;
}

export interface JsonDoc {
  json: string;
  /** Generator warnings (GET /api/config/preview only). */
  warnings?: string[];
  /** Which mode produced the preview (GET /api/config/preview only). */
  mode?: ConfigMode;
}

export interface AdaptResult {
  json: string;
  warnings: string[];
}

export interface FixResult {
  message: string;
  report: ReadinessReport;
}

export interface NotificationTestResult {
  ok: boolean;
  errors: string[];
}

export interface UiSettingsSaveResult {
  item: UiSettings;
  restartRequired: boolean;
}

export interface LogResult {
  lines: string[];
  file: string;
}

export interface AccessLogResult extends LogResult {
  hosts: string[];
}

export interface RestoreResult {
  restartRequired: boolean;
  message?: string;
}

/** GET /api/certificates/windows-store item. */
export interface WindowsStoreCertificate {
  thumbprint: string;
  subject: string;
  dnsNames: string[];
  issuer: string;
  notBefore: IsoDate;
  notAfter: IsoDate;
  hasPrivateKey: boolean;
  exportable: boolean;
  /** Certificate template (AD CS), when present. */
  template?: string;
}

/** POST /api/config/caddyfile/import (nothing is saved). */
export interface CaddyfileImportResult {
  drafts: SiteHostFields[];
  unmapped: string[];
  warnings: string[];
}

/** POST /api/config/caddyfile/import/commit. */
export interface CaddyfileImportCommitResult {
  created: number;
  apply: ApplyResult;
}

export interface Health {
  status: string;
  version?: string;
  product: string;
}

export interface UserCreateInput {
  email: string;
  name: string;
  role: UserRole;
  password: string;
}

export interface UserUpdateInput {
  email: string;
  name: string;
  role: UserRole;
  disabled: boolean;
  password?: string;
}

/** RFC 7807 problem details. */
export interface ProblemDetails {
  type?: string;
  title?: string;
  detail?: string;
  status?: number;
  errors?: Record<string, string[]>;
}

// ---------------------------------------------------------------- Round 3: servers, telemetry, cluster (Contracts/Dtos.cs)

export interface ServerInfo {
  hostname: string;
  fqdn?: string | null;
  os: string;
  isWindows: boolean;
  architecture: string;
  managerVersion: string;
  caddyVersion?: string | null;
  caddyState: CaddyRunState;
  caddyStartedAt?: IsoDate | null;
  caddyPlugins: string[];
  processorCount: number;
  totalMemoryBytes: number;
  systemUptimeSeconds: number;
  managerUptimeSeconds: number;
  dataDir: string;
  ipAddresses: string[];
  domain?: string | null;
  collectedAt: IsoDate;
}

export interface DiskUsage {
  name: string;
  label: string;
  totalBytes: number;
  freeBytes: number;
}

export interface ResourceSample {
  at: IsoDate;
  cpuPercent: number;
  memoryUsedBytes: number;
  memoryTotalBytes: number;
  disks: DiskUsage[];
  networkRxBytesPerSec: number;
  networkTxBytesPerSec: number;
  caddyCpuPercent?: number | null;
  caddyMemoryBytes?: number | null;
  managerCpuPercent: number;
  managerMemoryBytes: number;
  activeConnections?: number | null;
  requestsPerSecond: number;
}

export interface TrafficTotals {
  requests: number;
  bytesIn: number;
  bytesOut: number;
  uniqueClients: number;
  status2xx: number;
  status3xx: number;
  status4xx: number;
  status5xx: number;
  statusOther: number;
  avgDurationMs: number;
}

export interface TrafficPoint {
  at: IsoDate;
  requests: number;
  bytesIn: number;
  bytesOut: number;
  uniqueClients: number;
  status4xx: number;
  status5xx: number;
}

export interface TrafficHostRow {
  host: string;
  requests: number;
  bytesIn: number;
  bytesOut: number;
  uniqueClients: number;
  status4xx: number;
  status5xx: number;
}

export interface TrafficClientRow {
  ip: string;
  requests: number;
  bytesOut: number;
  lastSeen: IsoDate;
}

export interface StatusCount {
  code: number;
  count: number;
}

export interface TrafficReport {
  range: TrafficRange;
  from: IsoDate;
  to: IsoDate;
  bucketSize: 'minute' | 'hour' | 'day';
  host?: string | null;
  enabled: boolean;
  lastIngestAt?: IsoDate | null;
  totals: TrafficTotals;
  series: TrafficPoint[];
  topHosts: TrafficHostRow[];
  topClients: TrafficClientRow[];
  statusCodes: StatusCount[];
  notes: string[];
}

export interface ServerSyncState {
  desiredRevision?: string | null;
  appliedRevision?: string | null;
  inSync: boolean;
  lastSyncAt?: IsoDate | null;
  lastError?: string | null;
  warnings: string[];
}

export interface ServerSummary {
  /** "local" for this server, otherwise the node id. */
  id: string;
  name: string;
  isLocal: boolean;
  url?: string | null;
  status: ServerStatus;
  lastSeenAt?: IsoDate | null;
  lastError?: string | null;
  info?: ServerInfo | null;
  latest?: ResourceSample | null;
  sync?: ServerSyncState | null;
  addedAt?: IsoDate | null;
}

export interface ClusterStatus {
  role: ClusterRole;
  serverName: string;
  primaryName?: string | null;
  lastPrimaryContactAt?: IsoDate | null;
  appliedRevision?: string | null;
  nodeCount: number;
  storageBackend: StorageBackend;
  warnings: string[];
}

export interface AddServerResult {
  server: ServerSummary;
  /** Shown once: paste on the node (Settings > Cluster > Join) or run `CaddyManager.exe cluster join <token>`. */
  joinToken: string;
  /** SHA-256 fingerprint of the node's HTTPS certificate when pinned. */
  fingerprint?: string | null;
}
