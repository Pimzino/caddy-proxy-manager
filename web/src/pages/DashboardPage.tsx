import type { ReactNode } from 'react';
import { Link, useNavigate } from 'react-router';
import {
  Activity,
  ArrowLeftRight,
  ArrowUpCircle,
  Braces,
  ChevronRight,
  ClipboardCheck,
  HeartPulse,
  Play,
  Plus,
  ScrollText,
  Server,
  ShieldCheck,
} from 'lucide-react';
import { errorMessage } from '@/api/client';
import { useCaddyAction, useDashboard, useRunReadiness } from '@/api/hooks';
import type { Dashboard } from '@/api/types';
import { useAuth } from '@/auth';
import { useFeedback } from '@/components/feedback';
import { caddyStateInfo } from '@/components/layout/CaddyStatusPill';
import { Button, Callout, Card, EmptyState, PageHeader, Skeleton, StatusDot, useToast, type Tone } from '@/components/ui';
import { cn } from '@/lib/cn';
import { formatDateTime, formatDuration, formatNumber, formatRelative } from '@/lib/format';
import { useNow } from '@/lib/useNow';
import { SeverityBadge } from './EventsPage';

export default function DashboardPage() {
  const dash = useDashboard();
  const { canOperate } = useAuth();
  const navigate = useNavigate();
  const readiness = useRunReadiness();
  const feedback = useFeedback();
  const toast = useToast();

  return (
    <>
      <PageHeader
        title="Dashboard"
        description={dash.data ? `${dash.data.system.hostname} · ${dash.data.system.os}` : 'Overview of this Caddy server.'}
        actions={
          canOperate && (
            <>
              <Button
                icon={<ClipboardCheck size={14} />}
                loading={readiness.isPending}
                onClick={() =>
                  readiness.mutate(undefined, {
                    onSuccess: (r) =>
                      r.fail > 0 || r.warn > 0
                        ? toast.warning('Readiness checks finished', `${r.fail} failed, ${r.warn} warnings.`)
                        : toast.success('Readiness checks passed'),
                    onError: (err) => feedback.failed(err, { title: 'Readiness checks failed to run' }),
                  })
                }
              >
                Run readiness checks
              </Button>
              <Button variant="primary" icon={<Plus size={14} />} onClick={() => navigate('/hosts/proxy?new=1')}>
                Add proxy host
              </Button>
            </>
          )
        }
      />
      {dash.isPending ? (
        <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
          {Array.from({ length: 6 }, (_, i) => (
            <Card key={i} className="p-4">
              <Skeleton className="mb-3 h-4 w-32" />
              <Skeleton className="mb-2 h-7 w-24" />
              <Skeleton className="h-4 w-48" />
            </Card>
          ))}
        </div>
      ) : dash.isError ? (
        <Callout tone="danger" title="Could not load the dashboard">
          {errorMessage(dash.error)}
        </Callout>
      ) : (
        <DashboardBody d={dash.data} />
      )}
    </>
  );
}

