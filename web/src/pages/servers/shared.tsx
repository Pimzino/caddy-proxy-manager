// Helpers shared by the Servers, Server detail and Traffic pages.
import { useEffect, useRef } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { AlertTriangle, CheckCircle2, XCircle } from 'lucide-react';
import { errorMessage } from '@/api/client';
import { qk, useServerJob } from '@/api/hooks';
import type { CaddyRunState, ServerStatus, ServerSummary, TrafficReport } from '@/api/types';
import { Badge, Button, Callout, CodeBlock, CopyButton, Dialog, Spinner, type Tone } from '@/components/ui';
import { formatDateTime } from '@/lib/format';

export function serverStatusInfo(status: ServerStatus): { tone: Tone; label: string; pulse?: boolean } {
  switch (status) {
    case 'online':
      return { tone: 'success', label: 'Online' };
    case 'offline':
      return { tone: 'danger', label: 'Offline' };
    case 'pending':
      return { tone: 'neutral', label: 'Waiting to join', pulse: true };
    default:
      return { tone: 'warning', label: 'Error' };
  }
}

export function caddyRunStateInfo(state: CaddyRunState | undefined): { tone: Tone; label: string } {
  switch (state) {
    case 'running':
      return { tone: 'success', label: 'Running' };
    case 'starting':
      return { tone: 'warning', label: 'Starting' };
    case 'stopping':
      return { tone: 'warning', label: 'Stopping' };
    case 'stopped':
      return { tone: 'danger', label: 'Stopped' };
    case 'notInstalled':
      return { tone: 'neutral', label: 'Not installed' };
    default:
      return { tone: 'neutral', label: 'Unknown' };
  }
}

/** First 12 hex characters of a bundle revision (SHA-256). */
export function shortRevision(rev: string | null | undefined): string {
  return rev ? rev.replace(/^sha256:/, '').slice(0, 12) : '—';
}

/** Versions compare without a leading "v" ("v2.11.4" = "2.11.4"). */
export function sameVersion(a: string | null | undefined, b: string | null | undefined): boolean {
  if (!a || !b) return true;
  return a.replace(/^v/i, '') === b.replace(/^v/i, '');
}

/** The local server of a /api/servers list (always first, but found by flag). */
export function localServer(list: ServerSummary[] | undefined): ServerSummary | undefined {
  return list?.find((s) => s.isLocal) ?? list?.[0];
}

export const joinCommand = (token: string) => `CaddyManager.exe cluster join ${token}`;

