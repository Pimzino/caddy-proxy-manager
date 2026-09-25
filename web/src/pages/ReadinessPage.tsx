import { useMemo, useState } from 'react';
import { AlertTriangle, CheckCircle2, ChevronRight, CircleMinus, ClipboardCheck, Info, Play, Wrench, XCircle } from 'lucide-react';
import { errorMessage } from '@/api/client';
import { useFixReadiness, useGpoScript, useReadiness, useRunReadiness } from '@/api/hooks';
import type { CheckStatus, MachineInfo, ReadinessCheck, ReadinessReport } from '@/api/types';
import { useAuth } from '@/auth';
import { useFeedback } from '@/components/feedback';
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
  EmptyState,
  LoadingBlock,
  PageHeader,
  Segmented,
  useConfirm,
  useToast,
  type Tone,
} from '@/components/ui';
import { cn } from '@/lib/cn';
import { formatDateTime, formatRelative } from '@/lib/format';
import { useNow } from '@/lib/useNow';

const CATEGORY_ORDER = ['System', 'Caddy', 'Firewall', 'Network', 'Domain', 'Ports', 'Connectivity', 'DNS'];

export const checkStatusMeta: Record<CheckStatus, { tone: Tone; label: string; icon: typeof CheckCircle2 }> = {
  pass: { tone: 'success', label: 'Pass', icon: CheckCircle2 },
  warn: { tone: 'warning', label: 'Warning', icon: AlertTriangle },
  fail: { tone: 'danger', label: 'Fail', icon: XCircle },
  info: { tone: 'info', label: 'Info', icon: Info },
  skipped: { tone: 'neutral', label: 'Skipped', icon: CircleMinus },
};

const toneText: Record<Tone, string> = {
  success: 'text-success',
  warning: 'text-warning',
  danger: 'text-danger',
  info: 'text-info',
  neutral: 'text-neutral',
  accent: 'text-accent-text',
};

export default function ReadinessPage() {
  const { canOperate } = useAuth();
  const report = useReadiness();
  const run = useRunReadiness();
  const feedback = useFeedback();
  const now = useNow(30_000);

  const runChecks = () =>
    run.mutate(undefined, {
      onError: (err) => feedback.failed(err, { title: 'Readiness checks failed to run' }),
    });

  return (
    <>
      <PageHeader
        title="Readiness"
        description="Verifies that this Windows server can serve sites with Caddy: firewall, network profile, ports, outbound ACME access, DNS and more."
        actions={
          <>
            {report.data && (
              <span className="text-sm text-fg-subtle" title={formatDateTime(report.data.ranAt)}>
                Last run {formatRelative(report.data.ranAt, now)}
              </span>
            )}
            {canOperate && (
              <Button variant="primary" icon={<Play size={14} />} loading={run.isPending} onClick={runChecks}>
                {run.isPending ? 'Running checks…' : 'Run checks'}
              </Button>
            )}
          </>
        }
      />
      {report.isPending ? (
        <LoadingBlock />
      ) : report.isError ? (
        <Callout tone="danger" title="Could not load the readiness report">
          {errorMessage(report.error)}
        </Callout>
      ) : !report.data ? (
        <Card>
          <EmptyState
            icon={<ClipboardCheck size={18} />}
            title="Checks have not run yet"
            description="Run the checks to see whether firewall rules, ports, network profile and outbound connectivity are ready. It takes up to a minute."
            action={
              canOperate && (
                <Button variant="primary" icon={<Play size={14} />} loading={run.isPending} onClick={runChecks}>
                  Run checks
                </Button>
              )
            }
          />
        </Card>
      ) : (
        <Report report={report.data} />
      )}
    </>
  );
}

