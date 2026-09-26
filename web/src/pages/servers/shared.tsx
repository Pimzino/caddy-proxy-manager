// Helpers shared by the Servers, Server detail and Traffic pages.
import { useEffect, useRef, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { AlertTriangle, CheckCircle2, XCircle } from 'lucide-react';
import { errorMessage } from '@/api/client';
import { qk, useServerJob } from '@/api/hooks';
import type { CaddyRunState, ServerStatus, ServerSummary, TrafficReport } from '@/api/types';
import { Badge, Button, Callout, CodeBlock, CopyButton, Dialog, Spinner, type Tone } from '@/components/ui';
import { cn } from '@/lib/cn';
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

/** Default install folder of the MSI (installer/Package.wxs). */
const MANAGER_EXE = 'C:\\Program Files\\Caddy Proxy Manager\\CaddyManager.exe';

export type AdminShell = 'powershell' | 'cmd';

/**
 * The CLI join for an elevated prompt (docs/cli.md): the CLI opens the database directly, so the service is stopped
 * first and started again afterwards, when the primary pushes its configuration. The exe is called by its full path
 * (it is not on PATH, and an elevated prompt starts in System32); PowerShell needs the call operator for a quoted path.
 * Tokens are base64url (no quotes or spaces), quoted anyway so a pasted line break cannot split the command.
 */
export function joinCommand(token: string, shell: AdminShell = 'powershell'): string {
  const join = shell === 'powershell' ? `& '${MANAGER_EXE}' cluster join '${token}'` : `"${MANAGER_EXE}" cluster join "${token}"`;
  return ['net stop CaddyProxyManager', join, 'net start CaddyProxyManager'].join('\n');
}

/** `cluster leave` on a node (service stopped), for when the primary cannot reach it. */
export function leaveCommand(shell: AdminShell = 'powershell'): string {
  const leave = shell === 'powershell' ? `& '${MANAGER_EXE}' cluster leave` : `"${MANAGER_EXE}" cluster leave`;
  return ['net stop CaddyProxyManager', leave, 'net start CaddyProxyManager'].join('\n');
}

// Traffic buckets (docs/traffic-statistics.md): minute and hour buckets are shown in the browser's time zone; day buckets
// are UTC calendar days (00:00–24:00 UTC), so they are labelled with their UTC date — the local date of 00:00 UTC is the
// previous day west of UTC.
const dayFmt = new Intl.DateTimeFormat(undefined, { weekday: 'short', month: 'short', day: 'numeric', timeZone: 'UTC' });
const dayHourFmt = new Intl.DateTimeFormat(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
const hmFmt = new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit' });
const hmsFmt = new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit' });

/** Tooltip/table heading for a traffic bucket ("Sep 25, 14:00–15:00" local time, "Fri, Sep 25 (UTC)"). */
export function formatBucket(ms: number, size: TrafficReport['bucketSize']): string {
  if (size === 'day') return `${dayFmt.format(ms)} (UTC)`;
  const end = ms + (size === 'hour' ? 3_600_000 : 60_000);
  return `${dayHourFmt.format(ms)}–${hmFmt.format(end)}`;
}

/** The browser's time zone as shown next to traffic charts, e.g. "Europe/London, UTC+01:00". */
export function localTimeZoneLabel(at = Date.now()): string {
  const offset = -new Date(at).getTimezoneOffset();
  const sign = offset < 0 ? '−' : '+';
  const abs = Math.abs(offset);
  const utc = `UTC${sign}${String(Math.floor(abs / 60)).padStart(2, '0')}:${String(abs % 60).padStart(2, '0')}`;
  const zone = Intl.DateTimeFormat().resolvedOptions().timeZone;
  return zone && zone !== 'UTC' ? `${zone}, ${utc}` : utc;
}

/** Which clock a traffic report's time axis uses (shown with the charts). */
export function bucketClockNote(size: TrafficReport['bucketSize']): string {
  return size === 'day' ? 'Days are UTC calendar days (00:00–24:00 UTC)' : `Times in your time zone (${localTimeZoneLabel()})`;
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

const SHELLS: { value: AdminShell; label: string }[] = [
  { value: 'powershell', label: 'PowerShell' },
  { value: 'cmd', label: 'Command Prompt' },
];

/** A command for an elevated prompt, with a PowerShell / Command Prompt switch (quoting differs). */
export function AdminCommandBlock({ command, title }: { command: (shell: AdminShell) => string; title: string }) {
  const [shell, setShell] = useState<AdminShell>('powershell');
  return (
    <CodeBlock
      code={command(shell)}
      language={shell === 'powershell' ? 'powershell' : 'text'}
      title={`${title} — elevated ${shell === 'powershell' ? 'PowerShell' : 'Command Prompt'}`}
      wrap
      maxHeight={180}
      copyLabel="Copy commands"
      actions={
        <div role="radiogroup" aria-label="Shell" className="inline-flex items-center rounded border border-border bg-surface p-0.5">
          {SHELLS.map((o) => (
            <button
              key={o.value}
              type="button"
              role="radio"
              aria-checked={shell === o.value}
              onClick={() => setShell(o.value)}
              className={cn(
                'rounded px-1.5 py-0.5 text-xs font-medium focus-visible:outline-2 focus-visible:outline-ring',
                shell === o.value ? 'bg-accent-soft text-accent-text' : 'text-fg-muted hover:text-fg',
              )}
            >
              {o.label}
            </button>
          ))}
        </div>
      }
    />
  );
}

/**
 * Join token display: shown once, with copy buttons for the token and the CLI commands. `rejoin`: the node already
 * joined once (the token came from Regenerate join token), so it is only needed to join that server again.
 */
export function JoinTokenPanel({
  serverName,
  token,
  fingerprint,
  rejoin = false,
}: {
  serverName: string;
  token: string;
  fingerprint?: string | null;
  rejoin?: boolean;
}) {
  const node = <strong className="font-medium text-fg">{serverName}</strong>;
  return (
    <div className="flex flex-col gap-4">
      <Callout tone="warning" title="Copy the token now — it is shown only once">
        {rejoin ? (
          <>Anyone with this token can join a server to this cluster as {node}. Keep it like a password, or regenerate it again later.</>
        ) : (
          <>
            Anyone with this token can join {node} to this cluster until it has joined. Regenerate it from the server’s menu if it is
            lost; the old token then stops working.
          </>
        )}
      </Callout>
      <CodeBlock code={token} title="Join token" wrap maxHeight={120} copyLabel="Copy token" />
      <div>
        <p className="mb-1.5 text-sm text-fg-muted">
          {rejoin ? (
            <>
              To join {node} again (after it was reinstalled, restored or reset, or left the cluster), run these commands on it in an
              elevated PowerShell or Command Prompt. They stop the Caddy Proxy Manager service, join with the new token and start the
              service again. A server that left the cluster can instead paste the token in{' '}
              <span className="font-medium text-fg">Settings › Cluster › Join cluster</span>.
            </>
          ) : (
            <>
              On {node}, either paste the token in <span className="font-medium text-fg">Settings › Cluster › Join cluster</span>, or run
              these commands in an elevated PowerShell or Command Prompt. They stop the Caddy Proxy Manager service, join and start the
              service again; the configuration arrives with the next heartbeat.
            </>
          )}
        </p>
        <AdminCommandBlock command={(shell) => joinCommand(token, shell)} title="Join" />
        <p className="mt-1.5 text-xs text-fg-subtle">Adjust the path if Caddy Proxy Manager is installed in another folder.</p>
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
      {rejoin ? (
        <Callout tone="info">
          Joining again replaces the node’s proxy hosts, streams, access lists, certificates and Caddy settings with this server’s at
          the next sync, as on the first join.
        </Callout>
      ) : (
        <Callout tone="danger" title="The node’s existing sites are replaced">
          When the node joins, its proxy hosts, streams, access lists, certificates and Caddy settings are replaced by this
          server’s at the first sync. Back up the node first if it already serves sites.
        </Callout>
      )}
    </div>
  );
}

export function StaleIcon({ title }: { title: string }) {
  return <AlertTriangle size={13} className="shrink-0 text-warning" aria-label={title} role="img" />;
}
