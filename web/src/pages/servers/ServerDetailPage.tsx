// One server: facts, live resource charts (useServerSamples), Caddy state/actions, sync state, traffic section.
// See SPEC.md "Round 3".
import { useState, type ReactNode } from 'react';
import { Link, useNavigate, useParams } from 'react-router';
import { ArrowLeft, ArrowUpCircle, ChartLine, HardDrive, MoreHorizontal, RefreshCw, RotateCw, Server } from 'lucide-react';
import { errorMessage } from '@/api/client';
import { useServer, useServerCaddyRestart, useServerCaddyUpdate, useServerSamples, useServers } from '@/api/hooks';
import type { ResourceSample, ServerSummary, TrafficRange } from '@/api/types';
import { useAuth } from '@/auth';
import {
  ChartCard,
  Meter,
  StatTile,
  TimeSeriesChart,
  formatByteRate,
  formatBytesShort,
  formatCount,
  formatPercent,
  formatRate,
  seriesTable,
  type ChartSeries,
} from '@/components/charts';
import {
  Badge,
  Button,
  Callout,
  Card,
  CardBody,
  CardHeader,
  Chip,
  CodeBlock,
  DescriptionList,
  DropdownMenu,
  EmptyState,
  LoadingBlock,
  PageHeader,
  StatusDot,
  useConfirm,
  useToast,
} from '@/components/ui';
import { useFeedback } from '@/components/feedback';
import { DnsName } from '@/pages/hosts/DelegationRecords';
import { formatBytes, formatDateTime, formatDuration, formatRelative } from '@/lib/format';
import { useNow } from '@/lib/useNow';
import { useServerActions } from './ServerDialogs';
import { TrafficSection } from './TrafficSection';
import {
  ServerJobDialog,
  StaleIcon,
  caddyRunStateInfo,
  formatSampleTime,
  localServer,
  sameVersion,
  serverStatusInfo,
  shortRevision,
} from './shared';

export default function ServerDetailPage() {
  const { id = 'local' } = useParams();
  const server = useServer(id);
  const navigate = useNavigate();

  if (server.isPending) return <LoadingBlock label="Loading server…" />;
  // A failed background refresh keeps the last data (TanStack Query keeps `data` on error): the page, its live charts and
  // an open job dialog stay; only a first load that fails (unknown id, no access) replaces the page.
  if (server.isError && !server.data)
    return (
      <>
        <BackLink />
        <EmptyState
          icon={<Server size={18} />}
          title="Server not found"
          description={errorMessage(server.error)}
          action={<Button onClick={() => navigate('/servers')}>Back to servers</Button>}
        />
      </>
    );
  return <ServerDetail key={id} s={server.data} refreshError={server.isError ? server.error : null} />;
}

function BackLink() {
  return (
    <Link to="/servers" className="mb-2 inline-flex items-center gap-1 text-sm text-fg-subtle hover:text-fg">
      <ArrowLeft size={14} aria-hidden /> Servers
    </Link>
  );
}

