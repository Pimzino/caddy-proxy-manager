import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from 'react';
import { Link } from 'react-router';
import { ArrowRight, ArrowUpCircle, ChevronRight, Download, ExternalLink, RefreshCw } from 'lucide-react';
import { errorMessage } from '@/api/client';
import { useCheckManagerUpdate, useManagerUpdate } from '@/api/hooks';
import type { ManagerUpdateInfo, ReleaseAsset, ReleaseInfo } from '@/api/types';
import { useAuth } from '@/auth';
import { Badge, Button, buttonClasses, Callout, CopyButton, Dialog, Spinner } from '@/components/ui';
import { cn } from '@/lib/cn';
import { formatBytes, formatDateTime, formatRelative } from '@/lib/format';
import { useNow } from '@/lib/useNow';
import { ReleaseNotes, safeHttpUrl } from './ReleaseNotes';

// ---------------------------------------------------------------- opening the dialog from anywhere

const OpenContext = createContext<(() => void) | null>(null);

/** Hosts the single manager-update dialog; `useOpenManagerUpdate()` opens it (header pill, callouts, settings). */
export function ManagerUpdateProvider({ children }: { children: ReactNode }) {
  const [open, setOpen] = useState(false);
  const openDialog = useCallback(() => setOpen(true), []);
  return (
    <OpenContext.Provider value={openDialog}>
      {children}
      <ManagerUpdateDialog open={open} onClose={() => setOpen(false)} />
    </OpenContext.Provider>
  );
}

export function useOpenManagerUpdate(): () => void {
  const open = useContext(OpenContext);
  if (!open) throw new Error('useOpenManagerUpdate needs <ManagerUpdateProvider>');
  return open;
}

// ---------------------------------------------------------------- helpers

export const vText = (v: string) => `v${v.replace(/^v/, '')}`;

export function managerAssets(release: ReleaseInfo | undefined): { msi?: ReleaseAsset; zip?: ReleaseAsset } {
  const assets = (release?.assets ?? []).filter((a) => safeHttpUrl(a.downloadUrl));
  return {
    msi: assets.find((a) => /\.msi$/i.test(a.name)),
    zip: assets.find((a) => /\.zip$/i.test(a.name)),
  };
}

function title(info: ManagerUpdateInfo | undefined, loading: boolean): string {
  if (loading || !info) return 'Caddy Proxy Manager updates';
  if (info.updateAvailable && info.latest) return `Caddy Proxy Manager ${vText(info.latest.version)} is available`;
  if (!info.enabled) return 'Update check is off';
  if (info.error) return 'Could not check for updates';
  return 'You’re up to date';
}

// ---------------------------------------------------------------- dialog

export function ManagerUpdateDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const q = useManagerUpdate(open);
  const info = q.data;
  return (
    <Dialog
      open={open}
      onClose={onClose}
      size="lg"
      title={title(info, q.isPending)}
      footer={<Footer info={info} />}
      bodyClassName="flex flex-col gap-4"
    >
      {q.isPending ? (
        <div className="flex items-center gap-2 py-6 text-sm text-fg-subtle">
          <Spinner size={14} /> Checking for new versions…
        </div>
      ) : !info ? (
        <Callout tone="danger" title="Could not load the update status">
          {errorMessage(q.error)}
        </Callout>
      ) : (
        <Body info={info} onClose={onClose} />
      )}
    </Dialog>
  );
}

