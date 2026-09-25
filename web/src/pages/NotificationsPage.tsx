import { useState, type FormEvent } from 'react';
import { Save, Send } from 'lucide-react';
import { errorMessage } from '@/api/client';
import { notificationSettingsInput } from '@/api/settings';
import { useNotificationSettings, useSaveNotificationSettings, useTestNotifications } from '@/api/hooks';
import type { NotificationSettings, NotificationSettingsInput, NotificationTestResult, SmtpAuthMode, SmtpSecurity, WebhookFormat } from '@/api/types';
import { useFeedback } from '@/components/feedback';
import { SecretInput, secretPayload } from '@/components/SecretInput';
import {
  Button,
  Callout,
  Card,
  ChipInput,
  CodeBlock,
  Field,
  FormSection,
  Input,
  LoadingBlock,
  NumberInput,
  PageHeader,
  Segmented,
  Select,
  SwitchField,
  useToast,
} from '@/components/ui';
import { isAbsoluteHttpUrl, isValidEmail, isValidHostname, isValidPort, type FieldErrors } from '@/lib/validation';

export default function NotificationsPage() {
  const settings = useNotificationSettings();
  if (settings.isPending) return <LoadingBlock />;
  if (settings.isError)
    return (
      <Callout tone="danger" title="Could not load notification settings">
        {errorMessage(settings.error)}
      </Callout>
    );
  return <NotificationsForm key={JSON.stringify(settings.data)} settings={settings.data} />;
}

const SECURITY_OPTIONS: { value: SmtpSecurity; label: string; port: number }[] = [
  { value: 'startTls', label: 'STARTTLS (port 587)', port: 587 },
  { value: 'sslOnConnect', label: 'SSL/TLS on connect (port 465)', port: 465 },
  { value: 'auto', label: 'Automatic', port: 587 },
  { value: 'none', label: 'None — unencrypted (port 25)', port: 25 },
];

/** Exchange Online SMTP endpoints (smtp.office365.com, *.mail.protection.outlook.com, smtp-mail.outlook.com) — same rule as the server. */
function isExchangeOnline(host: string | null | undefined): boolean {
  const h = (host ?? '').trim().replace(/\.$/, '').toLowerCase();
  return h.endsWith('.office365.com') || h.endsWith('.outlook.com');
}

const AUTH_OPTIONS: { value: SmtpAuthMode; label: string }[] = [
  { value: 'none', label: 'None (relay)' },
  { value: 'password', label: 'Password' },
  { value: 'oAuth2ClientCredentials', label: 'Microsoft 365 OAuth2' },
];

const WEBHOOK_FORMATS: { value: WebhookFormat; label: string; hint: string }[] = [
  { value: 'generic', label: 'Generic JSON', hint: 'POSTs {"text": "…", …} with the event fields — for scripts, n8n, Power Automate HTTP triggers and similar.' },
  { value: 'slack', label: 'Slack incoming webhook', hint: 'Formatted message for a Slack incoming webhook (https://hooks.slack.com/services/…).' },
  {
    value: 'teamsWorkflow',
    label: 'Microsoft Teams (Workflows)',
    hint: 'Adaptive Card for the Teams Workflows template “Post to a channel when a webhook request is received”. Legacy Office 365 connector URLs are retired by Microsoft.',
  },
];

const GUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

const M365_SCRIPT = `# Exchange Online PowerShell (Connect-ExchangeOnline), as an Exchange administrator.
# <AppId>     = Application (client) ID of the app registration
# <ObjectId>  = Object ID of the Enterprise application (service principal) — not of the app registration
New-ServicePrincipal -AppId <AppId> -ObjectId <ObjectId> -DisplayName "Caddy Proxy Manager SMTP"
Add-MailboxPermission -Identity caddy-alerts@example.com -User <ObjectId> -AccessRights FullAccess
Set-CASMailbox -Identity caddy-alerts@example.com -SmtpClientAuthenticationDisabled $false`;

