import { useId, useMemo, useState, type FormEvent, type ReactNode } from 'react';
import { Link } from 'react-router';
import { AlertTriangle, Globe, KeyRound, Lock, LockOpen, Plus, ShieldCheck, Trash2, Wand2 } from 'lucide-react';
import { ApiError } from '@/api/client';
import { useAccessLists, useBinaryOverview, useCaddySettings, useCertificates, useDnsProviders, useSaveHost } from '@/api/hooks';
import type { HeaderOp, HostKind, ProxyLocation, SiteHost, SiteHostFields } from '@/api/types';
import { useAuth } from '@/auth';
import { useFeedback } from '@/components/feedback';
import {
  Badge,
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
import { isValidSiteDomain, serverFieldErrors, type FieldErrors } from '@/lib/validation';
import {
  certCovers,
  HEADER_ACTIONS,
  kindMeta,
  LOAD_BALANCING,
  newUpstream,
  NTLM_MODULE,
  NTLM_PLUGIN,
  REDIRECT_CODES,
  tabOfField,
  toFields,
  toPayload,
  validateHost,
  type HostTab,
} from './hostModel';
import { DelegationRecordsPanel, type DelegationCheckRequest } from './DelegationRecords';
import { checkRequests, dnsChallengeDomains, hostDelegationRecords, isDelegationName, usesDnsChallenge } from './dnsDelegation';
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
  const caddySettings = useCaddySettings();
  const validationCtx = { dnsProviderConfigured: caddySettings.data ? !!caddySettings.data.dnsProvider : undefined, settings: caddySettings.data };
  const feedback = useFeedback();
  const confirm = useConfirm();
  const { isAdmin } = useAuth();
  const meta = kindMeta[kind];
  // Raw Caddy routes are admin-only: other roles send the stored value back untouched (a new host gets none).
  const lockedRoutes = !isAdmin;
  const droppedRoutes = lockedRoutes && !host && !!initial.advancedRoutesJson?.trim();

  const dnsProviderConfigured = validationCtx.dnsProviderConfigured;
  const settingsData = caddySettings.data;
  const clientErrors = useMemo(
    () => (submitted ? validateHost(form, { dnsProviderConfigured, settings: settingsData }) : {}),
    [form, submitted, dnsProviderConfigured, settingsData],
  );
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
    const v = validateHost(form, validationCtx);
    const keys = Object.keys(v);
    if (keys.length) {
      const first = tabOfField(keys[0]);
      if (first && !keys.some((k) => tabOfField(k) === tab)) setTab(first);
      return;
    }
    const payload = toPayload(form, caddySettings.data);
    if (lockedRoutes) payload.advancedRoutesJson = host ? (host.advancedRoutesJson ?? null) : null;
    save.mutate(
      { id: host?.id, host: payload },
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
          if (err instanceof ApiError && err.status === 403) {
            // Privilege boundaries (SPEC round 2): e.g. only administrators may change raw Caddy routes.
            const message = err.detail ?? 'Your role does not allow this change.';
            const fe = err.errors ? serverFieldErrors(err.errors) : /route|advanced/i.test(message) ? { advancedRoutesJson: message } : {};
            const firstTab = Object.keys(fe).map(tabOfField).find(Boolean);
            setServerErrors(fe);
            if (firstTab) setTab(firstTab);
            else setGeneralError(message);
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

  const locked = !!readOnly || save.isPending;
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
        {/* The TLS tab sits outside the disabled fieldsets and locks its own inputs, so the delegation records' Copy and
            Check DNS work in read-only views too. */}
        <div className="min-w-0 px-5 py-5">
          <fieldset disabled={locked} className="contents">
            {(unmapped.length > 0 || generalError) && (
              <Callout tone="danger" className="mb-4" title="The server rejected this host">
                <ul className="list-disc pl-4">
                  {unmapped.map(([k, m]) => (
                    <li key={k}>{m}</li>
                  ))}
                  {generalError && <li>{generalError}</li>}
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
          </fieldset>
          <TabPanel idBase={idBase} value="tls" active={tab === 'tls'}>
            <TlsTab form={form} set={set} errors={errors} locked={locked} host={host} />
          </TabPanel>
          <fieldset disabled={locked} className="contents">
            <TabPanel idBase={idBase} value="access" active={tab === 'access'}>
              <AccessTab form={form} set={set} errors={errors} />
            </TabPanel>
            <TabPanel idBase={idBase} value="headers" active={tab === 'headers'}>
              <HeadersTab form={form} set={set} errors={errors} kind={kind} />
            </TabPanel>
            <TabPanel idBase={idBase} value="locations" active={tab === 'locations'}>
              <LocationsTab form={form} set={set} errors={errors} />
            </TabPanel>
            <TabPanel idBase={idBase} value="advanced" active={tab === 'advanced'}>
              <AdvancedTab form={form} set={set} errors={errors} locked={lockedRoutes} droppedRoutes={droppedRoutes} />
            </TabPanel>
          </fieldset>
        </div>
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
          hint="Press Enter after each name. Wildcards such as *.example.com are allowed (ACME wildcards need the DNS challenge — see the TLS tab)."
        >
          <ChipInput
            value={form.domains}
            onChange={(v) => set('domains', v)}
            placeholder="app.example.com"
            normalize={(s) => s.trim().toLowerCase().replace(/^https?:\/\//, '').replace(/\/.*$/, '')}
            validate={(d) => (isValidSiteDomain(d) ? null : 'not a valid host name')}
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
              <Field
                label="Load balancing"
                className="max-w-sm"
                hint={
                  ['first', 'ipHash', 'uriHash', 'cookie'].includes(form.loadBalancing) && !form.healthCheck.enabled
                    ? 'With this policy a backend that is down keeps getting its share of requests unless the active health check (below) is on.'
                    : 'Round robin and least connections retry a request on the next backend when one cannot be reached.'
                }
              >
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
                description="Only for internal backends with self-signed certificates. Traffic stays encrypted but the backend is not authenticated. When the Host header is kept, the requested domain is also sent as TLS SNI (IIS SNI bindings, name-based virtual hosts), with separate upstream connections per domain. Wildcard domains send no SNI for an IP upstream."
                checked={form.upstreamTlsInsecure}
                onChange={(v) => set('upstreamTlsInsecure', v)}
              />
            )}
            <NtlmField checked={form.upstreamNtlm} onChange={(v) => set('upstreamNtlm', v)} error={errors.upstreamNtlm} />
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
          <Section
            title="Active health check"
            description={
              form.upstreams.length > 1
                ? 'Caddy probes each upstream and stops sending traffic to failing ones. Without it, an upstream is taken out of rotation for 30 s after a failed request (passive check) and the others take over.'
                : 'Caddy probes the upstream on a schedule; while the check fails the host answers 503. Without a check this upstream is shown as “not monitored”: a single upstream is never taken offline because of failed requests, since there is nothing to fail over to.'
            }
          >
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
                <Field label="Expected status" hint="0 = any 2xx, 1–5 = any status of that class (3 = any redirect), or an exact code. A redirect counts as a failure unless you allow it here." error={errors['healthCheck.expectStatus']}>
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
            hint={
              <>
                Local path or UNC share. Caddy runs as LocalSystem, so shares must grant read access to this computer’s account
                (<span className="mono">DOMAIN\SERVER$</span>). Not allowed: drive roots such as <span className="mono">D:\</span>, the
                Windows and Program Files folders, and the manager’s data folder (including Caddy’s storage and the certificate
                store). UNC paths can only be set by an administrator.
              </>
            }
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
            description="List folder contents when there is no index.html. Files and folders starting with a dot and web.config are never served or listed (404); /.well-known/ is served."
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
              <NumberInput min={200} max={599} value={form.responseStatus} onValueChange={(v) => set('responseStatus', v)} />
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

/** "Upstream uses Windows authentication" with a warning when the installed Caddy lacks the NTLM transport. */
function NtlmField({ checked, onChange, error }: { checked: boolean; onChange: (v: boolean) => void; error?: string }) {
  const binary = useBinaryOverview();
  const installed = binary.data?.installed;
  const missing = !!installed && !installed.modules.includes(NTLM_MODULE);
  return (
    <div className="flex flex-col gap-2">
      <SwitchField
        label={
          <span className="inline-flex flex-wrap items-center gap-2">
            Upstream uses Windows authentication (NTLM)
            {missing && (
              <Badge tone="warning" icon={<AlertTriangle size={11} aria-hidden />} title={`The installed Caddy binary has no ${NTLM_MODULE} module`}>
                Plugin not installed
              </Badge>
            )}
          </span>
        }
        description={
          <>
            For IIS, SharePoint, SSRS or Exchange sites using Integrated Windows Authentication: keeps each client on its own upstream
            connection so the NTLM/Negotiate handshake succeeds. Requires the plugin <span className="mono">{NTLM_PLUGIN}</span> —{' '}
            <Link to="/caddy/plugins?q=ntlm" className="text-accent-text hover:underline">
              manage plugins
            </Link>
            .
          </>
        }
        checked={checked}
        onChange={onChange}
      />
      {checked && missing && (
        <Callout tone="warning" title="The NTLM transport is not available">
          The installed Caddy binary does not contain the module <span className="mono">{NTLM_MODULE}</span>, so Caddy cannot load
          this setting. Add <span className="mono">{NTLM_PLUGIN}</span> on the Plugins page and rebuild Caddy before saving.
        </Callout>
      )}
      {error && (
        <p className="text-xs text-danger" role="alert">
          {error}
        </p>
      )}
    </div>
  );
}

// ---------------------------------------------------------------- TLS

function TlsTab({ form, set, errors, locked, host }: { form: SiteHostFields; set: Setter; errors: FieldErrors; locked: boolean; host?: SiteHost }) {
  const certs = useCertificates();
  const settings = useCaddySettings();
  const custom = (certs.data ?? []).filter((c) => c.kind === 'custom');
  const selected = custom.find((c) => c.id === form.certificateId);
  const uncovered = selected ? form.domains.filter((d) => !certCovers(selected, d)) : [];
  const httpsPort = settings.data?.httpsPort ?? 443;
  const httpPort = settings.data?.httpPort ?? 80;
  // Port clients reach HTTPS on (Settings > Listeners > Public HTTPS port), else the HTTPS port itself.
  const publicHttpsPort = settings.data?.publicHttpsPort ?? httpsPort;
  const wildcard = form.domains.some((d) => d.startsWith('*.'));

  return (
    <div className="flex flex-col gap-6">
      <Section title="Certificate">
        <fieldset disabled={locked} className="contents">
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
          {form.tls === 'acme' && <AcmeChallengeField form={form} set={set} errors={errors} wildcard={wildcard} />}
        </fieldset>
        {form.tls === 'acme' && <DnsDelegationField form={form} set={set} errors={errors} locked={locked} host={host} />}
        <fieldset disabled={locked} className="contents">
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
        </fieldset>
      </Section>

      <Section title="HTTPS behaviour">
        <fieldset disabled={locked} className="contents">
          <SwitchField
            label="Force HTTPS"
            description={
              publicHttpsPort === 443
                ? `Redirect http:// requests on port ${httpPort} to https:// (port 443${httpsPort !== 443 ? `, forwarded to ${httpsPort}` : ''}). When off, the site is served on both.`
                : `Redirect http:// requests on port ${httpPort} to https:// on port ${publicHttpsPort}. If a router forwards public 443 to ${httpsPort}, set the public HTTPS port in Settings › Listeners. When off, the site is served on both.`
            }
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
        </fieldset>
      </Section>
    </div>
  );
}

const CHALLENGE_LABEL = { http: 'HTTP-01 / TLS-ALPN-01', dns: 'DNS-01' } as const;

/** TLS tab (ACME only): which challenge proves control of the domains (SPEC round 3 DNS-01). */
function AcmeChallengeField({ form, set, errors, wildcard }: { form: SiteHostFields; set: Setter; errors: FieldErrors; wildcard: boolean }) {
  const settings = useCaddySettings();
  const providers = useDnsProviders(!!settings.data?.dnsProvider);
  const s = settings.data;
  const provider = s?.dnsProvider ? providers.data?.find((p) => p.name === s.dnsProvider) : undefined;
  const providerLabel = provider?.label ?? s?.dnsProvider ?? null;
  const hasProvider = !!s?.dnsProvider;
  const defaultChallenge = s?.defaultAcmeChallenge ?? 'http';
  const effective = form.acmeChallenge === 'default' ? defaultChallenge : form.acmeChallenge;
  const settingsLink = (children: ReactNode) => (
    <Link to="/settings#acme-challenge" className="text-accent-text hover:underline">
      {children}
    </Link>
  );

  return (
    <>
      <Field
        label="ACME challenge"
        error={errors.acmeChallenge}
        hint={
          effective === 'dns' ? (
            <>
              Caddy creates a TXT record through {providerLabel ? <span className="font-medium">{providerLabel}</span> : 'the DNS provider'}. No inbound
              ports are needed.
            </>
          ) : (
            <>The CA connects to this server on port 80 (HTTP-01) or 443 (TLS-ALPN-01); both must be reachable from the Internet.</>
          )
        }
      >
        <Select value={form.acmeChallenge} onChange={(e) => set('acmeChallenge', e.target.value as SiteHostFields['acmeChallenge'])} className="max-w-sm">
          <option value="default">Default ({CHALLENGE_LABEL[defaultChallenge]})</option>
          <option value="http">{CHALLENGE_LABEL.http}</option>
          <option value="dns" disabled={s ? !hasProvider && form.acmeChallenge !== 'dns' : false}>
            {CHALLENGE_LABEL.dns}
            {s && !hasProvider ? ' — no DNS provider configured' : providerLabel ? ` (${providerLabel})` : ''}
          </option>
        </Select>
      </Field>
      {s && !hasProvider && !wildcard && (
        <p className="-mt-2 text-xs text-fg-subtle">
          DNS-01 is available once a DNS provider is set up in {settingsLink('Settings › Caddy › ACME challenge')}.
        </p>
      )}
      {s && effective === 'dns' && hasProvider && provider && !provider.installed && (
        <Callout tone="warning" title={`The ${provider.label} DNS plugin is not in the installed Caddy`}>
          Certificates for this host cannot be obtained until Caddy is rebuilt with <span className="mono">{provider.package}</span>.{' '}
          {settingsLink('Add it in Settings › Caddy')}.
        </Callout>
      )}
      {wildcard && s && (
        effective === 'dns' || hasProvider ? (
          <Callout tone="info">
            Wildcard names are validated with DNS-01{providerLabel ? <> through <span className="font-medium">{providerLabel}</span></> : null}
            {effective === 'http' ? ' (used automatically for wildcards; the other names keep HTTP-01 / TLS-ALPN-01)' : ''}.
          </Callout>
        ) : s.hasAcmeIssuerJson ? (
          <Callout tone="info">
            Wildcard names need the DNS challenge. No DNS provider is set in Settings › Caddy; the custom ACME issuer options under Plugins &amp;
            advanced must configure one, or issuance fails.
          </Callout>
        ) : (
          <Callout tone="warning">
            Wildcard certificates cannot be obtained with the HTTP or TLS-ALPN challenge. Set up a DNS provider in{' '}
            {settingsLink('Settings › Caddy › ACME challenge')}, or use a custom or internal certificate instead.
          </Callout>
        )
      )}
    </>
  );
}

const DELEGATION_HINT: Record<SiteHostFields['dnsDelegation'], string> = {
  default: 'Uses the default delegation name from Settings › Caddy.',
  off: 'Caddy writes the challenge record in each domain’s own zone, so the DNS provider token needs access to it.',
  custom: 'Uses the name below instead of the default, for example when this domain’s records belong in another validation zone.',
};

/**
 * TLS tab (ACME, DNS-01): challenge delegation (SPEC round 3b) — Use default / Off / Custom name, then the CNAME records this
 * host needs with copy buttons and "Check DNS". Its inputs lock themselves (locked); the records stay usable read-only.
 */
function DnsDelegationField({
  form,
  set,
  errors,
  locked,
  host,
}: {
  form: SiteHostFields;
  set: Setter;
  errors: FieldErrors;
  locked: boolean;
  host?: SiteHost;
}) {
  const settings = useCaddySettings();
  const s = settings.data;
  if (!s) return null;
  const dns = usesDnsChallenge(form, s);
  // Wildcard names of an HTTP-01 host switch to DNS-01 on their own; the host's delegation applies to them.
  const wildcardsOnly = !dns && dnsChallengeDomains(form, s).length > 0;
  if (!dns && !wildcardsOnly) return null;

  const defaultName = s.dnsOverrideDomain?.trim() || null;
  const customName = form.dnsOverrideDomain?.trim() ?? '';
  const nameOk = form.dnsDelegation !== 'custom' || isDelegationName(customName);
  const records = nameOk ? hostDelegationRecords(form, s) : [];
  // A saved host is checked by id when the fields that decide its records are unchanged; otherwise by the records shown.
  const recordFields = (h: SiteHostFields) => {
    const p = toPayload(h, s);
    return JSON.stringify([p.tls, p.acmeChallenge, p.dnsDelegation, p.dnsOverrideDomain, p.domains]);
  };
  const unchanged = !!host && recordFields(toFields(host)) === recordFields(form);
  const requests = (): DelegationCheckRequest[] => (host && unchanged ? [{ hostId: host.id }] : checkRequests(records));

  return (
    <div className="flex flex-col gap-4 rounded-md border border-border p-4">
      {wildcardsOnly && (
        <p className="text-sm text-fg-muted">
          Wildcard names use DNS-01; choose their delegation below. The other names keep HTTP-01 / TLS-ALPN-01.
        </p>
      )}
      <Field label="Challenge delegation (CNAME)" error={errors.dnsDelegation} hint={DELEGATION_HINT[form.dnsDelegation]}>
        <Select
          value={form.dnsDelegation}
          disabled={locked}
          className="max-w-md"
          onChange={(e) => {
            const v = e.target.value as SiteHostFields['dnsDelegation'];
            set('dnsDelegation', v);
            if (v === 'custom' && !form.dnsOverrideDomain) set('dnsOverrideDomain', defaultName ?? '');
          }}
        >
          <option value="default">Use default ({defaultName ?? 'none set'})</option>
          <option value="off">Off — use each domain’s own zone</option>
          <option value="custom">Custom name</option>
        </Select>
      </Field>
      {form.dnsDelegation === 'custom' && (
        <Field
          label="Delegation name"
          required
          error={errors.dnsOverrideDomain}
          hint="A name in a zone the DNS provider token can edit, for example _acme-challenge.shop.validation.example.net."
        >
          <Input
            mono
            autoComplete="off"
            spellCheck={false}
            placeholder="_acme-challenge.shop.validation.example.net"
            value={form.dnsOverrideDomain ?? ''}
            onChange={(e) => set('dnsOverrideDomain', e.target.value)}
            disabled={locked}
          />
        </Field>
      )}
      {records.length > 0 ? (
        <div className="flex flex-col gap-2">
          <p className="text-sm text-fg-muted">
            Create {records.length === 1 ? 'this CNAME record' : 'these CNAME records'} once in the zone of{' '}
            {records.length === 1 ? 'the domain' : 'each domain'}. Caddy then writes the challenge record only at the target.
          </p>
          <DelegationRecordsPanel compact rows={records} requests={requests} />
        </div>
      ) : (
        form.domains.length > 0 &&
        nameOk &&
        form.dnsDelegation === 'default' &&
        !defaultName && (
          <p className="text-xs text-fg-subtle">
            No default delegation name is set, so Caddy writes the challenge record in each domain’s own zone. Set one in{' '}
            <Link to="/settings#acme-challenge" className="text-accent-text hover:underline">
              Settings › Caddy › ACME challenge
            </Link>{' '}
            or choose a custom name.
          </p>
        )
      )}
    </div>
  );
}

// ---------------------------------------------------------------- Access

function AccessTab({ form, set, errors }: { form: SiteHostFields; set: Setter; errors: FieldErrors }) {
  const lists = useAccessLists();
  const selected = lists.data?.find((l) => l.id === form.accessListId);
  return (
    <div className="flex flex-col gap-6">
      <Section title="Access list" description="Restrict who can reach this host by IP address and/or basic authentication.">
        <Field
          label="Access list"
          error={errors.accessListId}
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
              X-Forwarded-For/Proto/Host are added by Caddy automatically. Caddy drops request headers from clients whose names contain an
              underscore (for example <span className="mono">SM_USER</span> or <span className="mono">X_Api_Key</span>) before they reach any
              upstream or matcher; headers set here, including names with underscores, are still sent.
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

function AdvancedTab({
  form,
  set,
  errors,
  locked,
  droppedRoutes,
}: {
  form: SiteHostFields;
  set: Setter;
  errors: FieldErrors;
  /** Not an administrator: raw routes are shown read-only. */
  locked: boolean;
  /** Duplicating a host with custom routes as a non-admin: the routes are not copied. */
  droppedRoutes: boolean;
}) {
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
        {locked && (
          <Callout tone="info" title="Only administrators can change raw Caddy routes">
            {droppedRoutes
              ? 'The custom routes of the host you duplicated are not copied. Ask an administrator to add them after saving.'
              : 'Raw routes can bypass access lists and other protections, so they are read-only for your role. All other settings of this host can still be changed.'}
          </Callout>
        )}
        <Field
          label="Routes (JSON array)"
          error={errors.advancedRoutesJson}
          labelAction={
            !locked && (
              <Button size="xs" variant="ghost" icon={<Wand2 size={12} />} onClick={format} disabled={!form.advancedRoutesJson?.trim()}>
                Format
              </Button>
            )
          }
        >
          <Textarea
            mono
            rows={12}
            spellCheck={false}
            readOnly={locked}
            placeholder={
              locked
                ? 'No custom routes.'
                : '[\n  {\n    "match": [{ "path": ["/health"] }],\n    "handle": [{ "handler": "static_response", "status_code": 200, "body": "ok" }]\n  }\n]'
            }
            value={droppedRoutes ? '' : (form.advancedRoutesJson ?? '')}
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
