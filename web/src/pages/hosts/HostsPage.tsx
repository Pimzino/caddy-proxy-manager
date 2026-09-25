import { useEffect, useMemo, useState } from 'react';
import { useParams, useSearchParams } from 'react-router';
import { Copy, Eye, Globe, KeyRound, LockOpen, MoreHorizontal, Pencil, Plus, Power, PowerOff, ShieldCheck, Trash2 } from 'lucide-react';
import { useAccessLists, useCertificates, useDeleteHost, useHosts, useIsManagedNode, useToggleHost } from '@/api/hooks';
import type { HostKind, SiteHost, SiteHostFields } from '@/api/types';
import { useAuth } from '@/auth';
import { useFeedback } from '@/components/feedback';
import { ReadOnlyOnNode } from '@/components/layout/ManagedNode';
import {
  Badge,
  Button,
  Card,
  Chip,
  DropdownMenu,
  EmptyState,
  PageHeader,
  SearchInput,
  Switch,
  Table,
  TableSkeleton,
  TBody,
  TD,
  TH,
  THead,
  TR,
  Callout,
  useConfirm,
} from '@/components/ui';
import { errorMessage } from '@/api/client';
import { upstreamUrl } from '@/lib/format';
import NotFoundPage from '@/pages/NotFoundPage';
import { HostEditor } from './HostEditor';
import { HOST_KINDS, kindMeta, newHost, toFields } from './hostModel';

type EditorState = { host?: SiteHost; initial: SiteHostFields; readOnly: boolean } | null;

export default function HostsPage() {
  const { kind: param } = useParams();
  if (!HOST_KINDS.includes(param as HostKind)) return <NotFoundPage />;
  // Remount per kind so search and editor state reset when switching pages.
  return <HostsList key={param} kind={param as HostKind} />;
}