function ServerDetail({ s, refreshError }: { s: ServerSummary; refreshError: unknown }) {
  const navigate = useNavigate();
  const now = useNow(10_000);
  const { canOperate } = useAuth();
  const actions = useServerActions({ onRemoved: () => navigate('/servers') });
  const all = useServers();
  const local = localServer(all.data);
  const st = serverStatusInfo(s.status);
  const info = s.info;
  const [range, setRange] = useState<TrafficRange>('day');
  const [host, setHost] = useState('');
  const menu = actions.menuItems(s, { includeOpen: false });

  return (
    <>
      <BackLink />
      <PageHeader
        title={
          <span className="flex flex-wrap items-center gap-2">
            {s.name}
            {s.isLocal && <Badge tone="accent">This server</Badge>}
            <StatusDot tone={st.tone} label={st.label} pulse={st.pulse} className="text-sm font-normal" />
          </span>
        }
        description={info ? `${info.fqdn ?? info.hostname} · ${info.os}` : s.url ?? undefined}
        actions={
          <>
            <Link
              to={`/traffic${s.isLocal ? '' : `?server=${encodeURIComponent(s.id)}`}`}
              className="inline-flex h-8 items-center gap-1.5 rounded-md border border-border-strong bg-surface px-3 text-sm font-medium text-fg shadow-xs hover:bg-surface-2 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring"
            >
              <ChartLine size={14} aria-hidden /> Traffic
            </Link>
            {!s.isLocal && canOperate && (
              <Button icon={<RefreshCw size={14} />} loading={actions.syncing === s.id} disabled={s.status === 'pending'} onClick={() => actions.syncNow(s)}>
                Sync now
              </Button>
            )}
            {menu.some((m) => m !== 'separator' && !m.hidden) && (
              <DropdownMenu
                items={menu}
                trigger={(p) => <Button {...p} iconOnly aria-label={`More actions for ${s.name}`} icon={<MoreHorizontal size={16} />} />}
              />
            )}
          </>
        }
      />

      <div className="flex flex-col gap-4">
        {refreshError != null && (
          <Callout tone="warning" title="Could not refresh this server">
            {errorMessage(refreshError)} Showing the last loaded data; retrying automatically.
          </Callout>
        )}
        {s.keyRotationPending && (
          <Callout tone="warning" title="Key rotation pending">
            {s.name} could not be reached when its key was rotated, so it still trusts its previous key (and the previous join token). The
            switch is retried on every contact. If {s.name} will not be reachable soon, join it again with a new token (menu above), or
            remove it and run <span className="mono text-fg">cluster leave</span> on it.
          </Callout>
        )}
        {s.status === 'offline' && (
          <Callout tone="danger" title={`${s.name} is not responding`}>
            {s.lastError ?? 'The primary could not reach it.'} Last seen {s.lastSeenAt ? formatRelative(s.lastSeenAt, now) : 'never'}. It keeps serving
            its last applied configuration; the values below are from its last heartbeat.
          </Callout>
        )}
        {s.status === 'error' && s.lastError && (
          <Callout tone="warning" title="The server reported a problem">
            {s.lastError}
          </Callout>
        )}
        {s.status === 'pending' && (
          <Callout tone="info" title="Waiting for the node to join">
            Paste the join token in {s.name}’s Settings › Cluster, or run the join commands on it in an elevated PowerShell or Command
            Prompt (they stop the service, join and start it again). Lost the token? Create a new one from the menu above.
          </Callout>
        )}

        {info && <FactsCard s={s} local={local} now={now} />}

        {s.status !== 'pending' && <LiveSection s={s} />}

        <div className="grid grid-cols-1 gap-4 xl:grid-cols-2">
          {s.status !== 'pending' && <CaddyCard s={s} local={local} now={now} />}
          {!s.isLocal && <SyncCard s={s} now={now} onSync={() => actions.syncNow(s)} syncing={actions.syncing === s.id} />}
        </div>

        {s.status !== 'pending' && (
          <section aria-labelledby="traffic-heading" className="mt-2 flex flex-col gap-3">
            <h2 id="traffic-heading" className="text-base font-semibold text-fg">
              Traffic
            </h2>
            <TrafficSection serverId={s.id} range={range} host={host} onRangeChange={setRange} onHostChange={setHost} />
          </section>
        )}
      </div>
      {actions.dialogs}
    </>
  );
}

function FactsCard({ s, local, now }: { s: ServerSummary; local?: ServerSummary; now: number }) {
  const info = s.info!;
  const managerMismatch = !s.isLocal && !sameVersion(info.managerVersion, local?.info?.managerVersion);
  return (
    <Card>
      <CardBody>
        <DescriptionList
          columns={3}
          items={[
            { label: 'Host name', value: info.hostname, mono: true },
            { label: 'FQDN', value: info.fqdn ?? '—', mono: true, hidden: !info.fqdn },
            { label: 'Domain', value: info.domain ?? 'Workgroup' },
            { label: 'Operating system', value: `${info.os} (${info.architecture})` },
            { label: 'Processors', value: `${info.processorCount} logical` },
            { label: 'Memory', value: formatBytes(info.totalMemoryBytes) },
            {
              label: 'Manager',
              value: (
                <span className="inline-flex items-center gap-1">
                  <span className="mono">v{info.managerVersion.replace(/^v/, '')}</span>
                  {managerMismatch && (
                    <>
                      <StaleIcon title="Differs from this server" />
                      <span className="text-xs text-warning">this server: v{local?.info?.managerVersion.replace(/^v/, '')}</span>
                    </>
                  )}
                </span>
              ),
            },
            { label: 'System uptime', value: formatDuration(info.systemUptimeSeconds) },
            { label: 'Manager uptime', value: formatDuration(info.managerUptimeSeconds) },
            {
              label: 'IP addresses',
              value: info.ipAddresses.length ? (
                <span className="flex flex-wrap gap-1">
                  {info.ipAddresses.map((ip) => (
                    <Chip key={ip}>{ip}</Chip>
                  ))}
                </span>
              ) : (
                '—'
              ),
            },
            { label: 'Data folder', value: info.dataDir, mono: true },
            { label: 'Management URL', value: s.url ? <DnsName name={s.url} /> : '—', mono: true, hidden: s.isLocal },
            { label: 'Pinned certificate', value: <span className="break-all">{s.fingerprint}</span>, mono: true, hidden: !s.fingerprint },
            { label: 'Join token issued', value: formatDateTime(s.tokenIssuedAt), hidden: !s.tokenIssuedAt },
            { label: 'Added', value: formatDateTime(s.addedAt), hidden: !s.addedAt },
            { label: 'Collected', value: <span title={formatDateTime(info.collectedAt)}>{formatRelative(info.collectedAt, now)}</span> },
          ]}
        />
      </CardBody>
    </Card>
  );
}