function Report({ report }: { report: ReadinessReport }) {
  const [filter, setFilter] = useState<'all' | 'problems'>('all');
  const groups = useMemo(() => {
    const map = new Map<string, ReadinessCheck[]>();
    for (const c of report.checks) {
      if (filter === 'problems' && c.status !== 'fail' && c.status !== 'warn') continue;
      const list = map.get(c.category) ?? [];
      list.push(c);
      map.set(c.category, list);
    }
    return [...map.entries()].sort(
      ([a], [b]) => (CATEGORY_ORDER.indexOf(a) + 1 || 99) - (CATEGORY_ORDER.indexOf(b) + 1 || 99) || a.localeCompare(b),
    );
  }, [report.checks, filter]);
  const info = report.checks.filter((c) => c.status === 'info').length;

  return (
    <div className="flex flex-col gap-4">
      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <SummaryTile tone="success" label="Passed" value={report.pass} />
        <SummaryTile tone="warning" label="Warnings" value={report.warn} />
        <SummaryTile tone="danger" label="Failed" value={report.fail} />
        <SummaryTile tone="info" label="Information" value={info} />
      </div>
      {report.fail === 0 && report.warn === 0 && (
        <Callout tone="success" title="This server looks ready">
          No failed checks or warnings.
        </Callout>
      )}
      <div className="grid gap-4 xl:grid-cols-[minmax(0,1fr)_420px]">
        <div className="flex min-w-0 flex-col gap-4">
          <div className="flex items-center justify-between">
            <h2 className="text-sm font-semibold text-fg">Checks</h2>
            <Segmented
              aria-label="Filter checks"
              size="sm"
              value={filter}
              onChange={setFilter}
              options={[
                { value: 'all', label: `All (${report.checks.length})` },
                { value: 'problems', label: `Problems (${report.fail + report.warn})` },
              ]}
            />
          </div>
          {groups.length === 0 ? (
            <Card>
              <EmptyState title="No problems" description="Every check passed or is informational." />
            </Card>
          ) : (
            groups.map(([category, checks]) => <CheckGroup key={category} category={category} checks={checks} />)
          )}
        </div>
        <div className="flex min-w-0 flex-col gap-4">
          <MachineCard machine={report.machine} />
          {report.machine.domainJoined && <GpoPanel machine={report.machine} />}
        </div>
      </div>
    </div>
  );
}

function SummaryTile({ tone, label, value }: { tone: Tone; label: string; value: number }) {
  return (
    <div className="flex items-center gap-3 rounded-lg border border-border bg-surface px-4 py-3 shadow-xs">
      <span className={cn('h-8 w-1 rounded-full', { success: 'bg-success', warning: 'bg-warning', danger: 'bg-danger', info: 'bg-info', neutral: 'bg-neutral', accent: 'bg-accent' }[tone])} aria-hidden />
      <div>
        <p className="text-xl font-semibold tabular-nums text-fg">{value}</p>
        <p className="text-xs text-fg-subtle">{label}</p>
      </div>
    </div>
  );
}

function CheckGroup({ category, checks }: { category: string; checks: ReadinessCheck[] }) {
  const fails = checks.filter((c) => c.status === 'fail').length;
  const warns = checks.filter((c) => c.status === 'warn').length;
  return (
    <Card>
      <div className="flex items-center gap-2 border-b border-border px-4 py-2.5">
        <h3 className="text-sm font-semibold text-fg">{category}</h3>
        <span className="text-xs text-fg-subtle">{checks.length} checks</span>
        <div className="flex-1" />
        {fails > 0 && <Badge tone="danger">{fails} failed</Badge>}
        {warns > 0 && <Badge tone="warning">{warns} warning{warns === 1 ? '' : 's'}</Badge>}
      </div>
      <ul className="divide-y divide-border">
        {checks.map((c) => (
          <CheckRow key={c.id} check={c} />
        ))}
      </ul>
    </Card>
  );
}

function CheckRow({ check }: { check: ReadinessCheck }) {
  const { isAdmin } = useAuth();
  const expandable = !!(check.details || check.remediation || check.script || check.fixable);
  const [open, setOpen] = useState(check.status === 'fail');
  const fix = useFixReadiness();
  const toast = useToast();
  const feedback = useFeedback();
  const confirm = useConfirm();
  const meta = checkStatusMeta[check.status];
  const Icon = meta.icon;
  const panelId = `check-${check.id.replace(/[^a-z0-9_-]/gi, '-')}`;

  const applyFix = async () => {
    const ok = await confirm({
      title: `Apply fix: ${check.title}?`,
      message: check.remediation ?? 'The manager applies the remediation automatically on this server.',
      confirmLabel: 'Apply fix',
    });
    if (!ok) return;
    fix.mutate(check.id, {
      onSuccess: (r) => toast.success('Fix applied', r.message),
      onError: (err) => feedback.failed(err, { title: 'The fix could not be applied' }),
    });
  };

  return (
    <li>
      <div className="flex items-start gap-3 px-4 py-3">
        <span className={cn('mt-0.5 shrink-0', toneText[meta.tone])}>
          <Icon size={16} aria-hidden />
        </span>
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-x-2 gap-y-0.5">
            <span className="text-sm font-medium text-fg">{check.title}</span>
            <span className={cn('text-xs font-medium', toneText[meta.tone])}>{meta.label}</span>
          </div>
          <p className="mt-0.5 text-sm break-words text-fg-muted">{check.summary}</p>
        </div>
        <div className="flex shrink-0 items-center gap-1">
          {check.fixable && isAdmin && (check.status === 'fail' || check.status === 'warn') && (
            <Button size="sm" icon={<Wrench size={13} />} loading={fix.isPending} onClick={() => void applyFix()}>
              Fix
            </Button>
          )}
          {expandable && (
            <Button
              size="sm"
              variant="ghost"
              iconOnly
              aria-expanded={open}
              aria-controls={panelId}
              aria-label={open ? `Hide details for ${check.title}` : `Show details for ${check.title}`}
              icon={<ChevronRight size={15} className={cn('transition-transform', open && 'rotate-90')} />}
              onClick={() => setOpen(!open)}
            />
          )}
        </div>
      </div>
      {expandable && open && (
        <div id={panelId} className="flex flex-col gap-3 border-t border-dashed border-border bg-surface-2/40 px-4 py-3 pl-11">
          {check.details && (
            <div>
              <p className="mb-1 text-xs font-medium text-fg-subtle uppercase">Details</p>
              <pre className="mono text-xs leading-relaxed break-words whitespace-pre-wrap text-fg">{check.details}</pre>
            </div>
          )}
          {check.remediation && (
            <div>
              <p className="mb-1 text-xs font-medium text-fg-subtle uppercase">Remediation</p>
              <p className="text-sm whitespace-pre-line text-fg-muted">{check.remediation}</p>
            </div>
          )}
          {check.script && (
            <CodeBlock code={check.script} title="PowerShell (run as Administrator)" language="powershell" maxHeight={260} />
          )}
          <p className="mono text-[11px] text-fg-subtle">{check.id}</p>
        </div>
      )}
    </li>
  );
}

