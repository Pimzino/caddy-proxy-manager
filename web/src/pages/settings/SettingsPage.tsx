import { useId, useState, type FormEvent, type ReactNode } from 'react';
import { Link, useSearchParams } from 'react-router';
import {
  useBinaryOverview,
  useBinarySettings,
  useCaddySettings,
  useDnsProviders,
  useIsManagedNode,
  useSaveBinarySettings,
  useSaveCaddySettings,
  useSaveUiSettings,
  useSystemInfo,
  useUiSettings,
} from '@/api/hooks';
import { uiSettingsInput } from '@/api/settings';
import type { AcmeCa, BinarySettings, CaddySettings, CaddySettingsInput, DefaultSiteBehavior, UiSettings, UiSettingsInput } from '@/api/types';
import { useAuth } from '@/auth';
import { useFeedback } from '@/components/feedback';
import { SecretInput, secretPayload } from '@/components/SecretInput';
import {
  Badge,
  Callout,
  Card,
  CardBody,
  CardHeader,
  ChipInput,
  DescriptionList,
  Field,
  FormSection,
  Input,
  NumberInput,
  PageHeader,
  Select,
  SwitchField,
  TabPanel,
  Tabs,
  Textarea,
  useToast,
} from '@/components/ui';
import { formatDateTime, formatDuration } from '@/lib/format';
import { isAbsoluteHttpUrl, isIpv4, isIpv6, isValidCidr, isValidEmail, isValidPort, jsonObjectError, type FieldErrors } from '@/lib/validation';
import { AcmeChallengeSection, DNS_FIELDS } from './AcmeChallengeSection';
import { BackupTab } from './BackupTab';
import { caddyFormInput, round3Payload, validateDns } from './caddyForm';
import { ClusterTab } from './ClusterTab';
import { LdapTab } from './LdapTab';
import { HiddenValue, PLUGIN_FIELDS, PluginsAdvancedSection, validatePluginFields } from './PluginsAdvancedSection';
import { RestartPanel } from './RestartPanel';
import { fieldError, Loader, SaveBar, UnplacedErrors } from './shared';

type SettingsTab = 'caddy' | 'cluster' | 'updates' | 'ui' | 'ldap' | 'backup';
const TABS: SettingsTab[] = ['caddy', 'cluster', 'updates', 'ui', 'ldap', 'backup'];
/** Tabs every role can open (read-only below admin). */
const OPEN_TABS: SettingsTab[] = ['caddy', 'cluster', 'updates'];

export default function SettingsPage() {
  const idBase = useId();
  const { isAdmin } = useAuth();
  const [params, setParams] = useSearchParams();
  const requested = params.get('tab') as SettingsTab | null;
  const tab: SettingsTab = requested && TABS.includes(requested) && (isAdmin || OPEN_TABS.includes(requested)) ? requested : 'caddy';
  return (
    <>
      <PageHeader
        title="Settings"
        description={
          isAdmin
            ? 'Global Caddy behaviour, clustering and shared storage, update policy, the management UI listener, directory sign-in and backups.'
            : 'Global settings (read-only for your role).'
        }
      />
      <Tabs
        idBase={idBase}
        aria-label="Settings sections"
        value={tab}
        onChange={(t) => setParams(t === 'caddy' ? {} : { tab: t }, { replace: true })}
        className="mb-4"
        items={[
          { value: 'caddy', label: 'Caddy' },
          { value: 'cluster', label: 'Cluster' },
          { value: 'updates', label: 'Updates' },
          { value: 'ui', label: 'Management UI', hidden: !isAdmin },
          { value: 'ldap', label: 'Directory (LDAP)', hidden: !isAdmin },
          { value: 'backup', label: 'Backups', hidden: !isAdmin },
        ]}
      />
      <TabPanel idBase={idBase} value="caddy" active={tab === 'caddy'}>
        <CaddySettingsTab />
      </TabPanel>
      <TabPanel idBase={idBase} value="cluster" active={tab === 'cluster'}>
        <ClusterTab />
      </TabPanel>
      <TabPanel idBase={idBase} value="updates" active={tab === 'updates'}>
        <UpdatesTab />
      </TabPanel>
      <TabPanel idBase={idBase} value="ui" active={tab === 'ui' && isAdmin}>
        <UiTab />
      </TabPanel>
      <TabPanel idBase={idBase} value="ldap" active={tab === 'ldap' && isAdmin}>
        <LdapTab />
      </TabPanel>
      <TabPanel idBase={idBase} value="backup" active={tab === 'backup' && isAdmin}>
        <BackupTab />
      </TabPanel>
    </>
  );
}

// ---------------------------------------------------------------- Caddy

function CaddySettingsTab() {
  const q = useCaddySettings();
  return <Loader query={q}>{(data) => <CaddySettingsForm key={JSON.stringify(data)} settings={data} />}</Loader>;
}

