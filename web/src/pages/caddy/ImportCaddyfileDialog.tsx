import { useId, useMemo, useState } from 'react';
import { ArrowLeft, FileInput as FileInputIcon, Import } from 'lucide-react';
import { ApiError } from '@/api/client';
import { useCommitCaddyfileImport, useHosts, useImportCaddyfile } from '@/api/hooks';
import type { CaddyfileImportResult, SiteHost, SiteHostFields } from '@/api/types';
import { useFeedback } from '@/components/feedback';
import { Badge, Button, Callout, Chip, CodeBlock, Dialog, Field, Table, TBody, TD, TH, THead, TR, Textarea, type Tone } from '@/components/ui';
import { pluralize, upstreamUrl } from '@/lib/format';
import { serverFieldErrors } from '@/lib/validation';
import { kindMeta } from '../hosts/hostModel';

const KIND_TONE: Record<SiteHostFields['kind'], Tone> = { proxy: 'accent', redirect: 'info', static: 'neutral', response: 'warning' };
const TLS_LABEL: Record<SiteHostFields['tls'], string> = { acme: 'ACME', internal: 'Internal', custom: 'Custom', none: 'HTTP only' };

/** One line per upstream / target. */
function targetOf(d: SiteHostFields): string[] {
  switch (d.kind) {
    case 'proxy':
      return (d.upstreams ?? []).length ? d.upstreams.map(upstreamUrl) : ['—'];
    case 'redirect':
      return [`${d.redirectCode} → ${d.redirectTarget ?? '—'}${d.preservePath ? ' (+ path)' : ''}`];
    case 'static':
      return [d.rootPath ?? '—'];
    case 'response':
      return [`${d.responseStatus} ${d.responseContentType}`];
  }
}

/** Domains of the draft that are already served by another enabled host. */
function conflictsOf(d: SiteHostFields, hosts: SiteHost[] | undefined): { domain: string; host: string }[] {
  const out: { domain: string; host: string }[] = [];
  for (const domain of d.domains ?? []) {
    const other = hosts?.find((h) => h.enabled && h.domains.some((x) => x.toLowerCase() === domain.toLowerCase()));
    if (other) out.push({ domain, host: other.domains[0] });
  }
  return out;
}

/**
 * Caddyfile → managed hosts: paste, convert (POST /api/config/caddyfile/import, nothing saved),
 * review the drafts, then create the selected ones (POST …/import/commit, transactional).
 */
export function ImportCaddyfileDialog({ open, onClose, initialText }: { open: boolean; onClose: () => void; initialText?: string }) {
  if (!open) return null;
  return <Inner onClose={onClose} initialText={initialText ?? ''} />;
}

