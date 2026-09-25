import { useState } from 'react';
import { Activity, AlertTriangle, BellRing, CheckCircle2, ChevronRight, Info, XCircle } from 'lucide-react';
import { errorMessage } from '@/api/client';
import { useEvents } from '@/api/hooks';
import type { EventEntry, EventSeverity } from '@/api/types';
import { Badge, Callout, Card, EmptyState, PageHeader, Pagination, Select, Table, TableSkeleton, TBody, TD, TH, THead, TR, type Tone } from '@/components/ui';
import { cn } from '@/lib/cn';
import { formatDateTime, formatRelative } from '@/lib/format';
import { useNow } from '@/lib/useNow';

export const severityMeta: Record<EventSeverity, { tone: Tone; label: string; icon: typeof Info }> = {
  info: { tone: 'info', label: 'Info', icon: Info },
  warning: { tone: 'warning', label: 'Warning', icon: AlertTriangle },
  error: { tone: 'danger', label: 'Error', icon: XCircle },
  recovered: { tone: 'success', label: 'Recovered', icon: CheckCircle2 },
};

export function SeverityBadge({ severity }: { severity: EventSeverity }) {
  const m = severityMeta[severity] ?? severityMeta.info;
  const Icon = m.icon;
  return (
    <Badge tone={m.tone} icon={<Icon size={11} />}>
      {m.label}
    </Badge>
  );
}

const TAKE = 50;

export default function EventsPage() {
  const [skip, setSkip] = useState(0);
  const [severity, setSeverity] = useState<EventSeverity | ''>('');
  const events = useEvents({ skip, take: TAKE, severity });
  const now = useNow(30_000);
  const [open, setOpen] = useState<string | null>(null);

  return (
    <>
      <PageHeader
        title="Events"
        description="Operational events raised by the monitor: Caddy outages, rejected configs, unhealthy upstreams, expiring certificates, updates and readiness failures."
      />
      <Card>
        <div className="flex flex-wrap items-center gap-3 border-b border-border px-4 py-2.5">
          <Select
            aria-label="Severity"
            value={severity}
            onChange={(e) => {
              setSeverity(e.target.value as EventSeverity | '');
              setSkip(0);
            }}
            className="w-44"
          >
            <option value="">All severities</option>
            <option value="error">Errors</option>
            <option value="warning">Warnings</option>
            <option value="recovered">Recovered</option>
            <option value="info">Info</option>
          </Select>
          <span className="ml-auto text-sm text-fg-subtle">Kept for 90 days</span>
        </div>
        {events.isPending ? (
          <TableSkeleton rows={8} cols={4} />
        ) : events.isError ? (
          <div className="p-4">
            <Callout tone="danger" title="Could not load events">
              {errorMessage(events.error)}
            </Callout>
          </div>
        ) : events.data.items.length === 0 ? (
          <EmptyState icon={<Activity size={18} />} title="No events" description={severity ? 'No events with this severity.' : 'Nothing has happened yet — that is good news.'} />
        ) : (
          <>
            <Table>
              <THead>
                <tr>
                  <TH className="w-8">
                    <span className="sr-only">Expand</span>
                  </TH>
                  <TH className="w-44">Time</TH>
                  <TH className="w-32">Severity</TH>
                  <TH className="w-32">Category</TH>
                  <TH>Message</TH>
                  <TH className="w-24">Notified</TH>
                </tr>
              </THead>
              <TBody>
                {events.data.items.map((e) => (
                  <EventRow key={e.id} e={e} now={now} open={open === e.id} onToggle={() => setOpen(open === e.id ? null : e.id)} />
                ))}
              </TBody>
            </Table>
            <Pagination skip={skip} take={TAKE} total={events.data.total} onChange={setSkip} />
          </>
        )}
      </Card>
    </>
  );
}

function EventRow({ e, now, open, onToggle }: { e: EventEntry; now: number; open: boolean; onToggle: () => void }) {
  return (
    <>
      <TR interactive={!!e.details} onClick={e.details ? onToggle : undefined}>
        <TD className="pr-0">
          {e.details && (
            <button
              type="button"
              aria-expanded={open}
              aria-label={open ? 'Hide details' : 'Show details'}
              onClick={(ev) => {
                ev.stopPropagation();
                onToggle();
              }}
              className="flex h-6 w-6 items-center justify-center rounded text-fg-subtle hover:bg-surface-3 hover:text-fg focus-visible:outline-2 focus-visible:outline-ring"
            >
              <ChevronRight size={14} className={cn('transition-transform', open && 'rotate-90')} />
            </button>
          )}
        </TD>
        <TD className="whitespace-nowrap" title={formatDateTime(e.createdAt)}>
          <span className="text-fg">{formatRelative(e.createdAt, now)}</span>
          <p className="text-xs text-fg-subtle">{formatDateTime(e.createdAt)}</p>
        </TD>
        <TD>
          <SeverityBadge severity={e.severity} />
        </TD>
        <TD className="mono text-xs text-fg-muted">{e.category}</TD>
        <TD className="max-w-[560px]">
          <p className="break-words text-fg">{e.message}</p>
          {e.key && <p className="mono truncate text-[11px] text-fg-subtle">{e.key}</p>}
        </TD>
        <TD>
          {e.notified ? (
            <span className="inline-flex items-center gap-1 text-xs text-fg-muted">
              <BellRing size={12} aria-hidden /> Sent
            </span>
          ) : (
            <span className="text-xs text-fg-subtle">—</span>
          )}
        </TD>
      </TR>
      {open && e.details && (
        <tr className="bg-surface-2/40">
          <td colSpan={6} className="px-4 py-3 pl-14">
            <pre className="mono max-h-72 overflow-auto text-xs leading-relaxed break-words whitespace-pre-wrap text-fg">{e.details}</pre>
          </td>
        </tr>
      )}
    </>
  );
}
