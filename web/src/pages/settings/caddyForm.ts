import { caddySettingsInput } from '@/api/settings';
import type { CaddySettings, CaddySettingsInput, DnsProviderInfo } from '@/api/types';
import { secretPayload } from '@/components/SecretInput';
import { isIpv4, isIpv6, type FieldErrors } from '@/lib/validation';
import { isDelegationName } from '../hosts/dnsDelegation';
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
    dnsOverrideDomain: rest.dnsOverrideDomain ?? null,
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
    dnsOverrideDomain: form.dnsOverrideDomain?.trim() || null,
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
  const od = f.dnsOverrideDomain?.trim();
  if (od && !isDelegationName(od)) e.dnsOverrideDomain = 'Enter a DNS name such as _acme-challenge.validation.example.net (no wildcard).';
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
    if (f.redisAddresses.length === 0) e.redisAddresses = 'Add at least one Redis server as host:port.';
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
