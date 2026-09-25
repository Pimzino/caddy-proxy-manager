import { useId, useMemo, useState } from 'react';
import { CheckCircle2, FileCheck2, Play, Save, XCircle } from 'lucide-react';
import { ApiError, errorMessage } from '@/api/client';
import {
  useAdaptCaddyfile,
  useApplyConfig,
  useCaddySettings,
  useConfigPreview,
  useRevision,
  useRevisions,
  useRunningConfig,
  useSaveCaddySettings,
} from '@/api/hooks';
import { caddySettingsInput } from '@/api/settings';
import type { AdaptResult, CaddySettings, ConfigMode } from '@/api/types';
import { useAuth } from '@/auth';
import { useFeedback } from '@/components/feedback';
import {
  Badge,
  Button,
  Callout,
  Card,
  CodeBlock,
  DescriptionList,
  Dialog,
  EmptyState,
  LoadingBlock,
  PageHeader,
  RadioCards,
  TabPanel,
  Tabs,
  Table,
  TableSkeleton,
  TBody,
  TD,
  TH,
  THead,
  TR,
  Textarea,
  useConfirm,
} from '@/components/ui';
import { formatDateTime, formatRelative } from '@/lib/format';
import { useNow } from '@/lib/useNow';

type Tab = 'generated' | 'running' | 'revisions' | 'caddyfile';

function normalizeJson(s: string | undefined): string | null {
  if (!s) return null;
  try {
    return JSON.stringify(JSON.parse(s));
  } catch {
    return null;
  }
}

function prettyJson(s: string): string {
  try {
    return JSON.stringify(JSON.parse(s), null, 2);
  } catch {
    return s;
  }
}

export default function ConfigPage() {
  const idBase = useId();
  const { canOperate } = useAuth();
  const [tab, setTab] = useState<Tab>('generated');
  const settings = useCaddySettings();
  const apply = useApplyConfig();
  const feedback = useFeedback();
  const mode = settings.data?.mode;

  return (
    <>
      <PageHeader
        title="Configuration"
        description="The Caddy JSON generated from your hosts, what Caddy is running now, and the history of applied revisions."
        actions={
          <>
            {mode && (
              <Badge tone={mode === 'managed' ? 'accent' : 'warning'}>{mode === 'managed' ? 'Managed mode' : 'Caddyfile mode'}</Badge>
            )}
            {canOperate && (
              <Button
                variant="primary"
                icon={<Play size={14} />}
                loading={apply.isPending}
                onClick={() =>
                  apply.mutate(undefined, {
                    onSuccess: (r) => feedback.applied(r, 'Configuration applied'),
                    onError: (err) => feedback.failed(err, { title: 'Apply failed' }),
                  })
                }
              >
                Apply now
              </Button>
            )}
          </>
        }
      />
      <Tabs
        idBase={idBase}
        aria-label="Configuration views"
        value={tab}
        onChange={setTab}
        className="mb-4"
        items={[
          { value: 'generated', label: mode === 'caddyfile' ? 'Adapted config' : 'Generated config' },
          { value: 'running', label: 'Running config' },
          { value: 'revisions', label: 'Revisions' },
          { value: 'caddyfile', label: 'Caddyfile mode' },
        ]}
      />
      <TabPanel idBase={idBase} value="generated" active={tab === 'generated'}>
        <GeneratedView />
      </TabPanel>
      <TabPanel idBase={idBase} value="running" active={tab === 'running'}>
        <RunningView />
      </TabPanel>
      <TabPanel idBase={idBase} value="revisions" active={tab === 'revisions'}>
        <RevisionsView />
      </TabPanel>
      <TabPanel idBase={idBase} value="caddyfile" active={tab === 'caddyfile'}>
        {settings.isPending ? (
          <LoadingBlock />
        ) : settings.isError ? (
          <Callout tone="danger" title="Could not load Caddy settings">
            {errorMessage(settings.error)}
          </Callout>
        ) : (
          <CaddyfileView key={`${settings.data.mode}:${settings.data.rawCaddyfile}`} settings={settings.data} />
        )}
      </TabPanel>
    </>
  );
}

function GeneratedView() {
  const preview = useConfigPreview();
  if (preview.isPending) return <LoadingBlock />;
  if (preview.isError)
    return (
      <Callout tone="danger" title="Could not generate the configuration">
        {errorMessage(preview.error)}
      </Callout>
    );
  return (
    <CodeBlock
      code={prettyJson(preview.data.json)}
      language="json"
      title="caddy.json — generated from the manager database"
      filename="caddy.generated.json"
      maxHeight={680}
    />
  );
}

