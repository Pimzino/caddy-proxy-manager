import { useMemo, useState, type FormEvent } from 'react';
import { AlertTriangle, Download, FileKey2, MoreHorizontal, Pencil, Plus, RefreshCw, ShieldCheck, Trash2 } from 'lucide-react';
import { ApiError, downloadFile, errorMessage } from '@/api/client';
import { useCertificates, useDeleteCertificate, useHosts, useUpdateCertificate } from '@/api/hooks';
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
import { formatDate } from '@/lib/format';
import { AddCertificateDialog } from './AddCertificateDialog';

type Filter = 'all' | CertificateKind;

const kindLabel: Record<CertificateKind, { label: string; tone: Tone }> = {
  custom: { label: 'Custom', tone: 'accent' },
  acme: { label: 'ACME', tone: 'success' },
  internal: { label: 'Internal', tone: 'info' },
  internalRoot: { label: 'Internal root CA', tone: 'neutral' },
};

export function expiryInfo(c: Pick<CertificateInfo, 'daysRemaining' | 'kind'>): { tone: Tone; label: string } {
  const d = c.daysRemaining;
  if (d < 0) return { tone: 'danger', label: `Expired ${Math.abs(d)} day${Math.abs(d) === 1 ? '' : 's'} ago` };
  if (d === 0) return { tone: 'danger', label: 'Expires today' };
  const auto = c.kind === 'acme' || c.kind === 'internal';
  if (d <= 7) return { tone: auto ? 'warning' : 'danger', label: `${d} day${d === 1 ? '' : 's'} left` };
  if (d <= 30) return { tone: 'warning', label: `${d} days left` };
  return { tone: 'success', label: `${d} days left` };
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
  const feedback = useFeedback();
  const confirm = useConfirm();
  const toast = useToast();
  const [q, setQ] = useState('');
  const [filter, setFilter] = useState<Filter>('all');
  const [adding, setAdding] = useState(false);
  const [replacing, setReplacing] = useState<CertificateInfo | null>(null);
  const [editing, setEditing] = useState<CertificateInfo | null>(null);

  const hostName = (id: string) => hosts.data?.find((h) => h.id === id)?.domains[0] ?? id;

  const list = useMemo(() => {
    const n = q.trim().toLowerCase();
    return (certs.data ?? [])
      .filter((c) => filter === 'all' || c.kind === filter)
      .filter((c) => !n || [c.name, c.issuer, ...c.subjects, c.certPath ?? ''].join(' ').toLowerCase().includes(n))
      .sort((a, b) => a.daysRemaining - b.daysRemaining);
  }, [certs.data, q, filter]);

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

  const remove = async (c: CertificateInfo) => {
    const ok = await confirm({
      title: 'Delete certificate?',
      message:
        c.source === 'filePath'
          ? `“${c.name}” will be removed from the manager. The referenced files on disk are not deleted.`
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
          <span className="ml-auto text-sm text-fg-subtle">{list.length} certificates</span>
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
                const exp = expiryInfo(c);
                const kind = kindLabel[c.kind];
                const isCustom = c.kind === 'custom';
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
                        {isCustom && c.source && `${c.source === 'filePath' ? 'By path' : 'Uploaded'} · `}
                        {issuerName(c.issuer)}
                      </p>
                      {c.error && <p className="truncate text-xs text-danger">{c.error}</p>}
                    </TD>
                    <TD>
                      <Badge tone={kind.tone}>{kind.label}</Badge>
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
                        {formatDate(c.notAfter)}
                        {(c.kind === 'acme' || c.kind === 'internal') && ' · auto-renews'}
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
                                    label: 'Replace…',
                                    icon: <RefreshCw size={14} />,
                                    onSelect: () => setReplacing(c),
                                    hidden: c.source === 'filePath',
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