function HostsList({ kind }: { kind: HostKind }) {
  const meta = kindMeta[kind];
  const { canOperate: roleCanOperate } = useAuth();
  // On a managed cluster node hosts are replicated from the primary: view only.
  const { managed } = useIsManagedNode();
  const canOperate = roleCanOperate && !managed;
  const hosts = useHosts(kind);
  const certs = useCertificates();
  const lists = useAccessLists();
  const toggle = useToggleHost();
  const del = useDeleteHost();
  const feedback = useFeedback();
  const confirm = useConfirm();
  const [q, setQ] = useState('');
  const [params, setParams] = useSearchParams();
  // "?new=1" (e.g. from the dashboard) opens the editor for a new host.
  const [editor, setEditor] = useState<EditorState>(() =>
    params.get('new') === '1' && canOperate ? { initial: newHost(kind), readOnly: false } : null,
  );
  useEffect(() => {
    if (params.has('new')) setParams({}, { replace: true });
  }, [params, setParams]);

  const certName = (id?: string | null) => certs.data?.find((c) => c.id === id)?.name;
  const listName = (id?: string | null) => lists.data?.find((l) => l.id === id)?.name;

  const filtered = useMemo(() => {
    const all = hosts.data ?? [];
    const needle = q.trim().toLowerCase();
    const sorted = [...all].sort((a, b) => (a.domains[0] ?? '').localeCompare(b.domains[0] ?? ''));
    if (!needle) return sorted;
    return sorted.filter((h) =>
      [
        ...h.domains,
        h.notes ?? '',
        h.redirectTarget ?? '',
        h.rootPath ?? '',
        ...h.upstreams.map((u) => `${u.host}:${u.port}`),
      ]
        .join(' ')
        .toLowerCase()
        .includes(needle),
    );
  }, [hosts.data, q]);

  const openNew = () => setEditor({ initial: newHost(kind), readOnly: false });
  const openEdit = (h: SiteHost) => setEditor({ host: h, initial: toFields(h), readOnly: !canOperate });
  const duplicate = (h: SiteHost) =>
    setEditor({ initial: { ...toFields(h), domains: [], enabled: true }, readOnly: false });

  const setEnabled = (h: SiteHost, enabled: boolean) =>
    toggle.mutate(
      { id: h.id, enabled },
      {
        onSuccess: (res) => feedback.applied(res.apply, enabled ? 'Host enabled and applied' : 'Host disabled and applied'),
        onError: (err) => feedback.failed(err, { title: `Could not ${enabled ? 'enable' : 'disable'} the host` }),
      },
    );

  const remove = async (h: SiteHost) => {
    const ok = await confirm({
      title: `Delete ${meta.singular}?`,
      message: (
        <>
          <span className="mono text-fg">{h.domains.join(', ')}</span> will be removed from the configuration and Caddy
          will stop serving {h.domains.length === 1 ? 'it' : 'them'}. This cannot be undone.
        </>
      ),
      confirmLabel: 'Delete',
      danger: true,
    });
    if (!ok) return;
    del.mutate(h.id, {
      onSuccess: (res) => feedback.applied(res.apply, 'Deleted and applied'),
      onError: (err) => feedback.failed(err, { title: 'Could not delete the host' }),
    });
  };

  const Icon = meta.icon;
  const total = hosts.data?.length ?? 0;

  return (
    <>
      <PageHeader
        title={meta.title}
        description={meta.description}
        actions={
          managed ? (
            <ReadOnlyOnNode />
          ) : (
            canOperate && (
              <Button variant="primary" icon={<Plus size={14} />} onClick={openNew}>
                Add {meta.singular}
              </Button>
            )
          )
        }
      />
      <Card>
        <div className="flex flex-wrap items-center gap-3 border-b border-border px-4 py-2.5">
          <SearchInput value={q} onChange={setQ} placeholder="Search domains, targets, notes…" />
          <span className="ml-auto text-sm text-fg-subtle">
            {q ? `${filtered.length} of ${total}` : total} {total === 1 ? 'host' : 'hosts'}
          </span>
        </div>
        {hosts.isPending ? (
          <TableSkeleton rows={4} cols={5} />
        ) : hosts.isError ? (
          <div className="p-4">
            <Callout tone="danger" title="Could not load hosts">
              {errorMessage(hosts.error)}
            </Callout>
          </div>
        ) : total === 0 ? (
          <EmptyState
            icon={<Icon size={18} />}
            title={`No ${meta.title.toLowerCase()} yet`}
            description={meta.emptyDescription}
            action={
              canOperate && (
                <Button variant="primary" icon={<Plus size={14} />} onClick={openNew}>
                  Add {meta.singular}
                </Button>
              )
            }
          />
        ) : filtered.length === 0 ? (
          <EmptyState title="No matches" description={`Nothing matches “${q}”.`} />
        ) : (
          <Table>
            <THead>
              <tr>
                <TH className="w-36">Status</TH>
                <TH>Domains</TH>
                <TH>{targetHeading(kind)}</TH>
                <TH>TLS</TH>
                <TH>Access</TH>
                <TH className="w-12">
                  <span className="sr-only">Actions</span>
                </TH>
              </tr>
            </THead>
            <TBody>
              {filtered.map((h) => (
                <TR key={h.id} interactive onClick={() => openEdit(h)}>
                  <TD onClick={(e) => e.stopPropagation()}>
                    <div className="flex items-center gap-2">
                      <Switch
                        size="sm"
                        checked={h.enabled}
                        disabled={!canOperate || (toggle.isPending && toggle.variables?.id === h.id)}
                        onChange={(v) => setEnabled(h, v)}
                        aria-label={`${h.enabled ? 'Disable' : 'Enable'} ${h.domains[0] ?? 'host'}`}
                      />
                      <span className={h.enabled ? 'text-sm text-fg' : 'text-sm text-fg-subtle'}>
                        {h.enabled ? 'Enabled' : 'Disabled'}
                      </span>
                    </div>
                  </TD>
                  <TD>
                    <DomainChips domains={h.domains} />
                  </TD>
                  <TD className="max-w-[340px]">
                    <Target host={h} />
                  </TD>
                  <TD>
                    <TlsBadge host={h} certName={certName(h.certificateId)} />
                  </TD>
                  <TD>
                    {h.accessListId ? (
                      <Badge tone="info" className="max-w-[180px]" title={listName(h.accessListId)}>
                        {listName(h.accessListId) ?? 'Unknown list'}
                      </Badge>
                    ) : (
                      <span className="text-sm text-fg-subtle">Public</span>
                    )}
                  </TD>
                  <TD onClick={(e) => e.stopPropagation()} className="text-right">
                    <DropdownMenu
                      items={[
                        {
                          label: canOperate ? 'Edit' : 'View',
                          icon: canOperate ? <Pencil size={14} /> : <Eye size={14} />,
                          onSelect: () => openEdit(h),
                        },
                        {
                          label: h.enabled ? 'Disable' : 'Enable',
                          icon: h.enabled ? <PowerOff size={14} /> : <Power size={14} />,
                          onSelect: () => setEnabled(h, !h.enabled),
                          hidden: !canOperate,
                        },
                        { label: 'Duplicate', icon: <Copy size={14} />, onSelect: () => duplicate(h), hidden: !canOperate },
                        ...(canOperate ? (['separator'] as const) : []),
                        {
                          label: 'Delete',
                          icon: <Trash2 size={14} />,
                          danger: true,
                          onSelect: () => void remove(h),
                          hidden: !canOperate,
                        },
                      ]}
                      trigger={(p) => (
                        <Button
                          {...p}
                          variant="ghost"
                          size="sm"
                          iconOnly
                          aria-label={`Actions for ${h.domains[0] ?? 'host'}`}
                          icon={<MoreHorizontal size={16} />}
                        />
                      )}
                    />
                  </TD>
                </TR>
              ))}
            </TBody>
          </Table>
        )}
      </Card>
      <HostEditor
        open={!!editor}
        onClose={() => setEditor(null)}
        kind={kind}
        host={editor?.host}
        initial={editor?.initial ?? newHost(kind)}
        readOnly={editor?.readOnly}
      />
    </>
  );
}