function RunningView() {
  const running = useRunningConfig();
  const preview = useConfigPreview();
  if (running.isPending) return <LoadingBlock />;
  if (running.isError) {
    const unreachable = running.error instanceof ApiError && running.error.status === 503;
    return (
      <Callout tone={unreachable ? 'warning' : 'danger'} title={unreachable ? 'Caddy’s admin API is not reachable' : 'Could not read the running configuration'}>
        {unreachable
          ? 'Caddy is stopped, not installed, or its admin endpoint is not listening. Start it from Service & Updates.'
          : errorMessage(running.error)}
      </Callout>
    );
  }
  const same = normalizeJson(running.data.json) === normalizeJson(preview.data?.json);
  return (
    <div className="flex flex-col gap-3">
      {preview.data &&
        (same ? (
          <Callout tone="success">Caddy is running exactly the configuration generated by the manager.</Callout>
        ) : (
          <Callout tone="warning" title="Running configuration differs from the generated one">
            Changes may not have been applied yet (for example while Caddy was stopped), or the config was changed through the admin API
            directly. Use “Apply now” to load the generated configuration.
          </Callout>
        ))}
      <CodeBlock code={prettyJson(running.data.json)} language="json" title="Running configuration (from Caddy admin API)" filename="caddy.running.json" maxHeight={680} />
    </div>
  );
}

function RevisionsView() {
  const revisions = useRevisions(50);
  const [selected, setSelected] = useState<string | null>(null);
  const now = useNow(60_000);
  return (
    <Card>
      {revisions.isPending ? (
        <TableSkeleton rows={6} cols={5} />
      ) : revisions.isError ? (
        <div className="p-4">
          <Callout tone="danger" title="Could not load revisions">
            {errorMessage(revisions.error)}
          </Callout>
        </div>
      ) : revisions.data.length === 0 ? (
        <EmptyState icon={<FileCheck2 size={18} />} title="No revisions yet" description="Every apply is recorded here with its result." />
      ) : (
        <Table>
          <THead>
            <tr>
              <TH>Applied</TH>
              <TH>Result</TH>
              <TH>Reason</TH>
              <TH>By</TH>
              <TH>Hash</TH>
            </tr>
          </THead>
          <TBody>
            {revisions.data.map((r) => (
              <TR key={r.id} interactive onClick={() => setSelected(r.id)}>
                <TD className="whitespace-nowrap">
                  <span title={formatDateTime(r.createdAt)}>{formatRelative(r.createdAt, now)}</span>
                </TD>
                <TD>
                  {r.success ? (
                    <Badge tone="success" icon={<CheckCircle2 size={11} />}>
                      Applied
                    </Badge>
                  ) : (
                    <Badge tone="danger" icon={<XCircle size={11} />}>
                      Rejected
                    </Badge>
                  )}
                </TD>
                <TD className="max-w-[420px]">
                  <p className="truncate">{r.reason}</p>
                  {r.error && <p className="truncate text-xs text-danger">{r.error}</p>}
                </TD>
                <TD className="text-fg-muted">{r.appliedBy}</TD>
                <TD className="mono text-xs text-fg-subtle">{r.hash.slice(0, 12)}</TD>
              </TR>
            ))}
          </TBody>
        </Table>
      )}
      <RevisionDrawer id={selected} onClose={() => setSelected(null)} />
    </Card>
  );
}

function RevisionDrawer({ id, onClose }: { id: string | null; onClose: () => void }) {
  const rev = useRevision(id);
  return (
    <Dialog open={!!id} onClose={onClose} side="right" size="xl" title="Configuration revision" description={rev.data ? formatDateTime(rev.data.createdAt) : undefined}>
      {rev.isPending ? (
        <LoadingBlock />
      ) : rev.isError ? (
        <Callout tone="danger">{errorMessage(rev.error)}</Callout>
      ) : (
        <div className="flex flex-col gap-4">
          <DescriptionList
            items={[
              { label: 'Result', value: rev.data.success ? <Badge tone="success">Applied</Badge> : <Badge tone="danger">Rejected</Badge> },
              { label: 'Reason', value: rev.data.reason },
              { label: 'Applied by', value: rev.data.appliedBy },
              { label: 'Hash', value: rev.data.hash, mono: true },
            ]}
          />
          {rev.data.error && <CodeBlock code={rev.data.error} title="Caddy error" wrap maxHeight={200} />}
          <CodeBlock code={prettyJson(rev.data.json)} language="json" title="Configuration" filename={`caddy.${rev.data.id}.json`} maxHeight="none" />
        </div>
      )}
    </Dialog>
  );
}