function DashboardBody({ d }: { d: Dashboard }) {
  const now = useNow(30_000);
  const { canOperate } = useAuth();
  const start = useCaddyAction();
  const feedback = useFeedback();
  const toast = useToast();
  const info = caddyStateInfo(d.caddy);
  const hostsTotal = d.counts.proxy + d.counts.redirect + d.counts.static + d.counts.response;

  const readinessTone: Tone = !d.readiness ? 'neutral' : d.readiness.fail > 0 ? 'danger' : d.readiness.warn > 0 ? 'warning' : 'success';

  return (
    <div className="flex flex-col gap-4">
      {d.caddy.state === 'stopped' && d.caddy.binaryInstalled && (
        <Callout
          tone="danger"
          title="Caddy is stopped — no sites are being served"
          actions={
            canOperate && (
              <Button
                size="sm"
                variant="primary"
                icon={<Play size={13} />}
                loading={start.isPending}
                onClick={() =>
                  start.mutate('start', {
                    onSuccess: () => toast.success('Caddy started'),
                    onError: (err) => feedback.failed(err, { title: 'Could not start Caddy' }),
                  })
                }
              >
                Start Caddy
              </Button>
            )
          }
        >
          {d.caddy.lastError ?? 'Start it from here or from Service & Updates.'}
        </Callout>
      )}
      {!d.caddy.binaryInstalled && (
        <Callout
          tone="warning"
          title="Caddy is not installed yet"
          actions={
            <Link to="/caddy/service" className="text-sm font-medium text-accent-text hover:underline">
              Install Caddy
            </Link>
          }
        >
          Install the Caddy binary to start serving sites.
        </Callout>
      )}

      <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
        <StatCard icon={<Server size={15} />} title="Caddy service" to="/caddy/service" tone={info.tone}>
          <div className="flex items-center gap-2">
            <StatusDot tone={info.tone} label={info.label} pulse={info.pulse} className="text-lg font-semibold" />
          </div>
          <p className="mt-1 text-sm text-fg-subtle">
            {d.caddy.state === 'running' && d.caddy.startedAt
              ? `Up since ${formatRelative(d.caddy.startedAt, now).replace(' ago', '')}`
              : d.caddy.hostMode === 'windows-service'
                ? 'Windows service'
                : 'Development process'}
            {d.caddy.adminReachable ? ' · admin API reachable' : ''}
          </p>
        </StatCard>

        <StatCard icon={<ArrowUpCircle size={15} />} title="Caddy version" to="/caddy/service" tone={d.binary.updateAvailable ? 'info' : d.binary.installed ? 'success' : 'neutral'}>
          <p className="mono text-lg font-semibold text-fg">{d.binary.installed?.version ?? 'Not installed'}</p>
          <p className="mt-1 text-sm text-fg-subtle">
            {d.binary.updateAvailable && d.binary.latest ? (
              <span className="font-medium text-accent-text">Update available: {d.binary.latest.version}</span>
            ) : d.binary.latest ? (
              'Up to date'
            ) : (
              'Latest version unknown'
            )}
            {d.binary.lastCheckedAt && ` · checked ${formatRelative(d.binary.lastCheckedAt, now)}`}
          </p>
        </StatCard>

        <StatCard icon={<ArrowLeftRight size={15} />} title="Sites" to="/hosts/proxy" tone={d.counts.hostsDisabled > 0 ? 'neutral' : 'accent'}>
          <p className="text-lg font-semibold text-fg tabular-nums">
            {formatNumber(hostsTotal)} <span className="text-sm font-normal text-fg-subtle">hosts</span>
            {d.counts.streams > 0 && (
              <>
                {' '}
                · {formatNumber(d.counts.streams)} <span className="text-sm font-normal text-fg-subtle">streams</span>
              </>
            )}
          </p>
          <p className="mt-1 text-sm text-fg-subtle">
            {d.counts.proxy} proxy · {d.counts.redirect} redirect · {d.counts.static} static · {d.counts.response} response
            {d.counts.hostsDisabled > 0 && ` · ${d.counts.hostsDisabled} disabled`}
          </p>
        </StatCard>

        <StatCard
          icon={<ShieldCheck size={15} />}
          title="Certificates"
          to="/certificates"
          tone={d.counts.certificatesExpiring > 0 ? 'warning' : 'success'}
        >
          <p className="text-lg font-semibold text-fg tabular-nums">{formatNumber(d.counts.certificates)}</p>
          <p className="mt-1 text-sm">
            {d.counts.certificatesExpiring > 0 ? (
              <span className="font-medium text-warning">{d.counts.certificatesExpiring} expiring soon</span>
            ) : (
              <span className="text-fg-subtle">None expiring soon</span>
            )}
            <span className="text-fg-subtle"> · {d.counts.accessLists} access lists</span>
          </p>
        </StatCard>

        <StatCard icon={<ClipboardCheck size={15} />} title="Server readiness" to="/readiness" tone={readinessTone}>
          {d.readiness ? (
            <>
              <div className="flex items-baseline gap-4 text-lg font-semibold tabular-nums">
                <span className="text-success">{d.readiness.pass} <span className="text-xs font-normal text-fg-subtle">pass</span></span>
                <span className={d.readiness.warn ? 'text-warning' : 'text-fg-subtle'}>{d.readiness.warn} <span className="text-xs font-normal text-fg-subtle">warn</span></span>
                <span className={d.readiness.fail ? 'text-danger' : 'text-fg-subtle'}>{d.readiness.fail} <span className="text-xs font-normal text-fg-subtle">fail</span></span>
              </div>
              <p className="mt-1 text-sm text-fg-subtle">{d.readiness.ranAt ? `Checked ${formatRelative(d.readiness.ranAt, now)}` : 'Checked'}</p>
            </>
          ) : (
            <>
              <p className="text-lg font-semibold text-fg-muted">Not checked yet</p>
              <p className="mt-1 text-sm text-fg-subtle">Run the checks to verify firewall, ports and connectivity.</p>
            </>
          )}
        </StatCard>

        <StatCard icon={<HeartPulse size={15} />} title="Upstream health" to="/caddy/service" tone={d.upstreams.unhealthy > 0 ? 'danger' : d.upstreams.total > 0 ? 'success' : 'neutral'}>
          <p className="text-lg font-semibold text-fg tabular-nums">
            {d.upstreams.total - d.upstreams.unhealthy}/{d.upstreams.total} <span className="text-sm font-normal text-fg-subtle">healthy</span>
          </p>
          <p className="mt-1 text-sm">
            {d.upstreams.unhealthy > 0 ? (
              <span className="font-medium text-danger">{d.upstreams.unhealthy} unhealthy</span>
            ) : (
              <span className="text-fg-subtle">{d.upstreams.total ? 'All backends responding' : 'No upstreams reported yet'}</span>
            )}
          </p>
        </StatCard>
      </div>

      <div className="grid gap-4 xl:grid-cols-[minmax(0,2fr)_minmax(0,1fr)]">
        <Card>
          <div className="flex items-center justify-between border-b border-border px-4 py-3">
            <h2 className="flex items-center gap-2 text-sm font-semibold text-fg">
              <Activity size={15} className="text-fg-subtle" aria-hidden /> Recent events
            </h2>
            <Link to="/events" className="text-sm text-accent-text hover:underline">
              View all
            </Link>
          </div>
          {d.recentEvents.length === 0 ? (
            <EmptyState title="No events" description="Outages, rejected configurations and certificate warnings appear here." />
          ) : (
            <ul className="divide-y divide-border">
              {d.recentEvents.map((e) => (
                <li key={e.id} className="flex items-start gap-3 px-4 py-2.5">
                  <div className="w-24 shrink-0 pt-0.5">
                    <SeverityBadge severity={e.severity} />
                  </div>
                  <div className="min-w-0 flex-1">
                    <p className="truncate text-sm text-fg" title={e.message}>
                      {e.message}
                    </p>
                    <p className="text-xs text-fg-subtle">
                      <span className="mono">{e.category}</span> · <span title={formatDateTime(e.createdAt)}>{formatRelative(e.createdAt, now)}</span>
                    </p>
                  </div>
                </li>
              ))}
            </ul>
          )}
        </Card>
        <div className="flex flex-col gap-4">
          <Card>
            <div className="border-b border-border px-4 py-3">
              <h2 className="text-sm font-semibold text-fg">Quick links</h2>
            </div>
            <ul className="py-1">
              <QuickLink to="/caddy/config" icon={<Braces size={15} />} label="View running configuration" />
              <QuickLink to="/logs" icon={<ScrollText size={15} />} label="Caddy logs" />
              <QuickLink to="/certificates" icon={<ShieldCheck size={15} />} label="Certificates" />
              <QuickLink to="/readiness" icon={<ClipboardCheck size={15} />} label="Readiness report" />
            </ul>
          </Card>
          <Card>
            <div className="border-b border-border px-4 py-3">
              <h2 className="text-sm font-semibold text-fg">System</h2>
            </div>
            <dl className="grid grid-cols-[110px_minmax(0,1fr)] gap-x-3 gap-y-2 px-4 py-3 text-sm">
              <dt className="text-fg-subtle">Server</dt>
              <dd className="mono truncate">{d.system.hostname}</dd>
              <dt className="text-fg-subtle">OS</dt>
              <dd className="truncate" title={d.system.os}>{d.system.os}</dd>
              <dt className="text-fg-subtle">Manager</dt>
              <dd className="mono">v{d.system.managerVersion}</dd>
              <dt className="text-fg-subtle">Uptime</dt>
              <dd>{formatDuration(d.system.uptimeSeconds)}</dd>
              <dt className="text-fg-subtle">Data folder</dt>
              <dd className="mono truncate" title={d.system.dataDir}>{d.system.dataDir}</dd>
            </dl>
          </Card>
        </div>
      </div>
    </div>
  );
}

