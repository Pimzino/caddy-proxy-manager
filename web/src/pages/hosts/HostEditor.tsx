import { useId, useMemo, useState, type FormEvent, type ReactNode } from 'react';
import { Link } from 'react-router';
import { Globe, KeyRound, Lock, LockOpen, Plus, ShieldCheck, Trash2, Wand2 } from 'lucide-react';
import { ApiError } from '@/api/client';
import { useAccessLists, useCaddySettings, useCertificates, useSaveHost } from '@/api/hooks';
import type { HeaderOp, HostKind, ProxyLocation, SiteHost, SiteHostFields } from '@/api/types';
import { useFeedback } from '@/components/feedback';
import {
  Button,
  Callout,
  ChipInput,
  Dialog,
  Field,
  Input,
  KeyValueEditor,
  NumberInput,
  RadioCards,
  Segmented,
  Select,
  SwitchField,
  TabPanel,
  Tabs,
  Textarea,
  useConfirm,
} from '@/components/ui';
import { formatDate } from '@/lib/format';
import { isValidHostname, type FieldErrors } from '@/lib/validation';
import {
  certCovers,
  HEADER_ACTIONS,
  kindMeta,
  LOAD_BALANCING,
  newUpstream,
  REDIRECT_CODES,
  tabOfField,
  toPayload,
  validateHost,
  type HostTab,
} from './hostModel';
import { UpstreamList } from './UpstreamList';

export interface HostEditorProps {
  open: boolean;
  onClose: () => void;
  kind: HostKind;
  /** Existing host being edited. */
  host?: SiteHost;
  /** Initial values for a new host (defaults or a duplicate). */
  initial: SiteHostFields;
  readOnly?: boolean;
}

export function HostEditor(props: HostEditorProps) {
  if (!props.open) return null;
  return <HostEditorInner {...props} />;
}

type HostHeaderMode = 'client' | 'upstream' | 'custom';

function hostHeaderModeOf(v: string | null | undefined): HostHeaderMode {
  if (!v) return 'client';
  return v === '{upstream}' ? 'upstream' : 'custom';
}