function targetHeading(kind: HostKind) {
  switch (kind) {
    case 'proxy':
      return 'Upstream';
    case 'redirect':
      return 'Redirects to';
    case 'static':
      return 'Folder';
    case 'response':
      return 'Response';
  }
}

export function DomainChips({ domains, max = 2 }: { domains: string[]; max?: number }) {
  const shown = domains.slice(0, max);
  const rest = domains.length - shown.length;
  return (
    <div className="flex flex-wrap items-center gap-1">
      {shown.map((d) => (
        <Chip key={d}>{d}</Chip>
      ))}
      {rest > 0 && (
        <span className="text-xs text-fg-subtle" title={domains.slice(max).join('\n')}>
          +{rest} more
        </span>
      )}
    </div>
  );
}

function Target({ host }: { host: SiteHost }) {
  switch (host.kind) {
    case 'proxy': {
      const first = host.upstreams[0];
      if (!first) return <span className="text-sm text-fg-subtle">No upstream</span>;
      const extra = host.upstreams.length - 1;
      return (
        <div className="flex min-w-0 items-center gap-1.5">
          <span className="mono truncate text-sm text-fg">{upstreamUrl(first)}</span>
          {extra > 0 && (
            <span className="shrink-0 text-xs text-fg-subtle" title={host.upstreams.slice(1).map(upstreamUrl).join('\n')}>
              +{extra}
            </span>
          )}
          {host.locations.length > 0 && (
            <Badge className="shrink-0">
              {host.locations.length} location{host.locations.length === 1 ? '' : 's'}
            </Badge>
          )}
        </div>
      );
    }
    case 'redirect':
      return (
        <div className="flex min-w-0 items-center gap-1.5">
          <Badge mono className="shrink-0">
            {host.redirectCode}
          </Badge>
          <span className="mono truncate text-sm text-fg">{host.redirectTarget}</span>
        </div>
      );
    case 'static':
      return (
        <div className="flex min-w-0 items-center gap-1.5">
          <span className="mono truncate text-sm text-fg">{host.rootPath}</span>
          {host.spaFallback && <Badge className="shrink-0">SPA</Badge>}
          {host.browse && <Badge className="shrink-0">Browse</Badge>}
        </div>
      );
    case 'response':
      return (
        <div className="flex min-w-0 items-center gap-1.5">
          <Badge mono className="shrink-0" tone={host.responseStatus >= 400 ? 'warning' : 'neutral'}>
            {host.responseStatus}
          </Badge>
          <span className="mono truncate text-sm text-fg-muted">{host.responseContentType}</span>
        </div>
      );
  }
}

export function TlsBadge({ host, certName }: { host: Pick<SiteHost, 'tls' | 'certificateId'> & { acmeChallenge?: SiteHost['acmeChallenge'] }; certName?: string }) {
  switch (host.tls) {
    case 'acme':
      return (
        <Badge
          tone="success"
          icon={<Globe size={11} />}
          title={host.acmeChallenge === 'dns' ? 'ACME with the DNS-01 challenge' : host.acmeChallenge === 'http' ? 'ACME with the HTTP-01 / TLS-ALPN-01 challenge' : undefined}
        >
          {host.acmeChallenge === 'dns' ? 'ACME · DNS' : 'ACME'}
        </Badge>
      );
    case 'internal':
      return (
        <Badge tone="info" icon={<ShieldCheck size={11} />}>
          Internal
        </Badge>
      );
    case 'custom':
      return (
        <Badge tone="accent" icon={<KeyRound size={11} />} title={certName} className="max-w-[200px]">
          Custom{certName ? `: ${certName}` : ''}
        </Badge>
      );
    case 'none':
      return (
        <Badge tone="warning" icon={<LockOpen size={11} />}>
          HTTP only
        </Badge>
      );
  }
}
