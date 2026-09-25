import { useEffect, useRef, useState } from 'react';
import { RotateCw } from 'lucide-react';
import { api } from '@/api/client';
import { useRestartManager } from '@/api/hooks';
import type { Health } from '@/api/types';
import { useFeedback } from '@/components/feedback';
import { Button, Callout, Spinner, useConfirm } from '@/components/ui';

type Phase = 'idle' | 'restarting' | 'back' | 'timeout';

/**
 * "Restart required" notice with a button that restarts the manager service (POST /api/system/restart)
 * and waits for /api/health to answer again. `nextUrl` is where the UI will live afterwards (port/HTTPS changes).
 */
export function RestartPanel({ reason, nextUrl }: { reason: string; nextUrl?: string }) {
  const restart = useRestartManager();
  const confirm = useConfirm();
  const feedback = useFeedback();
  const [phase, setPhase] = useState<Phase>('idle');
  const timer = useRef<number | null>(null);
  const sameOrigin = !nextUrl || new URL(nextUrl, window.location.href).origin === window.location.origin;

  useEffect(
    () => () => {
      if (timer.current) window.clearTimeout(timer.current);
    },
    [],
  );

  const waitForHealth = (started: number, sawDown: boolean) => {
    timer.current = window.setTimeout(async () => {
      let up: boolean;
      try {
        await api.get<Health>('/api/health');
        up = true;
      } catch {
        up = false;
      }
      const elapsed = Date.now() - started;
      if (up && (sawDown || elapsed > 8000)) {
        setPhase('back');
        if (sameOrigin) window.location.reload();
        return;
      }
      if (elapsed > 90_000) {
        setPhase('timeout');
        return;
      }
      waitForHealth(started, sawDown || !up);
    }, 1500);
  };

  const run = async () => {
    const ok = await confirm({
      title: 'Restart the management service?',
      message: 'The console is unavailable for a few seconds. Caddy keeps serving sites while the manager restarts.',
      confirmLabel: 'Restart now',
    });
    if (!ok) return;
    restart.mutate(undefined, {
      onSuccess: () => {
        setPhase('restarting');
        waitForHealth(Date.now(), false);
      },
      onError: (err) => feedback.failed(err, { title: 'Restart failed' }),
    });
  };

  if (phase === 'restarting')
    return (
      <Callout tone="info" title="Restarting the management service…">
        <span className="inline-flex items-center gap-2">
          <Spinner size={13} /> Waiting for it to come back{sameOrigin ? '; the page reloads automatically.' : '.'}
        </span>
      </Callout>
    );
  if (phase === 'back' && !sameOrigin)
    return (
      <Callout tone="success" title="The management service restarted">
        Continue at{' '}
        <a href={nextUrl} className="mono text-accent-text hover:underline">
          {nextUrl}
        </a>
        .
      </Callout>
    );
  if (phase === 'timeout')
    return (
      <Callout tone="warning" title="The service has not answered yet">
        It may still be starting, or the new listener settings are not reachable from here.
        {nextUrl && (
          <>
            {' '}
            Try{' '}
            <a href={nextUrl} className="mono text-accent-text hover:underline">
              {nextUrl}
            </a>
            .
          </>
        )}{' '}
        On the server, check the “Caddy Proxy Manager” service and its log.
      </Callout>
    );
  return (
    <Callout
      tone="warning"
      title="Restart required"
      actions={
        <Button size="sm" variant="primary" icon={<RotateCw size={13} />} loading={restart.isPending} onClick={() => void run()}>
          Restart now
        </Button>
      }
    >
      {reason}
      {nextUrl && !sameOrigin && (
        <>
          {' '}
          Afterwards the console is at <span className="mono text-fg">{nextUrl}</span>.
        </>
      )}
    </Callout>
  );
}