function Body({ info, onClose }: { info: ManagerUpdateInfo; onClose: () => void }) {
  const now = useNow(60_000);
  const latest = info.latest;
  const errorCallout = info.error && (
    <Callout tone="danger" title="The last check for updates failed">
      <span className="break-words">{info.error}</span>
    </Callout>
  );

  if (!info.enabled)
    return (
      <p className="text-sm text-fg-muted">
        Checking GitHub for new versions of Caddy Proxy Manager is switched off. Installed:{' '}
        <span className="mono text-fg">{vText(info.currentVersion)}</span>. Turn it on in{' '}
        <Link to="/settings?tab=updates" onClick={onClose} className="text-accent-text hover:underline">
          Settings › Updates
        </Link>
        .
      </p>
    );

  if (!info.updateAvailable || !latest)
    return (
      <>
        {errorCallout}
        <p className="text-sm text-fg-muted">
          Installed: <span className="mono text-fg">{vText(info.currentVersion)}</span>
          {!info.error && latest && <> — the newest release on GitHub is {vText(latest.version)}.</>}
          {!info.error && !latest && <> — no releases were found on GitHub.</>}
        </p>
        {(latest?.url || info.releasesUrl) && (
          <p>
            <ExternalAnchor href={latest?.url ?? info.releasesUrl}>{latest ? `${vText(latest.version)} on GitHub` : 'Releases on GitHub'}</ExternalAnchor>
          </p>
        )}
      </>
    );

  const { msi, zip } = managerAssets(latest);
  const githubUrl = safeHttpUrl(latest.url);
  return (
    <>
      {errorCallout}
      <div className="flex flex-wrap items-center gap-x-4 gap-y-2">
        <div className="flex items-center gap-2 text-sm">
          <span className="text-fg-subtle">Installed</span>
          <span className="mono text-fg" data-testid="installed-version">
            {vText(info.currentVersion)}
          </span>
          <ArrowRight size={14} className="text-fg-subtle" aria-label="to" />
          <span data-testid="latest-version">
            <Badge tone="accent" mono>
              {vText(latest.version)}
            </Badge>
          </span>
        </div>
        {latest.publishedAt && (
          <span className="text-sm text-fg-subtle" title={formatDateTime(latest.publishedAt)}>
            Published {formatRelative(latest.publishedAt, now)} · {formatDateTime(latest.publishedAt)}
          </span>
        )}
      </div>

      <div className="flex flex-wrap items-center gap-2">
        {msi ? (
          <a href={msi.downloadUrl} className={buttonClasses({ variant: 'primary' })} data-testid="download-msi">
            <Download size={14} aria-hidden />
            Download installer (.msi)
            <span className="font-normal opacity-80">· {formatBytes(msi.size)}</span>
          </a>
        ) : (
          githubUrl && (
            <a href={githubUrl} target="_blank" rel="noopener noreferrer" className={buttonClasses({ variant: 'primary' })}>
              <Download size={14} aria-hidden />
              Download from GitHub
            </a>
          )
        )}
        {zip && (
          <a href={zip.downloadUrl} className={buttonClasses({ variant: 'secondary' })} data-testid="download-zip">
            <Download size={14} aria-hidden />
            Download .zip
            <span className="font-normal text-fg-subtle">· {formatBytes(zip.size)}</span>
          </a>
        )}
        {githubUrl && (
          <a href={githubUrl} target="_blank" rel="noopener noreferrer" className={buttonClasses({ variant: 'ghost' })} data-testid="view-on-github">
            View on GitHub <ExternalLink size={13} aria-hidden />
          </a>
        )}
      </div>

      <div className="rounded-md border border-border bg-surface-2/60 px-3.5 py-3 text-sm text-fg-muted">
        <p>
          <span className="font-medium text-fg">How to upgrade:</span> run the MSI on this server; settings, hosts and certificates are kept; Caddy keeps
          serving while the manager restarts.
        </p>
        {msi?.sha256 && (
          <div className="mt-2.5 flex items-center gap-2">
            <span className="shrink-0 text-xs text-fg-subtle">MSI SHA-256</span>
            <code className="mono min-w-0 flex-1 truncate text-xs text-fg" title={msi.sha256} data-testid="msi-sha256">
              {msi.sha256}
            </code>
            <CopyButton text={msi.sha256} iconOnly size="xs" variant="ghost" label="Copy SHA-256" />
          </div>
        )}
      </div>

      <section aria-labelledby="mu-changes">
        <h3 id="mu-changes" className="mb-2 text-sm font-semibold text-fg">
          What’s changed since {vText(info.currentVersion)}
        </h3>
        <div className="flex flex-col gap-2">
          {info.newerReleases.map((r, i) => (
            <ReleaseEntry key={r.version} release={r} defaultOpen={i === 0} now={now} />
          ))}
        </div>
      </section>
    </>
  );
}