function HostEditorInner({ onClose, kind, host, initial, readOnly }: HostEditorProps) {
  const idBase = useId();
  const [form, setForm] = useState<SiteHostFields>(initial);
  const [tab, setTab] = useState<HostTab>('details');
  const [submitted, setSubmitted] = useState(false);
  const [serverErrors, setServerErrors] = useState<FieldErrors>({});
  const [generalError, setGeneralError] = useState<string | null>(null);
  const [hostHeaderMode, setHostHeaderMode] = useState<HostHeaderMode>(hostHeaderModeOf(initial.upstreamHostHeader));
  const save = useSaveHost();
  const feedback = useFeedback();
  const confirm = useConfirm();
  const meta = kindMeta[kind];

  const clientErrors = useMemo(() => (submitted ? validateHost(form) : {}), [form, submitted]);
  const errors: FieldErrors = { ...serverErrors, ...clientErrors };
  const errorCount = (t: HostTab) => Object.keys(errors).filter((k) => tabOfField(k) === t).length;
  const unmapped = Object.entries(serverErrors).filter(([k]) => tabOfField(k) === null);

  const dirty = JSON.stringify(form) !== JSON.stringify(initial);

  const set = <K extends keyof SiteHostFields>(key: K, value: SiteHostFields[K]) => {
    setForm((f) => ({ ...f, [key]: value }));
    if (Object.keys(serverErrors).length) setServerErrors({});
  };

  const requestClose = async () => {
    if (save.isPending) return;
    if (dirty && !readOnly) {
      const discard = await confirm({
        title: 'Discard changes?',
        message: 'You have unsaved changes to this host.',
        confirmLabel: 'Discard',
        danger: true,
      });
      if (!discard) return;
    }
    onClose();
  };

  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (readOnly) return;
    setSubmitted(true);
    setGeneralError(null);
    const v = validateHost(form);
    const keys = Object.keys(v);
    if (keys.length) {
      const first = tabOfField(keys[0]);
      if (first && !keys.some((k) => tabOfField(k) === tab)) setTab(first);
      return;
    }
    save.mutate(
      { id: host?.id, host: toPayload(form) },
      {
        onSuccess: (res) => {
          feedback.applied(res.apply, host ? 'Saved and applied' : `${capitalize(meta.singular)} created and applied`);
          onClose();
        },
        onError: (err) => {
          if (err instanceof ApiError && err.status === 409) {
            setServerErrors({ domains: err.detail ?? err.title });
            setTab('details');
            return;
          }
          feedback.failed(err, {
            onFieldErrors: (fe) => {
              setServerErrors(fe);
              const firstTab = Object.keys(fe).map(tabOfField).find(Boolean);
              if (firstTab) setTab(firstTab);
              else setGeneralError('The server rejected the host. See the messages above.');
            },
          });
        },
      },
    );
  };

  const formId = `${idBase}-form`;
  const title = readOnly
    ? `View ${meta.singular}`
    : host
      ? `Edit ${meta.singular}`
      : `New ${meta.singular}`;

  return (
    <Dialog
      open
      onClose={() => void requestClose()}
      title={title}
      description={host ? <span className="mono">{host.domains.join(', ')}</span> : meta.description}
      side="right"
      size="lg"
      dismissible={!save.isPending}
      bodyClassName="p-0"
      footer={
        readOnly ? (
          <Button onClick={onClose}>Close</Button>
        ) : (
          <>
            <Button onClick={() => void requestClose()} disabled={save.isPending}>
              Cancel
            </Button>
            <Button type="submit" form={formId} variant="primary" loading={save.isPending}>
              {host ? 'Save' : 'Create'}
            </Button>
          </>
        )
      }
    >
      <form id={formId} onSubmit={submit} noValidate className="flex h-full flex-col">
        <div className="sticky top-0 z-10 bg-surface px-5 pt-2">
          <Tabs
            idBase={idBase}
            aria-label="Host settings"
            value={tab}
            onChange={setTab}
            items={[
              { value: 'details', label: 'Details', badge: errorCount('details') },
              { value: 'tls', label: 'TLS', badge: errorCount('tls') },
              { value: 'access', label: 'Access', badge: errorCount('access') },
              { value: 'headers', label: 'Headers', badge: errorCount('headers') },
              { value: 'locations', label: 'Locations', badge: errorCount('locations'), hidden: kind !== 'proxy' },
              { value: 'advanced', label: 'Advanced', badge: errorCount('advanced') },
            ]}
          />
        </div>
        <fieldset disabled={readOnly || save.isPending} className="min-w-0 px-5 py-5">
          {(unmapped.length > 0 || generalError) && (
            <Callout tone="danger" className="mb-4" title="The server rejected this host">
              <ul className="list-disc pl-4">
                {unmapped.map(([k, m]) => (
                  <li key={k}>{m}</li>
                ))}
                {generalError && unmapped.length === 0 && <li>{generalError}</li>}
              </ul>
            </Callout>
          )}
          <TabPanel idBase={idBase} value="details" active={tab === 'details'}>
            <DetailsTab
              form={form}
              set={set}
              errors={errors}
              kind={kind}
              hostHeaderMode={hostHeaderMode}
              setHostHeaderMode={setHostHeaderMode}
            />
          </TabPanel>
          <TabPanel idBase={idBase} value="tls" active={tab === 'tls'}>
            <TlsTab form={form} set={set} errors={errors} />
          </TabPanel>
          <TabPanel idBase={idBase} value="access" active={tab === 'access'}>
            <AccessTab form={form} set={set} />
          </TabPanel>
          <TabPanel idBase={idBase} value="headers" active={tab === 'headers'}>
            <HeadersTab form={form} set={set} errors={errors} kind={kind} />
          </TabPanel>
          <TabPanel idBase={idBase} value="locations" active={tab === 'locations'}>
            <LocationsTab form={form} set={set} errors={errors} />
          </TabPanel>
          <TabPanel idBase={idBase} value="advanced" active={tab === 'advanced'}>
            <AdvancedTab form={form} set={set} errors={errors} />
          </TabPanel>
        </fieldset>
      </form>
    </Dialog>
  );
}

function capitalize(s: string) {
  return s.charAt(0).toUpperCase() + s.slice(1);
}

type Setter = <K extends keyof SiteHostFields>(key: K, value: SiteHostFields[K]) => void;

function Section({ title, description, children }: { title: string; description?: ReactNode; children: ReactNode }) {
  return (
    <section className="flex flex-col gap-4 border-t border-border pt-5 first:border-t-0 first:pt-0">
      <div>
        <h3 className="text-sm font-semibold text-fg">{title}</h3>
        {description && <p className="mt-0.5 text-xs text-fg-subtle">{description}</p>}
      </div>
      {children}
    </section>
  );
}