function validate(f: NotificationSettingsInput, hasClientSecret: boolean, hasPassword: boolean): FieldErrors {
  const e: FieldErrors = {};
  if (f.smtpEnabled) {
    if (f.smtpAuth === 'oAuth2ClientCredentials') {
      const tenant = f.oAuthTenantId?.trim() ?? '';
      if (!tenant) e.oAuthTenantId = 'Enter the Directory (tenant) ID or the tenant domain, e.g. contoso.onmicrosoft.com.';
      else if (!GUID_RE.test(tenant) && !isValidHostname(tenant)) e.oAuthTenantId = 'Use the tenant ID (GUID) or a verified domain such as contoso.onmicrosoft.com.';
      if (!f.oAuthClientId?.trim()) e.oAuthClientId = 'Enter the Application (client) ID of the app registration.';
      else if (!GUID_RE.test(f.oAuthClientId.trim())) e.oAuthClientId = 'The client ID is a GUID, e.g. 11111111-2222-3333-4444-555555555555.';
      if (!hasClientSecret && !secretPayload(f.oAuthClientSecret)) e.oAuthClientSecret = 'Enter a client secret of the app registration.';
      if (f.oAuthClientSecret === '') e.oAuthClientSecret = 'OAuth2 needs a client secret. Choose another sign-in method to remove it.';
      if (!isValidEmail(f.smtpUsername ?? '')) e.smtpUsername = 'Enter the mailbox that sends the alerts, e.g. caddy-alerts@example.com.';
    }
    if (f.smtpAuth === 'password' && !f.smtpUsername?.trim() && (hasPassword || secretPayload(f.smtpPassword)))
      e.smtpUsername = 'Enter the user name for the password, or choose “None” for an anonymous relay.';
    if (!f.smtpHost.trim()) e.smtpHost = 'Enter the SMTP server.';
    else if (!isValidHostname(f.smtpHost.trim()) && !/^[\d.:[\]a-f]+$/i.test(f.smtpHost.trim())) e.smtpHost = 'Not a valid host name.';
    if (!isValidPort(f.smtpPort)) e.smtpPort = 'Port must be between 1 and 65535.';
    if (!isValidEmail(f.smtpFrom)) e.smtpFrom = 'Enter the sender address, e.g. caddy@example.com.';
    if (f.recipients.length === 0) e.recipients = 'Add at least one recipient.';
  }
  if (f.webhookEnabled && !isAbsoluteHttpUrl(f.webhookUrl ?? '')) e.webhookUrl = 'Enter an absolute http(s) URL.';
  if (!(f.cooldownMinutes >= 0)) e.cooldownMinutes = 'Enter 0 or more minutes.';
  if (f.alertCertificateExpiry && !(f.certificateExpiryDays >= 1 && f.certificateExpiryDays <= 365)) e.certificateExpiryDays = 'Enter 1–365 days.';
  return e;
}

