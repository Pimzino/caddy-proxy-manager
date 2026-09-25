import { useState } from 'react';
import { useSearchParams } from 'react-router';
import { Check, ExternalLink, Hammer, Package, Plus, Puzzle, Save, Trash2, Undo2 } from 'lucide-react';
import { errorMessage } from '@/api/client';
import { useBinaryOverview, useInstallBinary, usePluginCatalog, useSavePlugins } from '@/api/hooks';
import type { BinaryOverview } from '@/api/types';
import { useAuth } from '@/auth';
import { useFeedback } from '@/components/feedback';
import { JobDialog } from '@/components/JobDialog';
import {
  Badge,
  Button,
  Callout,
  Card,
  CardHeader,
  EmptyState,
  Input,
  LoadingBlock,
  PageHeader,
  SearchInput,
  Spinner,
  useConfirm,
  useToast,
} from '@/components/ui';
import { formatCompact } from '@/lib/format';
import { useDebounced } from '@/lib/useDebounced';

const PACKAGE_RE = /^[a-z0-9.-]+\.[a-z]{2,}(\/[A-Za-z0-9._~-]+)+$/;

export default function PluginsPage() {
  const binary = useBinaryOverview();
  const [jobId, setJobId] = useState<string | null>(null);
  if (binary.isPending) return <LoadingBlock />;
  if (binary.isError)
    return (
      <Callout tone="danger" title="Could not load plugin information">
        {errorMessage(binary.error)}
      </Callout>
    );
  // Keyed so the editable draft resets when the server-side desired list changes.
  return (
    <>
      <PluginsEditor key={binary.data.desiredPlugins.join('|')} overview={binary.data} onJob={setJobId} />
      <JobDialog jobId={jobId} onClose={() => setJobId(null)} />
    </>
  );
}