const ACME_CAS: { value: AcmeCa; label: string }[] = [
  { value: 'letsEncrypt', label: 'Let’s Encrypt' },
  { value: 'letsEncryptStaging', label: 'Let’s Encrypt (staging — for testing, untrusted)' },
  { value: 'zeroSsl', label: 'ZeroSSL' },
  { value: 'custom', label: 'Custom ACME directory (internal CA)' },
];

const DEFAULT_SITES: { value: DefaultSiteBehavior; label: string }[] = [
  { value: 'notFound', label: 'Respond 404 Not Found' },
  { value: 'closeConnection', label: 'Close the connection' },
  { value: 'redirect', label: 'Redirect to a URL' },
  { value: 'caddyWelcome', label: 'Show the Caddy welcome text' },
];

function isValidBindAddress(v: string) {
  return isIpv4(v) || isIpv6(v.replace(/^\[(.*)\]$/, '$1'));
}

function validateCaddy(f: CaddySettingsInput): FieldErrors {
  const e: FieldErrors = {};
  if (f.acmeEmail && !isValidEmail(f.acmeEmail)) e.acmeEmail = 'Enter a valid e-mail address or leave empty.';
  if (f.acmeCa === 'custom' && !isAbsoluteHttpUrl(f.customAcmeDirectory ?? '')) e.customAcmeDirectory = 'Enter the ACME directory URL, e.g. https://ca.corp.local/acme/acme/directory.';
  if (!isValidPort(f.httpPort)) e.httpPort = 'Port must be between 1 and 65535.';
  if (!isValidPort(f.httpsPort)) e.httpsPort = 'Port must be between 1 and 65535.';
  if (isValidPort(f.httpPort) && f.httpPort === f.httpsPort) e.httpsPort = 'HTTP and HTTPS must use different ports.';
  if (f.publicHttpsPort != null && !isValidPort(f.publicHttpsPort)) e.publicHttpsPort = 'Port must be between 1 and 65535, or leave empty.';
  if (f.defaultSite === 'redirect' && !isAbsoluteHttpUrl(f.defaultRedirectUrl ?? '')) e.defaultRedirectUrl = 'Enter an absolute http(s) URL.';
  if (!/^(\[[0-9a-f:]+\]|[a-z0-9.-]+):\d{1,5}$/i.test(f.adminListen.trim())) e.adminListen = 'Use host:port, e.g. 127.0.0.1:2019.';
  const jsonErr = jsonObjectError(f.serverOptionsJson);
  if (jsonErr) e.serverOptionsJson = jsonErr;
  if (f.disableHttpChallenge && f.disableTlsAlpnChallenge)
    e.disableTlsAlpnChallenge = 'Keep at least one challenge enabled: Caddy needs HTTP-01 or TLS-ALPN-01 to obtain ACME certificates.';
  if (!!f.eabKeyId?.trim() && f.eabMacKey === '') e.eabKeyId = 'External account binding needs both the key ID and the HMAC key.';
  return { ...e, ...validatePluginFields(f) };
}

/** Field keys the Caddy settings form shows next to a field; other server errors go to a summary. */
const CADDY_FIELDS = [
  'acmeEmail', 'customAcmeDirectory', 'customAcmeRootPath', 'eabKeyId', 'disableTlsAlpnChallenge', 'httpPort', 'httpsPort', 'publicHttpsPort',
  'bindAddresses', 'defaultRedirectUrl', 'trustedProxies', 'logLevel', 'certificateStorePath', 'adminListen', 'serverOptionsJson',
  ...PLUGIN_FIELDS,
  ...DNS_FIELDS,
];

/** Disables every control of a settings group that a managed cluster node receives from the primary (display: contents keeps the layout). */
function Replicated({ locked, children }: { locked: boolean; children: ReactNode }) {
  return (
    <fieldset disabled={locked} className="contents">
      {children}
    </fieldset>
  );
}