const toneBar: Record<Tone, string> = {
  success: 'bg-success',
  warning: 'bg-warning',
  danger: 'bg-danger',
  info: 'bg-info',
  neutral: 'bg-border-strong',
  accent: 'bg-accent',
};

function StatCard({ icon, title, to, tone, children }: { icon: ReactNode; title: string; to: string; tone: Tone; children: ReactNode }) {
  return (
    <Link
      to={to}
      className="group relative block overflow-hidden rounded-lg border border-border bg-surface p-4 pl-5 shadow-xs transition-colors hover:border-border-strong focus-visible:outline-2 focus-visible:outline-ring"
    >
      <span className={cn('absolute inset-y-0 left-0 w-1', toneBar[tone])} aria-hidden />
      <div className="mb-2 flex items-center justify-between gap-2 text-sm text-fg-muted">
        <span className="flex items-center gap-1.5 font-medium">
          <span className="text-fg-subtle">{icon}</span>
          {title}
        </span>
        <ChevronRight size={14} className="text-fg-subtle opacity-0 transition-opacity group-hover:opacity-100" aria-hidden />
      </div>
      {children}
    </Link>
  );
}

function QuickLink({ to, icon, label }: { to: string; icon: ReactNode; label: string }) {
  return (
    <li>
      <Link to={to} className="flex items-center gap-2.5 px-4 py-2 text-sm text-fg hover:bg-surface-2 focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-ring">
        <span className="text-fg-subtle">{icon}</span>
        <span className="flex-1">{label}</span>
        <ChevronRight size={14} className="text-fg-subtle" aria-hidden />
      </Link>
    </li>
  );
}
