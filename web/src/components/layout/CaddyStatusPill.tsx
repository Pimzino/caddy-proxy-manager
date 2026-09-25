import { Link } from 'react-router';
import { ArrowUpCircle } from 'lucide-react';
import { useBinaryOverview, useCaddyStatus } from '@/api/hooks';
import type { CaddyStatus } from '@/api/types';
import { StatusDot, type Tone } from '@/components/ui';
import { cn } from '@/lib/cn';

export function caddyStateInfo(status: CaddyStatus | undefined): { tone: Tone; label: string; pulse?: boolean } {
  if (!status) return { tone: 'neutral', label: 'Checking…' };
  switch (status.state) {
    case 'running':
      return status.adminReachable
        ? { tone: 'success', label: 'Running' }
        : { tone: 'warning', label: 'Running · admin API unreachable' };
    case 'starting':
      return { tone: 'warning', label: 'Starting', pulse: true };
    case 'stopping':
      return { tone: 'warning', label: 'Stopping', pulse: true };
    case 'stopped':
      return { tone: 'danger', label: 'Stopped' };
    case 'notInstalled':
      return { tone: 'neutral', label: status.binaryInstalled ? 'Service not installed' : 'Not installed' };
    default:
      return { tone: 'neutral', label: 'Unknown' };
  }
}

/** Global Caddy status (polled every 10 s) shown in the top bar. */
export function CaddyStatusPill() {
  const status = useCaddyStatus();
  const binary = useBinaryOverview();
  const info = status.isError ? { tone: 'neutral' as Tone, label: 'Status unavailable' } : caddyStateInfo(status.data);
  const version = status.data?.version ?? binary.data?.installed?.version;
  const update = binary.data?.updateAvailable ? binary.data.latest?.version : undefined;
  return (
    <Link
      to="/caddy/service"
      className={cn(
        'inline-flex h-7 max-w-full items-center gap-2 rounded-full border border-border bg-surface px-2.5 text-sm text-fg shadow-xs',
        'hover:bg-surface-2 focus-visible:outline-2 focus-visible:outline-ring',
      )}
      aria-label={`Caddy: ${info.label}${version ? `, version ${version}` : ''}${update ? `, update ${update} available` : ''}`}
    >
      <span className="hidden text-xs font-medium text-fg-subtle sm:inline">Caddy</span>
      <StatusDot tone={info.tone} label={info.label} pulse={info.pulse} className="truncate" />
      {version && <span className="mono hidden text-xs text-fg-muted md:inline">{version}</span>}
      {update && (
        <span className="hidden items-center gap-1 rounded-full bg-accent-soft px-1.5 text-xs font-medium text-accent-text md:inline-flex">
          <ArrowUpCircle size={12} aria-hidden />
          Update available
        </span>
      )}
    </Link>
  );
}