function CaddySettingsForm({ settings }: { settings: CaddySettings }) {
  const { isAdmin } = useAuth();
  // On a managed cluster node only CaddySettings.NodeLocalProperties (listeners, bind addresses, admin API address,
  // certificate store path) are editable; everything else is replicated from the primary.
  const { managed, primaryName } = useIsManagedNode();
  const providers = useDnsProviders();
  const initial = caddyFormInput(settings);
  const [form, setForm] = useState<CaddySettingsInput>(initial);
  const [submitted, setSubmitted] = useState(false);
  const [serverErrors, setServerErrors] = useState<FieldErrors>({});
  const save = useSaveCaddySettings();
  const feedback = useFeedback();
  const validate = (f: CaddySettingsInput): FieldErrors => ({ ...validateCaddy(f), ...(managed ? {} : validateDns(f, settings, providers.data)) });
  const errors = { ...serverErrors, ...(submitted ? validate(form) : {}) };
  const dirty = JSON.stringify(form) !== JSON.stringify(initial);
  const set = <K extends keyof CaddySettingsInput>(k: K, v: CaddySettingsInput[K]) => {
    setForm((f) => ({ ...f, [k]: v }));
    setServerErrors({});
  };
  const locked = !isAdmin || save.isPending;
  const adminHost = form.adminListen.split(':').slice(0, -1).join(':').replace(/^\[|\]$/g, '');
  const adminLoopback = ['127.0.0.1', 'localhost', '::1'].includes(adminHost);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setSubmitted(true);
    if (Object.keys(validate(form)).length) return;
    try {
      const publicHttpsPort = form.publicHttpsPort == null || Number.isNaN(form.publicHttpsPort) ? null : form.publicHttpsPort;
      if (managed) {
        // Replicated values go back exactly as received (the node rejects changes to them); write-only secrets stay absent.
        const res = await save.mutateAsync({
          ...initial,
          httpPort: form.httpPort,
          httpsPort: form.httpsPort,
          publicHttpsPort,
          bindAddresses: form.bindAddresses,
          adminListen: form.adminListen.trim(),
          certificateStorePath: form.certificateStorePath?.trim() || null,
        });
        feedback.applied(res.apply, 'Settings saved and applied');
        return;
      }
      const res = await save.mutateAsync({
        ...form,
        ...round3Payload(form, settings, providers.data),
        acmeEmail: form.acmeEmail.trim(),
        customAcmeDirectory: form.customAcmeDirectory?.trim() || null,
        customAcmeRootPath: form.customAcmeRootPath?.trim() || null,
        eabKeyId: form.eabKeyId?.trim() || null,
        eabMacKey: secretPayload(form.eabMacKey),
        defaultRedirectUrl: form.defaultRedirectUrl?.trim() || null,
        certificateStorePath: form.certificateStorePath?.trim() || null,
        serverOptionsJson: form.serverOptionsJson?.trim() || null,
        extraAppsJson: form.extraAppsJson?.trim() || null,
        tlsConnectionPolicyJson: form.tlsConnectionPolicyJson?.trim() || null,
        acmeIssuerJson: secretPayload(form.acmeIssuerJson),
        adminListen: form.adminListen.trim(),
        publicHttpsPort,
      });
      feedback.applied(res.apply, 'Settings saved and applied');
    } catch (err) {
      feedback.failed(err, { onFieldErrors: setServerErrors });
    }
  };

  return (
    <form onSubmit={(e) => void submit(e)} noValidate>
      <Card className="p-5">
        {/* Three fieldsets instead of one: the ACME challenge section disables its own inputs, so its delegation panel's Copy and
            Check DNS stay usable for every role. The hidden spans keep FormSection's first/last-child spacing and borders. */}
        <fieldset disabled={locked} className="contents">
          <UnplacedErrors errors={errors} fields={CADDY_FIELDS} />
          {managed && (
            <Callout tone="info" className="mb-5" title={`Read-only on this node — managed by ${primaryName || 'the cluster primary'}`}>
              Caddy settings are replicated from the primary; change them there. Ports, bind addresses, the admin API address and the certificate
              store path belong to this server and stay editable.
            </Callout>
          )}
          <FormSection
            title="Certificates (ACME)"
            description="Used by hosts with Automatic TLS. With the HTTP challenge, the CA must reach this server on port 80 (HTTP-01) or 443 (TLS-ALPN-01) from the Internet; the DNS challenge below needs no inbound ports."
          >
            <Replicated locked={managed}>
              <Field label="Account e-mail" error={errors.acmeEmail} hint="Receives expiry warnings from the CA. Strongly recommended.">
                <Input type="email" placeholder="hostmaster@example.com" value={form.acmeEmail} onChange={(e) => set('acmeEmail', e.target.value)} />
              </Field>
              <Field label="Certificate authority">
                <Select value={form.acmeCa} onChange={(e) => set('acmeCa', e.target.value as AcmeCa)}>
                  {ACME_CAS.map((c) => (
                    <option key={c.value} value={c.value}>
                      {c.label}
                    </option>
                  ))}
                </Select>
              </Field>
              {form.acmeCa === 'custom' && (
                <>
                  <Field label="ACME directory URL" required error={errors.customAcmeDirectory}>
                    <Input mono type="url" value={form.customAcmeDirectory ?? ''} onChange={(e) => set('customAcmeDirectory', e.target.value)} />
                  </Field>
                  <Field label="CA root certificate (path)" error={errors.customAcmeRootPath} hint="PEM file used to trust the custom CA’s HTTPS endpoint, if it is not publicly trusted.">
                    <Input mono placeholder="C:\ProgramData\pki\root.pem" value={form.customAcmeRootPath ?? ''} onChange={(e) => set('customAcmeRootPath', e.target.value)} />
                  </Field>
                </>
              )}
              <div className="grid gap-4 md:grid-cols-2">
                <Field label="EAB key ID" error={errors.eabKeyId} hint="External account binding (ZeroSSL, some commercial and private CAs).">
                  <Input mono autoComplete="off" value={form.eabKeyId ?? ''} onChange={(e) => set('eabKeyId', e.target.value)} />
                </Field>
                <Field label="EAB HMAC key">
                  <SecretInput has={settings.hasEabMacKey} value={form.eabMacKey} onChange={(v) => set('eabMacKey', v)} disabled={!isAdmin} />
                </Field>
              </div>
              <SwitchField label="Disable HTTP-01 challenge" description="Use when port 80 is not reachable from the Internet." checked={form.disableHttpChallenge} onChange={(v) => set('disableHttpChallenge', v)} />
              <SwitchField label="Disable TLS-ALPN-01 challenge" description="Use when port 443 is behind a TLS-terminating load balancer." checked={form.disableTlsAlpnChallenge} onChange={(v) => set('disableTlsAlpnChallenge', v)} />
              {form.disableHttpChallenge && form.disableTlsAlpnChallenge && (
                <Callout tone={errors.disableTlsAlpnChallenge ? 'danger' : 'warning'}>
                  Keep at least one challenge enabled: Caddy needs HTTP-01 or TLS-ALPN-01 to obtain ACME certificates.
                </Callout>
              )}
            </Replicated>
          </FormSection>
          <span hidden />
        </fieldset>

        <AcmeChallengeSection settings={settings} form={form} set={set} errors={errors} providers={providers} disabled={locked || managed} />

        <fieldset disabled={locked} className="contents">
          <span hidden />
          <FormSection title="Listeners" description="Ports Caddy listens on for all sites. Remember the Windows Firewall rules (see Readiness).">
            <div className="grid gap-4 sm:grid-cols-2 lg:max-w-md">
              <Field label="HTTP port" error={errors.httpPort}>
                <NumberInput min={1} max={65535} value={form.httpPort} onValueChange={(v) => set('httpPort', v)} />
              </Field>
              <Field label="HTTPS port" error={errors.httpsPort}>
                <NumberInput min={1} max={65535} value={form.httpsPort} onValueChange={(v) => set('httpsPort', v)} />
              </Field>
            </div>
            <Field
              label="Public HTTPS port"
              error={errors.publicHttpsPort}
              className="lg:max-w-md"
              hint="Optional. The port clients use for HTTPS when a router or firewall forwards it to the HTTPS port above (for example public 443 → 8443). Leave empty when clients connect to the HTTPS port directly."
            >
              <NumberInput
                min={1}
                max={65535}
                placeholder={isValidPort(form.httpsPort) ? String(form.httpsPort) : ''}
                value={form.publicHttpsPort}
                onValueChange={(v) => set('publicHttpsPort', Number.isNaN(v) ? null : v)}
              />
            </Field>
            {(() => {
              const port = form.publicHttpsPort != null && isValidPort(form.publicHttpsPort) ? form.publicHttpsPort : form.httpsPort;
              return isValidPort(port) ? (
                <p className="-mt-2 text-xs text-fg-subtle">
                  “Force HTTPS” redirects send clients to <span className="mono">{port === 443 ? 'https://host/' : `https://host:${port}/`}</span>
                  {port === 443 ? ' (no port: 443 is the default).' : '.'}
                </p>
              ) : null;
            })()}
            <SwitchField disabled={managed} label="HTTP/3 (QUIC)" description={`Optional. Also listen on UDP ${form.httpsPort || 443} for HTTP/3; browsers use HTTP/2 when it is off. Needs an inbound UDP firewall rule. Off by default. 0-RTT (early data) stays disabled, so hosts with IP access lists never answer “425 Too Early”.`} checked={form.enableHttp3} onChange={(v) => set('enableHttp3', v)} />
            <Field label="Bind addresses" error={fieldError(errors, 'bindAddresses')} hint="Leave empty to listen on all interfaces.">
              <ChipInput
                value={form.bindAddresses}
                onChange={(v) => set('bindAddresses', v)}
                placeholder="10.0.0.5"
                validate={(v) => (isValidBindAddress(v) ? null : 'not an IP address')}
                disabled={!isAdmin}
              />
            </Field>
          </FormSection>

          <FormSection title="Unknown hosts" description="What Caddy does with requests for a domain that no host is configured for.">
            <Replicated locked={managed}>
              <Field label="Default site">
                <Select value={form.defaultSite} onChange={(e) => set('defaultSite', e.target.value as DefaultSiteBehavior)}>
                  {DEFAULT_SITES.map((d) => (
                    <option key={d.value} value={d.value}>
                      {d.label}
                    </option>
                  ))}
                </Select>
              </Field>
              {form.defaultSite === 'redirect' && (
                <Field label="Redirect URL" required error={errors.defaultRedirectUrl}>
                  <Input mono type="url" placeholder="https://www.example.com" value={form.defaultRedirectUrl ?? ''} onChange={(e) => set('defaultRedirectUrl', e.target.value)} />
                </Field>
              )}
              <p className="text-xs text-fg-subtle">
                Over HTTPS the default site is only reached for names that have a certificate. A browser that opens https:// with an unknown name, or
                with the server’s IP address, gets a TLS error before any site runs: Caddy has no certificate for that name. To answer those requests
                too, add <span className="mono">{'{"fallback_sni": "app.example.com", "default_sni": "app.example.com"}'}</span> (a name of one of your
                HTTPS hosts) to the TLS connection policy under Plugins &amp; advanced below; clients then see that host’s certificate and the default
                site’s response.
              </p>
            </Replicated>
          </FormSection>

          <FormSection
            title="Client IPs"
            description="When Caddy is behind a load balancer or CDN, trust its X-Forwarded-For header to get the real client IP (used by access lists, IP-hash load balancing and logs). The header is read strictly from right to left: the client IP is the first address, from the right, that is not a trusted proxy. Addresses a client puts at the left of the header itself are ignored, so list every proxy in front of Caddy (all CDN ranges), or real clients are seen as the last proxy."
          >
            <Field label="Trusted proxies" error={fieldError(errors, 'trustedProxies')}>
              <ChipInput
                value={form.trustedProxies}
                onChange={(v) => set('trustedProxies', v)}
                placeholder="10.0.0.0/8"
                validate={(v) => (isValidCidr(v) && v.toLowerCase() !== 'all' ? null : 'not an IP or CIDR range')}
                disabled={!isAdmin || managed}
              />
            </Field>
          </FormSection>

          <FormSection title="Logging & storage">
            <Field label="Caddy log level" error={errors.logLevel} className="max-w-xs">
              <Select value={form.logLevel} onChange={(e) => set('logLevel', e.target.value)} disabled={managed}>
                <option value="debug">Debug (verbose)</option>
                <option value="info">Info</option>
                <option value="warn">Warning</option>
                <option value="error">Error</option>
              </Select>
            </Field>
            <Field
              label="Certificate store path"
              error={errors.certificateStorePath}
              hint="Where uploaded certificates are written. Leave empty for the default under ProgramData. A UNC share must be readable by this computer’s account."
            >
              <Input mono placeholder="C:\ProgramData\CaddyProxyManager\certificates" value={form.certificateStorePath ?? ''} onChange={(e) => set('certificateStorePath', e.target.value)} />
            </Field>
            <SwitchField
              label="Traffic statistics"
              disabled={managed}
              description={
                <>
                  Caddy writes one line per HTTP request (client IP, host, method, URI, status, bytes, duration — no headers) to a local log that
                  feeds the <Link to="/traffic" className="text-accent-text hover:underline">Traffic</Link> page. Rotated at 10 MB, 5 files kept.
                  Managed mode only; per-host access logs are not affected.
                </>
              }
              checked={form.trafficStatsEnabled}
              onChange={(v) => set('trafficStatsEnabled', v)}
            />
          </FormSection>

          <PluginsAdvancedSection settings={settings} form={form} set={set} errors={errors} isAdmin={isAdmin} readOnly={managed} />

          <FormSection title="Advanced">
            <Field label="Configuration mode">
              <div className="flex items-center gap-2 text-sm">
                <Badge tone={form.mode === 'managed' ? 'accent' : 'warning'}>{form.mode === 'managed' ? 'Managed' : 'Caddyfile'}</Badge>
                <Link to="/caddy/config" className="text-accent-text hover:underline">
                  Change on the Configuration page
                </Link>
              </div>
            </Field>
            <Field label="Admin API listen address" error={errors.adminListen} hint="The manager controls Caddy through this endpoint.">
              <Input mono value={form.adminListen} onChange={(e) => set('adminListen', e.target.value)} className="max-w-xs" />
            </Field>
            {!adminLoopback && !errors.adminListen && (
              <Callout tone="danger" title="The admin API is not on loopback">
                Anyone who can reach this address can reconfigure Caddy without authentication. Keep it on 127.0.0.1.
              </Callout>
            )}
            <Field label="Server options (JSON)" error={errors.serverOptionsJson} hint="Merged into every apps.http.servers entry, e.g. timeouts or max_header_bytes. Leave empty unless you know you need it.">
              {isAdmin || form.serverOptionsJson !== undefined ? (
                <Textarea mono rows={5} spellCheck={false} disabled={managed} placeholder={'{\n  "timeouts": { "read_header": "10s" }\n}'} value={form.serverOptionsJson ?? ''} onChange={(e) => set('serverOptionsJson', e.target.value)} />
              ) : (
                <HiddenValue />
              )}
            </Field>
          </FormSection>
        </fieldset>
        <div className="mt-5 border-t border-border pt-4">
          <SaveBar dirty={dirty} saving={save.isPending} onReset={() => setForm(initial)} readOnly={!isAdmin} />
        </div>
      </Card>
    </form>
  );
}

