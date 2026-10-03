import { caddySettingsInput } from '@/api/settings';
import type { CaddySettings, CaddySettingsInput, DnsProviderInfo } from '@/api/types';
import { secretPayload } from '@/components/SecretInput';
import { isIpv4, isIpv6, type FieldErrors } from '@/lib/validation';
import { parseObject } from './pluginExamples';

// Round 3 helpers shared by the Caddy tab (ACME challenge / DNS) and the Cluster tab (shared storage): both edit the
// one CaddySettings document through PUT /api/settings/caddy.

/** GET shape → editable form (caddySettingsInput) with the Round 3 nullable fields sent explicitly so clearing them works. */
export function caddyFormInput(s: CaddySettings): CaddySettingsInput {
  const rest = caddySettingsInput(s);
  return {
    ...rest,
    dnsProvider: rest.dnsProvider ?? null,
    dnsProviderOptions: rest.dnsProviderOptions ?? {},
    dnsResolvers: rest.dnsResolvers ?? [],
    dnsPropagationDelaySeconds: rest.dnsPropagationDelaySeconds ?? null,
    dnsPropagationTimeoutSeconds: rest.dnsPropagationTimeoutSeconds ?? null,
    dnsTtlSeconds: rest.dnsTtlSeconds ?? null,
    storagePath: rest.storagePath ?? null,
    redisAddresses: rest.redisAddresses ?? [],
    redisUsername: rest.redisUsername ?? null,
  };
}

/** True when the stored secret field belongs to the provider currently selected in the form. */
export function hasStoredSecret(settings: CaddySettings, form: CaddySettingsInput, field: string): boolean {
  return (form.dnsProvider ?? null) === (settings.dnsProvider ?? null) && (settings.dnsProviderSecretFields ?? []).includes(field);
}

const nullableNumber = (v: number | null | undefined) => (v == null || Number.isNaN(v) ? null : v);

/**
 * Form → PUT body for the Round 3 fields (SPEC "Settings wire shape additions"):
 * dnsProviderSecrets only carries changed fields ("" removes a stored one); a provider change or removal clears the
 * previous provider's secrets first (dnsProviderSecretsClear), so a token never follows a field of the same name to
 * another provider; write-only storage secrets follow the SecretInput convention.
 */
export function round3Payload(form: CaddySettingsInput, settings: CaddySettings, providers: DnsProviderInfo[] | undefined): Partial<CaddySettingsInput> {
  const provider = form.dnsProvider ? providers?.find((p) => p.name === form.dnsProvider) : undefined;
  const providerChanged = (form.dnsProvider ?? null) !== (settings.dnsProvider ?? null);
  const fieldNames = provider ? new Set(provider.fields.map((f) => f.name)) : null;

  const secrets: Record<string, string> = {};
  for (const [k, v] of Object.entries(form.dnsProviderSecrets ?? {})) {
    if (fieldNames && !fieldNames.has(k)) continue;
    const payload = secretPayload(v);
    if (payload === undefined) continue;
    if (payload === '' && !hasStoredSecret(settings, form, k)) continue;
    secrets[k] = payload;
  }
  const storageJson = secretPayload(form.storageJson);
  const options: Record<string, string> = {};
  for (const [k, v] of Object.entries(form.dnsProviderOptions ?? {})) {
    if (fieldNames && !fieldNames.has(k)) continue;
    const t = String(v ?? '').trim();
    if (t) options[k] = t;
  }
  return {
    dnsProvider: form.dnsProvider || null,
    dnsProviderOptions: form.dnsProvider ? options : {},
    dnsProviderSecrets: Object.keys(secrets).length ? secrets : undefined,
    dnsProviderSecretsClear: (providerChanged && (settings.dnsProviderSecretFields ?? []).length > 0) || undefined,
    dnsPropagationDelaySeconds: nullableNumber(form.dnsPropagationDelaySeconds),
    dnsPropagationTimeoutSeconds: nullableNumber(form.dnsPropagationTimeoutSeconds),
    dnsTtlSeconds: nullableNumber(form.dnsTtlSeconds),
    storagePath: form.storagePath?.trim() || null,
    redisUsername: form.redisUsername?.trim() || null,
    redisKeyPrefix: form.redisKeyPrefix.trim() || 'caddy',
    redisPassword: secretPayload(form.redisPassword),
    redisEncryptionKey: secretPayload(form.redisEncryptionKey),
    storageJson: storageJson === undefined || storageJson === '' ? storageJson : storageJson.trim(),
  };
}

