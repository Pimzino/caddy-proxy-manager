import { useState } from 'react';
import { History } from 'lucide-react';
import { errorMessage } from '@/api/client';
import { useAudit } from '@/api/hooks';
import { Badge, Callout, Card, EmptyState, PageHeader, Pagination, SearchInput, Spinner, Table, TableSkeleton, TBody, TD, TH, THead, TR, type Tone } from '@/components/ui';
import { formatDateTime } from '@/lib/format';
import { useDebounced } from '@/lib/useDebounced';

const TAKE = 50;

function actionTone(action: string): Tone {
  const a = action.toLowerCase();
  if (a.includes('delet') || a.includes('fail') || a.includes('uninstall')) return 'danger';
  if (a.includes('creat') || a.includes('login') || a.includes('install')) return 'success';
  if (a.includes('disabl') || a.includes('stop')) return 'warning';
  if (a.includes('appl') || a.includes('enabl') || a.includes('start')) return 'accent';
  return 'neutral';
}

export default function AuditPage() {
  const [skip, setSkip] = useState(0);
  const [q, setQ] = useState('');
  const debounced = useDebounced(q.trim(), 350);
  const [lastQ, setLastQ] = useState(debounced);
  if (lastQ !== debounced) {
    // Reset paging when the search changes (derived state, adjusted during render).
    setLastQ(debounced);
    setSkip(0);
  }
  const audit = useAudit({ skip, take: TAKE, q: debounced });

  return (
    <>
      <PageHeader title="Audit Log" description="Every change made through the console or API, with who made it and from where." />
      <Card>
        <div className="flex flex-wrap items-center gap-3 border-b border-border px-4 py-2.5">
          <SearchInput value={q} onChange={setQ} placeholder="Search user, action, object…" />
          {audit.isFetching && !audit.isPending && <Spinner size={14} label="Loading" />}
          <span className="ml-auto text-sm text-fg-subtle">{audit.data ? `${audit.data.total} entries` : ''}</span>
        </div>
        {audit.isPending ? (
          <TableSkeleton rows={10} cols={5} />
        ) : audit.isError ? (
          <div className="p-4">
            <Callout tone="danger" title="Could not load the audit log">
              {errorMessage(audit.error)}
            </Callout>
          </div>
        ) : audit.data.items.length === 0 ? (
          <EmptyState icon={<History size={18} />} title={debounced ? 'No matching entries' : 'No audit entries yet'} />
        ) : (
          <>
            <Table>
              <THead>
                <tr>
                  <TH className="w-44">Time</TH>
                  <TH>User</TH>
                  <TH>Action</TH>
                  <TH>Object</TH>
                  <TH>Details</TH>
                  <TH>Remote IP</TH>
                </tr>
              </THead>
              <TBody>
                {audit.data.items.map((a) => (
                  <TR key={a.id}>
                    <TD className="whitespace-nowrap text-fg-muted">{formatDateTime(a.createdAt)}</TD>
                    <TD className="whitespace-nowrap">{a.userName}</TD>
                    <TD>
                      <Badge tone={actionTone(a.action)}>{a.action}</Badge>
                    </TD>
                    <TD className="max-w-[260px]">
                      <span className="mono text-xs text-fg-subtle">{a.objectType}</span>
                      {(a.objectName || a.objectId) && <p className="truncate text-fg" title={a.objectId}>{a.objectName ?? a.objectId}</p>}
                    </TD>
                    <TD className="max-w-[380px]">
                      <p className="line-clamp-2 break-words text-fg-muted" title={a.details}>
                        {a.details}
                      </p>
                    </TD>
                    <TD className="mono text-xs whitespace-nowrap text-fg-muted">{a.remoteIp ?? '—'}</TD>
                  </TR>
                ))}
              </TBody>
            </Table>
            <Pagination skip={skip} take={TAKE} total={audit.data.total} onChange={setSkip} />
          </>
        )}
      </Card>
    </>
  );
}
