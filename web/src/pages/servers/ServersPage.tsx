// Servers list (local + cluster nodes), add-server dialog with join token, row actions. See SPEC.md "Round 3".
import { useState } from 'react';
import { Link, useNavigate } from 'react-router';
import { ChartLine, Info, MoreHorizontal, Network, Plus, Server } from 'lucide-react';
import { errorMessage } from '@/api/client';
import { useCluster, useServers } from '@/api/hooks';
import type { ServerSummary } from '@/api/types';
import { useAuth } from '@/auth';
import { MiniMeter, formatPercent } from '@/components/charts';
import { Badge, Button, Callout, Card, DropdownMenu, EmptyState, PageHeader, StatusDot, Table, TableSkeleton, TBody, TD, TH, THead, TR } from '@/components/ui';
import { cn } from '@/lib/cn';
import { formatBytes, formatDateTime, formatRelative } from '@/lib/format';
import { useNow } from '@/lib/useNow';
import { AddServerDialog, useServerActions } from './ServerDialogs';
import { StaleIcon, caddyRunStateInfo, localServer, sameVersion, serverStatusInfo, shortRevision } from './shared';

export default function ServersPage() {
  const servers = useServers();
  const cluster = useCluster();
  const { isAdmin } = useAuth();
  const navigate = useNavigate();
  const now = useNow(15_000);
  const [adding, setAdding] = useState(false);
  const actions = useServerActions();

  const list = servers.data ?? [];
  const local = localServer(list);
  const role = cluster.data?.role;
  const isNode = role === 'node';
  const nodes = list.filter((s) => !s.isLocal);
  const online = list.filter((s) => s.status === 'online').length;

  return (
    <>
      <PageHeader
        title="Servers"
        description={
          isNode
            ? `This server is a node of a cluster managed by ${cluster.data?.primaryName ?? 'its primary'}.`
            : nodes.length
              ? `This server manages ${nodes.length === 1 ? 'one node' : `${nodes.length} nodes`}: sites, certificates and Caddy settings are pushed to them on every change. ${online} of ${list.length} online.`
              : 'Resource usage and versions of this server, and the nodes it manages.'
        }
        actions={
          <>
            <Link
              to="/traffic"
              className="inline-flex h-8 items-center gap-1.5 rounded-md border border-border-strong bg-surface px-3 text-sm font-medium text-fg shadow-xs hover:bg-surface-2 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring"
            >
              <ChartLine size={14} aria-hidden /> Traffic
            </Link>
            {isAdmin && !isNode && (
              <Button variant="primary" icon={<Plus size={14} />} onClick={() => setAdding(true)}>
                Add server
              </Button>
            )}
          </>
        }
      />

      {isNode && (
        <Callout tone="info" className="mb-4" title={`Managed by ${cluster.data?.primaryName ?? 'the primary'}`}>
          Servers are added and removed on the primary. This node receives its sites, certificates and Caddy settings from there.
          {cluster.data?.lastPrimaryContactAt && ` Last contact ${formatRelative(cluster.data.lastPrimaryContactAt, now)}.`}
        </Callout>
      )}

      <Card>
        {servers.isPending ? (
          <TableSkeleton rows={3} cols={7} />
        ) : servers.isError ? (
          <div className="p-4">
            <Callout tone="danger" title="Could not load servers">
              {errorMessage(servers.error)}
            </Callout>
          </div>
        ) : list.length === 0 ? (
          <EmptyState icon={<Server size={18} />} title="No servers" />
        ) : (
          <Table>
            <THead>
              <tr>
                <TH>Name</TH>
                <TH>Status</TH>
                <TH>Host</TH>
                <TH>Manager</TH>
                <TH>Caddy</TH>
                <TH>CPU</TH>
                <TH>Memory</TH>
                <TH>Sync</TH>
                <TH>Last seen</TH>
                <TH className="w-12">
                  <span className="sr-only">Actions</span>
                </TH>
              </tr>
            </THead>
            <TBody>
              {list.map((s) => (
                <ServerRow
                  key={s.id}
                  s={s}
                  local={local}
                  primary={nodes.length > 0}
                  now={now}
                  onOpen={() => navigate(`/servers/${encodeURIComponent(s.id)}`)}
                  menu={actions.menuItems(s)}
                />
              ))}
            </TBody>
          </Table>
        )}
      </Card>

      {servers.isSuccess && !isNode && nodes.length === 0 && <StandaloneIntro canAdd={isAdmin} onAdd={() => setAdding(true)} />}

      {adding && <AddServerDialog onClose={() => setAdding(false)} />}
      {actions.dialogs}
    </>
  );
}

