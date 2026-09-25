import { useState, type ReactNode } from 'react';
import { AlertTriangle, Check, CircleAlert, CircleDashed, SearchCheck } from 'lucide-react';
import { errorMessage } from '@/api/client';
import { useDelegationCheck } from '@/api/hooks';
import type { DelegationCheck, DelegationStatus } from '@/api/types';
import { Badge, Button, Checkbox, CopyButton, Table, TBody, TD, TH, THead, TR, type Tone } from '@/components/ui';
import { formatTime } from '@/lib/format';
import { cn } from '@/lib/cn';
import { checkKey, recordKey, zoneLine, type DelegationRecord } from './dnsDelegation';

/** One POST /api/dns/delegation-check body (publicResolvers is added by the panel). */
export type DelegationCheckRequest = { hostId: string } | { domains: string[]; target: string };

export interface DelegationRow extends DelegationRecord {
  /** Extra line under the record name, e.g. "Host disabled". */
  note?: ReactNode;
}

const STATUS: Record<DelegationStatus, { label: string; tone: Tone; icon: ReactNode }> = {
  ok: { label: 'OK', tone: 'success', icon: <Check size={11} aria-hidden /> },
  missing: { label: 'Missing', tone: 'warning', icon: <CircleDashed size={11} aria-hidden /> },
  wrong: { label: 'Points elsewhere', tone: 'danger', icon: <AlertTriangle size={11} aria-hidden /> },
  error: { label: 'Lookup failed', tone: 'neutral', icon: <CircleAlert size={11} aria-hidden /> },
};

interface CheckState {
  results: Map<string, DelegationCheck>;
  resolvers: string[];
  checkedAt: string | null;
  error: string | null;
}

const EMPTY: CheckState = { results: new Map(), resolvers: [], checkedAt: null, error: null };

/**
 * The CNAME records to create, with copy buttons, "Copy all" (zone-file lines) and "Check DNS"
 * (POST /api/dns/delegation-check, optionally through public resolvers). Copy and Check stay usable in read-only views:
 * render it outside disabled fieldsets.
 */
export function DelegationRecordsPanel({
  rows,
  requests,
  compact,
  toolbarNote,
}: {
  rows: DelegationRow[];
  /** Check requests covering the rows. */
  requests: () => DelegationCheckRequest[];
  /** Narrow layout (host editor). */
  compact?: boolean;
  toolbarNote?: ReactNode;
}) {
  const check = useDelegationCheck();
  const [publicDns, setPublicDns] = useState(false);
  const [running, setRunning] = useState(false);
  const [state, setState] = useState<CheckState>(EMPTY);

  const run = async () => {
    setRunning(true);
    try {
      const res = await Promise.all(requests().map((r) => check.mutateAsync({ ...r, publicResolvers: publicDns })));
      const results = new Map<string, DelegationCheck>();
      for (const c of res.flatMap((x) => x.checks)) results.set(checkKey(c), c);
      setState({
        results,
        resolvers: [...new Set(res.flatMap((x) => x.resolvers))],
        checkedAt: res.map((x) => x.checkedAt).sort().at(-1) ?? new Date().toISOString(),
        error: null,
      });
    } catch (err) {
      setState({ ...EMPTY, error: errorMessage(err) });
    } finally {
      setRunning(false);
    }
  };

  const found = rows.map((r) => state.results.get(recordKey(r.recordName, r.target)));
  const counts = found.reduce<Partial<Record<DelegationStatus, number>>>((acc, c) => (c ? { ...acc, [c.status]: (acc[c.status] ?? 0) + 1 } : acc), {});
  const checked = found.filter(Boolean).length;

  return (
    <div className="flex flex-col gap-3">
      <div className="flex flex-wrap items-center gap-2">
        <Button size="sm" icon={<SearchCheck size={14} />} loading={running} onClick={() => void run()} disabled={rows.length === 0}>
          Check DNS
        </Button>
        <CopyButton label="Copy all" text={() => rows.map(zoneLine).join('\n')} />
        {toolbarNote}
      </div>
      <Checkbox
        checked={publicDns}
        onChange={(v) => {
          setPublicDns(v);
          setState(EMPTY);
        }}
        label="Check with public DNS (what the certificate authority sees)"
        description="Asks 1.1.1.1 and 8.8.8.8 instead of this server’s DNS. Use it when internal DNS answers differently for your domains."
      />

      <Table className="rounded-md border border-border">
        <THead>
          <tr>
            <TH>Name</TH>
            <TH>Type</TH>
            <TH>Target</TH>
            <TH>Status</TH>
          </tr>
        </THead>
        <TBody>
          {rows.map((r, i) => (
            <TR key={r.recordName + r.target}>
              <TD className="align-top">
                <CopyableName value={r.recordName} label="Copy name" />
                {(!compact || r.domains.length > 1) && (
                  <p className="mono mt-0.5 text-xs text-fg-subtle" title="Domains this record covers">
                    {r.domains.join(', ')}
                  </p>
                )}
                {r.note && <div className="mt-1">{r.note}</div>}
              </TD>
              <TD className="mono align-top text-xs text-fg-muted">CNAME</TD>
              <TD className="align-top">
                <CopyableName value={r.target} label="Copy target" />
              </TD>
              <TD className={cn('align-top', compact ? 'min-w-36' : 'min-w-48')}>
                <StatusCell check={found[i]} />
              </TD>
            </TR>
          ))}
        </TBody>
      </Table>

      {state.error ? (
        <p role="alert" className="text-sm text-danger">
          DNS check failed: {state.error}
        </p>
      ) : (
        state.checkedAt && (
          <p className="text-xs text-fg-subtle" aria-live="polite">
            {summary(counts, checked)} Checked at {formatTime(state.checkedAt)} with {resolverLabel(state.resolvers)}. DNS changes can take a while to
            appear; check again after the record’s TTL.
          </p>
        )
      )}
    </div>
  );
}

function CopyableName({ value, label }: { value: string; label: string }) {
  return (
    <div className="flex items-center gap-1">
      <span className="mono text-sm break-all text-fg">{value}</span>
      <CopyButton iconOnly size="xs" variant="ghost" label={`${label} ${value}`} text={value} />
    </div>
  );
}

function StatusCell({ check }: { check?: DelegationCheck }) {
  if (!check) return <span className="text-xs text-fg-subtle">Not checked</span>;
  const s = STATUS[check.status];
  return (
    <div className="flex flex-col items-start gap-1">
      <Badge tone={s.tone} icon={s.icon}>
        {s.label}
      </Badge>
      {check.found.length > 0 && (
        <p className="mono text-xs break-all text-fg-subtle" title="CNAME chain found">
          → {check.found.join(' → ')}
        </p>
      )}
      {check.detail && <p className="text-xs text-fg-subtle">{check.detail}</p>}
    </div>
  );
}

function summary(counts: Partial<Record<DelegationStatus, number>>, checked: number): string {
  if (!checked) return '';
  const parts = (['ok', 'missing', 'wrong', 'error'] as const).filter((k) => counts[k]).map((k) => `${counts[k]} ${STATUS[k].label.toLowerCase()}`);
  return counts.ok === checked ? `All ${checked} ${checked === 1 ? 'record is' : 'records are'} in place.` : `${parts.join(', ')}.`;
}

function resolverLabel(resolvers: string[]): string {
  const list = resolvers.filter((r) => r !== 'system');
  if (!list.length) return 'this server’s DNS';
  return resolvers.includes('system') ? `this server’s DNS, ${list.join(', ')}` : list.join(', ');
}
