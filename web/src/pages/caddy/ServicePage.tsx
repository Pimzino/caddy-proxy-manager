import { useState, type FormEvent } from 'react';
import { Link } from 'react-router';
import {
  ArrowUpCircle,
  Download,
  ExternalLink,
  MoreHorizontal,
  Play,
  RefreshCw,
  RotateCw,
  Server,
  Square,
  Wrench,
} from 'lucide-react';
import { errorMessage } from '@/api/client';
import { useBinaryOverview, useCaddyAction, useCaddyStatus, useCheckForUpdates, useInstallBinary, useUpstreams } from '@/api/hooks';
import type { CaddyStatus } from '@/api/types';
import { useAuth } from '@/auth';
import { useFeedback } from '@/components/feedback';
import { JobDialog } from '@/components/JobDialog';
import { caddyStateInfo } from '@/components/layout/CaddyStatusPill';
import { Markdown } from '@/components/Markdown';
import {
  Badge,
  Button,
  Callout,
  Card,
  CardBody,
  CardHeader,
  Chip,
  DescriptionList,
  Dialog,
  DropdownMenu,
  EmptyState,
  Field,
  Input,
  LoadingBlock,
  PageHeader,
  StatusDot,
  Table,
  TBody,
  TD,
  TH,
  THead,
  TR,
  useConfirm,
  useToast,
} from '@/components/ui';
import { formatDateTime, formatNumber, formatRelative } from '@/lib/format';
import { useNow } from '@/lib/useNow';

export default function ServicePage() {
  const status = useCaddyStatus();
  const binary = useBinaryOverview();
  const [jobId, setJobId] = useState<string | null>(null);
  const [versionDialog, setVersionDialog] = useState(false);

  return (
    <>
      <PageHeader title="Service & Updates" description="Control the Caddy service and keep the Caddy binary up to date." />
      <div className="grid grid-cols-1 gap-4 xl:grid-cols-2">
        <ServiceCard status={status.data} error={status.error} loading={status.isPending} />
        <BinaryCard onJob={setJobId} onPickVersion={() => setVersionDialog(true)} />
      </div>
      {binary.data?.latest?.notes && (
        <Card className="mt-4">
          <CardHeader
            title={`Release notes — ${binary.data.latest.version}`}
            description={binary.data.latest.publishedAt ? `Published ${formatDateTime(binary.data.latest.publishedAt)}` : undefined}
            actions={
              binary.data.latest.url && (
                <a
                  href={binary.data.latest.url}
                  target="_blank"
                  rel="noopener noreferrer"
                  className="inline-flex items-center gap-1 text-sm text-accent-text hover:underline"
                >
                  View on GitHub <ExternalLink size={12} aria-hidden />
                </a>
              )
            }
          />
          <CardBody className="max-h-[420px] overflow-y-auto text-sm text-fg-muted">
            <Markdown source={binary.data.latest.notes} />
          </CardBody>
        </Card>
      )}
      <UpstreamsCard />
      <JobDialog jobId={jobId} onClose={() => setJobId(null)} />
      {versionDialog && <InstallVersionDialog onClose={() => setVersionDialog(false)} onJob={setJobId} />}
    </>
  );
}