function ServerRow({
  s,
  local,
  primary,
  now,
  onOpen,
  menu,
}: {
  s: ServerSummary;
  local?: ServerSummary;
  primary: boolean;
  now: number;
  onOpen: () => void;
  menu: ReturnType<ReturnType<typeof useServerActions>['menuItems']>;
}) {
  const st = serverStatusInfo(s.status);
  const info = s.info;
  const latest = s.latest;
  const stale = s.status !== 'online';
  const caddyMismatch = !s.isLocal && !sameVersion(info?.caddyVersion, local?.info?.caddyVersion);
  const managerMismatch = !s.isLocal && !sameVersion(info?.managerVersion, local?.info?.managerVersion);
  const memPct = latest && latest.memoryTotalBytes ? (latest.memoryUsedBytes / latest.memoryTotalBytes) * 100 : null;
  const caddyState = caddyRunStateInfo(info?.caddyState);

  return (
    <TR interactive onClick={onOpen}>
      <TD className="min-w-44">
        <div className="flex flex-wrap items-center gap-1.5">
          <Link
            to={`/servers/${encodeURIComponent(s.id)}`}
            onClick={(e) => e.stopPropagation()}
            className="font-medium text-fg hover:underline focus-visible:outline-2 focus-visible:outline-ring"
          >
            {s.name}
          </Link>
          {s.isLocal && <Badge tone="accent">This server</Badge>}
        </div>
        {s.url && (
          <p className="mono truncate text-xs text-fg-subtle" title={s.url}>
            {s.url}
          </p>
        )}
      </TD>
      <TD>
        <StatusDot tone={st.tone} label={st.label} pulse={st.pulse} className="whitespace-nowrap" />
        {s.lastError && s.status !== 'online' && (
          <p className="max-w-56 truncate text-xs text-fg-subtle" title={s.lastError}>
            {s.lastError}
          </p>
        )}
      </TD>
      <TD className="max-w-56">
        {info ? (
          <>
            <p className="mono truncate text-sm" title={info.fqdn ?? info.hostname}>
              {info.hostname}
            </p>
            <p className="truncate text-xs text-fg-subtle" title={info.os}>
              {info.os.replace(/^Microsoft /, '')}
            </p>
          </>
        ) : (
          <span className="text-fg-subtle">—</span>
        )}
      </TD>
      <TD className="whitespace-nowrap">
        {info ? (
          <span className="inline-flex items-center gap-1">
            <span className="mono">v{info.managerVersion.replace(/^v/, '')}</span>
            {managerMismatch && <StaleIcon title={`Differs from this server (v${local?.info?.managerVersion.replace(/^v/, '')})`} />}
          </span>
        ) : (
          <span className="text-fg-subtle">—</span>
        )}
      </TD>
      <TD className="whitespace-nowrap">
        {info?.caddyVersion ? (
          <>
            <span className="inline-flex items-center gap-1">
              <span className="mono">{info.caddyVersion}</span>
              {caddyMismatch && <StaleIcon title={`Differs from this server (${local?.info?.caddyVersion ?? 'unknown'})`} />}
            </span>
            <p className={cn('text-xs', caddyMismatch ? 'text-warning' : caddyState.tone === 'danger' ? 'text-danger' : 'text-fg-subtle')}>
              {caddyMismatch ? 'Differs from this server' : caddyState.label}
            </p>
          </>
        ) : (
          <span className="text-fg-subtle">{info ? caddyState.label : '—'}</span>
        )}
      </TD>
      <TD className={stale ? 'opacity-60' : undefined}>
        {latest ? (
          <MiniMeter percent={latest.cpuPercent} label={`CPU of ${s.name}`} text={formatPercent(latest.cpuPercent, 0)} />
        ) : (
          <span className="text-fg-subtle">—</span>
        )}
      </TD>
      <TD className={stale ? 'opacity-60' : undefined}>
        {latest && memPct !== null ? (
          <div title={`${formatBytes(latest.memoryUsedBytes)} of ${formatBytes(latest.memoryTotalBytes)}`}>
            <MiniMeter percent={memPct} label={`Memory of ${s.name}`} text={formatPercent(memPct, 0)} />
          </div>
        ) : (
          <span className="text-fg-subtle">—</span>
        )}
      </TD>
      <TD className="whitespace-nowrap">
        {s.isLocal ? (
          <span className="text-xs text-fg-subtle" title={primary ? 'Nodes receive their configuration from this server' : undefined}>
            {primary ? 'Primary' : '—'}
          </span>
        ) : !s.sync ? (
          <span className="text-fg-subtle">—</span>
        ) : s.sync.inSync ? (
          <Badge tone="success" title={`Revision ${shortRevision(s.sync.appliedRevision)}`}>
            In sync
          </Badge>
        ) : (
          <Badge tone={s.sync.lastError ? 'danger' : 'warning'} title={s.sync.lastError ?? `Applied ${shortRevision(s.sync.appliedRevision)}, expected ${shortRevision(s.sync.desiredRevision)}`}>
            {s.status === 'pending' ? 'Not joined' : s.sync.lastError ? 'Sync failed' : 'Out of date'}
          </Badge>
        )}
      </TD>
      <TD className="whitespace-nowrap text-fg-muted" title={formatDateTime(s.lastSeenAt)}>
        {s.isLocal ? 'Now' : s.lastSeenAt ? formatRelative(s.lastSeenAt, now) : 'Never'}
      </TD>
      <TD onClick={(e) => e.stopPropagation()} className="text-right">
        <DropdownMenu
          items={menu}
          trigger={(p) => <Button {...p} variant="ghost" size="sm" iconOnly aria-label={`Actions for ${s.name}`} icon={<MoreHorizontal size={16} />} />}
        />
      </TD>
    </TR>
  );
}