// ---------------------------------------------------------------- live resource usage

function LiveSection({ s }: { s: ServerSummary }) {
  const { samples, latest: live, error } = useServerSamples(s.id);
  const [hover, setHover] = useState<number | null>(null);
  const latest: ResourceSample | undefined = live ?? s.latest ?? undefined;
  const x = samples.map((p) => Date.parse(p.at));
  const end = x.length ? x[x.length - 1] : 0;
  const xDomain: [number, number] | undefined = x.length ? [Math.min(x[0], end - 10 * 60_000), end] : undefined;
  const span = 'last 10 minutes';

  const cpu: ChartSeries[] = [
    { id: 'system', label: 'System', color: 'var(--viz-1)', values: samples.map((p) => p.cpuPercent) },
    { id: 'caddy', label: 'Caddy', color: 'var(--viz-2)', values: samples.map((p) => p.caddyCpuPercent ?? null) },
  ];
  const memory: ChartSeries = { id: 'used', label: 'Used', color: 'var(--viz-1)', values: samples.map((p) => p.memoryUsedBytes) };
  const net: ChartSeries[] = [
    { id: 'rx', label: 'Received', color: 'var(--viz-1)', values: samples.map((p) => p.networkRxBytesPerSec) },
    { id: 'tx', label: 'Sent', color: 'var(--viz-2)', values: samples.map((p) => p.networkTxBytesPerSec) },
  ];
  const rps: ChartSeries = { id: 'rps', label: 'Requests/s', color: 'var(--viz-1)', values: samples.map((p) => p.requestsPerSecond) };
  const conns: ChartSeries = { id: 'conns', label: 'Connections', color: 'var(--viz-1)', values: samples.map((p) => p.activeConnections ?? null) };
  const procMem: ChartSeries[] = [
    { id: 'caddy', label: 'Caddy', color: 'var(--viz-1)', values: samples.map((p) => p.caddyMemoryBytes ?? null) },
    { id: 'manager', label: 'Manager', color: 'var(--viz-2)', values: samples.map((p) => p.managerMemoryBytes) },
  ];
  const memPct = latest && latest.memoryTotalBytes ? (latest.memoryUsedBytes / latest.memoryTotalBytes) * 100 : 0;
  const common = { x, xDomain, formatX: formatSampleTime, hoverIndex: hover, onHoverIndex: setHover, height: 160 };
  const pct = (v: number) => formatPercent(v);

  return (
    <section aria-labelledby="live-heading" className="flex flex-col gap-3">
      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <h2 id="live-heading" className="text-base font-semibold text-fg">
          Resource usage
        </h2>
        <p className="text-xs text-fg-subtle">
          {error ? (
            <span className="text-warning">Live data unavailable: {errorMessage(error)}</span>
          ) : latest ? (
            `Every 2 s · ${span} · updated ${formatSampleTime(Date.parse(latest.at))}`
          ) : (
            'Waiting for the first sample…'
          )}
        </p>
      </div>

      {latest && (
        <div className="grid grid-cols-2 gap-3 md:grid-cols-3 xl:grid-cols-6">
          <StatTile label="CPU" value={formatPercent(latest.cpuPercent)} detail={latest.caddyCpuPercent != null ? `Caddy ${formatPercent(latest.caddyCpuPercent)}` : 'Caddy not running'} />
          <StatTile label="Memory" value={formatPercent(memPct, 0)} detail={`${formatBytesShort(latest.memoryUsedBytes)} of ${formatBytesShort(latest.memoryTotalBytes)}`} />
          <StatTile label="Network in" value={formatByteRate(latest.networkRxBytesPerSec)} detail={`out ${formatByteRate(latest.networkTxBytesPerSec)}`} />
          <StatTile label="Requests/s" value={formatRate(latest.requestsPerSecond)} detail="handled by Caddy" />
          <StatTile label="Connections" value={latest.activeConnections != null ? formatCount(latest.activeConnections) : '—'} detail="established on HTTP/HTTPS ports" />
          <StatTile
            label="Caddy memory"
            value={latest.caddyMemoryBytes != null ? formatBytesShort(latest.caddyMemoryBytes) : '—'}
            detail={`Manager ${formatBytesShort(latest.managerMemoryBytes)}`}
          />
        </div>
      )}

      {samples.length > 0 && (
        <div className="grid grid-cols-1 gap-4 lg:grid-cols-2 2xl:grid-cols-3">
          <ChartCard
            title="CPU"
            description={`% of all processors · ${span}`}
            legend={cpu.map((c, i) => ({ label: c.label, color: c.color, value: i === 0 ? formatPercent(latest?.cpuPercent ?? 0) : latest?.caddyCpuPercent != null ? formatPercent(latest.caddyCpuPercent) : '—' }))}
            table={seriesTable(x, cpu, formatSampleTime, pct)}
          >
            <TimeSeriesChart {...common} series={cpu} yMax={100} formatValue={pct} formatTick={(v) => `${v}%`} ariaLabel={`CPU usage of ${s.name}, system and Caddy, ${span}`} />
          </ChartCard>
          <ChartCard title="Memory" description={`used of ${latest ? formatBytesShort(latest.memoryTotalBytes) : 'total'} · ${span}`} table={seriesTable(x, [memory], formatSampleTime, formatBytes)}>
            <TimeSeriesChart
              {...common}
              series={[memory]}
              yMax={latest?.memoryTotalBytes}
              bytes
              formatValue={formatBytesShort}
              ariaLabel={`Memory used on ${s.name}, ${span}`}
            />
          </ChartCard>
          <ChartCard
            title="Network"
            description={`all interfaces · ${span}`}
            legend={net.map((n, i) => ({ label: n.label, color: n.color, value: latest ? formatByteRate(i === 0 ? latest.networkRxBytesPerSec : latest.networkTxBytesPerSec) : undefined }))}
            table={seriesTable(x, net, formatSampleTime, formatByteRate)}
          >
            <TimeSeriesChart {...common} series={net} bytes formatValue={formatByteRate} formatTick={formatBytesShort} ariaLabel={`Network traffic received and sent by ${s.name}, ${span}`} />
          </ChartCard>
          <ChartCard title="Requests per second" description={`handled by Caddy · ${span}`} table={seriesTable(x, [rps], formatSampleTime, formatRate)}>
            <TimeSeriesChart {...common} series={[rps]} formatValue={formatRate} ariaLabel={`Requests per second on ${s.name}, ${span}`} />
          </ChartCard>
          <ChartCard title="Active connections" description={`established on the HTTP/HTTPS ports · ${span}`} table={seriesTable(x, [conns], formatSampleTime, formatCount)}>
            <TimeSeriesChart {...common} series={[conns]} integer formatValue={formatCount} ariaLabel={`Active connections on ${s.name}, ${span}`} emptyText="Not available while Caddy is stopped" />
          </ChartCard>
          <ChartCard
            title="Process memory"
            description={`working set · ${span}`}
            legend={procMem.map((m, i) => ({
              label: m.label,
              color: m.color,
              value: latest ? (i === 0 ? (latest.caddyMemoryBytes != null ? formatBytesShort(latest.caddyMemoryBytes) : '—') : formatBytesShort(latest.managerMemoryBytes)) : undefined,
            }))}
            table={seriesTable(x, procMem, formatSampleTime, formatBytes)}
          >
            <TimeSeriesChart {...common} series={procMem} bytes formatValue={formatBytesShort} ariaLabel={`Memory of the Caddy and manager processes on ${s.name}, ${span}`} />
          </ChartCard>
        </div>
      )}

      {latest && latest.disks.length > 0 && (
        <Card>
          <CardHeader title="Disks" icon={<HardDrive size={15} />} />
          <ul className="grid grid-cols-1 gap-x-8 gap-y-4 px-4 py-4 md:grid-cols-2 xl:grid-cols-3">
            {latest.disks.map((d) => {
              const used = d.totalBytes - d.freeBytes;
              const p = d.totalBytes ? (used / d.totalBytes) * 100 : 0;
              return (
                <li key={d.name} className="min-w-0">
                  <div className="mb-1.5 flex items-baseline justify-between gap-2 text-sm">
                    <span className="min-w-0 truncate">
                      <span className="mono font-medium text-fg">{d.name}</span> <span className="text-fg-subtle">{d.label}</span>
                    </span>
                    <span className="shrink-0 text-fg tabular-nums">{formatPercent(p, 0)} used</span>
                  </div>
                  <Meter value={p} label={`Disk ${d.name} ${d.label}`} valueText={`${formatPercent(p, 0)} used, ${formatBytes(d.freeBytes)} free of ${formatBytes(d.totalBytes)}`} warnAt={85} dangerAt={95} />
                  <p className="mt-1 text-xs text-fg-subtle">
                    {formatBytes(d.freeBytes)} free of {formatBytes(d.totalBytes)}
                  </p>
                </li>
              );
            })}
          </ul>
        </Card>
      )}
    </section>
  );
}

