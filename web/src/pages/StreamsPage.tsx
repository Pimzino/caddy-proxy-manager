import { useMemo, useState, type FormEvent } from 'react';
import { Link } from 'react-router';
import { Cable, MoreHorizontal, Pencil, Plus, Power, PowerOff, Trash2 } from 'lucide-react';
import { ApiError, errorMessage } from '@/api/client';
import { useDeleteStream, useSaveStream, useStreams, useStreamSupport } from '@/api/hooks';
import type { StreamHost, StreamHostFields } from '@/api/types';
import { useAuth } from '@/auth';
import { useFeedback } from '@/components/feedback';
import {
  Badge,
  Button,
  Callout,
  Card,
  Dialog,
  DropdownMenu,
  EmptyState,
  Field,
  Input,
  NumberInput,
  PageHeader,
  SearchInput,
  Segmented,
  Switch,
  SwitchField,
  Table,
  TableSkeleton,
  TBody,
  TD,
  TH,
  THead,
  TR,
  Textarea,
  useConfirm,
} from '@/components/ui';
import { isValidPort, isValidUpstreamHost, type FieldErrors } from '@/lib/validation';
import { pluralize } from '@/lib/format';

const emptyStream: StreamHostFields = {
  enabled: true,
  protocol: 'tcp',
  listenPort: Number.NaN,
  upstreamHost: '',
  upstreamPort: Number.NaN,
  notes: null,
};

function validateStream(s: StreamHostFields, others: StreamHost[]): FieldErrors {
  const e: FieldErrors = {};
  if (!isValidPort(s.listenPort)) e.listenPort = 'Port must be between 1 and 65535.';
  else if (others.some((o) => o.enabled && s.enabled && o.protocol === s.protocol && o.listenPort === s.listenPort))
    e.listenPort = `Another enabled ${s.protocol.toUpperCase()} stream already listens on port ${s.listenPort}.`;
  if (!s.upstreamHost.trim()) e.upstreamHost = 'Enter the upstream host name or IP address.';
  else if (!isValidUpstreamHost(s.upstreamHost)) e.upstreamHost = 'Not a valid host name or IP address.';
  if (!isValidPort(s.upstreamPort)) e.upstreamPort = 'Port must be between 1 and 65535.';
  return e;
}