function NotificationsForm({ settings }: { settings: NotificationSettings }) {
  const initial = notificationSettingsInput(settings);
  const [form, setForm] = useState<NotificationSettingsInput>(initial);
  const [submitted, setSubmitted] = useState(false);
  const [serverErrors, setServerErrors] = useState<FieldErrors>({});
  const [testResult, setTestResult] = useState<NotificationTestResult | null>(null);
  const save = useSaveNotificationSettings();
  const test = useTestNotifications();
  const toast = useToast();
  const feedback = useFeedback();
  const check = (f: NotificationSettingsInput) => validate(f, settings.hasOAuthClientSecret, settings.hasSmtpPassword);
  const errors = { ...serverErrors, ...(submitted ? check(form) : {}) };
  const dirty = JSON.stringify(form) !== JSON.stringify(initial);
  const set = <K extends keyof NotificationSettingsInput>(k: K, v: NotificationSettingsInput[K]) => {
    setForm((f) => ({ ...f, [k]: v }));
    setServerErrors({});
  };

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setSubmitted(true);
    if (Object.keys(check(form)).length) return;
    try {
      // The form re-mounts with the saved values afterwards, so use mutateAsync (not per-call callbacks).
      await save.mutateAsync({
        ...form,
        smtpHost: form.smtpHost.trim(),
        smtpFrom: form.smtpFrom.trim(),
        smtpUsername: form.smtpAuth === 'none' ? null : form.smtpUsername?.trim() || null,
        webhookUrl: form.webhookUrl?.trim() || null,
        smtpPassword: secretPayload(form.smtpPassword),
        oAuthTenantId: form.oAuthTenantId?.trim() || null,
        oAuthClientId: form.oAuthClientId?.trim() || null,
        oAuthClientSecret: secretPayload(form.oAuthClientSecret),
      });
      toast.success('Notification settings saved');
    } catch (err) {
      feedback.failed(err, { onFieldErrors: setServerErrors });
    }
  };

  const runTest = () => {
    setTestResult(null);
    test.mutate(undefined, {
      onSuccess: setTestResult,
      onError: (err) => feedback.failed(err, { title: 'Test failed to run' }),
    });
  };

  const anyChannel = settings.smtpEnabled || settings.webhookEnabled || settings.writeWindowsEventLog;
  return (
    <form onSubmit={(e) => void submit(e)} noValidate>
      <PageHeader
        title="Notifications"
        description="How the manager alerts you about outages, rejected configurations, unhealthy upstreams, expiring certificates and updates."
        actions={
          <>
            <Button icon={<Send size={14} />} onClick={runTest} loading={test.isPending} disabled={dirty || !anyChannel} title={dirty ? 'Save first — the test uses the saved settings' : undefined}>
              Send test
            </Button>
            <Button type="submit" variant="primary" icon={<Save size={14} />} loading={save.isPending} disabled={!dirty}>
              Save
            </Button>
          </>
        }
      />
      {testResult && (
        <Callout
          tone={testResult.ok ? 'success' : 'danger'}
          className="mb-4"
          title={testResult.ok ? 'Test notification sent through all enabled channels' : 'The test notification could not be delivered'}
        >
          {testResult.errors.length > 0 && (
            <ul className="list-disc pl-4">
              {testResult.errors.map((m, i) => (
                <li key={i} className="break-words">
                  {m}
                </li>
              ))}
            </ul>
          )}
        </Callout>
      )}
      {Object.keys(serverErrors).some((k) => !(k in form)) && (
        <Callout tone="danger" className="mb-4" title="Please correct the highlighted fields">
          {Object.entries(serverErrors)
            .filter(([k]) => !(k in form))
            .map(([k, m]) => (
              <p key={k}>{m}</p>
            ))}
        </Callout>
      )}
      <Card className="p-5">
        <FormSection title="E-mail (SMTP)" description="Sent with MailKit. Works with Exchange relays, Microsoft 365 (OAuth2, since basic authentication is being retired) and most SMTP services.">
          <SwitchField label="Send e-mail alerts" checked={form.smtpEnabled} onChange={(v) => set('smtpEnabled', v)} />
          <fieldset disabled={!form.smtpEnabled} className="flex min-w-0 flex-col gap-4 disabled:opacity-60">
            <div className="grid gap-4 md:grid-cols-[minmax(0,1fr)_120px_minmax(0,1fr)]">
              <Field label="SMTP server" required error={errors.smtpHost}>
                <Input mono placeholder="smtp.office365.com" value={form.smtpHost} onChange={(e) => set('smtpHost', e.target.value)} />
              </Field>
              <Field label="Port" required error={errors.smtpPort}>
                <NumberInput min={1} max={65535} value={form.smtpPort} onValueChange={(v) => set('smtpPort', v)} />
              </Field>
              <Field label="Security">
                <Select
                  value={form.smtpSecurity}
                  onChange={(e) => {
                    const sec = e.target.value as SmtpSecurity;
                    const known = SECURITY_OPTIONS.map((o) => o.port);
                    setForm((f) => ({
                      ...f,
                      smtpSecurity: sec,
                      smtpPort: known.includes(f.smtpPort) ? (SECURITY_OPTIONS.find((o) => o.value === sec)?.port ?? f.smtpPort) : f.smtpPort,
                    }));
                  }}
                >
                  {SECURITY_OPTIONS.map((o) => (
                    <option key={o.value} value={o.value}>
                      {o.label}
                    </option>
                  ))}
                </Select>
              </Field>
            </div>
            {form.smtpSecurity === 'auto' && (
              <p className="-mt-2 text-xs text-fg-subtle">
                Automatic uses TLS when the server offers it. When a password or OAuth2 token is sent, TLS is required (TLS on connect for port 465,
                STARTTLS otherwise): a server that offers no TLS gets an error, never the credentials. Without sign-in, Automatic may send mail
                unencrypted to a relay that offers no TLS.
              </p>
            )}
            <Field label="Sign-in">
              <Segmented aria-label="SMTP authentication" value={form.smtpAuth ?? 'password'} onChange={(v) => set('smtpAuth', v)} options={AUTH_OPTIONS} />
            </Field>
            {(form.smtpAuth ?? 'password') === 'password' && isExchangeOnline(form.smtpHost) && (
              <Callout tone="warning" title="Microsoft 365 is retiring password sign-in for SMTP">
                Microsoft disables Basic authentication (user name and password) for SMTP AUTH by default for existing tenants at the end of December
                2026; administrators can re-enable it only until it is removed, and new tenants do not offer it. Alert e-mails through{' '}
                <span className="mono">{form.smtpHost.trim()}</span> will then fail. Switch Sign-in to <strong>Microsoft 365 OAuth2</strong>.
              </Callout>
            )}
            {form.smtpAuth === 'none' && (
              <p className="-mt-2 text-xs text-fg-subtle">No authentication — for an internal relay (Exchange receive connector) that accepts mail from this server’s IP address.</p>
            )}
            {(form.smtpAuth ?? 'password') === 'password' && (
              <div className="grid gap-4 md:grid-cols-2">
                <Field label="User name" error={errors.smtpUsername}>
                  <Input autoComplete="off" value={form.smtpUsername ?? ''} onChange={(e) => set('smtpUsername', e.target.value)} />
                </Field>
                <Field label="Password">
                  <SecretInput has={settings.hasSmtpPassword} value={form.smtpPassword} onChange={(v) => set('smtpPassword', v)} disabled={!form.smtpEnabled} />
                </Field>
              </div>
            )}
            {form.smtpAuth === 'oAuth2ClientCredentials' && (
              <>
                <Field
                  label="Mailbox"
                  required
                  error={errors.smtpUsername}
                  hint="The Exchange Online mailbox the alerts are sent from (SMTP AUTH user name). Use it as the From address too."
                >
                  <Input type="email" autoComplete="off" placeholder="caddy-alerts@example.com" value={form.smtpUsername ?? ''} onChange={(e) => set('smtpUsername', e.target.value)} />
                </Field>
                <div className="grid gap-4 md:grid-cols-2">
                  <Field label="Directory (tenant) ID" required error={errors.oAuthTenantId}>
                    <Input mono autoComplete="off" placeholder="contoso.onmicrosoft.com or GUID" value={form.oAuthTenantId ?? ''} onChange={(e) => set('oAuthTenantId', e.target.value)} />
                  </Field>
                  <Field label="Application (client) ID" required error={errors.oAuthClientId}>
                    <Input mono autoComplete="off" placeholder="00000000-0000-0000-0000-000000000000" value={form.oAuthClientId ?? ''} onChange={(e) => set('oAuthClientId', e.target.value)} />
                  </Field>
                </div>
                <Field label="Client secret" required error={errors.oAuthClientSecret} hint="Client secrets expire (at most 24 months). Put a reminder in your calendar — expired secrets make alert e-mails fail.">
                  <SecretInput has={settings.hasOAuthClientSecret} value={form.oAuthClientSecret} onChange={(v) => set('oAuthClientSecret', v)} disabled={!form.smtpEnabled} />
                </Field>
                <Callout tone="info" title="Setting up Microsoft 365 (once per tenant)">
                  <ol className="mt-1 list-decimal space-y-1 pl-4">
                    <li>
                      In Microsoft Entra ID › App registrations, register a single-tenant app and create a client secret.
                    </li>
                    <li>
                      API permissions › Add a permission › APIs my organization uses › <span className="mono">Office 365 Exchange Online</span> ›
                      Application permissions › <span className="mono">SMTP.SendAsApp</span>, then grant admin consent.
                    </li>
                    <li>Register the app’s service principal in Exchange and give it access to the mailbox:</li>
                  </ol>
                  <CodeBlock code={M365_SCRIPT} language="powershell" title="Exchange Online PowerShell" wrap maxHeight={220} className="mt-2" />
                  <p className="mt-2">Use server <span className="mono">smtp.office365.com</span>, port 587, STARTTLS.</p>
                </Callout>
              </>
            )}
            <Field label="From address" required error={errors.smtpFrom}>
              <Input type="email" placeholder="caddy-alerts@example.com" value={form.smtpFrom} onChange={(e) => set('smtpFrom', e.target.value)} />
            </Field>
            <Field label="Recipients" required error={errors.recipients} hint="Press Enter after each address.">
              <ChipInput
                value={form.recipients}
                onChange={(v) => set('recipients', v)}
                placeholder="ops@example.com"
                mono={false}
                normalize={(s) => s.trim().toLowerCase()}
                validate={(v) => (isValidEmail(v) ? null : 'not a valid e-mail address')}
                disabled={!form.smtpEnabled}
              />
            </Field>
            <SwitchField
              label="Accept invalid server certificates"
              description="Only for internal relays with self-signed certificates."
              checked={form.allowInvalidCertificate}
              onChange={(v) => set('allowInvalidCertificate', v)}
              disabled={!form.smtpEnabled}
            />
          </fieldset>
        </FormSection>
        <FormSection title="Webhook" description="POSTs each alert as JSON to a chat channel or automation endpoint.">
          <SwitchField label="Send webhook alerts" checked={form.webhookEnabled} onChange={(v) => set('webhookEnabled', v)} />
          <Field label="Format" hint={WEBHOOK_FORMATS.find((w) => w.value === (form.webhookFormat ?? 'generic'))?.hint}>
            <Select value={form.webhookFormat ?? 'generic'} onChange={(e) => set('webhookFormat', e.target.value as WebhookFormat)} disabled={!form.webhookEnabled} className="max-w-sm">
              {WEBHOOK_FORMATS.map((w) => (
                <option key={w.value} value={w.value}>
                  {w.label}
                </option>
              ))}
            </Select>
          </Field>
          <Field label="Webhook URL" error={errors.webhookUrl}>
            <Input mono type="url" placeholder="https://…" value={form.webhookUrl ?? ''} onChange={(e) => set('webhookUrl', e.target.value)} disabled={!form.webhookEnabled} />
          </Field>
        </FormSection>
        <FormSection title="Windows Event Log" description="Writes events to the Application log (source “Caddy Proxy Manager”) for SCOM, Splunk or similar collectors.">
          <SwitchField label="Write to the Windows Event Log" checked={form.writeWindowsEventLog} onChange={(v) => set('writeWindowsEventLog', v)} />
        </FormSection>
        <FormSection title="Alert rules" description="Which events send notifications. All events are always recorded on the Events page.">
          <SwitchField label="Caddy is down" description="Service stopped or admin API unreachable." checked={form.alertCaddyDown} onChange={(v) => set('alertCaddyDown', v)} />
          <SwitchField label="Configuration rejected" description="Caddy refused a generated configuration." checked={form.alertConfigFailure} onChange={(v) => set('alertConfigFailure', v)} />
          <SwitchField label="Upstream unhealthy" description="A proxy backend fails health checks." checked={form.alertUpstreamUnhealthy} onChange={(v) => set('alertUpstreamUnhealthy', v)} />
          <div className="flex flex-col gap-3">
            <SwitchField label="Certificate expiring" checked={form.alertCertificateExpiry} onChange={(v) => set('alertCertificateExpiry', v)} />
            {form.alertCertificateExpiry && (
              <Field label="Warn this many days before expiry" error={errors.certificateExpiryDays} className="max-w-xs">
                <NumberInput min={1} max={365} value={form.certificateExpiryDays} onValueChange={(v) => set('certificateExpiryDays', v)} />
              </Field>
            )}
          </div>
          <SwitchField label="Caddy update available" checked={form.alertUpdateAvailable} onChange={(v) => set('alertUpdateAvailable', v)} />
          <SwitchField label="Readiness check failures" description="New failures found by the daily readiness run." checked={form.alertReadinessFailure} onChange={(v) => set('alertReadinessFailure', v)} />
          <SwitchField
            label="Server offline"
            description="A cluster node stopped answering the primary (3 missed heartbeats, about 45 s). Only sent by a primary."
            checked={form.alertServerOffline}
            onChange={(v) => set('alertServerOffline', v)}
          />
        </FormSection>
        <FormSection title="Behaviour">
          <SwitchField
            label="Restart Caddy automatically"
            description="When Caddy is down, try to start it (at most 3 attempts in 10 minutes)."
            checked={form.autoRestartCaddy}
            onChange={(v) => set('autoRestartCaddy', v)}
          />
          <SwitchField label="Send recovery notices" description="Notify when a problem clears." checked={form.sendRecoveryNotices} onChange={(v) => set('sendRecoveryNotices', v)} />
          <Field label="Cooldown (minutes)" hint="Minimum time between repeated alerts for the same problem." error={errors.cooldownMinutes} className="max-w-xs">
            <NumberInput min={0} value={form.cooldownMinutes} onValueChange={(v) => set('cooldownMinutes', v)} />
          </Field>
        </FormSection>
      </Card>
    </form>
  );
}
