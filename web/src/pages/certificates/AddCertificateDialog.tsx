import { useId, useMemo, useState, type FormEvent } from 'react';
import { KeyRound, RefreshCw } from 'lucide-react';
import { ApiError, errorMessage } from '@/api/client';
import { useCreateCertificate, useReplaceCertificate, useWindowsStoreCertificates, type CertificateCreate } from '@/api/hooks';
import type { ApplyResult, CertificateInfo, WindowsStoreCertificate } from '@/api/types';
import { useAuth } from '@/auth';
import { useFeedback } from '@/components/feedback';
import {
  Badge,
  Button,
  Callout,
  Dialog,
  EmptyState,
  Field,
  FileInput,
  Input,
  LoadingBlock,
  SearchInput,
  Segmented,
  Select,
  TabPanel,
  Tabs,
  Textarea,
} from '@/components/ui';
import { cn } from '@/lib/cn';
import { formatDate } from '@/lib/format';
import { serverFieldErrors, type FieldErrors } from '@/lib/validation';

type Method = 'pem' | 'pfx' | 'paste' | 'path' | 'pfxPath' | 'store';
type StoreMode = 'thumbprint' | 'subject';

const PEM_CERT = /-----BEGIN CERTIFICATE-----[\s\S]+-----END CERTIFICATE-----/;
const PEM_KEY = /-----BEGIN (?:RSA |EC |ENCRYPTED )?PRIVATE KEY-----[\s\S]+-----END (?:RSA |EC |ENCRYPTED )?PRIVATE KEY-----/;
const PEM_FILE = /\.(pem|crt|cer|key)$/i;
const PFX_FILE = /\.(pfx|p12)$/i;

const STORE_LOCATIONS = [
  { value: 'LocalMachine', label: 'Local computer (LocalMachine)' },
  { value: 'CurrentUser', label: 'Service account (CurrentUser)' },
];
const STORE_NAMES = [
  { value: 'My', label: 'Personal (My)' },
  { value: 'WebHosting', label: 'Web Hosting (WebHosting)' },
];

interface FormState {
  name: string;
  certFile: File | null;
  keyFile: File | null;
  pfxFile: File | null;
  pfxPassword: string;
  certPem: string;
  keyPem: string;
  certPath: string;
  keyPath: string;
  pfxPath: string;
  pfxPathPassword: string;
  storeLocation: string;
  storeName: string;
  storeMode: StoreMode;
  thumbprint: string;
  subject: string;
}

const emptyForm: FormState = {
  name: '',
  certFile: null,
  keyFile: null,
  pfxFile: null,
  pfxPassword: '',
  certPem: '',
  keyPem: '',
  certPath: '',
  keyPath: '',
  pfxPath: '',
  pfxPathPassword: '',
  storeLocation: 'LocalMachine',
  storeName: 'My',
  storeMode: 'thumbprint',
  thumbprint: '',
  subject: '',
};

function validate(method: Method, f: FormState): FieldErrors {
  const e: FieldErrors = {};
  if (method === 'pem') {
    if (!f.certFile) e.certFile = 'Choose the certificate (chain) file.';
    if (!f.keyFile) e.keyFile = 'Choose the private key file.';
  } else if (method === 'pfx') {
    if (!f.pfxFile) e.pfxFile = 'Choose the .pfx / .p12 file.';
  } else if (method === 'paste') {
    if (!PEM_CERT.test(f.certPem)) e.certPem = 'Paste a PEM certificate starting with -----BEGIN CERTIFICATE-----.';
    if (f.keyPem.includes('ENCRYPTED PRIVATE KEY')) e.keyPem = 'Encrypted keys are not supported. Export the key without a password.';
    else if (!PEM_KEY.test(f.keyPem)) e.keyPem = 'Paste a PEM private key starting with -----BEGIN PRIVATE KEY-----.';
  } else if (method === 'path') {
    if (!f.certPath.trim()) e.certPath = 'Enter the full path to the certificate file.';
    else if (!PEM_FILE.test(f.certPath.trim())) e.certPath = 'Use a PEM file ending in .pem, .crt or .cer.';
    if (!f.keyPath.trim()) e.keyPath = 'Enter the full path to the private key file.';
    else if (!PEM_FILE.test(f.keyPath.trim())) e.keyPath = 'Use a PEM file ending in .key or .pem.';
  } else if (method === 'pfxPath') {
    if (!f.pfxPath.trim()) e.pfxPath = 'Enter the full path to the .pfx / .p12 file.';
    else if (!PFX_FILE.test(f.pfxPath.trim())) e.pfxPath = 'Use a file ending in .pfx or .p12.';
  } else if (method === 'store') {
    if (f.storeMode === 'thumbprint' && !f.thumbprint) e.thumbprint = 'Select a certificate from the list.';
    if (f.storeMode === 'subject' && !f.subject.trim()) e.subject = 'Enter the host name the certificate is issued for.';
  }
  return e;
}

