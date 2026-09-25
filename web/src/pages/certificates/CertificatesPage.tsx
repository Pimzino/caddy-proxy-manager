import { useMemo, useState, type FormEvent } from 'react';
import { AlertTriangle, Download, FileKey2, MoreHorizontal, Pencil, Plus, RefreshCw, RefreshCcwDot, ShieldCheck, Trash2 } from 'lucide-react';
import { ApiError, downloadFile, errorMessage } from '@/api/client';
import { useCertificates, useDeleteCertificate, useHosts, useSyncCertificate, useUpdateCertificate } from '@/api/hooks';
import type { CertificateInfo, CertificateKind } from '@/api/types';
import { useAuth } from '@/auth';
import { useFeedback } from '@/components/feedback';
import {
  Badge,
  Button,
  Callout,
  Card,
  Chip,
  Dialog,
  DropdownMenu,
  EmptyState,
  Field,
  Input,
  PageHeader,
  SearchInput,
  Segmented,
  StatusDot,
  Table,
  TableSkeleton,
  TBody,
  TD,
  TH,
  THead,
  TR,
  Textarea,
  useConfirm,
  useToast,
  type Tone,
} from '@/components/ui';
import { formatDate, formatDateTime, pluralize } from '@/lib/format';
import { AddCertificateDialog } from './AddCertificateDialog';

type Filter = 'all' | CertificateKind;

const kindLabel: Record<CertificateKind, { label: string; tone: Tone }> = {
  custom: { label: 'Custom', tone: 'accent' },
  acme: { label: 'ACME', tone: 'success' },
  internal: { label: 'Internal', tone: 'info' },
  internalRoot: { label: 'Internal root CA', tone: 'neutral' },
};

/** Where a custom certificate comes from (CertificateInfo.source). */
const SOURCE_LABEL: Record<string, string> = {
  uploaded: 'Uploaded',
  filePath: 'File',
  pfxFile: 'PFX file',
  windowsStore: 'Windows store',
};

/** Sources the manager re-reads by itself (watcher / periodic sync) and that support "Sync now". */
const SYNCED_SOURCES = new Set(['filePath', 'pfxFile', 'windowsStore']);

/** Time left until `notAfter`, in hours below two days (Caddy's internal leaves live ~12h). */
function timeLeft(notAfter: string, now: number): string {
  const ms = new Date(notAfter).getTime() - now;
  const hours = Math.floor(ms / 3_600_000);
  if (hours < 1) return `${Math.max(1, Math.floor(ms / 60_000))} min left`;
  if (hours < 48) return `${hours}h left`;
  const days = Math.floor(hours / 24);
  if (days >= 730) return `${Math.floor(days / 365)} years left`;
  return `${days} days left`;
}

/**
 * Expiry status. Caddy renews ACME and internal certificates by itself: internal-CA leaves live about 12 hours
 * and are renewed continuously, so they are shown neutrally (never amber/red) unless actually expired. ACME
 * certificates are renewed with about a third of their lifetime left, so only the last week is worth a warning.
 */
export function expiryInfo(c: Pick<CertificateInfo, 'daysRemaining' | 'kind' | 'notAfter'>, now: number): { tone: Tone; label: string } {
  const ms = new Date(c.notAfter).getTime() - now;
  if (!Number.isFinite(ms)) return { tone: 'neutral', label: 'Unknown' };
  if (ms <= 0) {
    const d = Math.floor(-ms / 86_400_000);
    return { tone: 'danger', label: d === 0 ? 'Expired' : `Expired ${d} day${d === 1 ? '' : 's'} ago` };
  }
  const left = timeLeft(c.notAfter, now);
  if (c.kind === 'internal') return { tone: 'neutral', label: `Auto-renews · ${left}` };
  if (c.kind === 'internalRoot') return { tone: 'neutral', label: left };
  const d = c.daysRemaining;
  if (c.kind === 'acme') {
    if (d <= 7) return { tone: 'warning', label: `${left} · renewal overdue` };
    return { tone: 'success', label: `Auto-renews · ${left}` };
  }
  if (d < 1) return { tone: 'danger', label: `Expires today · ${left}` };
  if (d <= 7) return { tone: 'danger', label: left };
  if (d <= 30) return { tone: 'warning', label: left };
  return { tone: 'success', label: left };
}

