// Examples for Settings › Caddy › Plugins & advanced. Shapes checked with `caddy validate` (Caddy v2.11):
// DNS providers need the matching caddy-dns plugin compiled in (dns.providers.<name>).

export interface JsonExample {
  label: string;
  /** Plugin the example needs, if any. */
  plugin?: string;
  json: string;
}

const pretty = (v: unknown) => JSON.stringify(v, null, 2);

export const ACME_ISSUER_EXAMPLES: JsonExample[] = [
  {
    label: 'Cloudflare DNS challenge',
    plugin: 'github.com/caddy-dns/cloudflare',
    json: pretty({ challenges: { dns: { provider: { name: 'cloudflare', api_token: '<API token with Zone:DNS:Edit>' } } } }),
  },
  {
    label: 'Azure DNS challenge',
    plugin: 'github.com/caddy-dns/azure',
    json: pretty({
      challenges: {
        dns: {
          provider: {
            name: 'azure',
            tenant_id: '<tenant id>',
            client_id: '<app registration client id>',
            client_secret: '<client secret>',
            subscription_id: '<subscription id>',
            resource_group_name: '<resource group of the DNS zone>',
          },
        },
      },
    }),
  },
  {
    label: 'DNS challenge with public resolvers (split-horizon DNS)',
    plugin: 'github.com/caddy-dns/cloudflare',
    json: pretty({
      challenges: {
        dns: { provider: { name: 'cloudflare', api_token: '<API token with Zone:DNS:Edit>' }, resolvers: ['1.1.1.1', '8.8.8.8'] },
      },
    }),
  },
];

export const TLS_POLICY_EXAMPLES: JsonExample[] = [
  { label: 'TLS 1.3 only', json: pretty({ protocol_min: 'tls1.3' }) },
  { label: 'TLS 1.2+ with modern curves', json: pretty({ protocol_min: 'tls1.2', curves: ['x25519', 'secp256r1', 'secp384r1'] }) },
  {
    label: 'Require client certificates (mTLS)',
    json: pretty({
      client_authentication: {
        ca: { provider: 'file', pem_files: ['C:\\ProgramData\\pki\\client-ca.pem'] },
        mode: 'require_and_verify',
      },
    }),
  },
];

export const EXTRA_APPS_EXAMPLES: JsonExample[] = [
  {
    label: 'Dynamic DNS',
    plugin: 'github.com/mholt/caddy-dynamicdns',
    json: pretty({
      dynamic_dns: {
        dns_provider: { name: 'cloudflare', api_token: '<API token with Zone:DNS:Edit>' },
        domains: { 'example.com': ['@', 'www'] },
        check_interval: '5m',
      },
    }),
  },
  {
    label: 'CrowdSec bouncer',
    plugin: 'github.com/hslatman/caddy-crowdsec-bouncer',
    json: pretty({ crowdsec: { api_url: 'http://127.0.0.1:8080', api_key: '<bouncer API key>', ticker_interval: '15s' } }),
  },
];

/** Apps the manager generates itself; they cannot be supplied through "Extra apps". */
export const RESERVED_APPS = ['http', 'tls', 'pki', 'layer4'];

/** Parses a JSON object, returning null when it is not one. */
export function parseObject(text: string | null | undefined): Record<string, unknown> | null {
  if (!text || !text.trim() || text === '\u0000') return null;
  try {
    const v: unknown = JSON.parse(text);
    return v && typeof v === 'object' && !Array.isArray(v) ? (v as Record<string, unknown>) : null;
  } catch {
    return null;
  }
}

/** The DNS provider name referenced by an ACME issuer object, e.g. "cloudflare". */
export function dnsProviderName(issuer: Record<string, unknown> | null): string | null {
  const challenges = issuer?.challenges as Record<string, unknown> | undefined;
  const dns = challenges?.dns as Record<string, unknown> | undefined;
  const provider = dns?.provider as Record<string, unknown> | undefined;
  return typeof provider?.name === 'string' ? provider.name : null;
}