function ServiceCard({ status, error, loading }: { status?: CaddyStatus; error: unknown; loading: boolean }) {
  const { canOperate, isAdmin } = useAuth();
  const action = useCaddyAction();
  const confirm = useConfirm();
  const toast = useToast();
  const feedback = useFeedback();
  const now = useNow(30_000);
  const info = caddyStateInfo(status);
  const running = status?.state === 'running';
  const isService = status?.hostMode === 'windows-service';

  const run = async (a: Parameters<typeof action.mutate>[0], label: string) => {
    if (a === 'stop') {
      const ok = await confirm({
        title: 'Stop Caddy?',
        message: 'All sites, redirects and streams served by this server go offline until Caddy is started again.',
        confirmLabel: 'Stop Caddy',
        danger: true,
      });
      if (!ok) return;
    }
    if (a === 'service/uninstall') {
      const ok = await confirm({
        title: 'Uninstall the Caddy service?',
        message: 'Caddy is stopped and its Windows service registration removed. The binary and configuration are kept.',
        confirmLabel: 'Uninstall service',
        danger: true,
      });
      if (!ok) return;
    }
    action.mutate(a, {
      onSuccess: (s) => {
        if (s.lastError) toast.warning(`${label}: completed with a warning`, s.lastError);
        else toast.success(label);
      },
      onError: (err) => feedback.failed(err, { title: `${label} failed` }),
    });
  };

  return (
    <Card>
      <CardHeader
        icon={<Server size={16} />}
        title="Caddy service"
        description={isService ? 'Windows service “Caddy” (LocalSystem)' : status ? 'Child process of the manager (development mode)' : undefined}
        actions={
          canOperate &&
          status?.binaryInstalled && (
            <>
              {running ? (
                <Button size="sm" icon={<Square size={13} />} onClick={() => void run('stop', 'Caddy stopped')} loading={action.isPending && action.variables === 'stop'}>
                  Stop
                </Button>
              ) : (
                <Button
                  size="sm"
                  variant="primary"
                  icon={<Play size={13} />}
                  onClick={() => void run('start', 'Caddy started')}
                  loading={action.isPending && action.variables === 'start'}
                  disabled={isService && !status.serviceInstalled}
                >
                  Start
                </Button>
              )}
              <Button size="sm" icon={<RotateCw size={13} />} onClick={() => void run('restart', 'Caddy restarted')} loading={action.isPending && action.variables === 'restart'}>
                Restart
              </Button>
              {isAdmin && isService && (
                <DropdownMenu
                  width={220}
                  items={[
                    {
                      label: status.serviceInstalled ? 'Repair service' : 'Install service',
                      icon: <Wrench size={14} />,
                      onSelect: () => void run('service/install', status.serviceInstalled ? 'Service repaired' : 'Service installed'),
                    },
                    {
                      label: 'Uninstall service',
                      icon: <Square size={14} />,
                      danger: true,
                      hidden: !status.serviceInstalled,
                      onSelect: () => void run('service/uninstall', 'Service uninstalled'),
                    },
                  ]}
                  trigger={(p) => <Button {...p} size="sm" variant="ghost" iconOnly aria-label="More service actions" icon={<MoreHorizontal size={16} />} />}
                />
              )}
            </>
          )
        }
      />
      <CardBody>
        {loading ? (
          <LoadingBlock />
        ) : error ? (
          <Callout tone="danger" title="Could not read the Caddy status">
            {errorMessage(error)}
          </Callout>
        ) : status ? (
          <div className="flex flex-col gap-4">
            {!status.binaryInstalled && (
              <Callout tone="warning" title="Caddy is not installed">
                Install the Caddy binary from the panel on the right. The manager downloads the official release and verifies its checksum.
              </Callout>
            )}
            {status.binaryInstalled && isService && !status.serviceInstalled && (
              <Callout
                tone="warning"
                title="The Caddy service is not registered"
                actions={
                  isAdmin && (
                    <Button size="sm" onClick={() => void run('service/install', 'Service installed')}>
                      Install service
                    </Button>
                  )
                }
              >
                Caddy must run as a Windows service so it starts with the server.
              </Callout>
            )}
            {status.lastError && (
              <Callout tone="danger" title="Last error">
                <span className="mono text-xs break-words">{status.lastError}</span>
              </Callout>
            )}
            <DescriptionList
              items={[
                { label: 'State', value: <StatusDot tone={info.tone} label={info.label} pulse={info.pulse} /> },
                {
                  label: 'Admin API',
                  value: status.adminReachable ? (
                    <StatusDot tone="success" label="Reachable" />
                  ) : (
                    <StatusDot tone={running ? 'danger' : 'neutral'} label="Unreachable" />
                  ),
                },
                { label: 'Version', value: status.version ?? '—', mono: true },
                {
                  label: 'Running since',
                  value: status.startedAt ? `${formatDateTime(status.startedAt)} (${formatRelative(status.startedAt, now)})` : '—',
                  hidden: !running,
                },
                { label: 'Process ID', value: status.processId ?? '—', mono: true, hidden: !status.processId },
                { label: 'Host mode', value: isService ? 'Windows service' : 'Process (development)' },
                { label: 'Start type', value: status.serviceStartType ?? '—', hidden: !isService },
                { label: 'Binary', value: status.binaryPath || '—', mono: true },
                { label: 'Config file', value: status.configPath || '—', mono: true },
              ]}
            />
          </div>
        ) : null}
      </CardBody>
    </Card>
  );
}