const dayFmt = new Intl.DateTimeFormat(undefined, { weekday: 'short', month: 'short', day: 'numeric' });
const dayHourFmt = new Intl.DateTimeFormat(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
const hmFmt = new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit' });
const hmsFmt = new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit' });

/** Tooltip/table heading for a traffic bucket ("Sep 25, 14:00–15:00", "Thu, Sep 25"). */
export function formatBucket(ms: number, size: TrafficReport['bucketSize']): string {
  if (size === 'day') return dayFmt.format(ms);
  const end = ms + (size === 'hour' ? 3_600_000 : 60_000);
  return `${dayHourFmt.format(ms)}–${hmFmt.format(end)}`;
}

/** Tooltip heading for a live sample. */
export const formatSampleTime = (ms: number) => hmsFmt.format(ms);

/**
 * Follows a job that runs on a (possibly remote) server (GET /api/servers/{id}/jobs/{jobId}, 1 s polling)
 * and shows its live log, like JobDialog does for local jobs.
 */
export function ServerJobDialog({
  serverId,
  serverName,
  jobId,
  onClose,
}: {
  serverId: string;
  serverName: string;
  jobId: string | null;
  onClose: () => void;
}) {
  const job = useServerJob(serverId, jobId);
  const qc = useQueryClient();
  const logRef = useRef<HTMLPreElement>(null);
  const state = job.data?.state;
  const lines = job.data?.log.length ?? 0;

  useEffect(() => {
    const el = logRef.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [lines]);

  useEffect(() => {
    if (state === 'succeeded' || state === 'failed') {
      void qc.invalidateQueries({ queryKey: qk.servers });
      if (serverId === 'local') void qc.invalidateQueries({ queryKey: qk.caddy });
    }
  }, [state, qc, serverId]);

  if (!jobId) return null;
  const log = job.data?.log.join('\n') ?? '';
  return (
    <Dialog
      open
      onClose={onClose}
      size="lg"
      title={job.data?.title ?? `Background job on ${serverName}`}
      description={job.data ? `${serverName} · started ${formatDateTime(job.data.startedAt)}` : serverName}
      headerExtra={
        state === 'running' ? (
          <Badge tone="info" icon={<Spinner size={10} />}>
            Running
          </Badge>
        ) : state === 'succeeded' ? (
          <Badge tone="success" icon={<CheckCircle2 size={11} />}>
            Succeeded
          </Badge>
        ) : state === 'failed' ? (
          <Badge tone="danger" icon={<XCircle size={11} />}>
            Failed
          </Badge>
        ) : null
      }
      footer={
        <>
          {log && <CopyButton text={log} label="Copy log" />}
          <Button variant={state === 'running' ? 'secondary' : 'primary'} onClick={onClose}>
            {state === 'running' ? 'Hide (keeps running)' : 'Close'}
          </Button>
        </>
      }
    >
      {job.isError && (
        <Callout tone="danger" className="mb-3" title="Could not read the job status">
          {errorMessage(job.error)} The server may be restarting or unreachable; the job keeps running there.
        </Callout>
      )}
      {state === 'failed' && job.data?.error && (
        <Callout tone="danger" className="mb-3" title="The job failed">
          {job.data.error}
        </Callout>
      )}
      {state === 'succeeded' && (
        <Callout tone="success" className="mb-3">
          Completed{job.data?.finishedAt ? ` at ${formatDateTime(job.data.finishedAt)}` : ''}.
        </Callout>
      )}
      <pre
        ref={logRef}
        tabIndex={0}
        aria-label="Job log"
        aria-live="polite"
        className="mono h-80 overflow-auto rounded-md border border-border bg-code p-3 text-[12px] leading-[1.6] whitespace-pre-wrap text-fg"
      >
        {log || (job.isPending ? 'Waiting for output…' : 'No output yet.')}
      </pre>
    </Dialog>
  );
}

/** Join token display: shown once, with copy buttons for the token and the CLI command. */
export function JoinTokenPanel({ serverName, token, fingerprint }: { serverName: string; token: string; fingerprint?: string | null }) {
  return (
    <div className="flex flex-col gap-4">
      <Callout tone="warning" title="Copy the token now — it is shown only once">
        Anyone with this token can join <strong className="font-medium text-fg">{serverName}</strong> to this cluster until it
        has joined. Regenerate it from the server’s menu if it is lost; the old token then stops working.
      </Callout>
      <CodeBlock code={token} title="Join token" wrap maxHeight={120} copyLabel="Copy token" />
      <div>
        <p className="mb-1.5 text-sm text-fg-muted">
          On {serverName}, either paste the token in <span className="font-medium text-fg">Settings › Cluster › Join cluster</span>, or
          stop the Caddy Proxy Manager service and run in an elevated prompt:
        </p>
        <CodeBlock code={joinCommand(token)} language="powershell" title="Command" wrap maxHeight={140} copyLabel="Copy command" />
      </div>
      {fingerprint && (
        <div className="rounded-md border border-border bg-surface-2/60 px-3 py-2.5 text-sm">
          <p className="font-medium text-fg">Pinned HTTPS certificate</p>
          <p className="mt-0.5 text-xs text-fg-subtle">
            SHA-256 fingerprint of the node’s certificate. The primary only talks to the node while it presents this certificate.
          </p>
          <p className="mono mt-1.5 text-xs break-all text-fg">{fingerprint}</p>
        </div>
      )}
      <Callout tone="danger" title="The node’s existing sites are replaced">
        When the node joins, its proxy hosts, streams, access lists, certificates and Caddy settings are replaced by this
        server’s at the first sync. Back up the node first if it already serves sites.
      </Callout>
    </div>
  );
}

export function StaleIcon({ title }: { title: string }) {
  return <AlertTriangle size={13} className="shrink-0 text-warning" aria-label={title} role="img" />;
}