function PluginsEditor({ overview, onJob }: { overview: BinaryOverview; onJob: (id: string) => void }) {
  const { isAdmin } = useAuth();
  const [desired, setDesired] = useState<string[]>(overview.desiredPlugins);
  const [manual, setManual] = useState('');
  const [manualError, setManualError] = useState<string | null>(null);
  const [params] = useSearchParams();
  // "?q=ntlm" pre-fills the catalog search (links from the host editor and settings).
  const [q, setQ] = useState(() => params.get('q') ?? '');
  const debounced = useDebounced(q.trim(), 350);
  const catalog = usePluginCatalog(debounced);
  const save = useSavePlugins();
  const install = useInstallBinary();
  const feedback = useFeedback();
  const confirm = useConfirm();
  const toast = useToast();

  const installed = new Set(overview.installed?.plugins ?? []);
  const dirty = desired.join('|') !== overview.desiredPlugins.join('|');
  const add = (p: string) => setDesired((d) => (d.includes(p) ? d : [...d, p]));
  const remove = (p: string) => setDesired((d) => d.filter((x) => x !== p));

  const addManual = () => {
    const p = manual.trim().replace(/^https?:\/\//, '').replace(/\/$/, '');
    if (!PACKAGE_RE.test(p)) {
      setManualError('Enter a Go package path such as github.com/mholt/caddy-l4.');
      return;
    }
    add(p);
    setManual('');
    setManualError(null);
  };

  const saveList = async () => {
    try {
      await save.mutateAsync(desired);
      toast.success('Plugin list saved', desired.length ? 'Rebuild Caddy to include the changes.' : undefined);
    } catch (err) {
      feedback.failed(err, { title: 'Could not save the plugin list' });
    }
  };

  const rebuild = async () => {
    const ok = await confirm({
      title: 'Rebuild and install Caddy?',
      message: (
        <>
          A custom Caddy build with {desired.length === 0 ? 'no extra plugins (the official release)' : `${desired.length} plugin${desired.length === 1 ? '' : 's'}`} is
          downloaded{desired.length ? ' from caddyserver.com (no checksum is published for custom builds; the binary is verified by running it)' : ''}, validated
          against the current configuration and swapped in. Caddy restarts briefly; the previous binary is restored if it fails.
        </>
      ),
      confirmLabel: 'Rebuild & install',
    });
    if (!ok) return;
    // mutateAsync: the save below re-keys this component, so continue outside its lifecycle.
    try {
      if (dirty) await save.mutateAsync(desired);
      const job = await install.mutateAsync(undefined);
      onJob(job.id);
    } catch (err) {
      feedback.failed(err, { title: 'Rebuild failed to start' });
    }
  };

  return (
    <>
      <PageHeader
        title="Plugins"
        description="Extend Caddy with modules from the official package registry, e.g. layer4 streams or DNS providers for wildcard certificates."
        actions={
          isAdmin && (
            <>
              {dirty && (
                <Button icon={<Undo2 size={14} />} onClick={() => setDesired(overview.desiredPlugins)}>
                  Revert
                </Button>
              )}
              <Button icon={<Save size={14} />} disabled={!dirty} loading={save.isPending} onClick={() => void saveList()}>
                Save list
              </Button>
              <Button variant="primary" icon={<Hammer size={14} />} loading={install.isPending} onClick={() => void rebuild()}>
                Rebuild &amp; install
              </Button>
            </>
          )
        }
      />
      {overview.pluginsOutOfSync && !dirty && (
        <Callout tone="warning" className="mb-4" title="The installed binary does not match the desired plugins">
          Choose “Rebuild &amp; install” to download a matching Caddy build.
        </Callout>
      )}
      <div className="grid grid-cols-1 gap-4 xl:grid-cols-[minmax(0,5fr)_minmax(0,7fr)]">
        <Card className="self-start">
          <CardHeader
            icon={<Puzzle size={16} />}
            title="Desired plugins"
            description={`${desired.length} selected · installed build: ${overview.installed?.version ?? 'none'}`}
          />
          {desired.length === 0 ? (
            <EmptyState title="Standard Caddy build" description="No extra plugins. Add packages from the catalog to build a custom Caddy." />
          ) : (
            <ul className="divide-y divide-border">
              {desired.map((p) => (
                <li key={p} className="flex items-center gap-3 px-4 py-2.5">
                  <Package size={14} className="shrink-0 text-fg-subtle" aria-hidden />
                  <span className="mono min-w-0 flex-1 truncate text-sm" title={p}>
                    {p}
                  </span>
                  {installed.has(p) ? (
                    <Badge tone="success" icon={<Check size={11} />}>
                      Installed
                    </Badge>
                  ) : (
                    <Badge tone="warning">Pending rebuild</Badge>
                  )}
                  {isAdmin && (
                    <Button variant="ghost" size="sm" iconOnly aria-label={`Remove ${p}`} icon={<Trash2 size={14} />} onClick={() => remove(p)} />
                  )}
                </li>
              ))}
            </ul>
          )}
          {[...installed].filter((p) => !desired.includes(p)).length > 0 && (
            <div className="border-t border-border px-4 py-3 text-xs text-fg-subtle">
              Will be removed on rebuild:{' '}
              <span className="mono">{[...installed].filter((p) => !desired.includes(p)).join(', ')}</span>
            </div>
          )}
          {isAdmin && (
            <div className="border-t border-border px-4 py-3">
              <div className="flex gap-2">
                <Input
                  mono
                  aria-label="Package path"
                  placeholder="github.com/owner/caddy-module"
                  value={manual}
                  invalid={!!manualError}
                  onChange={(e) => {
                    setManual(e.target.value);
                    setManualError(null);
                  }}
                  onKeyDown={(e) => {
                    if (e.key === 'Enter') {
                      e.preventDefault();
                      addManual();
                    }
                  }}
                />
                <Button icon={<Plus size={14} />} onClick={addManual}>
                  Add
                </Button>
              </div>
              {manualError && <p className="mt-1 text-xs text-danger">{manualError}</p>}
            </div>
          )}
        </Card>

        <Card>
          <CardHeader
            title="Package catalog"
            description="From caddyserver.com (cached). Sorted by downloads."
            actions={catalog.isFetching && <Spinner size={14} label="Searching" />}
          />
          <div className="border-b border-border px-4 py-2.5">
            <SearchInput value={q} onChange={setQ} placeholder="Search packages, e.g. dns, l4, ratelimit…" className="sm:w-full" />
          </div>
          {catalog.isPending ? (
            <LoadingBlock />
          ) : catalog.isError ? (
            <div className="p-4">
              <Callout tone="danger" title="Catalog unavailable">
                {errorMessage(catalog.error)} The server may not have outbound Internet access — you can still add a package path manually.
              </Callout>
            </div>
          ) : catalog.data.length === 0 ? (
            <EmptyState title="No packages found" description={debounced ? `Nothing matches “${debounced}”.` : undefined} />
          ) : (
            <ul className="max-h-[640px] divide-y divide-border overflow-y-auto">
              {catalog.data.map((pkg) => {
                const selected = desired.includes(pkg.path);
                return (
                  <li key={pkg.path} className="flex items-start gap-3 px-4 py-3">
                    <div className="min-w-0 flex-1">
                      <div className="flex flex-wrap items-center gap-2">
                        <span className="mono truncate text-sm font-medium text-fg">{pkg.path}</span>
                        <span className="text-xs text-fg-subtle">{formatCompact(pkg.downloads)} downloads</span>
                        {pkg.repo && (
                          <a
                            href={pkg.repo}
                            target="_blank"
                            rel="noopener noreferrer"
                            className="inline-flex items-center gap-0.5 text-xs text-accent-text hover:underline"
                          >
                            Repository <ExternalLink size={11} aria-hidden />
                          </a>
                        )}
                      </div>
                      {pkg.modules.length > 0 && (
                        <p className="mono mt-1 truncate text-xs text-fg-subtle" title={pkg.modules.join('\n')}>
                          {pkg.modules.slice(0, 4).join(' · ')}
                          {pkg.modules.length > 4 ? ` · +${pkg.modules.length - 4}` : ''}
                        </p>
                      )}
                    </div>
                    {isAdmin &&
                      (selected ? (
                        <Button size="sm" variant="ghost" icon={<Check size={14} />} onClick={() => remove(pkg.path)} aria-label={`Remove ${pkg.path}`}>
                          Added
                        </Button>
                      ) : (
                        <Button size="sm" icon={<Plus size={14} />} onClick={() => add(pkg.path)} aria-label={`Add ${pkg.path}`}>
                          Add
                        </Button>
                      ))}
                  </li>
                );
              })}
            </ul>
          )}
        </Card>
      </div>
    </>
  );
}
