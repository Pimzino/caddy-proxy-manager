import { useState, type FormEvent } from 'react';
import { Save, Send } from 'lucide-react';
import { errorMessage } from '@/api/client';
import { notificationSettingsInput } from '@/api/settings';
import { useNotificationSettings, useSaveNotificationSettings, useTestNotifications } from '@/api/hooks';
import type { NotificationSettings, NotificationSettingsInput, NotificationTestResult, SmtpSecurity } from '@/api/types';
import { useFeedback } from '@/components/feedback';
import { SecretInput, secretPayload } from '@/components/SecretInput';
import {
  Button,
  Callout,
  Card,
  ChipInput,
  Field,
  FormSection,
  Input,
  LoadingBlock,
  NumberInput,
  PageHeader,
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

function validate(f: NotificationSettingsInput): FieldErrors {
  const e: FieldErrors = {};
  if (f.smtpEnabled) {
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
  const errors = { ...serverErrors, ...(submitted ? validate(form) : {}) };
  const dirty = JSON.stringify(form) !== JSON.stringify(initial);
  const set = <K extends keyof NotificationSettingsInput>(k: K, v: NotificationSettingsInput[K]) => {
    setForm((f) => ({ ...f, [k]: v }));
    setServerErrors({});
  };

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setSubmitted(true);
    if (Object.keys(validate(form)).length) return;
    try {
      // The form re-mounts with the saved values afterwards, so use mutateAsync (not per-call callbacks).
      await save.mutateAsync({
        ...form,
        smtpHost: form.smtpHost.trim(),
        smtpFrom: form.smtpFrom.trim(),
        smtpUsername: form.smtpUsername?.trim() || null,
        webhookUrl: form.webhookUrl?.trim() || null,
        smtpPassword: secretPayload(form.smtpPassword),
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
        <FormSection title="E-mail (SMTP)" description="Sent with MailKit. Works with Exchange, Microsoft 365 (authenticated SMTP), and most relays.">
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
            <div className="grid gap-4 md:grid-cols-2">
              <Field label="User name" hint="Leave empty for an anonymous relay.">
                <Input autoComplete="off" value={form.smtpUsername ?? ''} onChange={(e) => set('smtpUsername', e.target.value)} />
              </Field>
              <Field label="Password">
                <SecretInput has={settings.hasSmtpPassword} value={form.smtpPassword} onChange={(v) => set('smtpPassword', v)} disabled={!form.smtpEnabled} />
              </Field>
            </div>
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
        <FormSection title="Webhook" description="POSTs JSON with a “text” field — compatible with Microsoft Teams workflows and Slack incoming webhooks.">
          <SwitchField label="Send webhook alerts" checked={form.webhookEnabled} onChange={(v) => set('webhookEnabled', v)} />
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