const usable = (c: WindowsStoreCertificate, now: number) => c.hasPrivateKey && Date.parse(c.notAfter) > now;

/** First DNS name, else the CN of the subject. */
function primaryName(c: WindowsStoreCertificate): string {
  return c.dnsNames[0] ?? /CN=([^,]+)/i.exec(c.subject)?.[1]?.trim() ?? c.subject;
}

/** Add a certificate (6 methods, 3 of them admin-only) or replace an uploaded one (file methods only). */
export function AddCertificateDialog({
  open,
  onClose,
  replace,
}: {
  open: boolean;
  onClose: () => void;
  replace?: CertificateInfo;
}) {
  if (!open) return null;
  return <Inner onClose={onClose} replace={replace} />;
}

function Inner({ onClose, replace }: { onClose: () => void; replace?: CertificateInfo }) {
  const idBase = useId();
  const { isAdmin } = useAuth();
  const [method, setMethod] = useState<Method>('pem');
  const [form, setForm] = useState<FormState>({ ...emptyForm, name: replace?.name ?? '' });
  const [submitted, setSubmitted] = useState(false);
  const [serverErrors, setServerErrors] = useState<FieldErrors>({});
  const [serverMessage, setServerMessage] = useState<string | null>(null);
  const create = useCreateCertificate();
  const replaceMut = useReplaceCertificate();
  const feedback = useFeedback();
  const pending = create.isPending || replaceMut.isPending;

  const errors = { ...serverErrors, ...(submitted ? validate(method, form) : {}) };
  const set = <K extends keyof FormState>(k: K, v: FormState[K]) => {
    setForm((f) => ({ ...f, [k]: v }));
    setServerErrors({});
    setServerMessage(null);
  };

  const submit = (e: FormEvent) => {
    e.preventDefault();
    setSubmitted(true);
    setServerMessage(null);
    if (Object.keys(validate(method, form)).length) return;

    const onError = (err: unknown) => {
      if (err instanceof ApiError && (err.status === 400 || err.status === 403 || err.status === 422)) {
        if (err.errors) setServerErrors(serverFieldErrors(err.errors));
        setServerMessage(err.detail ?? err.title);
        if (err.status === 422) feedback.failed(err);
        return;
      }
      feedback.failed(err, { title: 'Could not import the certificate' });
    };
    const onSuccess = (res: { apply: ApplyResult }) => {
      feedback.applied(res.apply, replace ? 'Certificate replaced and applied' : 'Certificate added');
      onClose();
    };

    if (method === 'pem' || method === 'pfx') {
      const fd = new FormData();
      fd.append('name', form.name.trim() || replace?.name || '');
      if (method === 'pem') {
        fd.append('certFile', form.certFile as File);
        fd.append('keyFile', form.keyFile as File);
      } else {
        fd.append('pfxFile', form.pfxFile as File);
        fd.append('pfxPassword', form.pfxPassword);
      }
      if (replace) replaceMut.mutate({ id: replace.id, form: fd }, { onSuccess, onError });
      else create.mutate({ method: 'upload', form: fd }, { onSuccess, onError });
      return;
    }
    const name = form.name.trim();
    let input: CertificateCreate;
    switch (method) {
      case 'paste':
        input = { method: 'pem', body: { name, certPem: form.certPem.trim() + '\n', keyPem: form.keyPem.trim() + '\n' } };
        break;
      case 'path':
        input = { method: 'path', body: { name, certPath: form.certPath.trim(), keyPath: form.keyPath.trim() } };
        break;
      case 'pfxPath':
        input = {
          method: 'pfxPath',
          body: { name: name || undefined, pfxPath: form.pfxPath.trim(), pfxPassword: form.pfxPathPassword || undefined },
        };
        break;
      case 'store':
        input = {
          method: 'windowsStore',
          body: {
            name: name || undefined,
            storeLocation: form.storeLocation,
            storeName: form.storeName,
            ...(form.storeMode === 'thumbprint' ? { thumbprint: form.thumbprint } : { subject: form.subject.trim() }),
          },
        };
        break;
    }
    create.mutate(input, { onSuccess, onError });
  };

  const formId = `${idBase}-form`;
  return (
    <Dialog
      open
      onClose={onClose}
      dismissible={!pending}
      size="lg"
      title={replace ? `Replace certificate “${replace.name}”` : 'Add certificate'}
      description={
        replace
          ? 'Upload the renewed certificate and key. Hosts using this certificate pick it up immediately.'
          : 'Import a certificate from your own CA or a commercial provider, then select it in a host’s TLS tab.'
      }
      footer={
        <>
          <Button onClick={onClose} disabled={pending}>
            Cancel
          </Button>
          <Button type="submit" form={formId} variant="primary" loading={pending}>
            {replace ? 'Replace' : 'Add certificate'}
          </Button>
        </>
      }
    >
      <form id={formId} onSubmit={submit} noValidate className="flex flex-col gap-4">
        <Tabs
          idBase={idBase}
          aria-label="Import method"
          value={method}
          onChange={(m) => {
            setMethod(m);
            setSubmitted(false);
            setServerErrors({});
            setServerMessage(null);
          }}
          items={[
            { value: 'pem', label: 'Upload PEM' },
            { value: 'pfx', label: 'Upload PFX' },
            { value: 'paste', label: 'Paste PEM', hidden: !!replace },
            { value: 'path', label: 'File path', hidden: !!replace || !isAdmin },
            { value: 'pfxPath', label: 'PFX on disk/share', hidden: !!replace || !isAdmin },
            { value: 'store', label: 'Windows store', hidden: !!replace || !isAdmin },
          ]}
        />
        {serverMessage && (
          <Callout tone="danger" title="The certificate was not accepted">
            {serverMessage}
          </Callout>
        )}
        {!replace && (
          <Field
            label="Name"
            error={errors.name}
            hint="Shown in host editors, e.g. “Wildcard corp.example.com 2026”. Leave empty to use the certificate’s first domain name."
          >
            <Input value={form.name} onChange={(e) => set('name', e.target.value)} />
          </Field>
        )}
        <TabPanel idBase={idBase} value="pem" active={method === 'pem'} className="flex flex-col gap-4">
          <Field label="Certificate file" required error={errors.certFile} hint="PEM (.pem, .crt, .cer) — include intermediate certificates after the server certificate.">
            <FileInput accept=".pem,.crt,.cer,.txt" onFile={(f) => set('certFile', f)} />
          </Field>
          <Field label="Private key file" required error={errors.keyFile} hint="Unencrypted PEM (.key, .pem).">
            <FileInput accept=".key,.pem,.txt" onFile={(f) => set('keyFile', f)} />
          </Field>
        </TabPanel>
        <TabPanel idBase={idBase} value="pfx" active={method === 'pfx'} className="flex flex-col gap-4">
          <Field label="PFX / PKCS#12 file" required error={errors.pfxFile} hint="Exported from Windows with “Include all certificates in the certification path”.">
            <FileInput accept=".pfx,.p12" onFile={(f) => set('pfxFile', f)} />
          </Field>
          <Field label="PFX password" error={errors.pfxPassword} hint="Used only to open the file; it is not stored.">
            <Input type="password" autoComplete="off" value={form.pfxPassword} onChange={(e) => set('pfxPassword', e.target.value)} />
          </Field>
        </TabPanel>
        <TabPanel idBase={idBase} value="paste" active={method === 'paste'} className="flex flex-col gap-4">
          <Field label="Certificate (PEM)" required error={errors.certPem}>
            <Textarea
              mono
              rows={7}
              spellCheck={false}
              placeholder={'-----BEGIN CERTIFICATE-----\n…\n-----END CERTIFICATE-----'}
              value={form.certPem}
              onChange={(e) => set('certPem', e.target.value)}
            />
          </Field>
          <Field label="Private key (PEM)" required error={errors.keyPem}>
            <Textarea
              mono
              rows={6}
              spellCheck={false}
              placeholder={'-----BEGIN PRIVATE KEY-----\n…\n-----END PRIVATE KEY-----'}
              value={form.keyPem}
              onChange={(e) => set('keyPem', e.target.value)}
            />
          </Field>
        </TabPanel>
        <TabPanel idBase={idBase} value="path" active={method === 'path'} className="flex flex-col gap-4">
          <Callout tone="info">
            The files are referenced, not copied — renew them in place (for example with your PKI tooling) and the manager reloads
            them automatically. Caddy runs as LocalSystem: on a share, grant read access to this computer’s account
            (<span className="mono">DOMAIN\SERVER$</span>). Files inside the manager’s data folder are not accepted, except in the
            configured certificate store.
          </Callout>
          <Field label="Certificate path" required error={errors.certPath}>
            <Input mono placeholder="\\fileserver\pki\web01\fullchain.pem" value={form.certPath} onChange={(e) => set('certPath', e.target.value)} />
          </Field>
          <Field label="Private key path" required error={errors.keyPath}>
            <Input mono placeholder="\\fileserver\pki\web01\privkey.pem" value={form.keyPath} onChange={(e) => set('keyPath', e.target.value)} />
          </Field>
        </TabPanel>
        <TabPanel idBase={idBase} value="pfxPath" active={method === 'pfxPath'} className="flex flex-col gap-4">
          <Callout tone="info">
            For tools that renew a PFX in place, such as win-acme or a PKI export job. The manager converts it to PEM in the certificate
            store and converts it again whenever the file changes. The password is stored encrypted so renewals can be read
            unattended.
          </Callout>
          <Field label="PFX path" required error={errors.pfxPath} hint="Local path or UNC share readable by this computer’s account. .pfx or .p12 only.">
            <Input mono placeholder="C:\ProgramData\win-acme\certificates\app.example.com.pfx" value={form.pfxPath} onChange={(e) => set('pfxPath', e.target.value)} />
          </Field>
          <Field label="PFX password" error={errors.pfxPassword} hint="Leave empty when the file has no password.">
            <Input type="password" autoComplete="new-password" value={form.pfxPathPassword} onChange={(e) => set('pfxPathPassword', e.target.value)} />
          </Field>
        </TabPanel>
        <TabPanel idBase={idBase} value="store" active={method === 'store'} className="flex flex-col gap-4">
          <WindowsStorePicker form={form} set={set} errors={errors} />
        </TabPanel>
      </form>
    </Dialog>
  );
}