export default function StreamsPage() {
  const { canOperate } = useAuth();
  const streams = useStreams();
  const support = useStreamSupport();
  const del = useDeleteStream();
  const save = useSaveStream();
  const feedback = useFeedback();
  const confirm = useConfirm();
  const [q, setQ] = useState('');
  const [editor, setEditor] = useState<{ stream?: StreamHost } | null>(null);

  const list = useMemo(() => {
    const all = [...(streams.data ?? [])].sort((a, b) => a.listenPort - b.listenPort);
    const n = q.trim().toLowerCase();
    return n
      ? all.filter((s) => `${s.listenPort} ${s.upstreamHost}:${s.upstreamPort} ${s.notes ?? ''} ${s.protocol}`.toLowerCase().includes(n))
      : all;
  }, [streams.data, q]);

  const toggle = (s: StreamHost, enabled: boolean) => {
    const { id, createdAt: _c, updatedAt: _u, ...fields } = s;
    save.mutate(
      { id, stream: { ...fields, enabled } },
      {
        onSuccess: (res) =>
          feedback.applied(
            res.apply,
            enabled ? 'Stream enabled and applied' : 'Stream disabled and applied',
            enabled && res.apply.warnings?.some((w) => w.includes('layer4')) ? 'Stream enabled but not active' : undefined,
          ),
        onError: (err) => feedback.failed(err),
      },
    );
  };

  const remove = async (s: StreamHost) => {
    const ok = await confirm({
      title: 'Delete stream?',
      message: (
        <>
          {s.protocol.toUpperCase()} port <span className="mono text-fg">{s.listenPort}</span> →{' '}
          <span className="mono text-fg">
            {s.upstreamHost}:{s.upstreamPort}
          </span>{' '}
          will stop being forwarded.
        </>
      ),
      confirmLabel: 'Delete',
      danger: true,
    });
    if (!ok) return;
    del.mutate(s.id, {
      onSuccess: (res) => feedback.applied(res.apply, 'Deleted and applied'),
      onError: (err) => feedback.failed(err, { title: 'Could not delete the stream' }),
    });
  };

  const unsupported = support.data && !support.data.supported;

  return (
    <>
      <PageHeader
        title="Streams"
        description="Forward raw TCP or UDP ports (databases, RDP, SSH, game servers) to another host — layer 4, no HTTP."
        actions={
          canOperate && (
            <Button variant="primary" icon={<Plus size={14} />} onClick={() => setEditor({})}>
              Add stream
            </Button>
          )
        }
      />
      {unsupported && (
        <Callout
          tone="warning"
          className="mb-4"
          title="The installed Caddy binary does not include the layer4 plugin"
          actions={
            <Link
              to="/caddy/plugins"
              className="inline-flex h-7 items-center rounded-md border border-border-strong bg-surface px-2.5 text-sm font-medium text-fg hover:bg-surface-2"
            >
              Open Plugins
            </Link>
          }
        >
          Streams are saved but not applied until Caddy is rebuilt with <span className="mono text-fg">{support.data?.plugin}</span>.
          Add it on the Plugins page and choose “Rebuild &amp; install”.
        </Callout>
      )}
      <Card>
        <div className="flex flex-wrap items-center gap-3 border-b border-border px-4 py-2.5">
          <SearchInput value={q} onChange={setQ} placeholder="Search ports, hosts, notes…" />
          <span className="ml-auto text-sm text-fg-subtle">{streams.data && pluralize(streams.data.length, 'stream')}</span>
        </div>
        {streams.isPending ? (
          <TableSkeleton rows={3} cols={5} />
        ) : streams.isError ? (
          <div className="p-4">
            <Callout tone="danger" title="Could not load streams">
              {errorMessage(streams.error)}
            </Callout>
          </div>
        ) : (streams.data ?? []).length === 0 ? (
          <EmptyState
            icon={<Cable size={18} />}
            title="No streams yet"
            description="Forward a port such as TCP 3389 to an internal RDP host, or UDP 1194 to a VPN server."
            action={
              canOperate && (
                <Button variant="primary" icon={<Plus size={14} />} onClick={() => setEditor({})}>
                  Add stream
                </Button>
              )
            }
          />
        ) : list.length === 0 ? (
          <EmptyState title="No matches" description={`Nothing matches “${q}”.`} />
        ) : (
          <Table>
            <THead>
              <tr>
                <TH className="w-36">Status</TH>
                <TH>Protocol</TH>
                <TH>Listen port</TH>
                <TH>Upstream</TH>
                <TH>Notes</TH>
                <TH className="w-12">
                  <span className="sr-only">Actions</span>
                </TH>
              </tr>
            </THead>
            <TBody>
              {list.map((s) => (
                <TR key={s.id} interactive={canOperate} onClick={canOperate ? () => setEditor({ stream: s }) : undefined}>
                  <TD onClick={(e) => e.stopPropagation()}>
                    <div className="flex items-center gap-2">
                      <Switch
                        size="sm"
                        checked={s.enabled}
                        disabled={!canOperate}
                        onChange={(v) => toggle(s, v)}
                        aria-label={`${s.enabled ? 'Disable' : 'Enable'} stream on port ${s.listenPort}`}
                      />
                      <span className={s.enabled ? 'text-sm' : 'text-sm text-fg-subtle'}>{s.enabled ? 'Enabled' : 'Disabled'}</span>
                    </div>
                  </TD>
                  <TD>
                    <Badge tone={s.protocol === 'tcp' ? 'info' : 'accent'}>{s.protocol.toUpperCase()}</Badge>
                  </TD>
                  <TD className="mono">{s.listenPort}</TD>
                  <TD className="mono">
                    {s.upstreamHost}:{s.upstreamPort}
                  </TD>
                  <TD className="max-w-[280px] truncate text-fg-muted">{s.notes}</TD>
                  <TD onClick={(e) => e.stopPropagation()} className="text-right">
                    {canOperate && (
                      <DropdownMenu
                        items={[
                          { label: 'Edit', icon: <Pencil size={14} />, onSelect: () => setEditor({ stream: s }) },
                          {
                            label: s.enabled ? 'Disable' : 'Enable',
                            icon: s.enabled ? <PowerOff size={14} /> : <Power size={14} />,
                            onSelect: () => toggle(s, !s.enabled),
                          },
                          'separator',
                          { label: 'Delete', icon: <Trash2 size={14} />, danger: true, onSelect: () => void remove(s) },
                        ]}
                        trigger={(p) => (
                          <Button {...p} variant="ghost" size="sm" iconOnly aria-label={`Actions for port ${s.listenPort}`} icon={<MoreHorizontal size={16} />} />
                        )}
                      />
                    )}
                  </TD>
                </TR>
              ))}
            </TBody>
          </Table>
        )}
      </Card>
      {editor && (
        <StreamEditor
          stream={editor.stream}
          others={(streams.data ?? []).filter((s) => s.id !== editor.stream?.id)}
          onClose={() => setEditor(null)}
        />
      )}
    </>
  );
}