// ---------------------------------------------------------------- Updates

function UpdatesTab() {
  const q = useBinarySettings();
  return <Loader query={q}>{(data) => <UpdatesForm key={JSON.stringify(data)} settings={data} />}</Loader>;
}

const REPO_RE = /^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})\/[A-Za-z0-9._-]{1,100}$/;

/** Accepts "owner/repo" or a pasted GitHub URL. */
function normalizeRepo(v: string | null | undefined): string {
  return (v ?? '')
    .trim()
    .replace(/^https?:\/\/(www\.)?github\.com\//i, '')
    .replace(/\.git$/i, '')
    .replace(/\/+$/, '');
}

function UpdatesForm({ settings }: { settings: BinarySettings }) {
  const { isAdmin } = useAuth();
  const binary = useBinaryOverview();
  const versionText = binary.data?.managerVersion ? `v${binary.data.managerVersion.replace(/^v/, '')}` : null;
  const [form, setForm] = useState<BinarySettings>(settings);
  const [submitted, setSubmitted] = useState(false);
  const save = useSaveBinarySettings();
  const feedback = useFeedback();
  const toast = useToast();
  const validate = (f: BinarySettings): FieldErrors => {
    const e: FieldErrors = {};
    if (!(Number.isInteger(f.checkIntervalHours) && f.checkIntervalHours >= 1 && f.checkIntervalHours <= 168)) e.checkIntervalHours = 'Enter 1–168 hours.';
    if (f.outboundProxy && !isAbsoluteHttpUrl(f.outboundProxy)) e.outboundProxy = 'Enter a proxy URL such as http://proxy.corp.local:8080.';
    if (f.proxyCaddyTraffic && !f.outboundProxy?.trim()) e.proxyCaddyTraffic = 'Enter the proxy URL above first.';
    const badNoProxy = (f.noProxy ?? '').split(',').map((x) => x.trim()).filter((x) => x && /\s|\/\/|[;]/.test(x));
    if (badNoProxy.length) e.noProxy = `Separate entries with commas; not valid: ${badNoProxy.join(', ')}`;
    const repo = normalizeRepo(f.managerReleaseRepo);
    if (repo && !REPO_RE.test(repo)) e.managerReleaseRepo = 'Use the GitHub “owner/repository” form, e.g. contoso/caddy-proxy-manager.';
    return e;
  };
  const errors = submitted ? validate(form) : {};
  const dirty = JSON.stringify(form) !== JSON.stringify(settings);
  const set = <K extends keyof BinarySettings>(k: K, v: BinarySettings[K]) => setForm((f) => ({ ...f, [k]: v }));

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setSubmitted(true);
    if (Object.keys(validate(form)).length) return;
    try {
      await save.mutateAsync({
        ...form,
        outboundProxy: form.outboundProxy?.trim() || null,
        noProxy: (form.noProxy ?? '')
          .split(',')
          .map((x) => x.trim())
          .filter(Boolean)
          .join(','),
        managerReleaseRepo: normalizeRepo(form.managerReleaseRepo) || null,
      });
      toast.success('Update settings saved');
    } catch (err) {
      feedback.failed(err);
    }
  };

  return (
    <form onSubmit={(e) => void submit(e)} noValidate>
      <Card className="p-5">
        <fieldset disabled={!isAdmin || save.isPending} className="min-w-0">
          <FormSection title="Update checks" description="The manager checks GitHub for new Caddy releases and raises an event when one is available.">
            <SwitchField label="Check for updates automatically" checked={form.autoCheckUpdates} onChange={(v) => set('autoCheckUpdates', v)} />
            <Field label="Check interval (hours)" error={errors.checkIntervalHours} className="max-w-xs">
              <NumberInput min={1} max={168} value={form.checkIntervalHours} onValueChange={(v) => set('checkIntervalHours', v)} disabled={!form.autoCheckUpdates} />
            </Field>
            <SwitchField
              label="Install updates automatically"
              description="Off by default. When on, new releases are installed after validation, with automatic rollback if Caddy fails to start."
              checked={form.autoInstallUpdates}
              onChange={(v) => set('autoInstallUpdates', v)}
              disabled={!form.autoCheckUpdates}
            />
            <DescriptionList
              items={[
                { label: 'Last checked', value: formatDateTime(form.lastCheckedAt) },
                { label: 'Latest known version', value: form.latestKnownVersion ?? '—', mono: true },
                {
                  label: 'Plugins',
                  value: (
                    <Link to="/caddy/plugins" className="text-accent-text hover:underline">
                      {form.plugins.length ? `${form.plugins.length} selected` : 'Standard build'} — manage
                    </Link>
                  ),
                },
              ]}
            />
          </FormSection>
          <FormSection title="Outbound proxy" description="Used by the manager for downloads from GitHub and caddyserver.com and for update checks. Optionally also given to Caddy for ACME and DNS-provider API calls.">
            <Field label="Proxy URL" error={errors.outboundProxy} hint="Leave empty for a direct connection.">
              <Input mono placeholder="http://proxy.corp.local:8080" value={form.outboundProxy ?? ''} onChange={(e) => set('outboundProxy', e.target.value)} />
            </Field>
            <div className="flex flex-col gap-1.5">
              <SwitchField
                label="Send Caddy’s own traffic through the proxy"
                description="Sets HTTPS_PROXY and HTTP_PROXY in the Caddy service environment so certificates can be obtained behind a corporate proxy. Takes effect when Caddy next starts."
                checked={form.proxyCaddyTraffic}
                onChange={(v) => set('proxyCaddyTraffic', v)}
                disabled={!form.outboundProxy?.trim() && !form.proxyCaddyTraffic}
              />
              {errors.proxyCaddyTraffic && (
                <p className="text-xs text-danger" role="alert">
                  {errors.proxyCaddyTraffic}
                </p>
              )}
            </div>
            {form.proxyCaddyTraffic && (
              <Field
                label="Bypass the proxy for (NO_PROXY)"
                error={errors.noProxy}
                hint="Comma-separated host names, domain suffixes (.corp.local) and CIDR ranges. Every upstream must match an entry, otherwise Caddy sends backend traffic through the proxy too."
              >
                <Input mono value={form.noProxy ?? ''} onChange={(e) => set('noProxy', e.target.value)} placeholder="localhost,127.0.0.1,::1,10.0.0.0/8,.corp.local" />
              </Field>
            )}
          </FormSection>
          <FormSection title="Manager updates" description="Checks a GitHub repository for new releases of Caddy Proxy Manager itself and shows a notice on the dashboard. Updating the manager is done with the installer.">
            <Field
              label="Release repository"
              error={errors.managerReleaseRepo}
              hint={
                <>
                  GitHub <span className="mono">owner/repository</span>. Leave empty to disable the check.
                  {versionText && <> Installed: <span className="mono">{versionText}</span>.</>}
                </>
              }
            >
              <Input mono placeholder="contoso/caddy-proxy-manager" value={form.managerReleaseRepo ?? ''} onChange={(e) => set('managerReleaseRepo', e.target.value)} />
            </Field>
          </FormSection>
        </fieldset>
        <div className="mt-5 border-t border-border pt-4">
          <SaveBar dirty={dirty} saving={save.isPending} onReset={() => setForm(settings)} readOnly={!isAdmin} />
        </div>
      </Card>
    </form>
  );
}

