import { useState, type FormEvent } from 'react';
import { CheckCircle2, LogIn, XCircle } from 'lucide-react';
import { ApiError } from '@/api/client';
import { useLdapSettings, useSaveLdapSettings, useTestLdap } from '@/api/hooks';
import { ldapSettingsInput } from '@/api/settings';
import type { LdapSecurity, LdapSettings, LdapSettingsInput, LdapTestResult } from '@/api/types';
import { useFeedback } from '@/components/feedback';
import { SecretInput, secretPayload } from '@/components/SecretInput';
import {
  Badge,
  Button,
  Callout,
  Card,
  DescriptionList,
  Dialog,
  Field,
  FormSection,
  Input,
  NumberInput,
  Segmented,
  SwitchField,
  useToast,
} from '@/components/ui';
import { isValidHostname, isValidPort, type FieldErrors } from '@/lib/validation';
import { Loader, SaveBar, UnplacedErrors } from './shared';

export const DEFAULT_USER_FILTER = '(&(objectClass=user)(|(sAMAccountName={0})(userPrincipalName={0})))';

const LDAP_FIELDS = [
  'enabled', 'server', 'port', 'security', 'allowInvalidCertificate', 'bindDn', 'bindPassword', 'baseDn', 'userFilter',
  'adminGroupDn', 'operatorGroupDn', 'viewerGroupDn', 'nestedGroups',
];

const SECURITY: { value: LdapSecurity; label: string; port: number }[] = [
  { value: 'ldaps', label: 'LDAPS (636)', port: 636 },
  { value: 'startTls', label: 'StartTLS (389)', port: 389 },
  { value: 'none', label: 'None (389)', port: 389 },
];

/** Loose distinguished-name check: attribute=value pairs separated by commas. */
const DN_RE = /^\s*[A-Za-z][\w-]*\s*=\s*[^,]+(\s*,\s*[A-Za-z][\w-]*\s*=\s*[^,]+)*\s*$/;

function validate(f: LdapSettingsInput, hasBindPassword: boolean): FieldErrors {
  const e: FieldErrors = {};
  if (!f.enabled) return e;
  const server = f.server.trim();
  if (!server) e.server = 'Enter a domain controller or the domain name, e.g. corp.example.com.';
  else if (!isValidHostname(server) && !/^[\d.]+$/.test(server)) e.server = 'Enter a host name or IP address (no ldap:// prefix).';
  if (!isValidPort(f.port)) e.port = 'Port must be between 1 and 65535.';
  if (!f.baseDn.trim()) e.baseDn = 'Enter the search base, e.g. DC=corp,DC=example,DC=com.';
  else if (!DN_RE.test(f.baseDn)) e.baseDn = 'Not a distinguished name. Example: OU=Staff,DC=corp,DC=example,DC=com.';
  if (!f.userFilter.includes('{0}')) e.userFilter = 'The filter must contain {0}, which is replaced with the user name typed at sign-in.';
  if (f.bindDn?.trim() && !hasBindPassword && !secretPayload(f.bindPassword)) e.bindPassword = 'Enter the password of the bind account.';
  for (const k of ['adminGroupDn', 'operatorGroupDn', 'viewerGroupDn'] as const) {
    const v = f[k]?.trim();
    if (v && !DN_RE.test(v)) e[k] = 'Enter the group’s distinguished name, e.g. CN=CPM Admins,OU=Groups,DC=corp,DC=example,DC=com.';
  }
  if (!f.adminGroupDn?.trim() && !f.operatorGroupDn?.trim() && !f.viewerGroupDn?.trim())
    e.adminGroupDn = 'Map at least one group to a role; directory users without a matching group cannot sign in.';
  if (f.security === 'none' && !e.server) e.security = 'Without TLS, passwords cross the network in clear text. Use LDAPS or StartTLS.';
  return e;
}

export function LdapTab() {
  const q = useLdapSettings();
  return <Loader query={q}>{(data) => <LdapForm key={JSON.stringify(data)} settings={data} />}</Loader>;
}

