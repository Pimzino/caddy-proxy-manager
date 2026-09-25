import { useId, useState, type FormEvent } from 'react';
import { Archive, CalendarClock, Download, HardDriveDownload, Play, Upload } from 'lucide-react';
import { downloadFile, errorMessage } from '@/api/client';
import { useBackups, useBackupSettings, useRestoreBackup, useRunBackup, useSaveBackupSettings } from '@/api/hooks';
import { backupSettingsInput } from '@/api/settings';
import type { BackupFile, BackupSettings, BackupSettingsInput } from '@/api/types';
import { useFeedback } from '@/components/feedback';
import { SecretInput, secretPayload } from '@/components/SecretInput';
import {
  Button,
  Callout,
  Card,
  CardBody,
  CardHeader,
  EmptyState,
  Field,
  FileInput,
  FormSection,
  Input,
  NumberInput,
  Select,
  SwitchField,
  Table,
  TableSkeleton,
  TBody,
  TD,
  TH,
  THead,
  TR,
  useConfirm,
  useToast,
} from '@/components/ui';
import { formatBytes, formatDateTime, formatRelative } from '@/lib/format';
import { useNow } from '@/lib/useNow';
import type { FieldErrors } from '@/lib/validation';
import { RestartPanel } from './RestartPanel';
import { Loader, SaveBar, UnplacedErrors } from './shared';

const HOURS = Array.from({ length: 24 }, (_, h) => h);
const BACKUP_FIELDS = ['enabled', 'hourLocal', 'directory', 'keep', 'password'];

export function BackupTab() {
  const [restartNeeded, setRestartNeeded] = useState(false);
  const settings = useBackupSettings();
  return (
    <div className="flex flex-col gap-4">
      {restartNeeded && <RestartPanel reason="A restored backup is staged and will be applied when the management service restarts." />}
      <Loader query={settings}>{(data) => <ScheduleForm key={JSON.stringify(data)} settings={data} />}</Loader>
      <BackupList />
      <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
        <DownloadCard />
        <RestoreCard onStaged={() => setRestartNeeded(true)} />
      </div>
    </div>
  );
}

