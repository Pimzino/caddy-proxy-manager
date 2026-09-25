import { useId, useState, type FormEvent } from 'react';
import { ApiError } from '@/api/client';
import { useCreateCertificate, useReplaceCertificate, type CertificateCreate } from '@/api/hooks';
import type { ApplyResult, CertificateInfo } from '@/api/types';
import { useFeedback } from '@/components/feedback';
import { Button, Callout, Dialog, Field, FileInput, Input, TabPanel, Tabs, Textarea } from '@/components/ui';
import { serverFieldErrors, type FieldErrors } from '@/lib/validation';

type Method = 'pem' | 'pfx' | 'paste' | 'path';

const PEM_CERT = /-----BEGIN CERTIFICATE-----[\s\S]+-----END CERTIFICATE-----/;
const PEM_KEY = /-----BEGIN (?:RSA |EC |ENCRYPTED )?PRIVATE KEY-----[\s\S]+-----END (?:RSA |EC |ENCRYPTED )?PRIVATE KEY-----/;

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
    if (!f.keyPath.trim()) e.keyPath = 'Enter the full path to the private key file.';
  }
  return e;
}

/** Add a certificate (4 methods) or replace an uploaded one (file methods only). */
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
      if (err instanceof ApiError && (err.status === 400 || err.status === 422)) {
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
    const input: CertificateCreate =
      method === 'paste'
        ? { method: 'pem', body: { name: form.name.trim(), certPem: form.certPem.trim() + '\n', keyPem: form.keyPem.trim() + '\n' } }
        : { method: 'path', body: { name: form.name.trim(), certPath: form.certPath.trim(), keyPath: form.keyPath.trim() } };
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
            { value: 'path', label: 'File path', hidden: !!replace },
          ]}
        />
        {serverMessage && <Callout tone="danger" title="The certificate was not accepted">{serverMessage}</Callout>}
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
            (<span className="mono">DOMAIN\SERVER$</span>).
          </Callout>
          <Field label="Certificate path" required error={errors.certPath}>
            <Input mono placeholder="\\fileserver\pki\web01\fullchain.pem" value={form.certPath} onChange={(e) => set('certPath', e.target.value)} />
          </Field>
          <Field label="Private key path" required error={errors.keyPath}>
            <Input mono placeholder="\\fileserver\pki\web01\privkey.pem" value={form.keyPath} onChange={(e) => set('keyPath', e.target.value)} />
          </Field>
        </TabPanel>
      </form>
    </Dialog>
  );
}