const profileTone = (category: string): Tone =>
  category === 'DomainAuthenticated' ? 'success' : category === 'Public' ? 'warning' : 'neutral';

function MachineCard({ machine }: { machine: MachineInfo }) {
  return (
    <Card>
      <CardHeader title="This server" />
      <CardBody className="flex flex-col gap-4">
        <DescriptionList
          items={[
            { label: 'Host name', value: machine.hostname, mono: true },
            { label: 'FQDN', value: machine.fqdn, mono: true, hidden: !machine.fqdn },
            { label: 'Operating system', value: machine.osDescription },
            {
              label: 'Domain',
              value: machine.domainJoined ? (
                <span className="mono">{machine.domain}</span>
              ) : (
                <span className="text-fg-subtle">Workgroup (not domain joined)</span>
              ),
            },
            { label: 'Computer DN', value: machine.computerDn, mono: true, hidden: !machine.computerDn },
            {
              label: 'IP addresses',
              value:
                machine.ipAddresses.length > 0 ? (
                  <span className="flex flex-wrap gap-1">
                    {machine.ipAddresses.map((ip) => (
                      <Chip key={ip}>{ip}</Chip>
                    ))}
                  </span>
                ) : (
                  '—'
                ),
            },
          ]}
        />
        {machine.networkProfiles.length > 0 && (
          <div>
            <p className="mb-2 text-xs font-medium text-fg-subtle uppercase">Network profiles</p>
            <ul className="flex flex-col gap-2">
              {machine.networkProfiles.map((p) => (
                <li key={p.interfaceAlias + p.name} className="flex items-center justify-between gap-2 text-sm">
                  <span className="min-w-0">
                    <span className="block truncate text-fg">{p.interfaceAlias}</span>
                    <span className="block truncate text-xs text-fg-subtle">{p.name}</span>
                  </span>
                  <Badge tone={profileTone(p.category)}>{p.category}</Badge>
                </li>
              ))}
            </ul>
          </div>
        )}
      </CardBody>
    </Card>
  );
}

function GpoPanel({ machine }: { machine: MachineInfo }) {
  const script = useGpoScript(true);
  return (
    <Card>
      <CardHeader
        title="Group Policy firewall script"
        description={`Domain ${machine.domain ?? ''}: deploy the inbound rules through a GPO so local rule merging cannot block them.`}
      />
      <CardBody className="flex flex-col gap-3">
        <p className="text-sm text-fg-muted">
          Run on a machine with the GroupPolicy RSAT module as a Domain Admin. It creates or updates the GPO “Caddy Proxy Manager - Firewall”,
          adds the rules and links it to this computer’s OU. Then run <span className="mono">gpupdate /force</span> here.
        </p>
        {script.isPending ? (
          <LoadingBlock />
        ) : script.isError ? (
          <Callout tone="danger">{errorMessage(script.error)}</Callout>
        ) : (
          <CodeBlock code={script.data} language="powershell" title="PowerShell" filename="Caddy-Firewall-GPO.ps1" maxHeight={320} />
        )}
      </CardBody>
    </Card>
  );
}