// ---------------------------------------------------------------- Caddy

function CaddyCard({ s, local, now }: { s: ServerSummary; local?: ServerSummary; now: number }) {
  const { canOperate, isAdmin } = useAuth();
  const restart = useServerCaddyRestart();
  const update = useServerCaddyUpdate();
  const confirm = useConfirm();
  const toast = useToast();
  const feedback = useFeedback();
  const [jobId, setJobId] = useState<string | null>(null);
  const info = s.info;
  const state = caddyRunStateInfo(info?.caddyState);
  const mismatch = !s.isLocal && !sameVersion(info?.caddyVersion, local?.info?.caddyVersion);
  const reachable = s.status === 'online';

  const doRestart = async () => {
    const ok = await confirm({
      title: `Restart Caddy on ${s.name}?`,
      message: 'Open connections are closed while Caddy restarts (usually under a second). Sites resume with the same configuration.',
      confirmLabel: 'Restart Caddy',
    });
    if (!ok) return;
    restart.mutate(s.id, {
      onSuccess: (r) => (r.state === 'running' ? toast.success(`Caddy restarted on ${s.name}`) : toast.warning(`Caddy on ${s.name} is ${r.state}`, r.lastError)),
      onError: (err) => feedback.failed(err, { title: `Could not restart Caddy on ${s.name}` }),
    });
  };

  const doUpdate = async () => {
    const ok = await confirm({
      title: `Update Caddy on ${s.name}?`,
      message: (
        <>
          Downloads the latest Caddy release{info?.caddyPlugins.length ? ' with this server’s plugins' : ''}, validates the configuration with it
          and restarts Caddy. The previous binary is kept for rollback.
          {s.isLocal ? '' : ' The job runs on the node; you can close the progress window.'}
        </>
      ),
      confirmLabel: 'Update Caddy',
    });
    if (!ok) return;
    update.mutate(
      { id: s.id },
      {
        onSuccess: (job) => setJobId(job.id),
        onError: (err) => feedback.failed(err, { title: `Could not start the update on ${s.name}` }),
      },
    );
  };

  return (
    <Card>
      <CardHeader
        title="Caddy"
        icon={<Server size={15} />}
        actions={
          <>
            {canOperate && (
              <Button size="sm" icon={<RotateCw size={13} />} loading={restart.isPending} disabled={!reachable} onClick={() => void doRestart()}>
                Restart
              </Button>
            )}
            {isAdmin && (
              <Button size="sm" icon={<ArrowUpCircle size={13} />} loading={update.isPending} disabled={!reachable} onClick={() => void doUpdate()}>
                Update Caddy
              </Button>
            )}
          </>
        }
      />
      <CardBody>
        <DescriptionList
          items={[
            { label: 'State', value: <StatusDot tone={reachable ? state.tone : 'neutral'} label={reachable ? state.label : `${state.label} (last known)`} /> },
            {
              label: 'Version',
              value: info?.caddyVersion ? (
                <span className="inline-flex flex-wrap items-center gap-1.5">
                  <span className="mono">{info.caddyVersion}</span>
                  {mismatch && (
                    <>
                      <StaleIcon title="Differs from this server" />
                      <span className="text-xs text-warning">this server runs {local?.info?.caddyVersion ?? 'an unknown version'}</span>
                    </>
                  )}
                </span>
              ) : (
                'Not installed'
              ),
            },
            {
              label: 'Running for',
              value: info?.caddyStartedAt ? (
                <span title={formatDateTime(info.caddyStartedAt)}>{formatDuration((now - Date.parse(info.caddyStartedAt)) / 1000)}</span>
              ) : (
                '—'
              ),
              hidden: info?.caddyState !== 'running',
            },
            {
              label: 'Plugins',
              value: info?.caddyPlugins.length ? (
                <span className="flex flex-wrap gap-1">
                  {info.caddyPlugins.map((p) => (
                    <Chip key={p}>{p}</Chip>
                  ))}
                </span>
              ) : (
                <span className="text-fg-subtle">Standard build</span>
              ),
            },
          ]}
        />
        {s.isLocal && (
          <p className="mt-3 text-xs text-fg-subtle">
            Service control, rollback and plugins of this server are on{' '}
            <Link to="/caddy/service" className="text-accent-text hover:underline">
              Service & Updates
            </Link>
            .
          </p>
        )}
      </CardBody>
      <ServerJobDialog serverId={s.id} serverName={s.name} jobId={jobId} onClose={() => setJobId(null)} />
    </Card>
  );
}