function StreamEditor({ stream, others, onClose }: { stream?: StreamHost; others: StreamHost[]; onClose: () => void }) {
  const initial: StreamHostFields = stream
    ? {
        enabled: stream.enabled,
        protocol: stream.protocol,
        listenPort: stream.listenPort,
        upstreamHost: stream.upstreamHost,
        upstreamPort: stream.upstreamPort,
        notes: stream.notes ?? null,
      }
    : emptyStream;
  const [form, setForm] = useState<StreamHostFields>(initial);
  const [submitted, setSubmitted] = useState(false);
  const [serverErrors, setServerErrors] = useState<FieldErrors>({});
  const save = useSaveStream();
  const feedback = useFeedback();
  const errors = { ...serverErrors, ...(submitted ? validateStream(form, others) : {}) };
  const set = <K extends keyof StreamHostFields>(k: K, v: StreamHostFields[K]) => {
    setForm((f) => ({ ...f, [k]: v }));
    setServerErrors({});
  };

  const submit = (e: FormEvent) => {
    e.preventDefault();
    setSubmitted(true);
    if (Object.keys(validateStream(form, others)).length) return;
    save.mutate(
      { id: stream?.id, stream: { ...form, upstreamHost: form.upstreamHost.trim(), notes: form.notes?.trim() || null } },
      {
        onSuccess: (res) => {
          // Without the layer4 module the stream is stored but not active; don't claim it was applied.
          const inactive = res.apply.warnings?.some((w) => w.includes('layer4'));
          feedback.applied(res.apply, stream ? 'Saved and applied' : 'Stream created and applied', inactive ? 'Stream saved but not active' : undefined);
          onClose();
        },
        onError: (err) => {
          if (err instanceof ApiError && err.status === 409) {
            setServerErrors({ listenPort: err.detail ?? err.title });
            return;
          }
          feedback.failed(err, { onFieldErrors: setServerErrors });
        },
      },
    );
  };

  return (
    <Dialog
      open
      onClose={onClose}
      dismissible={!save.isPending}
      title={stream ? 'Edit stream' : 'New stream'}
      description="Caddy listens on this port and forwards raw connections to the upstream."
      footer={
        <>
          <Button onClick={onClose} disabled={save.isPending}>
            Cancel
          </Button>
          <Button type="submit" form="stream-form" variant="primary" loading={save.isPending}>
            {stream ? 'Save' : 'Create'}
          </Button>
        </>
      }
    >
      <form id="stream-form" onSubmit={submit} noValidate className="flex flex-col gap-4">
        <Field label="Protocol">
          <div>
            <Segmented
              aria-label="Protocol"
              value={form.protocol}
              onChange={(v) => set('protocol', v)}
              options={[
                { value: 'tcp', label: 'TCP' },
                { value: 'udp', label: 'UDP' },
              ]}
            />
          </div>
        </Field>
        <div className="grid gap-4 sm:grid-cols-[140px_minmax(0,1fr)_120px]">
          <Field label="Listen port" required error={errors.listenPort}>
            <NumberInput min={1} max={65535} value={form.listenPort} onValueChange={(v) => set('listenPort', v)} placeholder="3389" />
          </Field>
          <Field label="Upstream host" required error={errors.upstreamHost}>
            <Input mono value={form.upstreamHost} onChange={(e) => set('upstreamHost', e.target.value)} placeholder="10.0.0.40" />
          </Field>
          <Field label="Upstream port" required error={errors.upstreamPort}>
            <NumberInput min={1} max={65535} value={form.upstreamPort} onValueChange={(v) => set('upstreamPort', v)} placeholder="3389" />
          </Field>
        </div>
        <SwitchField label="Enabled" checked={form.enabled} onChange={(v) => set('enabled', v)} />
        <Field label="Notes">
          <Textarea rows={3} value={form.notes ?? ''} onChange={(e) => set('notes', e.target.value)} />
        </Field>
        <p className="text-xs text-fg-subtle">
          Remember to allow the listen port in Windows Firewall ({form.protocol.toUpperCase()} inbound) — the Readiness page only
          checks the HTTP/HTTPS ports.
        </p>
      </form>
    </Dialog>
  );
}