// ---------------------------------------------------------------- Details

function DetailsTab({
  form,
  set,
  errors,
  kind,
  hostHeaderMode,
  setHostHeaderMode,
}: {
  form: SiteHostFields;
  set: Setter;
  errors: FieldErrors;
  kind: HostKind;
  hostHeaderMode: HostHeaderMode;
  setHostHeaderMode: (m: HostHeaderMode) => void;
}) {
  const anyHttps = form.upstreams.some((u) => u.scheme === 'https');
  return (
    <div className="flex flex-col gap-6">
      <Section title="Domains">
        <Field
          label="Domain names"
          required
          error={errors.domains}
          hint="Press Enter after each name. Wildcards such as *.example.com are allowed (ACME wildcards need a DNS challenge plugin)."
        >
          <ChipInput
            value={form.domains}
            onChange={(v) => set('domains', v)}
            placeholder="app.example.com"
            normalize={(s) => s.trim().toLowerCase().replace(/^https?:\/\//, '').replace(/\/.*$/, '')}
            validate={(d) => (isValidHostname(d) ? null : 'not a valid host name')}
          />
        </Field>
        <SwitchField
          label="Enabled"
          description="Disabled hosts stay configured here but are not served by Caddy."
          checked={form.enabled}
          onChange={(v) => set('enabled', v)}
        />
      </Section>

      {kind === 'proxy' && (
        <>
          <Section title="Upstream servers" description="Requests are forwarded to these backends. Paste a URL like https://10.0.0.5:8443 into the host field to fill all columns.">
            <UpstreamList value={form.upstreams} onChange={(v) => set('upstreams', v)} errors={errors} prefix="upstreams" />
            {form.upstreams.length > 1 && (
              <Field label="Load balancing" className="max-w-sm">
                <Select value={form.loadBalancing} onChange={(e) => set('loadBalancing', e.target.value as SiteHostFields['loadBalancing'])}>
                  {LOAD_BALANCING.map((l) => (
                    <option key={l.value} value={l.value}>
                      {l.label}
                    </option>
                  ))}
                </Select>
              </Field>
            )}
            {anyHttps && (
              <SwitchField
                label="Skip upstream certificate verification"
                description="Only for internal backends with self-signed certificates. Traffic stays encrypted but the backend is not authenticated."
                checked={form.upstreamTlsInsecure}
                onChange={(v) => set('upstreamTlsInsecure', v)}
              />
            )}
            <Field label="Host header sent to upstream" error={errors.upstreamHostHeader}>
              <div className="flex flex-col gap-2">
                <Segmented
                  aria-label="Host header"
                  value={hostHeaderMode}
                  onChange={(m) => {
                    setHostHeaderMode(m);
                    set('upstreamHostHeader', m === 'client' ? null : m === 'upstream' ? '{upstream}' : '');
                  }}
                  options={[
                    { value: 'client', label: 'Keep client Host' },
                    { value: 'upstream', label: 'Upstream host:port' },
                    { value: 'custom', label: 'Custom' },
                  ]}
                />
                {hostHeaderMode === 'custom' && (
                  <Input
                    mono
                    aria-label="Custom Host header"
                    placeholder="intranet.corp.local"
                    value={form.upstreamHostHeader ?? ''}
                    onChange={(e) => set('upstreamHostHeader', e.target.value)}
                  />
                )}
              </div>
            </Field>
          </Section>
          <Section title="Active health check" description="Caddy probes each upstream and stops sending traffic to failing ones.">
            <SwitchField
              label="Enable health checks"
              checked={form.healthCheck.enabled}
              onChange={(v) => set('healthCheck', { ...form.healthCheck, enabled: v })}
            />
            {form.healthCheck.enabled && (
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label="Path" error={errors['healthCheck.path']}>
                  <Input
                    mono
                    value={form.healthCheck.path}
                    onChange={(e) => set('healthCheck', { ...form.healthCheck, path: e.target.value })}
                  />
                </Field>
                <Field label="Expected status" hint="0 = any 2xx response" error={errors['healthCheck.expectStatus']}>
                  <NumberInput
                    value={form.healthCheck.expectStatus}
                    onValueChange={(v) => set('healthCheck', { ...form.healthCheck, expectStatus: v })}
                  />
                </Field>
                <Field label="Interval (seconds)" error={errors['healthCheck.intervalSeconds']}>
                  <NumberInput
                    min={1}
                    value={form.healthCheck.intervalSeconds}
                    onValueChange={(v) => set('healthCheck', { ...form.healthCheck, intervalSeconds: v })}
                  />
                </Field>
                <Field label="Timeout (seconds)" error={errors['healthCheck.timeoutSeconds']}>
                  <NumberInput
                    min={1}
                    value={form.healthCheck.timeoutSeconds}
                    onValueChange={(v) => set('healthCheck', { ...form.healthCheck, timeoutSeconds: v })}
                  />
                </Field>
              </div>
            )}
          </Section>
        </>
      )}

      {kind === 'redirect' && (
        <Section title="Redirect">
          <Field label="Target URL" required error={errors.redirectTarget} hint="Absolute URL, e.g. https://www.example.com">
            <Input
              mono
              type="url"
              placeholder="https://www.example.com"
              value={form.redirectTarget ?? ''}
              onChange={(e) => set('redirectTarget', e.target.value)}
            />
          </Field>
          <Field label="Status code" error={errors.redirectCode} className="max-w-sm">
            <Select value={form.redirectCode} onChange={(e) => set('redirectCode', Number(e.target.value))}>
              {REDIRECT_CODES.map((c) => (
                <option key={c.value} value={c.value}>
                  {c.label}
                </option>
              ))}
            </Select>
          </Field>
          <SwitchField
            label="Preserve path and query"
            description={
              <>
                /docs?x=1 redirects to <span className="mono">{(form.redirectTarget || 'https://target').replace(/\/$/, '')}/docs?x=1</span>
              </>
            }
            checked={form.preservePath}
            onChange={(v) => set('preservePath', v)}
          />
        </Section>
      )}

      {kind === 'static' && (
        <Section title="Files">
          <Field
            label="Root folder"
            required
            error={errors.rootPath}
            hint="Local path or UNC share. Caddy runs as LocalSystem, so shares must grant read access to this computer's account (DOMAIN\SERVER$)."
          >
            <Input
              mono
              placeholder="D:\Sites\intranet"
              value={form.rootPath ?? ''}
              onChange={(e) => set('rootPath', e.target.value)}
            />
          </Field>
          <SwitchField
            label="Directory browsing"
            description="List folder contents when there is no index.html."
            checked={form.browse}
            onChange={(v) => set('browse', v)}
          />
          <SwitchField
            label="Single-page application fallback"
            description="Serve /index.html for paths that do not exist (React, Angular, Vue routers)."
            checked={form.spaFallback}
            onChange={(v) => set('spaFallback', v)}
          />
        </Section>
      )}

      {kind === 'response' && (
        <Section title="Response">
          <div className="grid gap-4 sm:grid-cols-[160px_minmax(0,1fr)]">
            <Field label="Status code" required error={errors.responseStatus}>
              <NumberInput min={100} max={599} value={form.responseStatus} onValueChange={(v) => set('responseStatus', v)} />
            </Field>
            <Field label="Content type" required error={errors.responseContentType}>
              <Input
                mono
                list="cpm-content-types"
                value={form.responseContentType}
                onChange={(e) => set('responseContentType', e.target.value)}
              />
            </Field>
          </div>
          <datalist id="cpm-content-types">
            <option value="text/plain; charset=utf-8" />
            <option value="text/html; charset=utf-8" />
            <option value="application/json" />
          </datalist>
          <Field label="Body" hint="Leave empty for an empty body.">
            <Textarea mono rows={8} value={form.responseBody ?? ''} onChange={(e) => set('responseBody', e.target.value)} />
          </Field>
        </Section>
      )}

      {kind !== 'redirect' && (
        <Section title="Performance">
          <SwitchField
            label="Compression"
            description="Compress responses with zstd or gzip when the client supports it."
            checked={form.compression}
            onChange={(v) => set('compression', v)}
          />
        </Section>
      )}
    </div>
  );
}

// ---------------------------------------------------------------- TLS

function TlsTab({ form, set, errors }: { form: SiteHostFields; set: Setter; errors: FieldErrors }) {
  const certs = useCertificates();
  const settings = useCaddySettings();
  const custom = (certs.data ?? []).filter((c) => c.kind === 'custom');
  const selected = custom.find((c) => c.id === form.certificateId);
  const uncovered = selected ? form.domains.filter((d) => !certCovers(selected, d)) : [];
  const httpsPort = settings.data?.httpsPort ?? 443;
  const httpPort = settings.data?.httpPort ?? 80;
  const wildcard = form.domains.some((d) => d.startsWith('*.'));

  return (
    <div className="flex flex-col gap-6">
      <Section title="Certificate">
        <RadioCards
          aria-label="TLS mode"
          value={form.tls}
          onChange={(v) => set('tls', v)}
          options={[
            {
              value: 'acme',
              label: 'Automatic (ACME)',
              icon: <Globe size={14} className="text-fg-subtle" />,
              description: 'Public certificate from Let’s Encrypt / ZeroSSL. Needs public DNS and inbound ports 80/443.',
            },
            {
              value: 'internal',
              label: 'Internal CA',
              icon: <ShieldCheck size={14} className="text-fg-subtle" />,
              description: 'Issued by Caddy’s local CA. Distribute the root via GPO for trusted internal names.',
            },
            {
              value: 'custom',
              label: 'Custom certificate',
              icon: <KeyRound size={14} className="text-fg-subtle" />,
              description: 'Use an uploaded certificate or one referenced by path (e.g. from your enterprise PKI).',
            },
            {
              value: 'none',
              label: 'None (HTTP only)',
              icon: <LockOpen size={14} className="text-fg-subtle" />,
              description: `Plain HTTP on port ${httpPort}. No encryption — use only on trusted networks.`,
            },
          ]}
        />
        {form.tls === 'acme' && wildcard && (
          <Callout tone="warning">
            Wildcard certificates cannot be obtained with the HTTP or TLS-ALPN challenge. They require a DNS provider plugin
            configured through advanced settings, or use a custom or internal certificate instead.
          </Callout>
        )}
        {form.tls === 'custom' && (
          <Field
            label="Certificate"
            required
            error={errors.certificateId}
            hint={
              custom.length === 0 ? (
                <>
                  No custom certificates yet. <Link className="text-accent-text hover:underline" to="/certificates">Add one on the Certificates page</Link>.
                </>
              ) : undefined
            }
          >
            <Select value={form.certificateId ?? ''} onChange={(e) => set('certificateId', e.target.value || null)}>
              <option value="">Select a certificate…</option>
              {custom.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.name} — {c.subjects.slice(0, 2).join(', ')}
                  {c.subjects.length > 2 ? ` +${c.subjects.length - 2}` : ''} — expires {formatDate(c.notAfter)}
                </option>
              ))}
            </Select>
          </Field>
        )}
        {selected && uncovered.length > 0 && (
          <Callout tone="warning" title="Certificate does not cover every domain">
            <span className="mono">{uncovered.join(', ')}</span> {uncovered.length === 1 ? 'is' : 'are'} not in the
            certificate’s subjects. Browsers will show a name mismatch warning for {uncovered.length === 1 ? 'it' : 'them'}.
          </Callout>
        )}
        {selected && selected.daysRemaining < 0 && (
          <Callout tone="danger">This certificate expired on {formatDate(selected.notAfter)}.</Callout>
        )}
      </Section>

      <Section title="HTTPS behaviour">
        <SwitchField
          label="Force HTTPS"
          description={`Redirect http:// requests on port ${httpPort} to https:// on port ${httpsPort}. When off, the site is served on both.`}
          checked={form.tls !== 'none' && form.forceHttps}
          disabled={form.tls === 'none'}
          onChange={(v) => set('forceHttps', v)}
        />
        <SwitchField
          label="HSTS"
          description="Tell browsers to always use HTTPS for this domain (Strict-Transport-Security). Hard to undo — enable once HTTPS works."
          checked={form.tls !== 'none' && form.hsts}
          disabled={form.tls === 'none'}
          onChange={(v) => set('hsts', v)}
        />
        {form.tls !== 'none' && form.hsts && (
          <div className="grid gap-4 border-l-2 border-border pl-4 sm:grid-cols-2">
            <Field label="Max age (seconds)" hint="31536000 = 1 year" error={errors.hstsMaxAgeSeconds}>
              <NumberInput min={0} value={form.hstsMaxAgeSeconds} onValueChange={(v) => set('hstsMaxAgeSeconds', v)} />
            </Field>
            <SwitchField
              className="sm:pt-6"
              label="Include subdomains"
              checked={form.hstsSubdomains}
              onChange={(v) => set('hstsSubdomains', v)}
            />
          </div>
        )}
        {form.tls !== 'none' && settings.data && (
          <p className="flex items-center gap-1.5 text-xs text-fg-subtle">
            <Lock size={12} aria-hidden />
            HTTP/3 (QUIC on UDP {httpsPort}) is {settings.data.enableHttp3 ? 'enabled' : 'disabled'} globally —{' '}
            <Link to="/settings" className="text-accent-text hover:underline">
              change in Settings
            </Link>
            .
          </p>
        )}
      </Section>
    </div>
  );
}