function LdapForm({ settings }: { settings: LdapSettings }) {
  const initial = ldapSettingsInput(settings);
  const [form, setForm] = useState<LdapSettingsInput>(initial);
  const [submitted, setSubmitted] = useState(false);
  const [serverErrors, setServerErrors] = useState<FieldErrors>({});
  const [testing, setTesting] = useState(false);
  const save = useSaveLdapSettings();
  const feedback = useFeedback();
  const toast = useToast();
  const clientErrors = submitted ? validate(form, settings.hasBindPassword) : {};
  // "security: none" is advice, not a blocker.
  const { security: securityWarning, ...blocking } = clientErrors;
  const errors = { ...serverErrors, ...blocking };
  const dirty = JSON.stringify(form) !== JSON.stringify(initial);
  const set = <K extends keyof LdapSettingsInput>(k: K, v: LdapSettingsInput[K]) => {
    setForm((f) => ({ ...f, [k]: v }));
    setServerErrors({});
  };

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setSubmitted(true);
    const { security: _s, ...v } = validate(form, settings.hasBindPassword);
    if (Object.keys(v).length) return;
    try {
      await save.mutateAsync({
        ...form,
        server: form.server.trim(),
        bindDn: form.bindDn?.trim() || null,
        bindPassword: secretPayload(form.bindPassword),
        baseDn: form.baseDn.trim(),
        userFilter: form.userFilter.trim(),
        adminGroupDn: form.adminGroupDn?.trim() || null,
        operatorGroupDn: form.operatorGroupDn?.trim() || null,
        viewerGroupDn: form.viewerGroupDn?.trim() || null,
      });
      toast.success('Directory settings saved');
    } catch (err) {
      feedback.failed(err, { onFieldErrors: setServerErrors });
    }
  };

  return (
    <>
      <form onSubmit={(e) => void submit(e)} noValidate>
        <Card className="p-5">
          <fieldset disabled={save.isPending} className="min-w-0">
            <UnplacedErrors errors={errors} fields={LDAP_FIELDS} />
            <FormSection
              title="Directory sign-in"
              description="Let Active Directory (or another LDAP directory) users sign in with DOMAIN\user, user@domain or their e-mail. Local accounts are always tried first, so a local admin keeps working if the directory is unreachable."
            >
              <SwitchField label="Allow directory sign-in" checked={form.enabled} onChange={(v) => set('enabled', v)} />
            </FormSection>
            <fieldset disabled={!form.enabled} className="min-w-0 pt-6 disabled:opacity-60">
              <FormSection title="Connection" description="Use a domain controller name, or the domain name to let DNS pick one. The certificate must be trusted by this server for LDAPS and StartTLS.">
                <div className="grid gap-4 md:grid-cols-[minmax(0,1fr)_120px]">
                  <Field label="Server" required error={errors.server}>
                    <Input mono placeholder="corp.example.com" value={form.server} onChange={(e) => set('server', e.target.value)} />
                  </Field>
                  <Field label="Port" required error={errors.port}>
                    <NumberInput min={1} max={65535} value={form.port} onValueChange={(v) => set('port', v)} />
                  </Field>
                </div>
                <Field label="Security">
                  <Segmented
                    aria-label="Connection security"
                    value={form.security}
                    onChange={(sec) => {
                      const known = SECURITY.map((o) => o.port);
                      setForm((f) => ({
                        ...f,
                        security: sec,
                        port: known.includes(f.port) ? (SECURITY.find((o) => o.value === sec)?.port ?? f.port) : f.port,
                      }));
                    }}
                    options={SECURITY.map((o) => ({ value: o.value, label: o.label }))}
                  />
                </Field>
                {form.enabled && form.security === 'none' && (
                  <Callout tone={securityWarning ? 'danger' : 'warning'}>
                    Without TLS, passwords cross the network in clear text, and Active Directory may reject simple binds (LDAP signing). Use LDAPS or StartTLS.
                  </Callout>
                )}
                <SwitchField
                  label="Accept invalid server certificates"
                  description="Only for testing. The connection stays encrypted but the domain controller is not authenticated."
                  checked={form.allowInvalidCertificate}
                  onChange={(v) => set('allowInvalidCertificate', v)}
                  disabled={form.security === 'none'}
                />
              </FormSection>
              <FormSection title="Search" description="A service account that may read users and groups. Leave the bind DN empty to search with the signing-in user’s own credentials.">
                <div className="grid gap-4 md:grid-cols-2">
                  <Field label="Bind DN or UPN" error={errors.bindDn}>
                    <Input mono autoComplete="off" placeholder="svc-cpm@corp.example.com" value={form.bindDn ?? ''} onChange={(e) => set('bindDn', e.target.value)} />
                  </Field>
                  <Field label="Bind password" error={errors.bindPassword}>
                    <SecretInput has={settings.hasBindPassword} value={form.bindPassword} onChange={(v) => set('bindPassword', v)} disabled={!form.enabled} />
                  </Field>
                </div>
                <Field label="Search base" required error={errors.baseDn}>
                  <Input mono placeholder="DC=corp,DC=example,DC=com" value={form.baseDn} onChange={(e) => set('baseDn', e.target.value)} />
                </Field>
                <Field
                  label="User filter"
                  required
                  error={errors.userFilter}
                  labelAction={
                    form.userFilter !== DEFAULT_USER_FILTER && (
                      <Button size="xs" variant="ghost" onClick={() => set('userFilter', DEFAULT_USER_FILTER)}>
                        Reset to default
                      </Button>
                    )
                  }
                  hint={
                    <>
                      <span className="mono">{'{0}'}</span> is the user name typed at sign-in (without the <span className="mono">DOMAIN\</span> prefix).
                    </>
                  }
                >
                  <Input mono value={form.userFilter} onChange={(e) => set('userFilter', e.target.value)} />
                </Field>
              </FormSection>
              <FormSection
                title="Role mapping"
                description="Roles come from group membership at every sign-in; the highest matching role wins. Directory users in none of these groups are refused."
              >
                <Field label="Admin group" error={errors.adminGroupDn}>
                  <Input mono placeholder="CN=CPM Admins,OU=Groups,DC=corp,DC=example,DC=com" value={form.adminGroupDn ?? ''} onChange={(e) => set('adminGroupDn', e.target.value)} />
                </Field>
                <Field label="Operator group" error={errors.operatorGroupDn}>
                  <Input mono placeholder="CN=CPM Operators,OU=Groups,DC=corp,DC=example,DC=com" value={form.operatorGroupDn ?? ''} onChange={(e) => set('operatorGroupDn', e.target.value)} />
                </Field>
                <Field label="Viewer group" error={errors.viewerGroupDn}>
                  <Input mono placeholder="CN=CPM Viewers,OU=Groups,DC=corp,DC=example,DC=com" value={form.viewerGroupDn ?? ''} onChange={(e) => set('viewerGroupDn', e.target.value)} />
                </Field>
                <SwitchField
                  label="Include nested groups"
                  description="Also match users who are members through other groups (Active Directory LDAP_MATCHING_RULE_IN_CHAIN). Slightly slower in large directories."
                  checked={form.nestedGroups}
                  onChange={(v) => set('nestedGroups', v)}
                />
              </FormSection>
            </fieldset>
          </fieldset>
          <div className="mt-5 border-t border-border pt-4">
            <SaveBar
              dirty={dirty}
              saving={save.isPending}
              onReset={() => setForm(initial)}
              extra={
                <Button
                  icon={<LogIn size={14} />}
                  onClick={() => setTesting(true)}
                  disabled={dirty || !settings.enabled}
                  title={dirty ? 'Save first — the test uses the saved settings' : !settings.enabled ? 'Enable and save directory sign-in first' : undefined}
                  className="mr-auto"
                >
                  Test sign-in…
                </Button>
              }
            />
          </div>
        </Card>
      </form>
      {testing && <TestDialog onClose={() => setTesting(false)} />}
    </>
  );
}