function ReleaseEntry({ release, defaultOpen, now }: { release: ReleaseInfo; defaultOpen: boolean; now: number }) {
  // The notes of collapsed releases are only built when first opened.
  const [seen, setSeen] = useState(defaultOpen);
  const url = safeHttpUrl(release.url);
  return (
    <details
      open={defaultOpen}
      onToggle={(e) => {
        if ((e.currentTarget as HTMLDetailsElement).open) setSeen(true);
      }}
      className="group rounded-md border border-border bg-surface"
      data-release={release.version}
    >
      <summary className="flex cursor-pointer items-center gap-2 rounded-md px-3 py-2 select-none hover:bg-surface-2 [&::-webkit-details-marker]:hidden">
        <ChevronRight size={14} className="shrink-0 text-fg-subtle transition-transform group-open:rotate-90" aria-hidden />
        <span className="min-w-0 flex-1 truncate text-sm font-medium text-fg">{release.name?.trim() || vText(release.version)}</span>
        {release.publishedAt && (
          <span className="shrink-0 text-xs text-fg-subtle" title={formatDateTime(release.publishedAt)}>
            {formatRelative(release.publishedAt, now)}
          </span>
        )}
      </summary>
      {seen && (
        <div className="border-t border-border px-3.5 py-3">
          <div data-release-notes={release.version}>
            <ReleaseNotes release={release} />
          </div>
          {url && (
            <p className="mt-3 text-xs">
              <ExternalAnchor href={url}>{vText(release.version)} on GitHub</ExternalAnchor>
            </p>
          )}
        </div>
      )}
    </details>
  );
}

function Footer({ info }: { info: ManagerUpdateInfo | undefined }) {
  const { canOperate } = useAuth();
  const check = useCheckManagerUpdate();
  const now = useNow(30_000);
  return (
    <>
      <p className="mr-auto min-w-0 text-xs text-fg-subtle">
        {info?.checkedAt && (
          <span title={formatDateTime(info.checkedAt)}>Checked {formatRelative(info.checkedAt, now)}</span>
        )}
        {info?.checkedAt && info.repo && ' · '}
        {info?.repo && (
          <>
            repo{' '}
            {info.releasesUrl && safeHttpUrl(info.releasesUrl) ? (
              <a href={safeHttpUrl(info.releasesUrl)!} target="_blank" rel="noopener noreferrer" className="mono hover:underline">
                {info.repo}
              </a>
            ) : (
              <span className="mono">{info.repo}</span>
            )}
            {info.repoIsDefault && ' (official)'}
          </>
        )}
        {check.isError && <span className="block text-danger">{errorMessage(check.error)}</span>}
      </p>
      {canOperate && info?.enabled !== false && (
        <Button size="sm" icon={<RefreshCw size={13} />} loading={check.isPending} onClick={() => check.mutate()}>
          Check now
        </Button>
      )}
    </>
  );
}

function ExternalAnchor({ href, children }: { href: string | undefined; children: ReactNode }) {
  const safe = safeHttpUrl(href);
  if (!safe) return <>{children}</>;
  return (
    <a href={safe} target="_blank" rel="noopener noreferrer" className="inline-flex items-center gap-1 text-sm text-accent-text hover:underline">
      {children} <ExternalLink size={12} aria-hidden />
    </a>
  );
}

// ---------------------------------------------------------------- header pill

/** Top-bar notice for every signed-in user while a newer manager release exists. */
export function ManagerUpdatePill() {
  const q = useManagerUpdate();
  const open = useOpenManagerUpdate();
  const version = useMemo(() => (q.data?.updateAvailable ? q.data.latest?.version : undefined), [q.data]);
  if (!version) return null;
  return (
    <button
      type="button"
      onClick={open}
      data-testid="manager-update-pill"
      aria-label={`Caddy Proxy Manager ${vText(version)} is available`}
      className={cn(
        'inline-flex h-7 shrink-0 items-center gap-1.5 rounded-full border border-accent/40 bg-accent-soft px-2.5 text-sm font-medium text-accent-text shadow-xs',
        'hover:border-accent/70 focus-visible:outline-2 focus-visible:outline-ring',
      )}
    >
      <ArrowUpCircle size={14} aria-hidden />
      <span className="hidden sm:inline">Update available</span>
      <span className="mono text-xs">{vText(version)}</span>
    </button>
  );
}