function BinaryCard({ onJob, onPickVersion }: { onJob: (id: string) => void; onPickVersion: () => void }) {
  const { canOperate, isAdmin } = useAuth();
  const binary = useBinaryOverview();
  const check = useCheckForUpdates();
  const install = useInstallBinary();
  const feedback = useFeedback();
  const confirm = useConfirm();
  const toast = useToast();
  const now = useNow(60_000);
  const o = binary.data;

  const startInstall = async () => {
    if (!o) return;
    const target = o.latest?.version;
    const verb = o.installed ? 'Update' : 'Install';
    const ok = await confirm({
      title: o.installed ? `Update Caddy to ${target ?? 'the latest version'}?` : 'Install Caddy?',
      message: (
        <>
          The new binary is downloaded{o.desiredPlugins.length ? ' (custom build with your plugins)' : ' and its checksum verified'}, and
          the current configuration is validated against it.
          {o.installed && ' Caddy is then restarted — expect a few seconds of downtime. If it fails to start, the previous version is restored automatically.'}
        </>
      ),
      confirmLabel: verb,
    });
    if (!ok) return;
    install.mutate(undefined, {
      onSuccess: (job) => onJob(job.id),
      onError: (err) => feedback.failed(err, { title: `${verb} failed to start` }),
    });
  };

  return (
    <Card>
      <CardHeader
        icon={<ArrowUpCircle size={16} />}
        title="Caddy binary"
        description={o?.platform ? `Platform ${o.platform}` : undefined}
        actions={
          <>
            {canOperate && (
              <Button
                size="sm"
                icon={<RefreshCw size={13} />}
                loading={check.isPending}
                onClick={() =>
                  check.mutate(undefined, {
                    onSuccess: (r) =>
                      r.updateAvailable
                        ? toast.info('Update available', `Caddy ${r.latest?.version} is available.`)
                        : toast.success('Caddy is up to date', r.latest ? `Latest release: ${r.latest.version}` : undefined),
                    onError: (err) => feedback.failed(err, { title: 'Could not check for updates' }),
                  })
                }
              >
                Check now
              </Button>
            )}
            {isAdmin && o && (
              <DropdownMenu
                width={230}
                items={[{ label: 'Install specific version…', icon: <Download size={14} />, onSelect: onPickVersion }]}
                trigger={(p) => <Button {...p} size="sm" variant="ghost" iconOnly aria-label="More binary actions" icon={<MoreHorizontal size={16} />} />}
              />
            )}
          </>
        }
      />
      <CardBody>
        {binary.isPending ? (
          <LoadingBlock />
        ) : binary.isError ? (
          <Callout tone="danger" title="Could not read binary information">
            {errorMessage(binary.error)}
          </Callout>
        ) : o ? (
          <div className="flex flex-col gap-4">
            {o.updateAvailable && o.latest && (
              <Callout
                tone="info"
                title={`Caddy ${o.latest.version} is available`}
                actions={
                  isAdmin && (
                    <Button size="sm" variant="primary" onClick={() => void startInstall()} loading={install.isPending}>
                      Update to {o.latest.version}
                    </Button>
                  )
                }
              >
                Installed: <span className="mono">{o.installed?.version ?? 'none'}</span>. Review the release notes below before updating.
              </Callout>
            )}
            {!o.installed && (
              <Callout
                tone="warning"
                title="No Caddy binary installed"
                actions={
                  isAdmin && (
                    <Button size="sm" variant="primary" onClick={() => void startInstall()} loading={install.isPending}>
                      Install Caddy{o.latest ? ` ${o.latest.version}` : ''}
                    </Button>
                  )
                }
              >
                The manager downloads Caddy from GitHub (or from caddyserver.com when plugins are selected).
              </Callout>
            )}
            {o.pluginsOutOfSync && (
              <Callout
                tone="warning"
                title="Plugins out of sync"
                actions={
                  <Link to="/caddy/plugins" className="text-sm font-medium text-accent-text hover:underline">
                    Review
                  </Link>
                }
              >
                The installed binary does not contain exactly the desired plugins. Rebuild to apply them.
              </Callout>
            )}
            <DescriptionList
              items={[
                {
                  label: 'Installed',
                  value: o.installed ? (
                    <span className="flex flex-wrap items-center gap-2">
                      <span className="mono">{o.installed.version}</span>
                      {o.updateAvailable ? <Badge tone="info">Update available</Badge> : o.latest ? <Badge tone="success">Latest</Badge> : null}
                    </span>
                  ) : (
                    'Not installed'
                  ),
                },
                { label: 'Installed on', value: formatDateTime(o.installed?.installedAt), hidden: !o.installed?.installedAt },
                {
                  label: 'Latest release',
                  value: o.latest ? (
                    <span>
                      <span className="mono">{o.latest.version}</span>
                      {o.latest.publishedAt && <span className="text-fg-subtle"> · {formatRelative(o.latest.publishedAt, now)}</span>}
                    </span>
                  ) : (
                    'Unknown (not checked yet or GitHub unreachable)'
                  ),
                },
                { label: 'Last checked', value: o.lastCheckedAt ? formatRelative(o.lastCheckedAt, now) : 'Never' },
                {
                  label: 'Plugins',
                  value:
                    o.installed && o.installed.plugins.length > 0 ? (
                      <span className="flex flex-wrap gap-1">
                        {o.installed.plugins.map((p) => (
                          <Chip key={p}>{p}</Chip>
                        ))}
                      </span>
                    ) : (
                      <span className="text-fg-subtle">Standard build</span>
                    ),
                },
                { label: 'Binary path', value: o.installed?.path ?? '—', mono: true, hidden: !o.installed },
              ]}
            />
            {isAdmin && o.installed && !o.updateAvailable && (
              <div>
                <Button size="sm" icon={<Download size={13} />} onClick={() => void startInstall()} loading={install.isPending}>
                  Reinstall {o.latest?.version ?? 'latest'}
                </Button>
              </div>
            )}
          </div>
        ) : null}
      </CardBody>
    </Card>
  );
}

