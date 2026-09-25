import type { AcmeChallengeType, CaddySettings, DelegationCheck, SiteHostFields } from '@/api/types';

// DNS challenge delegation (SPEC "Round 3b: guided DNS challenge delegation"): a one-time CNAME
// _acme-challenge.<domain> → <override name> lets the CA follow the challenge into a separate validation zone, where Caddy
// writes the TXT record (challenges.dns.override_domain). Shared by Settings › Caddy (all hosts) and the host editor (one host).

/** The CaddySettings values the delegation rules read (the settings form passes its unsaved values). */
export type DelegationSettings = Pick<CaddySettings, 'defaultAcmeChallenge' | 'dnsProvider' | 'dnsOverrideDomain'>;
type HostDelegationFields = Pick<SiteHostFields, 'tls' | 'domains' | 'acmeChallenge' | 'dnsDelegation' | 'dnsOverrideDomain'>;

/** Lower-case, trimmed, without the trailing dot of a fully qualified name. */
export function normalizeDnsName(name: string): string {
  return name.trim().toLowerCase().replace(/\.$/, '');
}

const DNS_NAME = /^(?=.{1,253}$)([a-z0-9_]([a-z0-9_-]{0,61}[a-z0-9])?\.)+[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.?$/i;

/** A delegation (override) name: a DNS name with at least two labels, "_" labels allowed, no wildcard. */
export function isDelegationName(v: string): boolean {
  return DNS_NAME.test(v.trim());
}

/** The host's ACME challenge: its own choice, or the settings default for "default" (CaddyConfigGenerator.EffectiveChallenge). */
export function effectiveChallenge(h: Pick<SiteHostFields, 'acmeChallenge'>, s: Pick<DelegationSettings, 'defaultAcmeChallenge'>): AcmeChallengeType {
  return h.acmeChallenge === 'default' ? s.defaultAcmeChallenge : h.acmeChallenge;
}

/** True when the host's certificates are validated with DNS-01 as a whole (ACME, effective challenge DNS, provider set). */
export function usesDnsChallenge(h: Pick<SiteHostFields, 'tls' | 'acmeChallenge'>, s: DelegationSettings): boolean {
  return h.tls === 'acme' && !!s.dnsProvider && effectiveChallenge(h, s) === 'dns';
}

/**
 * The names of the host validated with DNS-01, following the generator: every domain when the host uses the DNS challenge;
 * otherwise only its wildcard names, which switch to DNS-01 automatically when a provider is configured.
 */
export function dnsChallengeDomains(h: HostDelegationFields, s: DelegationSettings): string[] {
  if (h.tls !== 'acme' || !s.dnsProvider) return [];
  return usesDnsChallenge(h, s) ? h.domains : h.domains.filter((d) => d.startsWith('*.'));
}

/** Effective override name: Custom → the host's name; Off → none; Default → the settings value (none when empty). */
export function effectiveDelegation(h: Pick<SiteHostFields, 'dnsDelegation' | 'dnsOverrideDomain'>, s: Pick<DelegationSettings, 'dnsOverrideDomain'>): string | null {
  const name = h.dnsDelegation === 'custom' ? h.dnsOverrideDomain : h.dnsDelegation === 'off' ? null : s.dnsOverrideDomain;
  return name?.trim() ? normalizeDnsName(name) : null;
}

/** The record to create for a domain: _acme-challenge.<domain>, a wildcard using its base name. */
export function challengeRecordName(domain: string): string {
  return `_acme-challenge.${normalizeDnsName(domain).replace(/^\*\./, '')}`;
}

export interface DelegationRecord {
  /** First domain that needs the record (a wildcard shares its base name's record). */
  domain: string;
  /** Every domain of the host covered by this record, e.g. example.com and *.example.com. */
  domains: string[];
  recordName: string;
  target: string;
}

/** The CNAME records a host needs, one per record name (empty without delegation or without DNS-01 names). */
export function hostDelegationRecords(h: HostDelegationFields, s: DelegationSettings): DelegationRecord[] {
  const target = effectiveDelegation(h, s);
  if (!target) return [];
  const out = new Map<string, DelegationRecord>();
  for (const d of dnsChallengeDomains(h, s)) {
    const recordName = challengeRecordName(d);
    const r = out.get(recordName);
    if (r) r.domains.push(d);
    else out.set(recordName, { domain: d, domains: [d], recordName, target });
  }
  return [...out.values()];
}

/** Zone-file line for a record, e.g. "_acme-challenge.example.com. CNAME _acme-challenge.validation.example.net." */
export function zoneLine(r: Pick<DelegationRecord, 'recordName' | 'target'>): string {
  return `${r.recordName}. CNAME ${r.target}.`;
}

/** Key matching a record with its check result (DelegationCheck.recordName / expectedTarget). */
export function recordKey(recordName: string, target: string): string {
  return `${normalizeDnsName(recordName)}|${normalizeDnsName(target)}`;
}

export function checkKey(c: DelegationCheck): string {
  return recordKey(c.recordName, c.expectedTarget);
}

/** Groups records into delegation-check requests (one per target, domains deduplicated by record). */
export function checkRequests(records: DelegationRecord[]): { domains: string[]; target: string }[] {
  const byTarget = new Map<string, string[]>();
  for (const r of records) byTarget.set(r.target, [...(byTarget.get(r.target) ?? []), r.domain]);
  return [...byTarget].map(([target, domains]) => ({ target, domains }));
}