function WindowsStorePicker({
  form,
  set,
  errors,
}: {
  form: FormState;
  set: <K extends keyof FormState>(k: K, v: FormState[K]) => void;
  errors: FieldErrors;
}) {
  const [q, setQ] = useState('');
  const list = useWindowsStoreCertificates(form.storeLocation, form.storeName, true);
  // Captured when the list loads; keeps render pure.
  const now = list.dataUpdatedAt || 0;
  const filtered = useMemo(() => {
    const n = q.trim().toLowerCase();
    return (list.data ?? [])
      .filter((c) => !n || [c.subject, c.issuer, c.thumbprint, c.template ?? '', ...c.dnsNames].join(' ').toLowerCase().includes(n))
      // Usable certificates first (private key, not expired), newest expiry first within each group.
      .sort(
        (a, b) =>
          Number(usable(b, now)) - Number(usable(a, now)) || Date.parse(b.notAfter) - Date.parse(a.notAfter),
      );
  }, [list.data, q, now]);
  const selected = list.data?.find((c) => c.thumbprint === form.thumbprint);

  const pick = (c: WindowsStoreCertificate) => {
    set('thumbprint', c.thumbprint);
    set('subject', primaryName(c));
  };

  return (
    <>
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label="Store location" error={errors.storeLocation}>
          <Select
            value={form.storeLocation}
            onChange={(e) => {
              set('storeLocation', e.target.value);
              set('thumbprint', '');
            }}
          >
            {STORE_LOCATIONS.map((o) => (
              <option key={o.value} value={o.value}>
                {o.label}
              </option>
            ))}
          </Select>
        </Field>
        <Field label="Store" error={errors.storeName}>
          <Select
            value={form.storeName}
            onChange={(e) => {
              set('storeName', e.target.value);
              set('thumbprint', '');
            }}
          >
            {STORE_NAMES.map((o) => (
              <option key={o.value} value={o.value}>
                {o.label}
              </option>
            ))}
          </Select>
        </Field>
      </div>

      <div className="overflow-hidden rounded-md border border-border">
        <div className="flex items-center gap-2 border-b border-border bg-surface-2/60 px-3 py-2">
          <SearchInput value={q} onChange={setQ} placeholder="Search subject, SAN, issuer, thumbprint…" className="flex-1" />
          <Button size="sm" variant="ghost" iconOnly aria-label="Reload certificate list" icon={<RefreshCw size={14} />} onClick={() => void list.refetch()} loading={list.isFetching} />
        </div>
        <div className="max-h-64 overflow-y-auto" role="radiogroup" aria-label="Certificates in the store">
          {list.isPending ? (
            <LoadingBlock label="Reading the certificate store…" />
          ) : list.isError ? (
            <div className="p-3">
              <Callout tone="danger" title="Could not read the certificate store">
                {errorMessage(list.error)}
              </Callout>
            </div>
          ) : filtered.length === 0 ? (
            <EmptyState
              icon={<KeyRound size={18} />}
              title={list.data.length === 0 ? 'No certificates in this store' : 'No matches'}
              description={
                list.data.length === 0
                  ? `Nothing was found in ${form.storeLocation}\\${form.storeName}. Certificates enrolled for the computer (AD CS autoenrollment, certreq, IIS) are usually in Local computer › Personal. On non-Windows development hosts this list is always empty.`
                  : 'No certificate matches the search.'
              }
            />
          ) : (
            <ul className="divide-y divide-border">
              {filtered.map((c) => {
                const chosen = c.thumbprint === form.thumbprint;
                const expired = Date.parse(c.notAfter) <= now;
                const unusable = !c.hasPrivateKey;
                return (
                  <li key={c.thumbprint}>
                    <button
                      type="button"
                      role="radio"
                      aria-checked={chosen}
                      disabled={unusable}
                      onClick={() => pick(c)}
                      className={cn(
                        'flex w-full items-start gap-3 px-3 py-2 text-left transition-colors',
                        'focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-ring',
                        'disabled:cursor-not-allowed disabled:opacity-55',
                        chosen ? 'bg-accent-soft' : 'hover:bg-surface-2',
                      )}
                    >
                      <span
                        aria-hidden
                        className={cn('mt-0.5 flex h-4 w-4 shrink-0 items-center justify-center rounded-full border', chosen ? 'border-accent' : 'border-border-strong')}
                      >
                        {chosen && <span className="h-2 w-2 rounded-full bg-accent" />}
                      </span>
                      <span className="min-w-0 flex-1">
                        <span className="flex flex-wrap items-center gap-1.5">
                          <span className="mono truncate text-sm font-medium text-fg">{primaryName(c)}</span>
                          {expired ? (
                            <Badge tone="danger">Expired {formatDate(c.notAfter)}</Badge>
                          ) : (
                            <Badge tone="neutral">Expires {formatDate(c.notAfter)}</Badge>
                          )}
                          {!c.hasPrivateKey && <Badge tone="danger">No private key</Badge>}
                          {c.hasPrivateKey && !c.exportable && <Badge tone="warning">Key not exportable</Badge>}
                          {c.template && <Badge tone="info">Template: {c.template}</Badge>}
                        </span>
                        <span className="mt-0.5 block truncate text-xs text-fg-subtle" title={c.dnsNames.join(', ')}>
                          {c.dnsNames.length > 1 ? `SAN: ${c.dnsNames.join(', ')}` : c.subject}
                        </span>
                        <span className="block truncate text-xs text-fg-subtle" title={c.issuer}>
                          Issuer: {c.issuer} · <span className="mono">{c.thumbprint}</span>
                        </span>
                      </span>
                    </button>
                  </li>
                );
              })}
            </ul>
          )}
        </div>
      </div>
      {errors.thumbprint && form.storeMode === 'thumbprint' && (
        <p className="-mt-2 text-xs text-danger" role="alert">
          {errors.thumbprint}
        </p>
      )}
      {selected && selected.hasPrivateKey && !selected.exportable && (
        <Callout tone="warning" title="The private key is marked as not exportable">
          The manager has to export the key to PEM for Caddy, which Windows refuses for non-exportable keys. Re-issue the certificate from a
          template that allows exporting the private key, or import a PFX instead.
        </Callout>
      )}

      <Field label="Which certificate to use">
        <Segmented
          aria-label="Selection mode"
          value={form.storeMode}
          onChange={(m) => set('storeMode', m)}
          options={[
            { value: 'thumbprint', label: 'This certificate (thumbprint)' },
            { value: 'subject', label: 'Follow renewals by subject' },
          ]}
        />
      </Field>
      {form.storeMode === 'thumbprint' ? (
        <p className="text-sm text-fg-subtle">
          {selected ? (
            <>
              Pinned to thumbprint <span className="mono text-fg">{selected.thumbprint}</span>. When the certificate is renewed, pick the new
              one here or switch to following renewals.
            </>
          ) : (
            'Select a certificate above. It stays pinned by thumbprint, so renewals are not picked up automatically.'
          )}
        </p>
      ) : (
        <Field
          label="Subject or SAN to follow"
          required
          error={errors.subject}
          hint="Every 15 minutes (and on “Sync now”) the newest currently valid certificate with a private key whose CN or SAN matches this name is exported — ideal for AD CS autoenrollment and certreq renewals."
        >
          <Input mono placeholder="app.corp.example.com" value={form.subject} onChange={(e) => set('subject', e.target.value)} />
        </Field>
      )}
    </>
  );
}
