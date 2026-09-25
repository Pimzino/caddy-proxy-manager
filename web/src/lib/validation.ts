// Client-side validation mirroring the server rules (SPEC "Hosts" validation). The server remains authoritative.

const LABEL = '[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?';
const HOSTNAME_RE = new RegExp(`^(\\*\\.)?(${LABEL}\\.)*${LABEL}$`, 'i');
const IPV4_RE = /^(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(\.(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)){3}$/;

export function isValidHostname(value: string): boolean {
  const v = value.trim();
  if (!v || v.length > 253) return false;
  return HOSTNAME_RE.test(v);
}

export function isIpv4(value: string): boolean {
  return IPV4_RE.test(value.trim());
}

export function isIpv6(value: string): boolean {
  const v = value.trim();
  if (!v.includes(':') || !/^[0-9a-f:.]+$/i.test(v)) return false;
  const doubleColons = v.split('::').length - 1;
  if (doubleColons > 1) return false;
  const parts = v.split(':');
  return parts.length >= 3 && parts.length <= 8;
}

/** Upstream host: hostname, IPv4 or IPv6 (with or without brackets). */
export function isValidUpstreamHost(value: string): boolean {
  const v = value.trim().replace(/^\[(.*)\]$/, '$1');
  return isValidHostname(v) || isIpv4(v) || isIpv6(v);
}

export function isValidPort(n: unknown): boolean {
  return typeof n === 'number' && Number.isInteger(n) && n >= 1 && n <= 65535;
}

/** IP, CIDR (v4/v6) or the keyword "all". */
export function isValidCidr(value: string): boolean {
  const v = value.trim();
  if (v.toLowerCase() === 'all') return true;
  const [ip, prefix, ...rest] = v.split('/');
  if (rest.length) return false;
  if (isIpv4(ip)) return prefix === undefined || (/^\d{1,2}$/.test(prefix) && Number(prefix) <= 32);
  if (isIpv6(ip)) return prefix === undefined || (/^\d{1,3}$/.test(prefix) && Number(prefix) <= 128);
  return false;
}

export function isAbsoluteHttpUrl(value: string): boolean {
  try {
    const u = new URL(value.trim());
    return (u.protocol === 'http:' || u.protocol === 'https:') && !!u.hostname;
  } catch {
    return false;
  }
}

export function isValidEmail(value: string): boolean {
  return /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(value.trim());
}

/** Returns an error message when the text is not a JSON array, else null. Empty = valid (optional field). */
export function jsonArrayError(text: string | null | undefined): string | null {
  if (!text || !text.trim()) return null;
  try {
    const parsed: unknown = JSON.parse(text);
    return Array.isArray(parsed) ? null : 'Must be a JSON array of route objects, e.g. [ { "handle": [ ... ] } ].';
  } catch (err) {
    return `Invalid JSON: ${err instanceof Error ? err.message : String(err)}`;
  }
}

export function jsonObjectError(text: string | null | undefined): string | null {
  if (!text || !text.trim()) return null;
  try {
    const parsed: unknown = JSON.parse(text);
    return parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? null : 'Must be a JSON object.';
  } catch (err) {
    return `Invalid JSON: ${err instanceof Error ? err.message : String(err)}`;
  }
}

export const MIN_PASSWORD_LENGTH = 12;

export type FieldErrors = Record<string, string>;

/**
 * Normalises server field keys ("Domains[0]", "upstreams[1].Port", "$.redirectTarget") into
 * the camelCase dotted form the forms use ("domains.0", "upstreams.1.port", "redirectTarget").
 */
export function normalizeFieldKey(key: string): string {
  return key
    .replace(/^\$\.?/, '')
    .replace(/\[(\d+)\]/g, '.$1')
    .split('.')
    .filter(Boolean)
    .map((part) => (part ? part[0].toLowerCase() + part.slice(1) : part))
    .join('.');
}

export function serverFieldErrors(errors: Record<string, string[]> | undefined): FieldErrors {
  const out: FieldErrors = {};
  if (!errors) return out;
  for (const [k, msgs] of Object.entries(errors)) {
    const key = normalizeFieldKey(k) || '_';
    out[key] = msgs.join(' ');
  }
  return out;
}