/** Sort key: what needs attention first. Auto-renewing internal leaves never need attention. */
function urgency(c: CertificateInfo, now: number): number {
  const ms = new Date(c.notAfter).getTime() - now;
  if (c.error) return -Infinity;
  if (ms <= 0) return ms;
  if (c.kind === 'internal') return Number.MAX_SAFE_INTEGER - 1;
  if (c.kind === 'internalRoot') return Number.MAX_SAFE_INTEGER;
  return ms;
}

/** "CN=R11, O=Let's Encrypt, C=US" → "R11 (Let's Encrypt)". */
function issuerName(dn: string): string {
  const cn = /CN=([^,]+)/i.exec(dn)?.[1]?.trim();
  const o = /(?:^|,\s*)O=([^,]+)/i.exec(dn)?.[1]?.trim();
  if (cn && o) return `${cn} (${o})`;
  return cn ?? dn;
}

export default function CertificatesPage() {
  const { canOperate } = useAuth();
  const certs = useCertificates();
  const hosts = useHosts();
  const del = useDeleteCertificate();
  const sync = useSyncCertificate();
  const feedback = useFeedback();
  const confirm = useConfirm();
  const toast = useToast();
  const [q, setQ] = useState('');
  const [filter, setFilter] = useState<Filter>('all');
  const [adding, setAdding] = useState(false);
  const [replacing, setReplacing] = useState<CertificateInfo | null>(null);
  const [editing, setEditing] = useState<CertificateInfo | null>(null);

  const hostName = (id: string) => hosts.data?.find((h) => h.id === id)?.domains[0] ?? id;

  // Refreshed with every fetch; keeps render pure.
  const now = certs.dataUpdatedAt || 0;
  const list = useMemo(() => {
    const n = q.trim().toLowerCase();
    return (certs.data ?? [])
      .filter((c) => filter === 'all' || c.kind === filter)
      .filter((c) => !n || [c.name, c.issuer, ...c.subjects, c.certPath ?? '', c.notes ?? ''].join(' ').toLowerCase().includes(n))
      .sort((a, b) => urgency(a, now) - urgency(b, now) || a.name.localeCompare(b.name));
  }, [certs.data, q, filter, now]);

  const hasRoot = (certs.data ?? []).some((c) => c.kind === 'internalRoot');

  const downloadRoot = () =>
    downloadFile('/api/certificates/internal-root', 'caddy-local-root.crt').catch((err: unknown) =>
      toast.error(
        'Could not download the root certificate',
        err instanceof ApiError && err.status === 404
          ? 'Caddy has not created its internal CA yet. It is generated the first time a host uses Internal TLS.'
          : errorMessage(err),
      ),
    );

  const syncNow = (c: CertificateInfo) =>
    sync.mutate(c.id, {
      onSuccess: (res) => {
        if (res.item.lastSyncError) toast.warning(`“${c.name}” could not be re-read`, res.item.lastSyncError);
        else
          feedback.applied(
            res.apply,
            res.item.lastSyncedAt ? `“${c.name}” synced at ${formatDateTime(res.item.lastSyncedAt)}` : `“${c.name}” synced`,
          );
      },
      onError: (err) => feedback.failed(err, { title: `Could not sync “${c.name}”` }),
    });

  const remove = async (c: CertificateInfo) => {
    const ok = await confirm({
      title: 'Delete certificate?',
      message:
        c.source === 'filePath'
          ? `“${c.name}” will be removed from the manager. The referenced files on disk are not deleted.`
          : c.source === 'pfxFile'
            ? `“${c.name}” and its converted PEM copy will be removed. The PFX file on disk is not deleted.`
            : c.source === 'windowsStore'
              ? `“${c.name}” and its exported PEM copy will be removed. The certificate stays in the Windows certificate store.`
              : `“${c.name}” and its private key will be deleted from the certificate store. This cannot be undone.`,
      confirmLabel: 'Delete',
      danger: true,
    });
    if (!ok) return;
    del.mutate(c.id, {
      onSuccess: (res) => feedback.applied(res.apply, 'Certificate deleted'),
      onError: (err) => feedback.failed(err, { title: 'Could not delete the certificate' }),
    });
  };

  return (
    <>
      <PageHeader
        title="Certificates"
        description="Automatic ACME certificates, Caddy’s internal CA, and your own certificates mapped to hosts."
        actions={
          <>
            <Button icon={<Download size={14} />} onClick={() => void downloadRoot()} title="Caddy local CA root, for distribution via GPO">
              Internal root CA
            </Button>
            {canOperate && (
              <Button variant="primary" icon={<Plus size={14} />} onClick={() => setAdding(true)}>
                Add certificate
              </Button>
            )}
          </>
        }
      />
      <Card>
        <div className="flex flex-wrap items-center gap-3 border-b border-border px-4 py-2.5">
          <SearchInput value={q} onChange={setQ} placeholder="Search name, subject, issuer…" />
          <Segmented
            aria-label="Certificate type"
            size="sm"
            value={filter}
            onChange={setFilter}
            options={[
              { value: 'all', label: 'All' },
              { value: 'custom', label: 'Custom' },
              { value: 'acme', label: 'ACME' },
              { value: 'internal', label: 'Internal' },
            ]}
          />
          <span className="ml-auto text-sm text-fg-subtle">{certs.data && pluralize(list.length, 'certificate')}</span>
        </div>
        {certs.isPending ? (
          <TableSkeleton rows={4} cols={6} />
        ) : certs.isError ? (
          <div className="p-4">
            <Callout tone="danger" title="Could not load certificates">
              {errorMessage(certs.error)}
            </Callout>
          </div>
        ) : (certs.data ?? []).length === 0 ? (
          <EmptyState
            icon={<ShieldCheck size={18} />}
            title="No certificates yet"
            description="ACME and internal certificates appear here once Caddy obtains them. Add your own certificate to use it on hosts with Custom TLS."
            action={
              canOperate && (
                <Button variant="primary" icon={<Plus size={14} />} onClick={() => setAdding(true)}>
                  Add certificate
                </Button>
              )
            }
          />
        ) : list.length === 0 ? (
          <EmptyState title="No matches" description="No certificate matches the current filter." />
        ) : (
          <Table>
            <THead>
              <tr>
                <TH>Name</TH>
                <TH>Type</TH>
                <TH>Source</TH>
                <TH>Subjects</TH>
                <TH>Expires</TH>
                <TH>Used by</TH>
                <TH className="w-12">
                  <span className="sr-only">Actions</span>
                </TH>
              </tr>
            </THead>
            <TBody>
              {list.map((c) => {
                const exp = expiryInfo(c, now);
                const kind = kindLabel[c.kind];
                const isCustom = c.kind === 'custom';
                const synced = isCustom && SYNCED_SOURCES.has(c.source ?? '');
                return (
                  <TR key={`${c.kind}:${c.id}`}>
                    <TD className="max-w-[280px]">
                      <div className="flex items-center gap-1.5">
                        <span className="truncate font-medium text-fg" title={c.name}>
                          {c.name || c.subjects[0]}
                        </span>
                        {c.error && (
                          <span title={c.error} className="text-danger">
                            <AlertTriangle size={14} aria-label={`Problem: ${c.error}`} />
                          </span>
                        )}
                      </div>
                      <p className="truncate text-xs text-fg-subtle" title={c.issuer}>
                        {issuerName(c.issuer)}
                      </p>
                      {c.notes && (
                        <p className="truncate text-xs text-fg-muted italic" title={c.notes}>
                          {c.notes}
                        </p>
                      )}
                      {c.error && <p className="truncate text-xs text-danger">{c.error}</p>}
                    </TD>
                    <TD>
                      <Badge tone={kind.tone}>{kind.label}</Badge>
                    </TD>
                    <TD className="whitespace-nowrap">
                      {isCustom ? (
                        <>
                          <span className="text-sm text-fg">{SOURCE_LABEL[c.source ?? ''] ?? c.source ?? '—'}</span>
                          {synced && (
                            <p className="text-xs" title={c.error ?? 'Re-read automatically when the source changes'}>
                              {c.error ? <StatusDot tone="danger" label="Sync failed" /> : <StatusDot tone="success" label="Auto-sync" />}
                            </p>
                          )}
                        </>
                      ) : (
                        <span className="text-sm text-fg-subtle">{c.kind === 'acme' ? 'ACME (Caddy)' : 'Caddy local CA'}</span>
                      )}
                    </TD>
                    <TD className="max-w-[260px]">
                      <div className="flex flex-wrap gap-1">
                        {c.subjects.slice(0, 2).map((s) => (
                          <Chip key={s}>{s}</Chip>
                        ))}
                        {c.subjects.length > 2 && (
                          <span className="text-xs text-fg-subtle" title={c.subjects.slice(2).join('\n')}>
                            +{c.subjects.length - 2}
                          </span>
                        )}
                      </div>
                    </TD>
                    <TD className="whitespace-nowrap">
                      <StatusDot tone={exp.tone} label={exp.label} />
                      <p className="text-xs text-fg-subtle">
                        {c.kind === 'internal' ? formatDateTime(c.notAfter) : formatDate(c.notAfter)}
                      </p>
                    </TD>
                    <TD className="max-w-[200px]">
                      {c.usedByHostIds.length === 0 ? (
                        <span className="text-sm text-fg-subtle">{isCustom ? 'Not used' : '—'}</span>
                      ) : (
                        <div className="flex flex-wrap gap-1" title={c.usedByHostIds.map(hostName).join('\n')}>
                          {c.usedByHostIds.slice(0, 2).map((id) => (
                            <Chip key={id}>{hostName(id)}</Chip>
                          ))}
                          {c.usedByHostIds.length > 2 && <span className="text-xs text-fg-subtle">+{c.usedByHostIds.length - 2}</span>}
                        </div>
                      )}
                    </TD>
                    <TD className="text-right">
                      {(isCustom && canOperate) || c.kind === 'internalRoot' ? (
                        <DropdownMenu
                          width={220}
                          items={
                            c.kind === 'internalRoot'
                              ? [{ label: 'Download root certificate', icon: <Download size={14} />, onSelect: () => void downloadRoot() }]
                              : [
                                  { label: 'Edit name & notes', icon: <Pencil size={14} />, onSelect: () => setEditing(c) },
                                  {
                                    label: 'Sync now',
                                    icon: <RefreshCcwDot size={14} />,
                                    onSelect: () => syncNow(c),
                                    hidden: !synced,
                                    disabled: sync.isPending,
                                  },
                                  {
                                    label: 'Replace…',
                                    icon: <RefreshCw size={14} />,
                                    onSelect: () => setReplacing(c),
                                    hidden: c.source !== 'uploaded',
                                  },
                                  'separator',
                                  {
                                    label: c.usedByHostIds.length ? 'Delete (in use)' : 'Delete',
                                    icon: <Trash2 size={14} />,
                                    danger: true,
                                    disabled: c.usedByHostIds.length > 0,
                                    onSelect: () => void remove(c),
                                  },
                                ]
                          }
                          trigger={(p) => (
                            <Button {...p} variant="ghost" size="sm" iconOnly aria-label={`Actions for ${c.name}`} icon={<MoreHorizontal size={16} />} />
                          )}
                        />
                      ) : null}
                    </TD>
                  </TR>
                );
              })}
            </TBody>
          </Table>
        )}
      </Card>
      {!hasRoot && certs.isSuccess && (
        <p className="mt-3 flex items-center gap-1.5 text-xs text-fg-subtle">
          <FileKey2 size={12} aria-hidden />
          Caddy creates its internal root CA the first time a host uses Internal TLS. Deploy it to clients via GPO (Trusted Root
          Certification Authorities).
        </p>
      )}
      <AddCertificateDialog open={adding} onClose={() => setAdding(false)} />
      <AddCertificateDialog open={!!replacing} onClose={() => setReplacing(null)} replace={replacing ?? undefined} />
      {editing && <EditCertificateDialog cert={editing} onClose={() => setEditing(null)} />}
    </>
  );
}