function InstallVersionDialog({ onClose, onJob }: { onClose: () => void; onJob: (id: string) => void }) {
  const [version, setVersion] = useState('');
  const [error, setError] = useState<string | null>(null);
  const install = useInstallBinary();
  const feedback = useFeedback();
  const submit = (e: FormEvent) => {
    e.preventDefault();
    const v = version.trim().startsWith('v') ? version.trim() : `v${version.trim()}`;
    if (!/^v\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$/.test(v)) {
      setError('Enter a release tag such as v2.11.4.');
      return;
    }
    install.mutate(v, {
      onSuccess: (job) => {
        onClose();
        onJob(job.id);
      },
      onError: (err) => feedback.failed(err, { title: 'Install failed to start' }),
    });
  };
  return (
    <Dialog
      open
      onClose={onClose}
      size="sm"
      title="Install a specific version"
      description="Use this to roll back or pin a version. Plugins are only available for the latest version."
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button type="submit" form="install-version" variant="primary" loading={install.isPending}>
            Install
          </Button>
        </>
      }
    >
      <form id="install-version" onSubmit={submit} noValidate>
        <Field label="Version" error={error} hint="GitHub release tag of caddyserver/caddy.">
          <Input mono placeholder="v2.11.4" value={version} onChange={(e) => { setVersion(e.target.value); setError(null); }} />
        </Field>
      </form>
    </Dialog>
  );
}

function UpstreamsCard() {
  const ups = useUpstreams();
  if (ups.isError || (ups.data && ups.data.length === 0)) {
    return (
      <Card className="mt-4">
        <CardHeader title="Upstream health" description="Live state of reverse-proxy backends reported by Caddy." />
        <EmptyState
          title={ups.isError ? 'Upstream data unavailable' : 'No upstreams reported'}
          description={ups.isError ? 'Caddy’s admin API is not reachable.' : 'Caddy reports upstreams once proxy hosts receive traffic.'}
        />
      </Card>
    );
  }
  return (
    <Card className="mt-4">
      <CardHeader title="Upstream health" description="Live state of reverse-proxy backends reported by Caddy." />
      {ups.isPending ? (
        <LoadingBlock />
      ) : (
        <Table>
          <THead>
            <tr>
              <TH>Address</TH>
              <TH>Health</TH>
              <TH className="text-right">Active requests</TH>
              <TH className="text-right">Recent failures</TH>
            </tr>
          </THead>
          <TBody>
            {(ups.data ?? []).map((u) => (
              <TR key={u.address}>
                <TD className="mono">{u.address}</TD>
                <TD>{u.healthy ? <StatusDot tone="success" label="Healthy" /> : <StatusDot tone="danger" label="Unhealthy" />}</TD>
                <TD className="mono text-right">{formatNumber(u.numRequests)}</TD>
                <TD className="mono text-right">{formatNumber(u.fails)}</TD>
              </TR>
            ))}
          </TBody>
        </Table>
      )}
    </Card>
  );
}