const HOSTNAME = /^(?=.{1,253}$)([a-z0-9_]([a-z0-9_-]{0,61}[a-z0-9])?\.)*[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.?$/i;

/** host, host:port, IPv4, IPv4:port, IPv6 or [IPv6]:port (DNS resolvers). */
export function isResolverAddress(v: string): boolean {
  const s = v.trim();
  const bracket = /^\[([0-9a-f:.]+)\](?::(\d{1,5}))?$/i.exec(s);
  if (bracket) return isIpv6(bracket[1]) && (!bracket[2] || validPort(bracket[2]));
  if (isIpv6(s)) return true;
  const [host, port, extra] = s.split(':');
  if (extra !== undefined) return false;
  return (isIpv4(host) || HOSTNAME.test(host)) && (port === undefined || validPort(port));
}

/** host:port with a mandatory port (Redis addresses). */
export function isHostPort(v: string): boolean {
  const s = v.trim();
  const bracket = /^\[([0-9a-f:.]+)\]:(\d{1,5})$/i.exec(s);
  if (bracket) return isIpv6(bracket[1]) && validPort(bracket[2]);
  const m = /^([^:]+):(\d{1,5})$/.exec(s);
  return !!m && (isIpv4(m[1]) || HOSTNAME.test(m[1])) && validPort(m[2]);
}

function validPort(p: string) {
  const n = Number(p);
  return Number.isInteger(n) && n >= 1 && n <= 65535;
}

const isWholeSeconds = (v: number | null | undefined, min: number) => v == null || Number.isNaN(v) || (Number.isInteger(v) && v >= min);

/** Client-side checks for the ACME challenge / DNS provider fields (the server stays authoritative). */
export function validateDns(f: CaddySettingsInput, settings: CaddySettings, providers: DnsProviderInfo[] | undefined): FieldErrors {
  const e: FieldErrors = {};
  if (f.defaultAcmeChallenge === 'dns' && !f.dnsProvider) e.dnsProvider = 'Choose a DNS provider, or keep HTTP-01 / TLS-ALPN-01 as the default challenge.';
  const provider = f.dnsProvider ? providers?.find((p) => p.name === f.dnsProvider) : undefined;
  for (const field of provider?.fields ?? []) {
    if (field.secret) {
      const v = f.dnsProviderSecrets?.[field.name];
      const typed = typeof v === 'string' && v !== '' && v !== '\u0000';
      const stored = hasStoredSecret(settings, f, field.name) && v !== '';
      if (field.required && !typed && !stored) e[`dnsProviderSecrets.${field.name}`] = `${field.label} is required for ${provider!.label}.`;
      continue;
    }
    const raw = (f.dnsProviderOptions?.[field.name] ?? '').trim();
    const key = `dnsProviderOptions.${field.name}`;
    if (field.required && !raw && field.type !== 'boolean') e[key] = `${field.label} is required for ${provider!.label}.`;
    else if (raw && field.type === 'number' && !/^-?\d+$/.test(raw)) e[key] = 'Enter a whole number.';
    else if (raw && field.type === 'duration' && !/^\d+$/.test(raw)) e[key] = 'Enter a whole number of seconds.';
  }
  if (!isWholeSeconds(f.dnsPropagationDelaySeconds, 0)) e.dnsPropagationDelaySeconds = 'Enter whole seconds (0 or more), or leave empty.';
  const t = f.dnsPropagationTimeoutSeconds;
  if (t != null && !Number.isNaN(t) && t !== -1 && !(Number.isInteger(t) && t >= 1)) e.dnsPropagationTimeoutSeconds = 'Enter whole seconds (1 or more), or leave empty for the default.';
  if (!isWholeSeconds(f.dnsTtlSeconds, 0)) e.dnsTtlSeconds = 'Enter whole seconds, or leave empty for the provider default.';
  return e;
}

/** Bounds of the request limit fields (CaddyConfigGenerator.MinRequestHeaderKb / MaxRequestHeaderKb / MaxIdleTimeoutSeconds). */
export const REQUEST_HEADER_KB = { min: 4, max: 1024 } as const;
export const IDLE_TIMEOUT_SECONDS = { min: 1, max: 3600 } as const;

/**
 * Why a "Request headers to keep" entry is refused, or null (mirrors CaddyConfigGenerator.KeptRequestHeaderError):
 * a header name with an underscore or a dot, optionally ending in * (prefix).
 */
export function keptRequestHeaderError(entry: string): string | null {
  const e = entry.trim();
  const name = e.endsWith('*') ? e.slice(0, -1) : e;
  if (!name) return 'enter a header name';
  if (!/^[A-Za-z0-9!#$%&'+\-.^_`|~]+$/.test(name)) return 'not a header name';
  if (!/[_.]/.test(name)) return e.endsWith('*') ? 'no underscore or dot before the *' : 'no underscore or dot';
  return null;
}

export const MIN_RATE_BYTES = { min: 1, max: 1_000_000_000 } as const;
export const ACCESS_LOG_ROLL_DAYS = { min: 1, max: 365 } as const;

/** Why an entry is not a cookie name, or null (mirrors CaddyConfigGenerator.IsCookieName). */
export function cookieNameError(name: string): string | null {
  return /^[!#$%&'*+\-.^_`|~0-9A-Za-z]+$/.test(name.trim()) ? null : 'not a cookie name';
}

/** Why the text cannot be the Proxy-Status name, or null (mirrors CaddyConfigGenerator.ProxyStatusNameError). */
export function proxyStatusNameError(name: string | null | undefined): string | null {
  const n = (name ?? '').trim();
  if (!n) return null;
  if (n.length > 255) return 'At most 255 characters.';
  return /^[\x20-\x7e]+$/.test(n) && !/["\\]/.test(n) ? null : 'Use letters, digits and punctuation only, without quotes or backslashes.';
}

const inRange = (v: number | null | undefined, r: { min: number; max: number }) => v == null || Number.isNaN(v) || (Number.isInteger(v) && v >= r.min && v <= r.max);

/** Client-side checks for Request limits and headers (the server stays authoritative). */
export function validateRequestLimits(f: CaddySettingsInput): FieldErrors {
  const e: FieldErrors = {};
  if (!inRange(f.maxRequestHeaderKb, REQUEST_HEADER_KB)) e.maxRequestHeaderKb = `Enter ${REQUEST_HEADER_KB.min} to ${REQUEST_HEADER_KB.max} KiB, or leave empty.`;
  if (!inRange(f.readIdleTimeoutSeconds, IDLE_TIMEOUT_SECONDS)) e.readIdleTimeoutSeconds = `Enter ${IDLE_TIMEOUT_SECONDS.min} to ${IDLE_TIMEOUT_SECONDS.max} seconds, or leave empty.`;
  if (!inRange(f.writeIdleTimeoutSeconds, IDLE_TIMEOUT_SECONDS)) e.writeIdleTimeoutSeconds = `Enter ${IDLE_TIMEOUT_SECONDS.min} to ${IDLE_TIMEOUT_SECONDS.max} seconds, or leave empty.`;
  if (!inRange(f.readMinRateBytes, MIN_RATE_BYTES)) e.readMinRateBytes = 'Enter 1 or more bytes per second, or leave empty.';
  if (!inRange(f.writeMinRateBytes, MIN_RATE_BYTES)) e.writeMinRateBytes = 'Enter 1 or more bytes per second, or leave empty.';
  if (!inRange(f.accessLogRollDays, ACCESS_LOG_ROLL_DAYS)) e.accessLogRollDays = `Enter ${ACCESS_LOG_ROLL_DAYS.min} to ${ACCESS_LOG_ROLL_DAYS.max} days, or leave empty.`;
  const ps = proxyStatusNameError(f.proxyStatusName);
  if (ps) e.proxyStatusName = ps;
  return e;
}

/** CaddySettings.NodeLocalProperties (Core Models/Settings.cs): what a managed cluster node keeps editable. */
export const NODE_LOCAL_FIELDS = ['httpPort', 'httpsPort', 'publicHttpsPort', 'bindAddresses', 'adminListen', 'certificateStorePath', 'customAcmeRootPath'] as const;

/** Only the errors of node-local fields (a managed node cannot change, and so cannot fix, the replicated ones). */
export function nodeLocalErrors(e: FieldErrors): FieldErrors {
  return Object.fromEntries(Object.entries(e).filter(([k]) => (NODE_LOCAL_FIELDS as readonly string[]).includes(k)));
}

/**
 * Whether certificates can be obtained without HTTP-01 and TLS-ALPN-01 (ModelValidation: both challenges may be
 * disabled then): DNS-01 is the default challenge with a DNS provider, or the ACME issuer JSON configures challenges.dns.
 * null = unknown: a stored issuer JSON is write-only, so the form cannot see it (the server decides).
 */
export function dnsChallengeAvailable(f: CaddySettingsInput, settings: CaddySettings): boolean | null {
  if (f.defaultAcmeChallenge === 'dns' && !!f.dnsProvider) return true;
  const issuer = secretPayload(f.acmeIssuerJson);
  if (issuer === undefined) return settings.hasAcmeIssuerJson ? null : false;
  const challenges = parseObject(issuer)?.challenges;
  const dns = challenges && typeof challenges === 'object' ? (challenges as Record<string, unknown>).dns : undefined;
  return !!dns && typeof dns === 'object' && !Array.isArray(dns);
}

/** Client-side checks for the shared storage fields. */
export function validateStorage(f: CaddySettingsInput, settings: CaddySettings): FieldErrors {
  const e: FieldErrors = {};
  if (f.storageBackend === 'fileSystem') {
    const p = f.storagePath?.trim() ?? '';
    if (!p) e.storagePath = 'Enter the folder that every server of the cluster uses.';
    else if (!/^([A-Za-z]:\\|\\\\[^\\]+\\[^\\]+)/.test(p)) e.storagePath = 'Use an absolute path such as D:\\CaddyStorage or a UNC share such as \\\\fs01\\caddy$. Mapped drive letters are not visible to services.';
  }
  if (f.storageBackend === 'redis') {
    if (f.redisAddresses.length === 0) e.redisAddresses = 'Add the Redis server as host:port.';
    else if (f.redisAddresses.length > 1) e.redisAddresses = 'Enter one address: several addresses switch the plugin to Redis Cluster, which is not supported.';
    if (!(Number.isInteger(f.redisDb) && f.redisDb >= 0 && f.redisDb <= 15)) e.redisDb = 'Enter a database number, usually 0–15.';
    if (!/^[A-Za-z0-9._:-]*$/.test(f.redisKeyPrefix.trim())) e.redisKeyPrefix = 'Use letters, digits and . _ : - only.';
  }
  if (f.storageBackend === 'custom') {
    const v = f.storageJson;
    const typed = typeof v === 'string' && v !== '\u0000' && v !== '';
    if (typed) {
      try {
        const o: unknown = JSON.parse(v);
        if (!o || typeof o !== 'object' || Array.isArray(o)) e.storageJson = 'Enter a JSON object.';
        else if (typeof (o as { module?: unknown }).module !== 'string' || !(o as { module: string }).module)
          e.storageJson = 'The object needs a "module" string, e.g. "consul".';
      } catch (err) {
        e.storageJson = `Invalid JSON: ${(err as Error).message}`;
      }
    } else if (!(settings.hasStorageJson && v !== '')) e.storageJson = 'Enter the storage module configuration as a JSON object.';
  }
  return e;
}
