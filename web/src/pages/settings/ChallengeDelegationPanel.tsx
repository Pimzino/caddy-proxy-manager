import { errorMessage } from '@/api/client';
import { useHosts } from '@/api/hooks';
import type { CaddySettings, CaddySettingsInput, SiteHost } from '@/api/types';
import { Badge, Callout, Field, Input, Spinner } from '@/components/ui';
import { pluralize } from '@/lib/format';
import { DelegationRecordsPanel, type DelegationRow } from '../hosts/DelegationRecords';
import {
  checkRequests,
  dnsChallengeDomains,
  effectiveDelegation,
  hostDelegationRecords,
  normalizeDnsName,
  type DelegationSettings,
} from '../hosts/dnsDelegation';

type Set = <K extends keyof CaddySettingsInput>(k: K, v: CaddySettingsInput[K]) => void;

/**
 * Settings › Caddy › ACME challenge › "Challenge delegation (CNAME)" (SPEC round 3b): the default delegation name, why
 * delegating helps, and the CNAME records every DNS-01 host needs — computed from the unsaved form so a new name previews
 * its records — with copy buttons and "Check DNS".
 */
export function ChallengeDelegationPanel({
  settings,
  form,
  set,
  error,
  disabled,
}: {
  settings: CaddySettings;
  form: CaddySettingsInput;
  set: Set;
  error?: string;
  /** Viewer role, a managed cluster node or saving: the name is read-only; copying and checking stay available. */
  disabled: boolean;
}) {
  const hosts = useHosts();
  const s: DelegationSettings = {
    defaultAcmeChallenge: form.defaultAcmeChallenge,
    dnsProvider: form.dnsProvider,
    dnsOverrideDomain: form.dnsOverrideDomain,
  };
  const { rows, undelegated, conflicts } = delegationRows(hosts.data ?? [], s);
  const unsavedName = normalizeDnsName(form.dnsOverrideDomain ?? '') !== normalizeDnsName(settings.dnsOverrideDomain ?? '');

  return (
    <div className="flex flex-col gap-4 rounded-md border border-border p-4">
      <div>
        <h4 className="text-sm font-medium text-fg">Challenge delegation (CNAME)</h4>
        <div className="mt-1 flex flex-col gap-1.5 text-sm text-fg-muted">
          <p>
            Keep DNS credentials away from your real zones. For each domain you add one CNAME record, once, that points its
            <span className="mono"> _acme-challenge</span> name to a name in a separate validation zone. The certificate authority follows the
            CNAME, and Caddy writes the challenge record only in the validation zone: the manager never changes your real zone.
          </p>
          <p>
            Recommended: give the DNS provider a token that can only edit the validation zone, for example a Cloudflare token scoped to
            <span className="mono"> validation.example.net</span>. Leave the name empty to write challenge records in each domain’s own zone.
          </p>
        </div>
      </div>

      <Field
        label="Default delegation name"
        error={error}
        hint="Used by every host with DNS-01 whose TLS tab says “Use default”. Hosts can set their own name or turn delegation off."
        className="max-w-xl"
      >
        <Input
          mono
          autoComplete="off"
          spellCheck={false}
          placeholder="_acme-challenge.validation.example.net"
          value={form.dnsOverrideDomain ?? ''}
          onChange={(e) => set('dnsOverrideDomain', e.target.value)}
          disabled={disabled}
        />
      </Field>

      <div className="flex flex-col gap-2">
        <h5 className="text-sm font-medium text-fg">Records to create</h5>
        {hosts.isPending ? (
          <p className="flex items-center gap-2 text-sm text-fg-subtle">
            <Spinner size={12} /> Loading hosts…
          </p>
        ) : hosts.isError ? (
          <Callout tone="danger" title="Could not load the hosts">
            {errorMessage(hosts.error)}
          </Callout>
        ) : rows.length === 0 ? (
          <p className="text-sm text-fg-subtle">
            {undelegated > 0
              ? 'No records needed yet. Enter a default delegation name above, or set a custom name in a host’s TLS tab, to see the CNAME records to create.'
              : 'No records needed yet. Records appear here for hosts with automatic TLS that use DNS-01: when it is the default challenge, when a host’s TLS tab selects it, or for wildcard names.'}
          </p>
        ) : (
          <>
            {conflicts.length > 0 && (
              <Callout tone="warning" title="Conflicting delegation names">
                {conflicts.map((c) => (
                  <span key={c.recordName} className="block">
                    <span className="mono">{c.recordName}</span> is needed with different targets ({c.targets.join(', ')}).
                  </span>
                ))}
                A name can hold only one CNAME, so certificates for some of these domains will fail. Give these hosts the same delegation name.
              </Callout>
            )}
            <DelegationRecordsPanel
              rows={rows}
              requests={() => checkRequests(rows)}
              toolbarNote={unsavedName ? <span className="text-xs text-fg-subtle">Shows the unsaved default name. Save to apply it.</span> : undefined}
            />
          </>
        )}
        {undelegated > 0 && rows.length > 0 && (
          <p className="text-xs text-fg-subtle">
            {pluralize(undelegated, 'host')} with DNS-01 {undelegated === 1 ? 'writes its' : 'write their'} challenge records in the domain’s own zone
            (no delegation), so the provider token needs access to {undelegated === 1 ? 'that zone' : 'those zones'} too.
          </p>
        )}
      </div>
    </div>
  );
}

/** All hosts' records (sorted by record name), the number of DNS-01 hosts without delegation, and record names needed with two targets. */
function delegationRows(hosts: SiteHost[], s: DelegationSettings) {
  const rows: DelegationRow[] = [];
  let undelegated = 0;
  for (const h of hosts) {
    if (dnsChallengeDomains(h, s).length === 0) continue;
    if (!effectiveDelegation(h, s)) {
      undelegated++;
      continue;
    }
    for (const r of hostDelegationRecords(h, s))
      rows.push({ ...r, note: h.enabled ? undefined : <Badge title="Create the record before enabling the host">Host disabled</Badge> });
  }
  // Two hosts may need the same record (example.com and *.example.com): list it once per target.
  const unique = new Map<string, DelegationRow>();
  for (const r of rows) {
    const key = `${r.recordName}|${r.target}`;
    const seen = unique.get(key);
    if (seen) {
      seen.domains = [...new Set([...seen.domains, ...r.domains])];
      if (!r.note) seen.note = undefined;
    }
    else unique.set(key, { ...r, domains: [...r.domains] });
  }
  const list = [...unique.values()].sort((a, b) => a.recordName.localeCompare(b.recordName) || a.target.localeCompare(b.target));
  const targets = new Map<string, string[]>();
  for (const r of list) targets.set(r.recordName, [...(targets.get(r.recordName) ?? []), r.target]);
  const conflicts = [...targets].filter(([, t]) => t.length > 1).map(([recordName, t]) => ({ recordName, targets: t }));
  return { rows: list, undelegated, conflicts };
}