// ---------------------------------------------------------------- Access

function AccessTab({ form, set }: { form: SiteHostFields; set: Setter }) {
  const lists = useAccessLists();
  const selected = lists.data?.find((l) => l.id === form.accessListId);
  return (
    <div className="flex flex-col gap-6">
      <Section title="Access list" description="Restrict who can reach this host by IP address and/or basic authentication.">
        <Field
          label="Access list"
          hint={
            <>
              Manage lists on the{' '}
              <Link to="/access-lists" className="text-accent-text hover:underline">
                Access Lists
              </Link>{' '}
              page.
            </>
          }
        >
          <Select value={form.accessListId ?? ''} onChange={(e) => set('accessListId', e.target.value || null)}>
            <option value="">Public — no restriction</option>
            {(lists.data ?? []).map((l) => (
              <option key={l.id} value={l.id}>
                {l.name}
              </option>
            ))}
          </Select>
        </Field>
        {form.accessListId && !selected && lists.isSuccess && (
          <Callout tone="warning">The selected access list no longer exists. Choose another one or make the host public.</Callout>
        )}
        {selected && (
          <div className="rounded-md border border-border bg-surface-2 px-3 py-2.5 text-sm text-fg-muted">
            <span className="font-medium text-fg">{selected.name}</span>: {selected.rules.length} IP rule
            {selected.rules.length === 1 ? '' : 's'}, {selected.users.length} user{selected.users.length === 1 ? '' : 's'},{' '}
            {selected.satisfyAny ? 'IP rules OR login' : 'IP rules AND login'}.
          </div>
        )}
      </Section>
      <Section title="Protection">
        <SwitchField
          label="Block common exploits"
          description="Return 403 for typical attack probes (SQL injection, path traversal, requests for .env/.git files)."
          checked={form.blockExploits}
          onChange={(v) => set('blockExploits', v)}
        />
      </Section>
    </div>
  );
}