function Inner({ onClose, initialText }: { onClose: () => void; initialText: string }) {
  const id = useId();
  const [text, setText] = useState(initialText);
  const [result, setResult] = useState<CaddyfileImportResult | null>(null);
  const [selected, setSelected] = useState<Set<number>>(new Set());
  const [inputError, setInputError] = useState<string | null>(null);
  const [commitError, setCommitError] = useState<{ title: string; items: string[] } | null>(null);
  const convert = useImportCaddyfile();
  const commit = useCommitCaddyfileImport();
  const hosts = useHosts();
  const feedback = useFeedback();
  const pending = convert.isPending || commit.isPending;

  const rows = useMemo(
    () =>
      (result?.drafts ?? []).map((d, i) => {
        const conflicts = conflictsOf(d, hosts.data);
        const warnings = (result?.warnings ?? []).filter((w) => (d.domains ?? []).some((dom) => w.includes(dom)));
        return { d, i, conflicts, warnings };
      }),
    [result, hosts.data],
  );
  const generalWarnings = (result?.warnings ?? []).filter((w) => !rows.some((r) => r.warnings.includes(w)));

  const runConvert = () => {
    setInputError(null);
    if (!text.trim()) {
      setInputError('Paste a Caddyfile first.');
      return;
    }
    convert.mutate(text, {
      onSuccess: (r) => {
        setResult(r);
        setCommitError(null);
        // Pre-select everything that does not collide with an existing enabled host.
        setSelected(new Set(r.drafts.map((d, i) => (conflictsOf(d, hosts.data).length ? -1 : i)).filter((i) => i >= 0)));
      },
      onError: (err) => {
        if (err instanceof ApiError && (err.status === 400 || err.status === 422)) setInputError(err.detail ?? err.title);
        else feedback.failed(err, { title: 'Could not convert the Caddyfile' });
      },
    });
  };

  const runCommit = () => {
    if (!result) return;
    setCommitError(null);
    const chosen = result.drafts.filter((_, i) => selected.has(i));
    commit.mutate(chosen, {
      onSuccess: (r) => {
        feedback.applied(r.apply, `${pluralize(r.created, 'host')} imported and applied`);
        onClose();
      },
      onError: (err) => {
        if (err instanceof ApiError && (err.status === 400 || err.status === 409 || err.status === 403)) {
          const fe = err.errors ? Object.values(serverFieldErrors(err.errors)) : [];
          setCommitError({
            title: err.status === 409 ? 'Nothing was imported: a domain is already in use' : 'Nothing was imported',
            items: fe.length ? fe : [err.detail ?? err.title],
          });
          return;
        }
        feedback.failed(err, { title: 'Import failed' });
      },
    });
  };

  const toggle = (i: number, on: boolean) =>
    setSelected((s) => {
      const next = new Set(s);
      if (on) next.add(i);
      else next.delete(i);
      return next;
    });
  const allSelected = rows.length > 0 && rows.every((r) => selected.has(r.i));

  return (
    <Dialog
      open
      onClose={onClose}
      dismissible={!pending}
      size={result ? 'xl' : 'lg'}
      title="Import a Caddyfile"
      description={
        result
          ? 'Review the hosts that were recognised. Only the selected drafts are created; nothing has been saved yet.'
          : 'Convert site blocks of an existing Caddyfile into managed hosts. Nothing is saved until you confirm the review.'
      }
      footer={
        result ? (
          <>
            <Button icon={<ArrowLeft size={14} />} onClick={() => setResult(null)} disabled={pending} className="mr-auto">
              Back to Caddyfile
            </Button>
            <Button onClick={onClose} disabled={pending}>
              Cancel
            </Button>
            <Button variant="primary" icon={<Import size={14} />} onClick={runCommit} loading={commit.isPending} disabled={selected.size === 0}>
              {selected.size === 0 ? 'Select hosts to import' : `Create ${pluralize(selected.size, 'host')}`}
            </Button>
          </>
        ) : (
          <>
            <Button onClick={onClose} disabled={pending}>
              Cancel
            </Button>
            <Button variant="primary" icon={<FileInputIcon size={14} />} onClick={runConvert} loading={convert.isPending} disabled={!text.trim()}>
              Convert
            </Button>
          </>
        )
      }
    >
      {!result ? (
        <div className="flex flex-col gap-4">
          <Callout tone="info">
            Site blocks with <span className="mono">reverse_proxy</span>, <span className="mono">redir</span>,{' '}
            <span className="mono">file_server</span> and <span className="mono">respond</span> become proxy hosts, redirects, static
            sites and custom responses. Directives the manager cannot represent are listed as unmapped snippets so you can recreate them
            by hand.
          </Callout>
          <Field label="Caddyfile" error={inputError}>
            <Textarea
              id={`${id}-caddyfile`}
              mono
              rows={16}
              spellCheck={false}
              value={text}
              onChange={(e) => {
                setText(e.target.value);
                setInputError(null);
              }}
              placeholder={'app.example.com {\n\treverse_proxy 10.0.10.21:8080\n}\n\nold.example.com {\n\tredir https://www.example.com{uri} permanent\n}'}
              style={{ tabSize: 4 }}
            />
          </Field>
        </div>
      ) : (
        <div className="flex flex-col gap-4">
          {commitError && (
            <Callout tone="danger" title={commitError.title}>
              <ul className="list-disc pl-4">
                {commitError.items.map((m, i) => (
                  <li key={i}>{m}</li>
                ))}
              </ul>
            </Callout>
          )}
          {generalWarnings.length > 0 && (
            <Callout tone="warning" title="Converted with warnings">
              <ul className="list-disc pl-4">
                {generalWarnings.map((w, i) => (
                  <li key={i}>{w}</li>
                ))}
              </ul>
            </Callout>
          )}
          {result.drafts.length === 0 ? (
            <Callout tone="warning" title="No hosts were recognised">
              The Caddyfile contains no site block the manager can represent. See the unmapped snippets below.
            </Callout>
          ) : (
            <div className="overflow-hidden rounded-md border border-border">
              <Table>
                <THead>
                  <tr>
                    <TH className="w-10">
                      <input
                        type="checkbox"
                        aria-label="Select all drafts"
                        checked={allSelected}
                        onChange={(e) => setSelected(e.target.checked ? new Set(rows.map((r) => r.i)) : new Set())}
                        className="h-4 w-4 rounded border-border-strong accent-[var(--accent)] align-middle"
                      />
                    </TH>
                    <TH>Domains</TH>
                    <TH>Type</TH>
                    <TH>Target</TH>
                    <TH>TLS</TH>
                    <TH>Notes</TH>
                  </tr>
                </THead>
                <TBody>
                  {rows.map(({ d, i, conflicts, warnings }) => (
                    <TR key={i} className={selected.has(i) ? undefined : 'opacity-70'}>
                      <TD className="align-top">
                        <input
                          type="checkbox"
                          aria-label={`Import ${d.domains?.[0] ?? `draft ${i + 1}`}`}
                          checked={selected.has(i)}
                          onChange={(e) => toggle(i, e.target.checked)}
                          className="mt-0.5 h-4 w-4 rounded border-border-strong accent-[var(--accent)]"
                        />
                      </TD>
                      <TD className="max-w-[260px] align-top">
                        <div className="flex flex-wrap gap-1">
                          {(d.domains ?? []).map((dom) => (
                            <Chip key={dom}>{dom}</Chip>
                          ))}
                        </div>
                      </TD>
                      <TD className="align-top">
                        <Badge tone={KIND_TONE[d.kind]} className="capitalize">
                          {kindMeta[d.kind].singular}
                        </Badge>
                      </TD>
                      <TD className="mono max-w-[280px] align-top text-xs break-words text-fg-muted">
                        {targetOf(d).map((t, j) => (
                          <div key={j}>{t}</div>
                        ))}
                      </TD>
                      <TD className="align-top">
                        <Badge tone={d.tls === 'none' ? 'neutral' : 'success'}>{TLS_LABEL[d.tls]}</Badge>
                      </TD>
                      <TD className="max-w-[300px] align-top text-xs">
                        {conflicts.map((c) => (
                          <p key={c.domain} className="text-danger">
                            <span className="mono">{c.domain}</span> is already served by the enabled host{' '}
                            <span className="mono">{c.host}</span>.
                          </p>
                        ))}
                        {warnings.map((w, j) => (
                          <p key={j} className="text-warning">
                            {w}
                          </p>
                        ))}
                        {d.notes && <p className="text-fg-subtle">{d.notes}</p>}
                        {!conflicts.length && !warnings.length && !d.notes && <span className="text-fg-subtle">—</span>}
                      </TD>
                    </TR>
                  ))}
                </TBody>
              </Table>
            </div>
          )}
          {result.unmapped.length > 0 && (
            <section className="flex flex-col gap-2">
              <div>
                <h3 className="text-sm font-semibold text-fg">Unmapped snippets ({result.unmapped.length})</h3>
                <p className="text-xs text-fg-subtle">
                  Not imported. Recreate them with host settings, custom routes (Advanced tab) or global settings after the import.
                </p>
              </div>
              {result.unmapped.map((snippet, i) => (
                <CodeBlock key={i} code={snippet} language="caddyfile" title={`Snippet ${i + 1}`} maxHeight={200} />
              ))}
            </section>
          )}
        </div>
      )}
    </Dialog>
  );
}