function StandaloneIntro({ canAdd, onAdd }: { canAdd: boolean; onAdd: () => void }) {
  return (
    <Card className="mt-4">
      <div className="flex flex-col gap-4 p-5 md:flex-row md:items-start">
        <span className="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg border border-border bg-surface-2 text-fg-subtle">
          <Network size={18} aria-hidden />
        </span>
        <div className="min-w-0 flex-1 text-sm">
          <h2 className="font-semibold text-fg">Run several servers as one cluster</h2>
          <p className="mt-1 max-w-3xl text-fg-muted">
            Add another Windows server that runs Caddy Proxy Manager as a <em>node</em>. This server becomes the <em>primary</em>: you
            keep editing sites here and every change is pushed to the nodes, which keep serving with their last configuration when the
            primary is unreachable. Put a load balancer or DNS round-robin in front of the servers.
          </p>
          <ul className="mt-2 max-w-3xl list-disc space-y-1 pl-5 text-fg-muted">
            <li>
              Certificates are shared only when every server uses the same <span className="font-medium text-fg">shared storage</span> (a
              file share or Redis) — then one server obtains and renews each certificate for all of them.
            </li>
            <li>The node’s existing sites and settings are replaced when it joins.</li>
          </ul>
          <div className="mt-3 flex flex-wrap items-center gap-3">
            {canAdd && (
              <Button variant="primary" icon={<Plus size={14} />} onClick={onAdd}>
                Add server
              </Button>
            )}
            <Link to="/settings?tab=cluster" className="inline-flex items-center gap-1 text-sm font-medium text-accent-text hover:underline">
              <Info size={14} aria-hidden /> Shared storage and joining: Settings › Cluster
            </Link>
          </div>
        </div>
      </div>
    </Card>
  );
}