function TestDialog({ onClose }: { onClose: () => void }) {
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [result, setResult] = useState<LdapTestResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const test = useTestLdap();
  const feedback = useFeedback();

  const run = (e: FormEvent) => {
    e.preventDefault();
    setResult(null);
    setError(null);
    if (!username.trim() || !password) {
      setError('Enter a user name and password.');
      return;
    }
    test.mutate(
      { username: username.trim(), password },
      {
        onSuccess: setResult,
        onError: (err) => {
          if (err instanceof ApiError && err.status < 500 && err.status !== 401) setError(err.display);
          else feedback.failed(err, { title: 'The test could not run' });
        },
      },
    );
  };

  return (
    <Dialog
      open
      onClose={onClose}
      dismissible={!test.isPending}
      size="lg"
      title="Test directory sign-in"
      description="Signs in against the saved directory settings and shows the role the user would get. No session is created."
      footer={
        <>
          <Button onClick={onClose} disabled={test.isPending}>
            Close
          </Button>
          <Button type="submit" form="ldap-test" variant="primary" loading={test.isPending}>
            Test
          </Button>
        </>
      }
    >
      <form id="ldap-test" onSubmit={run} noValidate className="flex flex-col gap-4">
        {error && <Callout tone="danger">{error}</Callout>}
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="User name" hint="DOMAIN\user, user or user@domain">
            <Input mono autoComplete="off" value={username} onChange={(e) => setUsername(e.target.value)} />
          </Field>
          <Field label="Password">
            <Input type="password" autoComplete="off" value={password} onChange={(e) => setPassword(e.target.value)} />
          </Field>
        </div>
        {result && (
          <div className="flex flex-col gap-3 rounded-md border border-border p-3">
            {result.ok ? (
              <p className="flex items-center gap-1.5 text-sm font-medium text-success">
                <CheckCircle2 size={15} aria-hidden /> Sign-in succeeded
              </p>
            ) : (
              <p className="flex items-center gap-1.5 text-sm font-medium text-danger">
                <XCircle size={15} aria-hidden /> Sign-in failed
              </p>
            )}
            {result.error && <p className="text-sm break-words text-fg-muted">{result.error}</p>}
            {(result.ok || result.displayName || result.groups) && (
              <DescriptionList
                items={[
                  {
                    label: 'Role',
                    value: result.role ? (
                      <Badge tone={result.role === 'admin' ? 'accent' : result.role === 'operator' ? 'info' : 'neutral'} className="capitalize">
                        {result.role}
                      </Badge>
                    ) : (
                      <span className="text-danger">None — not in a mapped group, sign-in would be refused</span>
                    ),
                  },
                  { label: 'Display name', value: result.displayName ?? '—' },
                  { label: 'E-mail', value: result.email ?? '—', mono: true },
                  {
                    label: `Groups (${result.groups?.length ?? 0})`,
                    value: result.groups?.length ? (
                      <ul className="max-h-40 space-y-1 overflow-y-auto">
                        {result.groups.map((g) => (
                          <li key={g} className="mono rounded border border-border bg-surface-2 px-1.5 py-0.5 text-xs break-all">
                            {g}
                          </li>
                        ))}
                      </ul>
                    ) : (
                      '—'
                    ),
                  },
                ]}
              />
            )}
          </div>
        )}
      </form>
    </Dialog>
  );
}