function ScheduleForm({ settings }: { settings: BackupSettings }) {
  const initial = backupSettingsInput(settings);
  const [form, setForm] = useState<BackupSettingsInput>(initial);
  const [submitted, setSubmitted] = useState(false);
  const [serverErrors, setServerErrors] = useState<FieldErrors>({});
  const save = useSaveBackupSettings();
  const feedback = useFeedback();
  const toast = useToast();

  const validate = (f: BackupSettingsInput): FieldErrors => {
    const e: FieldErrors = {};
    if (!(Number.isInteger(f.keep) && f.keep >= 1 && f.keep <= 365)) e.keep = 'Keep 1–365 backups.';
    if (!(Number.isInteger(f.hourLocal) && f.hourLocal >= 0 && f.hourLocal <= 23)) e.hourLocal = 'Choose an hour between 0 and 23.';
    const dir = f.directory?.trim() ?? '';
    if (dir && !/^([a-z]:\\|\\\\[^\\]+\\[^\\]+)/i.test(dir) && !dir.startsWith('/'))
      e.directory = 'Enter an absolute path such as D:\\Backups\\caddy or a share such as \\\\nas01\\backups\\web-proxy01.';
    return e;
  };
  const errors = { ...serverErrors, ...(submitted ? validate(form) : {}) };
  const dirty = JSON.stringify(form) !== JSON.stringify(initial);
  const set = <K extends keyof BackupSettingsInput>(k: K, v: BackupSettingsInput[K]) => {
    setForm((f) => ({ ...f, [k]: v }));
    setServerErrors({});
  };

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setSubmitted(true);
    if (Object.keys(validate(form)).length) return;
    try {
      await save.mutateAsync({ ...form, directory: form.directory?.trim() || null, password: secretPayload(form.password) });
      toast.success('Backup schedule saved');
    } catch (err) {
      feedback.failed(err, { onFieldErrors: setServerErrors });
    }
  };

  return (
    <form onSubmit={(e) => void submit(e)} noValidate>
      <Card className="p-5">
        <fieldset disabled={save.isPending} className="min-w-0">
          <UnplacedErrors errors={errors} fields={BACKUP_FIELDS} />
          <FormSection
            title={
              <span className="inline-flex items-center gap-1.5">
                <CalendarClock size={15} className="text-fg-subtle" aria-hidden /> Scheduled backups
              </span>
            }
            description="A daily backup of the database, certificate store and caddy.json, written to a local folder or a share. Older backups are removed automatically."
          >
            <SwitchField label="Create a backup every day" checked={form.enabled} onChange={(v) => set('enabled', v)} />
            <div className="grid gap-4 sm:grid-cols-2 lg:max-w-md">
              <Field label="Time (server local time)" error={errors.hourLocal}>
                <Select value={form.hourLocal} onChange={(e) => set('hourLocal', Number(e.target.value))} disabled={!form.enabled}>
                  {HOURS.map((h) => (
                    <option key={h} value={h}>
                      {String(h).padStart(2, '0')}:00
                    </option>
                  ))}
                </Select>
              </Field>
              <Field label="Backups to keep" error={errors.keep}>
                <NumberInput min={1} max={365} value={form.keep} onValueChange={(v) => set('keep', v)} disabled={!form.enabled} />
              </Field>
            </div>
            <Field
              label="Folder"
              error={errors.directory}
              hint="Leave empty for the default backups folder under ProgramData. A share must grant write access to this computer’s account (DOMAIN\SERVER$). Keep copies off this server."
            >
              <Input mono placeholder="\\nas01\backups\web-proxy01" value={form.directory ?? ''} onChange={(e) => set('directory', e.target.value)} />
            </Field>
            <Field
              label="Encryption password"
              error={errors.password}
              hint="Encrypts backup zips with AES-256 (open with 7-Zip). Without the password a backup cannot be restored — keep it in your password manager."
            >
              <SecretInput has={settings.hasPassword} value={form.password} onChange={(v) => set('password', v)} placeholder="Enter a new password" />
            </Field>
            {!settings.hasPassword && !form.password && form.enabled && (
              <Callout tone="warning">
                Backups contain private keys and encrypted secrets. Without a password, anyone who can read the backup folder can read the
                private keys.
              </Callout>
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

function BackupList() {
  const backups = useBackups();
  const run = useRunBackup();
  const feedback = useFeedback();
  const toast = useToast();
  const now = useNow(60_000);
  const [downloading, setDownloading] = useState<string | null>(null);

  const download = async (b: BackupFile) => {
    setDownloading(b.name);
    try {
      await downloadFile(`/api/backups/${encodeURIComponent(b.name)}`, b.name);
    } catch (err) {
      feedback.failed(err, { title: 'Download failed' });
    } finally {
      setDownloading(null);
    }
  };

  const list = [...(backups.data ?? [])].sort((a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt));
  return (
    <Card>
      <CardHeader
        icon={<Archive size={16} />}
        title="Backups on the server"
        description="Created by the schedule or with “Run backup now”."
        actions={
          <Button
            size="sm"
            icon={<Play size={13} />}
            loading={run.isPending}
            onClick={() =>
              run.mutate(undefined, {
                onSuccess: (r) => toast.success('Backup created', r.name),
                onError: (err) => feedback.failed(err, { title: 'Backup failed' }),
              })
            }
          >
            Run backup now
          </Button>
        }
      />
      {backups.isPending ? (
        <TableSkeleton rows={3} cols={4} />
      ) : backups.isError ? (
        <div className="p-4">
          <Callout tone="danger" title="Could not list backups">
            {errorMessage(backups.error)}
          </Callout>
        </div>
      ) : list.length === 0 ? (
        <EmptyState title="No backups yet" description="Enable the schedule above or run a backup now." />
      ) : (
        <Table>
          <THead>
            <tr>
              <TH>File</TH>
              <TH>Created</TH>
              <TH className="text-right">Size</TH>
              <TH className="w-12">
                <span className="sr-only">Actions</span>
              </TH>
            </tr>
          </THead>
          <TBody>
            {list.map((b) => (
              <TR key={b.name}>
                <TD className="mono max-w-[360px] truncate text-sm" title={b.name}>
                  {b.name}
                </TD>
                <TD className="whitespace-nowrap text-fg-muted" title={formatDateTime(b.createdAt)}>
                  {formatDateTime(b.createdAt)} <span className="text-fg-subtle">· {formatRelative(b.createdAt, now)}</span>
                </TD>
                <TD className="mono text-right whitespace-nowrap text-fg-muted">{formatBytes(b.size)}</TD>
                <TD className="text-right">
                  <Button
                    size="sm"
                    variant="ghost"
                    iconOnly
                    aria-label={`Download ${b.name}`}
                    icon={<HardDriveDownload size={15} />}
                    loading={downloading === b.name}
                    onClick={() => void download(b)}
                  />
                </TD>
              </TR>
            ))}
          </TBody>
        </Table>
      )}
    </Card>
  );
}

function DownloadCard() {
  const [downloading, setDownloading] = useState(false);
  const feedback = useFeedback();
  const download = async () => {
    setDownloading(true);
    try {
      const stamp = new Date().toISOString().slice(0, 10);
      await downloadFile('/api/backup', `caddy-proxy-manager-backup-${stamp}.zip`);
    } catch (err) {
      feedback.failed(err, { title: 'Backup failed' });
    } finally {
      setDownloading(false);
    }
  };
  return (
    <Card>
      <CardHeader icon={<Download size={16} />} title="Download a backup now" />
      <CardBody className="flex flex-col gap-4">
        <p className="text-sm text-fg-muted">
          A zip containing the manager database, the certificate store, the current <span className="mono">caddy.json</span> and a manifest. It
          contains private keys — store it securely.
        </p>
        <div>
          <Button variant="primary" icon={<Download size={14} />} loading={downloading} onClick={() => void download()}>
            Download backup
          </Button>
        </div>
      </CardBody>
    </Card>
  );
}

function RestoreCard({ onStaged }: { onStaged: () => void }) {
  const [file, setFile] = useState<File | null>(null);
  const [password, setPassword] = useState('');
  const [fileError, setFileError] = useState<string | null>(null);
  const [passwordError, setPasswordError] = useState<string | null>(null);
  const restore = useRestoreBackup();
  const confirm = useConfirm();
  const feedback = useFeedback();
  const toast = useToast();
  const inputKey = useId();
  const [inputVersion, setInputVersion] = useState(0);

  const runRestore = async () => {
    setPasswordError(null);
    if (!file) {
      setFileError('Choose a backup .zip file.');
      return;
    }
    if (!file.name.toLowerCase().endsWith('.zip')) {
      setFileError('Backups are .zip files created by this console.');
      return;
    }
    const ok = await confirm({
      title: 'Restore this backup?',
      message:
        'The database, certificates and Caddy configuration are replaced when the management service restarts. Changes made since the backup are lost. Secrets (SMTP password, EAB key) from another server must be re-entered.',
      confirmLabel: 'Stage restore',
      danger: true,
    });
    if (!ok) return;
    restore.mutate(
      { file, password: password || undefined },
      {
        onSuccess: (r) => {
          toast.success('Backup staged', r.message ?? 'It is applied the next time the management service starts.');
          if (r.restartRequired) onStaged();
          setFile(null);
          setPassword('');
          setInputVersion((v) => v + 1);
        },
        onError: (err) =>
          feedback.failed(err, {
            title: 'Restore failed',
            onFieldErrors: (fe) => {
              if (fe.password) setPasswordError(fe.password);
              if (fe.file) setFileError(fe.file);
            },
          }),
      },
    );
  };

  return (
    <Card>
      <CardHeader icon={<Upload size={16} />} title="Restore from a backup" />
      <CardBody className="flex flex-col gap-4">
        <Field label="Backup file" error={fileError}>
          <FileInput
            key={`${inputKey}-${inputVersion}`}
            accept=".zip,application/zip"
            onFile={(f) => {
              setFile(f);
              setFileError(null);
            }}
          />
        </Field>
        <Field label="Backup password" error={passwordError} hint="Only for encrypted backups (scheduled backups with a password). Leave empty otherwise.">
          <Input
            type="password"
            autoComplete="off"
            value={password}
            onChange={(e) => {
              setPassword(e.target.value);
              setPasswordError(null);
            }}
          />
        </Field>
        <div>
          <Button variant="danger" icon={<Upload size={14} />} loading={restore.isPending} onClick={() => void runRestore()} disabled={!file}>
            Restore…
          </Button>
        </div>
      </CardBody>
    </Card>
  );
}