// ---------------------------------------------------------------- sync (nodes)

function SyncCard({ s, now, onSync, syncing }: { s: ServerSummary; now: number; onSync: () => void; syncing: boolean }) {
  const { canOperate } = useAuth();
  const sync = s.sync;
  let state: ReactNode;
  if (!sync) state = <span className="text-fg-subtle">Unknown</span>;
  else if (s.status === 'pending') state = <Badge tone="neutral">Not joined yet</Badge>;
  else if (sync.inSync) state = <Badge tone="success">In sync</Badge>;
  else state = <Badge tone={sync.lastError ? 'danger' : 'warning'}>{sync.lastError ? 'Sync failed' : 'Out of date'}</Badge>;

  return (
    <Card>
      <CardHeader
        title="Configuration sync"
        description="Sites, certificates and Caddy settings pushed from this server."
        icon={<RefreshCw size={15} />}
        actions={
          canOperate && (
            <Button size="sm" icon={<RefreshCw size={13} />} loading={syncing} disabled={s.status === 'pending'} onClick={onSync}>
              Sync now
            </Button>
          )
        }
      />
      <CardBody className="flex flex-col gap-3">
        <DescriptionList
          items={[
            { label: 'State', value: state },
            {
              label: 'Desired revision',
              value: <span className="mono" title={sync?.desiredRevision ?? undefined}>{shortRevision(sync?.desiredRevision)}</span>,
            },
            {
              label: 'Applied revision',
              value: (
                <span className="inline-flex items-center gap-1.5">
                  <span className="mono" title={sync?.appliedRevision ?? undefined}>
                    {shortRevision(sync?.appliedRevision)}
                  </span>
                  {sync && !sync.inSync && sync.appliedRevision && <StaleIcon title="Older than the desired revision" />}
                </span>
              ),
            },
            {
              label: 'Last sync',
              value: sync?.lastSyncAt ? <span title={formatDateTime(sync.lastSyncAt)}>{formatRelative(sync.lastSyncAt, now)}</span> : 'Never',
            },
          ]}
        />
        {sync?.lastError && <CodeBlock code={sync.lastError} title="Last error" wrap maxHeight={160} />}
        {sync && sync.warnings.length > 0 && (
          <Callout tone="warning" title="Warnings from the node">
            <ul className="list-disc pl-4">
              {sync.warnings.map((w, i) => (
                <li key={i}>{w}</li>
              ))}
            </ul>
          </Callout>
        )}
      </CardBody>
    </Card>
  );
}