// ---------------------------------------------------------------- Headers

function HeadersTab({ form, set, errors, kind }: { form: SiteHostFields; set: Setter; errors: FieldErrors; kind: HostKind }) {
  return (
    <div className="flex flex-col gap-6">
      {kind === 'proxy' && (
        <Section
          title="Request headers"
          description={
            <>
              Sent to the upstream. Placeholders are allowed, e.g. <span className="mono">{'{http.request.remote.host}'}</span>.
              X-Forwarded-For/Proto/Host are added by Caddy automatically.
            </>
          }
        >
          <KeyValueEditor
            rows={form.requestHeaders}
            onChange={(rows) => set('requestHeaders', rows)}
            newRow={(): HeaderOp => ({ action: 'set', name: '', value: '' })}
            actions={HEADER_ACTIONS}
            namePlaceholder="X-Custom-Header"
            valuePlaceholder="value"
            addLabel="Add request header"
            errors={errors}
            errorPrefix="requestHeaders"
            valueDisabledFor={['delete']}
          />
        </Section>
      )}
      <Section title="Response headers" description="Applied to responses sent to clients (for example security headers or removing Server).">
        <KeyValueEditor
          rows={form.responseHeaders}
          onChange={(rows) => set('responseHeaders', rows)}
          newRow={(): HeaderOp => ({ action: 'set', name: '', value: '' })}
          actions={HEADER_ACTIONS}
          namePlaceholder="X-Frame-Options"
          valuePlaceholder="SAMEORIGIN"
          addLabel="Add response header"
          errors={errors}
          errorPrefix="responseHeaders"
          valueDisabledFor={['delete']}
        />
      </Section>
    </div>
  );
}