// ---------------------------------------------------------------- Management UI

function UiTab() {
  const q = useUiSettings();
  const system = useSystemInfo();
  const [restart, setRestart] = useState<{ nextUrl: string } | null>(null);
  return (
    <div className="flex flex-col gap-4">
      {/* Lives outside the form: the form re-mounts with the saved values after every save. */}
      {restart && <RestartPanel reason="The new listener settings take effect when the management service restarts." nextUrl={restart.nextUrl} />}
      <Loader query={q}>{(data) => <UiForm key={JSON.stringify(data)} settings={data} onRestartRequired={(nextUrl) => setRestart({ nextUrl })} />}</Loader>
      {system.data && (
        <Card>
          <CardHeader title="About this installation" />
          <CardBody>
            <DescriptionList
              columns={2}
              items={[
                { label: 'Product', value: system.data.product },
                { label: 'Version', value: system.data.version, mono: true },
                { label: 'Server', value: system.data.machineName, mono: true },
                { label: 'Operating system', value: system.data.os },
                { label: 'Running as', value: system.data.isService ? 'Windows service (LocalSystem)' : `Console (${system.data.hostMode})` },
                { label: 'Uptime', value: formatDuration(system.data.uptimeSeconds) },
                { label: 'Install folder', value: system.data.installDir, mono: true },
                { label: 'Data folder', value: system.data.dataDir, mono: true },
              ]}
            />
          </CardBody>
        </Card>
      )}
    </div>
  );
}

