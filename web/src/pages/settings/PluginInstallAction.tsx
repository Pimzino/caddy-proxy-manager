import { useEffect, useState, type ReactNode } from 'react';
import { Link } from 'react-router';
import { useQueryClient } from '@tanstack/react-query';
import { AlertTriangle, Hammer } from 'lucide-react';
import { qk, useBinaryOverview, useInstallBinary, useIsManagedNode, useJob, useSavePlugins } from '@/api/hooks';
import { useAuth } from '@/auth';
import { useFeedback } from '@/components/feedback';
import { JobDialog } from '@/components/JobDialog';
import { Button, useConfirm } from '@/components/ui';

/**
 * Warning box for a Caddy plugin the configuration needs but the installed binary lacks, with the
 * "Add plugin and rebuild Caddy" action: adds the Go package to the desired plugins (PUT /api/caddy/plugins) and starts the
 * binary install job (POST /api/caddy/binary/install), following it in the job dialog. Unsaved settings on the page are
 * kept (neither call touches the settings document). Stays mounted once the plugin is installed so the job dialog is not
 * torn down when the build finishes.
 */
export function PluginRequirement({
  pkg,
  what,
  installed,
  title,
  children,
}: {
  pkg: string;
  /** Short name used in the confirmation, e.g. "Cloudflare DNS". */
  what: string;
  installed: boolean;
  title: ReactNode;
  children?: ReactNode;
}) {
  return (
    <PluginInstallAction pkg={pkg} what={what} installed={installed}>
      {(action) =>
        installed ? null : (
          <div className="flex gap-3 rounded-md border border-warning/35 bg-warning-soft px-3.5 py-3">
            <AlertTriangle size={16} className="mt-0.5 shrink-0 text-warning" aria-hidden />
            <div className="flex min-w-0 flex-1 flex-col gap-2 text-sm">
              <div>
                <p className="font-medium text-fg">{title}</p>
                {children && <div className="mt-0.5 text-fg-muted">{children}</div>}
              </div>
              {action}
            </div>
          </div>
        )
      }
    </PluginInstallAction>
  );
}

function PluginInstallAction({
  pkg,
  what,
  installed,
  children,
}: {
  pkg: string;
  what: string;
  installed: boolean;
  children: (action: ReactNode) => ReactNode;
}) {
  const { isAdmin } = useAuth();
  const { managed } = useIsManagedNode();
  const binary = useBinaryOverview();
  const save = useSavePlugins();
  const install = useInstallBinary();
  const confirm = useConfirm();
  const feedback = useFeedback();
  const qc = useQueryClient();
  // The job keeps being followed after its dialog is hidden, so the catalog refreshes when it ends.
  const [jobId, setJobId] = useState<string | null>(null);
  const [dialogOpen, setDialogOpen] = useState(false);
  const job = useJob(jobId);
  const state = job.data?.state;

  useEffect(() => {
    // The DNS provider catalog's "installed" flags come from the installed binary.
    if (state === 'succeeded' || state === 'failed') void qc.invalidateQueries({ queryKey: qk.dnsProviders });
  }, [state, qc]);

  const desired = binary.data?.desiredPlugins ?? [];
  const alreadyDesired = desired.includes(pkg);
  const next = alreadyDesired ? desired : [...desired, pkg];

  const run = async () => {
    const ok = await confirm({
      title: alreadyDesired ? 'Rebuild and install Caddy?' : `Add the ${what} plugin and rebuild Caddy?`,
      message: (
        <>
          {alreadyDesired ? (
            <>
              <span className="mono text-fg">{pkg}</span> is already in the desired plugins but not in the installed binary.{' '}
            </>
          ) : (
            <>
              <span className="mono text-fg">{pkg}</span> is added to the desired plugins.{' '}
            </>
          )}
          A custom Caddy build with {next.length} plugin{next.length === 1 ? '' : 's'} is downloaded from caddyserver.com, validated against the
          current configuration and swapped in. Caddy restarts briefly; the previous binary is restored if it fails. Unsaved changes on this page
          are kept — save them after the build.
        </>
      ),
      confirmLabel: 'Add & rebuild',
    });
    if (!ok) return;
    try {
      if (!alreadyDesired) await save.mutateAsync(next);
      const started = await install.mutateAsync(undefined);
      setJobId(started.id);
      setDialogOpen(true);
    } catch (err) {
      feedback.failed(err, { title: 'Could not start the rebuild' });
    }
  };

  const action = managed ? (
    <span className="text-xs text-fg-subtle">Plugins are managed on the primary; this server installs them automatically after the next sync.</span>
  ) : !isAdmin ? (
    <span className="text-xs text-fg-subtle">An administrator can add the plugin and rebuild Caddy.</span>
  ) : (
    <div className="flex flex-wrap items-center gap-2">
      <Button
        size="sm"
        variant="primary"
        icon={<Hammer size={13} />}
        loading={save.isPending || install.isPending}
        disabled={binary.isPending || state === 'running'}
        onClick={() => void run()}
      >
        {alreadyDesired ? 'Rebuild Caddy' : 'Add plugin and rebuild Caddy'}
      </Button>
      {state === 'running' && !dialogOpen && (
        <Button size="sm" variant="ghost" onClick={() => setDialogOpen(true)}>
          Show progress
        </Button>
      )}
      <Link to={`/caddy/plugins?q=${encodeURIComponent(pkg.split('/').pop() ?? pkg)}`} className="text-xs text-accent-text hover:underline">
        Plugins page
      </Link>
    </div>
  );

  return (
    <>
      {children(installed && state !== 'running' ? null : action)}
      <JobDialog jobId={dialogOpen ? jobId : null} onClose={() => setDialogOpen(false)} />
    </>
  );
}