function CaddyfileView({ settings }: { settings: CaddySettings }) {
  const { isAdmin } = useAuth();
  const [text, setText] = useState(settings.rawCaddyfile);
  const [mode, setMode] = useState<ConfigMode>(settings.mode);
  const [result, setResult] = useState<AdaptResult | null>(null);
  const [adaptError, setAdaptError] = useState<string | null>(null);
  const adapt = useAdaptCaddyfile();
  const save = useSaveCaddySettings();
  const feedback = useFeedback();
  const confirm = useConfirm();
  const dirty = text !== settings.rawCaddyfile || mode !== settings.mode;
  const lineCount = useMemo(() => text.split('\n').length, [text]);

  const runAdapt = () => {
    setAdaptError(null);
    setResult(null);
    adapt.mutate(text, {
      onSuccess: setResult,
      onError: (err) => {
        if (err instanceof ApiError && (err.status === 422 || err.status === 400)) setAdaptError(err.detail ?? err.title);
        else feedback.failed(err, { title: 'Could not adapt the Caddyfile' });
      },
    });
  };

  const persist = async () => {
    if (mode === 'caddyfile' && settings.mode !== 'caddyfile') {
      const ok = await confirm({
        title: 'Switch to Caddyfile mode?',
        message:
          'Caddy will run only this Caddyfile. Proxy hosts, redirects, static sites, streams and access lists configured in the manager are ignored until you switch back to Managed mode.',
        confirmLabel: 'Switch and apply',
        danger: true,
      });
      if (!ok) return;
    }
    if (mode === 'caddyfile' && !text.trim()) {
      setAdaptError('The Caddyfile is empty. Caddy would serve nothing.');
      return;
    }
    save.mutate(
      { ...caddySettingsInput(settings), rawCaddyfile: text, mode },
      {
        onSuccess: (res) => feedback.applied(res.apply, mode === 'caddyfile' ? 'Caddyfile saved and applied' : 'Saved and applied'),
        onError: (err) => feedback.failed(err, { title: 'Could not save' }),
      },
    );
  };

  return (
    <div className="flex flex-col gap-4">
      <RadioCards
        aria-label="Configuration mode"
        value={mode}
        onChange={setMode}
        disabled={!isAdmin}
        options={[
          { value: 'managed', label: 'Managed (recommended)', description: 'Caddy config is generated from the hosts, certificates and settings in this console.' },
          { value: 'caddyfile', label: 'Caddyfile', description: 'Escape hatch: Caddy runs a hand-written Caddyfile. Managed objects are ignored.' },
        ]}
      />
      <Card>
        <div className="flex flex-wrap items-center gap-2 border-b border-border px-4 py-2.5">
          <span className="text-sm font-medium text-fg">Caddyfile</span>
          <span className="text-xs text-fg-subtle">{lineCount} lines</span>
          <div className="flex-1" />
          {isAdmin && (
            <>
              <Button size="sm" icon={<FileCheck2 size={13} />} onClick={runAdapt} loading={adapt.isPending} disabled={!text.trim()}>
                Adapt &amp; validate
              </Button>
              <Button size="sm" variant="primary" icon={<Save size={13} />} onClick={() => void persist()} loading={save.isPending} disabled={!dirty}>
                {mode === 'caddyfile' ? 'Save & apply' : 'Save'}
              </Button>
            </>
          )}
        </div>
        <Textarea
          aria-label="Caddyfile"
          mono
          spellCheck={false}
          readOnly={!isAdmin}
          value={text}
          onChange={(e) => {
            setText(e.target.value);
            setResult(null);
            setAdaptError(null);
          }}
          onKeyDown={(e) => {
            if (e.key === 'Tab' && !e.shiftKey && isAdmin) {
              e.preventDefault();
              const el = e.currentTarget;
              const { selectionStart, selectionEnd } = el;
              const next = text.slice(0, selectionStart) + '\t' + text.slice(selectionEnd);
              setText(next);
              requestAnimationFrame(() => el.setSelectionRange(selectionStart + 1, selectionStart + 1));
            }
          }}
          placeholder={'example.com {\n\treverse_proxy 10.0.0.20:8080\n}'}
          className="min-h-[420px] rounded-none border-0 shadow-none focus:ring-0"
          style={{ tabSize: 4 }}
        />
      </Card>
      {mode === 'managed' && settings.mode === 'managed' && (
        <p className="text-xs text-fg-subtle">The Caddyfile is stored but not used while Managed mode is active. Tab inserts a tab; Shift+Tab leaves the editor.</p>
      )}
      {adaptError && (
        <div>
          <Callout tone="danger" title="Caddy could not adapt this Caddyfile" className="mb-2" />
          <CodeBlock code={adaptError} title="Error" wrap maxHeight={240} />
        </div>
      )}
      {result && (
        <div className="flex flex-col gap-2">
          <Callout tone={result.warnings.length ? 'warning' : 'success'} title={result.warnings.length ? 'Adapted with warnings' : 'The Caddyfile is valid'}>
            {result.warnings.length > 0 && (
              <ul className="list-disc pl-4">
                {result.warnings.map((w, i) => (
                  <li key={i}>{w}</li>
                ))}
              </ul>
            )}
          </Callout>
          <CodeBlock code={prettyJson(result.json)} language="json" title="Adapted JSON" filename="caddy.adapted.json" maxHeight={480} />
        </div>
      )}
    </div>
  );
}