function UiForm({ settings, onRestartRequired }: { settings: UiSettings; onRestartRequired: (nextUrl: string) => void }) {
  const initial = uiSettingsInput(settings);
  const [form, setForm] = useState<UiSettingsInput>(initial);
  const [submitted, setSubmitted] = useState(false);
  const [bindMode, setBindMode] = useState<'all' | 'local' | 'custom'>(
    settings.bindAddress === '0.0.0.0' ? 'all' : settings.bindAddress === '127.0.0.1' ? 'local' : 'custom',
  );
  const save = useSaveUiSettings();
  const feedback = useFeedback();
  const toast = useToast();

  const validate = (f: UiSettingsInput): FieldErrors => {
    const e: FieldErrors = {};
    if (!isValidPort(f.port)) e.port = 'Port must be between 1 and 65535.';
    if (f.httpsEnabled && !isValidPort(f.httpsPort)) e.httpsPort = 'Port must be between 1 and 65535.';
    if (f.httpsEnabled && f.httpsPort === f.port) e.httpsPort = 'Use a different port than HTTP.';
    if (!isIpv4(f.bindAddress) && !isIpv6(f.bindAddress)) e.bindAddress = 'Enter an IP address of this server.';
    if (!(f.sessionHours >= 1 && f.sessionHours <= 720)) e.sessionHours = 'Enter 1–720 hours.';
    return e;
  };
  const errors = submitted ? validate(form) : {};
  const dirty = JSON.stringify(form) !== JSON.stringify(initial);
  const set = <K extends keyof UiSettingsInput>(k: K, v: UiSettingsInput[K]) => setForm((f) => ({ ...f, [k]: v }));

  const nextUrl = () => {
    const host = window.location.hostname.includes(':') ? `[${window.location.hostname}]` : window.location.hostname;
    return form.httpsEnabled ? `https://${host}:${form.httpsPort}/settings?tab=ui` : `http://${host}:${form.port}/settings?tab=ui`;
  };

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setSubmitted(true);
    if (Object.keys(validate(form)).length) return;
    try {
      const url = nextUrl();
      const res = await save.mutateAsync({
        ...form,
        displayName: form.displayName?.trim() || null,
        httpsPfxPath: form.httpsPfxPath?.trim() || null,
        httpsPfxPassword: secretPayload(form.httpsPfxPassword),
        redirectHttpToHttps: form.httpsEnabled && form.redirectHttpToHttps,
      });
      toast.success('Management UI settings saved');
      if (res.restartRequired) onRestartRequired(url);
    } catch (err) {
      feedback.failed(err);
    }
  };

  return (
    <form onSubmit={(e) => void submit(e)} noValidate className="flex flex-col gap-4">
      <Card className="p-5">
        <fieldset disabled={save.isPending} className="min-w-0">
          <FormSection title="General">
            <Field label="Display name" hint="Shown in alert e-mails, e.g. “DMZ proxy – London”.">
              <Input value={form.displayName ?? ''} onChange={(e) => set('displayName', e.target.value)} />
            </Field>
            <Field label="Session length (hours)" error={errors.sessionHours} className="max-w-xs">
              <NumberInput min={1} max={720} value={form.sessionHours} onValueChange={(v) => set('sessionHours', v)} />
            </Field>
          </FormSection>
          <FormSection title="Listener" description="Changing these requires a restart of the management service. Make sure the new port is allowed in the firewall first.">
            <Field label="Listen on" error={errors.bindAddress}>
              <div className="flex flex-col gap-2">
                <Select
                  value={bindMode}
                  onChange={(e) => {
                    const m = e.target.value as typeof bindMode;
                    setBindMode(m);
                    if (m === 'all') set('bindAddress', '0.0.0.0');
                    else if (m === 'local') set('bindAddress', '127.0.0.1');
                  }}
                >
                  <option value="all">All interfaces (0.0.0.0)</option>
                  <option value="local">This server only (127.0.0.1)</option>
                  <option value="custom">Specific address…</option>
                </Select>
                {bindMode === 'custom' && <Input mono aria-label="Bind address" value={form.bindAddress} onChange={(e) => set('bindAddress', e.target.value)} />}
              </div>
            </Field>
            <Field label="HTTP port" error={errors.port} hint="Default 81." className="max-w-xs">
              <NumberInput min={1} max={65535} value={form.port} onValueChange={(v) => set('port', v)} />
            </Field>
          </FormSection>
          <FormSection title="HTTPS" description="Serve the console over HTTPS. Without a PFX, a self-signed certificate is generated.">
            <SwitchField label="Enable HTTPS" checked={form.httpsEnabled} onChange={(v) => set('httpsEnabled', v)} />
            <SwitchField
              label="Redirect HTTP to HTTPS"
              description={
                form.httpsEnabled
                  ? `Plain-HTTP requests on port ${Number.isFinite(form.port) ? form.port : 'HTTP'} are redirected to HTTPS, so sign-in cookies never travel unencrypted.`
                  : 'Available when HTTPS is enabled.'
              }
              checked={form.httpsEnabled && form.redirectHttpToHttps}
              onChange={(v) => set('redirectHttpToHttps', v)}
              disabled={!form.httpsEnabled}
            />
            {form.httpsEnabled && (
              <>
                <Field label="HTTPS port" error={errors.httpsPort} className="max-w-xs">
                  <NumberInput min={1} max={65535} value={form.httpsPort} onValueChange={(v) => set('httpsPort', v)} />
                </Field>
                <Field label="PFX certificate path" hint="Optional. Local path readable by LocalSystem.">
                  <Input mono placeholder="C:\ProgramData\CaddyProxyManager\ui.pfx" value={form.httpsPfxPath ?? ''} onChange={(e) => set('httpsPfxPath', e.target.value)} />
                </Field>
                <Field label="PFX password">
                  <SecretInput has={settings.hasHttpsPfxPassword} value={form.httpsPfxPassword} onChange={(v) => set('httpsPfxPassword', v)} />
                </Field>
              </>
            )}
          </FormSection>
        </fieldset>
        <div className="mt-5 border-t border-border pt-4">
          <SaveBar dirty={dirty} saving={save.isPending} onReset={() => setForm(initial)} />
        </div>
      </Card>
    </form>
  );
}
