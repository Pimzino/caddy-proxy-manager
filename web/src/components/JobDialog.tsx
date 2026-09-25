import { useEffect, useRef } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { CheckCircle2, XCircle } from 'lucide-react';
import { useJob, qk } from '@/api/hooks';
import { errorMessage } from '@/api/client';
import { Badge, Button, Callout, CopyButton, Dialog, Spinner } from '@/components/ui';
import { formatDateTime } from '@/lib/format';

/** Follows a background job (GET /api/jobs/{id}, 1 s polling) and shows its live log. */
export function JobDialog({ jobId, onClose, title }: { jobId: string | null; onClose: () => void; title?: string }) {
  if (!jobId) return null;
  return <JobDialogInner jobId={jobId} onClose={onClose} title={title} />;
}

function JobDialogInner({ jobId, onClose, title }: { jobId: string; onClose: () => void; title?: string }) {
  const job = useJob(jobId);
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
      void qc.invalidateQueries({ queryKey: qk.caddy });
      void qc.invalidateQueries({ queryKey: qk.dashboard });
      void qc.invalidateQueries({ queryKey: qk.streamSupport });
      void qc.invalidateQueries({ queryKey: qk.settings('binary') });
    }
  }, [state, qc]);

  const log = job.data?.log.join('\n') ?? '';
  return (
    <Dialog
      open
      onClose={onClose}
      size="lg"
      title={job.data?.title ?? title ?? 'Background job'}
      description={job.data ? `Started ${formatDateTime(job.data.startedAt)}` : undefined}
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
          {errorMessage(job.error)} The job may have finished while the manager restarted.
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