// ---------------------------------------------------------------- Locations

function LocationsTab({ form, set, errors }: { form: SiteHostFields; set: Setter; errors: FieldErrors }) {
  const update = (i: number, patch: Partial<ProxyLocation>) =>
    set(
      'locations',
      form.locations.map((l, j) => (j === i ? { ...l, ...patch } : l)),
    );
  return (
    <div className="flex flex-col gap-4">
      <p className="text-sm text-fg-subtle">
        Send a path prefix to different upstream servers, e.g. <span className="mono">/api</span> to an API backend. Locations are
        matched before the default upstreams.
      </p>
      {form.locations.map((loc, i) => (
        <div key={i} className="rounded-md border border-border">
          <div className="flex items-center gap-2 border-b border-border bg-surface-2/60 px-3 py-2">
            <span className="text-sm font-medium text-fg">Location {i + 1}</span>
            <span className="mono truncate text-xs text-fg-subtle">{loc.path}</span>
            <div className="flex-1" />
            <Button
              size="sm"
              variant="danger-ghost"
              icon={<Trash2 size={14} />}
              onClick={() => set('locations', form.locations.filter((_, j) => j !== i))}
            >
              Remove
            </Button>
          </div>
          <div className="flex flex-col gap-4 p-3">
            <Field label="Path prefix" required error={errors[`locations.${i}.path`]} hint="Matches this path and everything below it.">
              <Input mono placeholder="/api" value={loc.path} onChange={(e) => update(i, { path: e.target.value })} />
            </Field>
            <UpstreamList
              value={loc.upstreams}
              onChange={(v) => update(i, { upstreams: v })}
              errors={errors}
              prefix={`locations.${i}.upstreams`}
            />
            <SwitchField
              label="Strip path prefix"
              description={`Forward ${loc.path || '/api'}/users as /users.`}
              checked={loc.stripPrefix}
              onChange={(v) => update(i, { stripPrefix: v })}
            />
            {loc.upstreams.some((u) => u.scheme === 'https') && (
              <SwitchField
                label="Skip upstream certificate verification"
                checked={loc.upstreamTlsInsecure}
                onChange={(v) => update(i, { upstreamTlsInsecure: v })}
              />
            )}
          </div>
        </div>
      ))}
      <div>
        <Button
          size="sm"
          icon={<Plus size={14} />}
          onClick={() =>
            set('locations', [
              ...form.locations,
              { path: '/', upstreams: [newUpstream()], stripPrefix: false, upstreamTlsInsecure: false },
            ])
          }
        >
          Add location
        </Button>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------- Advanced

function AdvancedTab({ form, set, errors }: { form: SiteHostFields; set: Setter; errors: FieldErrors }) {
  const first = form.domains[0] ?? '<first-domain>';
  const format = () => {
    try {
      const parsed: unknown = JSON.parse(form.advancedRoutesJson ?? '');
      set('advancedRoutesJson', JSON.stringify(parsed, null, 2));
    } catch {
      /* the validation message explains the problem */
    }
  };
  return (
    <div className="flex flex-col gap-6">
      <Section title="Logging">
        <SwitchField
          label="Access log"
          description={
            <>
              Write requests to <span className="mono">logs\access\{first}.log</span> (JSON). View them on the Logs page.
            </>
          }
          checked={form.accessLog}
          onChange={(v) => set('accessLog', v)}
        />
      </Section>
      <Section
        title="Custom Caddy routes"
        description="Raw Caddy JSON: an array of route objects inserted before the generated handlers of this host. Invalid routes make Caddy reject the whole configuration."
      >
        <Field
          label="Routes (JSON array)"
          error={errors.advancedRoutesJson}
          labelAction={
            <Button size="xs" variant="ghost" icon={<Wand2 size={12} />} onClick={format} disabled={!form.advancedRoutesJson?.trim()}>
              Format
            </Button>
          }
        >
          <Textarea
            mono
            rows={12}
            spellCheck={false}
            placeholder={'[\n  {\n    "match": [{ "path": ["/health"] }],\n    "handle": [{ "handler": "static_response", "status_code": 200, "body": "ok" }]\n  }\n]'}
            value={form.advancedRoutesJson ?? ''}
            onChange={(e) => set('advancedRoutesJson', e.target.value)}
          />
        </Field>
      </Section>
      <Section title="Notes">
        <Field label="Notes" hint="For your team — not used in the configuration.">
          <Textarea rows={4} value={form.notes ?? ''} onChange={(e) => set('notes', e.target.value)} />
        </Field>
      </Section>
    </div>
  );
}