function EditCertificateDialog({ cert, onClose }: { cert: CertificateInfo; onClose: () => void }) {
  const [name, setName] = useState(cert.name);
  const [notes, setNotes] = useState(cert.notes ?? '');
  const [error, setError] = useState<string | null>(null);
  const update = useUpdateCertificate();
  const feedback = useFeedback();
  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (!name.trim()) {
      setError('Enter a name.');
      return;
    }
    update.mutate(
      { id: cert.id, name: name.trim(), notes: notes.trim() },
      {
        onSuccess: (res) => {
          if (res && typeof res === 'object' && 'apply' in res) feedback.applied(res.apply, 'Certificate updated');
          else feedback.applied(undefined, 'Certificate updated');
          onClose();
        },
        onError: (err) => feedback.failed(err, { title: 'Could not update the certificate' }),
      },
    );
  };
  return (
    <Dialog
      open
      onClose={onClose}
      title="Edit certificate"
      size="sm"
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button type="submit" form="cert-edit" variant="primary" loading={update.isPending}>
            Save
          </Button>
        </>
      }
    >
      <form id="cert-edit" onSubmit={submit} className="flex flex-col gap-4" noValidate>
        <Field label="Name" required error={error}>
          <Input value={name} onChange={(e) => setName(e.target.value)} />
        </Field>
        <Field label="Notes" hint="Where it came from, who renews it, etc.">
          <Textarea rows={4} value={notes} onChange={(e) => setNotes(e.target.value)} />
        </Field>
      </form>
    </Dialog>
  );
}
