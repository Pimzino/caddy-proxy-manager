import { useState, type FormEvent } from 'react';
import { Link } from 'react-router';
import { LogIn, LogOut, Network } from 'lucide-react';
import { errorMessage } from '@/api/client';
import { useQueryClient } from '@tanstack/react-query';
import { qk, useBinaryOverview, useCaddySettings, useCluster, useIsManagedNode, useJoinCluster, useLeaveCluster, useSaveCaddySettings } from '@/api/hooks';
import type { CaddySettings, CaddySettingsInput, ClusterRole, ClusterStatus, StorageBackend } from '@/api/types';
import { useAuth } from '@/auth';
import { useFeedback } from '@/components/feedback';
import { SecretInput } from '@/components/SecretInput';
import { SecretJsonInput } from '@/components/SecretJsonInput';
import {
  Badge,
  Button,
  Callout,
  Card,
  CardBody,
  CardHeader,
  ChipInput,
  DescriptionList,
  Field,
  FormSection,
  Input,
  LoadingBlock,
  NumberInput,
  RadioCards,
  SwitchField,
  Textarea,
  useConfirm,
  useToast,
  type Tone,
} from '@/components/ui';
import { formatDateTime } from '@/lib/format';
import type { FieldErrors } from '@/lib/validation';
import { caddyFormInput, isHostPort, round3Payload, validateStorage } from './caddyForm';
import { PluginRequirement } from './PluginInstallAction';
import { fieldError, Loader, SaveBar, UnplacedErrors } from './shared';

// Settings › Cluster (SPEC round 3 "Cluster module" / "Storage / clustering at the Caddy level"): this server's role,
// join / leave, and the shared Caddy storage that turns the servers into one certificate cluster.

const ROLE: Record<ClusterRole, { label: string; tone: Tone }> = {
  standalone: { label: 'Standalone', tone: 'neutral' },
  primary: { label: 'Primary', tone: 'accent' },
  node: { label: 'Node', tone: 'info' },
};

const BACKEND_LABEL: Record<StorageBackend, string> = {
  local: 'Local folder',
  fileSystem: 'Shared folder',
  redis: 'Redis',
  custom: 'Custom module',
};

/** Storage plugins (docs/research/round3-cluster.md §2). Module id = caddy.storage.<key>. */
const STORAGE_PLUGINS: Record<string, { pkg: string; label: string; example: string }> = {
  redis: {
    pkg: 'github.com/pberkel/caddy-storage-redis',
    label: 'Redis storage',
    example: '',
  },
  consul: {
    pkg: 'github.com/pteich/caddy-tlsconsul',
    label: 'Consul storage',
    example: '{\n  "module": "consul",\n  "address": "consul.corp.local:8500",\n  "token": "…",\n  "prefix": "caddytls"\n}',
  },
  s3: {
    pkg: 'github.com/ss098/certmagic-s3',
    label: 'S3 storage',
    example: '{\n  "module": "s3",\n  "host": "s3.eu-west-1.amazonaws.com",\n  "bucket": "caddy-certificates",\n  "access_id": "…",\n  "secret_key": "…",\n  "prefix": "caddy"\n}',
  },
  postgres: {
    pkg: 'github.com/yroc92/postgres-storage',
    label: 'PostgreSQL storage',
    example: '{\n  "module": "postgres",\n  "connection_string": "postgres://caddy:…@pg01.corp.local:5432/caddy?sslmode=require"\n}',
  },
};

export function ClusterTab() {
  const cluster = useCluster();
  const settings = useCaddySettings();
  return (
    <div className="flex flex-col gap-4">
      <MembershipCard cluster={cluster} />
      <Loader query={settings}>
        {(data) => <StorageForm key={JSON.stringify(data)} settings={data} role={cluster.data?.role} />}
      </Loader>
    </div>
  );
}

// ---------------------------------------------------------------- membership

function MembershipCard({ cluster }: { cluster: ReturnType<typeof useCluster> }) {
  const { isAdmin } = useAuth();
  const leave = useLeaveCluster();
  const confirm = useConfirm();
  const feedback = useFeedback();
  const toast = useToast();

  if (cluster.isPending)
    return (
      <Card>
        <LoadingBlock />
      </Card>
    );
  if (cluster.isError || !cluster.data)
    return (
      <Callout tone="danger" title="Could not load the cluster status">
        {errorMessage(cluster.error)}
      </Callout>
    );
  const c = cluster.data;

  const doLeave = async () => {
    const ok = await confirm({
      title: `Leave the cluster of ${c.primaryName ?? 'the primary'}?`,
      message: (
        <>
          This server stops receiving configuration and becomes standalone. It keeps the hosts, streams, access lists, certificates and
          Caddy settings it applied last; they become editable here. Caddy keeps running. Also remove the server on the primary’s Servers
          page, or the primary reports it offline.
        </>
      ),
      confirmLabel: 'Leave cluster',
      danger: true,
    });
    if (!ok) return;
    try {
      await leave.mutateAsync();
      toast.success('Left the cluster', 'This server is standalone now.');
    } catch (err) {
      feedback.failed(err, { title: 'Could not leave the cluster' });
    }
  };

  return (
    <Card>
      <CardHeader
        icon={<Network size={16} />}
        title="Cluster membership"
        description={ROLE_DESCRIPTION[c.role](c)}
        actions={
          <>
            <Badge tone={ROLE[c.role].tone}>{ROLE[c.role].label}</Badge>
            {c.role === 'node' && isAdmin && (
              <Button variant="danger-ghost" icon={<LogOut size={14} />} loading={leave.isPending} onClick={() => void doLeave()}>
                Leave cluster
              </Button>
            )}
          </>
        }
      />
      <CardBody className="flex flex-col gap-4">
        {c.warnings.map((w, i) => (
          <Callout key={i} tone="warning">
            {w}
          </Callout>
        ))}
        <DescriptionList
          columns={2}
          items={[
            { label: 'This server', value: c.serverName, mono: true },
            { label: 'Role', value: ROLE[c.role].label },
            { label: 'Primary', value: c.primaryName ?? '—', mono: true, hidden: c.role !== 'node' },
            { label: 'Last contact', value: formatDateTime(c.lastPrimaryContactAt), hidden: c.role !== 'node' },
            {
              label: 'Applied revision',
              value: c.appliedRevision ? <span title={c.appliedRevision}>{c.appliedRevision.slice(0, 12)}</span> : 'Not synced yet',
              mono: !!c.appliedRevision,
              hidden: c.role !== 'node',
            },
            {
              label: 'Nodes',
              value: (
                <>
                  {c.nodeCount}{' '}
                  <Link to="/servers" className="font-sans text-accent-text hover:underline">
                    — manage on the Servers page
                  </Link>
                </>
              ),
              hidden: c.role !== 'primary',
            },
            { label: 'Caddy storage', value: BACKEND_LABEL[c.storageBackend] ?? c.storageBackend },
          ]}
        />
        {c.role === 'standalone' && (isAdmin ? <JoinForm /> : <p className="text-sm text-fg-subtle">An administrator can join this server to a cluster.</p>)}
      </CardBody>
    </Card>
  );
}

const ROLE_DESCRIPTION: Record<ClusterRole, (c: ClusterStatus) => string> = {
  standalone: () =>
    'This server manages its own configuration. Add servers on the Servers page to make it a primary, or join an existing primary with a token.',
  primary: (c) =>
    `This server pushes hosts, streams, access lists, certificates, Caddy settings and plugins to ${c.nodeCount} node${c.nodeCount === 1 ? '' : 's'}.`,
  node: (c) =>
    `Managed by ${c.primaryName ?? 'the primary'}. Sites, certificates, access lists, streams and Caddy settings are made on the primary; this server applies them and keeps serving if the primary is unreachable.`,
};

/** Decodes the primary name from a join token cpmj1.<base64url(json)> for the confirmation (the server validates it). */
function tokenPrimary(token: string): string | null {
  const m = /^cpmj1\.([A-Za-z0-9_-]+)$/.exec(token.trim());
  if (!m) return null;
  try {
    const b64 = m[1].replace(/-/g, '+').replace(/_/g, '/');
    const json = JSON.parse(atob(b64 + '='.repeat((4 - (b64.length % 4)) % 4))) as { primary?: unknown };
    return typeof json.primary === 'string' ? json.primary : null;
  } catch {
    return null;
  }
}

function JoinForm() {
  const join = useJoinCluster();
  const confirm = useConfirm();
  const feedback = useFeedback();
  const toast = useToast();
  const [token, setToken] = useState('');
  const [error, setError] = useState<string | null>(null);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    const t = token.trim();
    if (!/^cpmj1\.[A-Za-z0-9_-]+$/.test(t)) {
      setError('Paste the whole join token shown on the primary. It starts with cpmj1.');
      return;
    }
    const primary = tokenPrimary(t);
    const ok = await confirm({
      title: `Join the cluster of ${primary ?? 'this primary'}?`,
      message: (
        <>
          On the first sync, the hosts, streams, access lists, certificates, Caddy settings and plugins of this server are replaced by the
          primary’s. Users, management UI settings, backups and this server’s ports, bind addresses, admin API address and certificate store
          path stay as they are. Take a backup first if you may need the current configuration.
        </>
      ),
      confirmLabel: 'Join cluster',
      danger: true,
    });
    if (!ok) return;
    try {
      const res = await join.mutateAsync(t);
      setToken('');
      toast.success(`Joined the cluster of ${res.primaryName ?? primary ?? 'the primary'}`, 'Configuration arrives with the first sync.');
    } catch (err) {
      feedback.failed(err, {
        title: 'Could not join the cluster',
        onFieldErrors: (fe) => setError(fe.token ?? Object.values(fe)[0] ?? 'The server rejected the token.'),
      });
    }
  };

  return (
    <form onSubmit={(e) => void submit(e)} noValidate className="flex flex-col gap-3 border-t border-border pt-4">
      <Field
        label="Join a cluster"
        error={error}
        hint={
          <>
            Paste the token shown on the primary when this server was added on its Servers page. The primary then connects to this server’s
            management port, so allow it through the firewall. Command-line alternative, in an elevated PowerShell:{' '}
            <span className="mono">net stop CaddyProxyManager</span>, then{' '}
            <span className="mono">&amp; &apos;C:\Program Files\Caddy Proxy Manager\CaddyManager.exe&apos; cluster join &apos;&lt;token&gt;&apos;</span>, then{' '}
            <span className="mono">net start CaddyProxyManager</span>.
          </>
        }
      >
        <Textarea
          mono
          rows={3}
          spellCheck={false}
          autoComplete="off"
          placeholder="cpmj1.eyJ2Ijox…"
          value={token}
          onChange={(e) => {
            setToken(e.target.value);
            setError(null);
          }}
        />
      </Field>
      <div>
        <Button type="submit" variant="primary" icon={<LogIn size={14} />} loading={join.isPending} disabled={!token.trim()}>
          Join cluster
        </Button>
      </div>
    </form>
  );
}

// ---------------------------------------------------------------- shared storage

const STORAGE_FIELDS = ['storageBackend', 'storagePath', 'redisAddresses', 'redisDb', 'redisUsername', 'redisPassword', 'redisTls', 'redisTlsInsecure', 'redisKeyPrefix', 'redisEncryptionKey', 'storageJson'];

function StorageForm({ settings, role }: { settings: CaddySettings; role?: ClusterRole }) {
  const { isAdmin } = useAuth();
  const { managed, primaryName } = useIsManagedNode();
  const binary = useBinaryOverview();
  const modules = binary.data?.installed?.modules;
  const initial = caddyFormInput(settings);
  const [form, setForm] = useState<CaddySettingsInput>(initial);
  const [submitted, setSubmitted] = useState(false);
  const [serverErrors, setServerErrors] = useState<FieldErrors>({});
  const save = useSaveCaddySettings();
  const qc = useQueryClient();
  const feedback = useFeedback();
  const errors = { ...serverErrors, ...(submitted ? validateStorage(form, settings) : {}) };
  const dirty = JSON.stringify(form) !== JSON.stringify(initial);
  const readOnly = !isAdmin || managed;
  const set = <K extends keyof CaddySettingsInput>(k: K, v: CaddySettingsInput[K]) => {
    setForm((f) => ({ ...f, [k]: v }));
    setServerErrors({});
  };

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setSubmitted(true);
    if (Object.keys(validateStorage(form, settings)).length) return;
    try {
      // The whole settings document is PUT; only the storage fields differ from what was loaded.
      const res = await save.mutateAsync({ ...form, ...round3Payload(form, settings, undefined) });
      feedback.applied(res.apply, 'Storage settings saved and applied');
    } catch (err) {
      feedback.failed(err, { onFieldErrors: setServerErrors });
    } finally {
      // The membership card above shows the storage backend and its warnings from GET /api/cluster (useSaveCaddySettings
      // refreshes the settings, not the cluster status).
      void qc.invalidateQueries({ queryKey: qk.cluster });
    }
  };

  const backend = form.storageBackend;
  const customModule = customStorageModule(form.storageJson);
  const customPlugin = customModule ? STORAGE_PLUGINS[customModule] : undefined;
  const hasModule = (m: string) => !modules || modules.includes(m);
  const copyNote = settings.storageBackend === 'local' && backend === 'fileSystem';

  return (
    <form onSubmit={(e) => void submit(e)} noValidate>
      <Card className="p-5">
        <fieldset disabled={readOnly || save.isPending} className="min-w-0">
          <UnplacedErrors errors={errors} fields={STORAGE_FIELDS} />
          <FormSection
            title="Shared Caddy storage"
            description={
              <>
                Caddy servers that use the same storage share certificates, ACME accounts, locks and challenge data: one server obtains or renews
                each certificate, and HTTP-01 / TLS-ALPN-01 challenges succeed on whichever server the CA reaches. Configuration is not shared
                through storage — the primary pushes it to the nodes.
              </>
            }
          >
            {managed && (
              <Callout tone="info" title={`Read-only on this node — managed by ${primaryName || 'the cluster primary'}`}>
                Every server of the cluster uses the storage configured on the primary. The storage must be reachable from this server too.
              </Callout>
            )}
            {role === 'primary' && backend === 'local' && !managed && (
              <Callout tone="warning">
                This server has nodes but keeps certificates in its local folder, so each node obtains its own certificates and ACME challenges can
                fail behind a load balancer. Choose a shared folder, Redis or a custom module.
              </Callout>
            )}
            <Field label="Storage backend" error={errors.storageBackend}>
              <RadioCards<StorageBackend>
                aria-label="Storage backend"
                value={backend}
                onChange={(v) => set('storageBackend', v)}
                columns={2}
                disabled={readOnly}
                options={[
                  { value: 'local', label: 'Local folder', description: 'Caddy’s data folder on this server. Right for a single server; not shared.' },
                  { value: 'fileSystem', label: 'Shared folder', description: 'A folder on a file share that every server uses. No plugin needed.' },
                  { value: 'redis', label: 'Redis', description: 'A Redis server. Needs the caddy-storage-redis plugin on every server.' },
                  { value: 'custom', label: 'Custom JSON', description: 'Any Caddy storage module (Consul, S3, PostgreSQL…) configured as JSON. Needs its plugin.' },
                ]}
              />
            </Field>

            {backend === 'fileSystem' && (
              <>
                <Field
                  label="Folder"
                  required
                  error={errors.storagePath}
                  hint={
                    <>
                      Absolute path or UNC share, e.g. <span className="mono">\\fs01\caddy$\storage</span>. Caddy runs as LocalSystem and reaches shares as
                      each server’s computer account (<span className="mono">DOMAIN\SERVER$</span>): grant every cluster server Modify on the share and the
                      folder. Mapped drive letters are not visible to services. Locks are files in this folder, so all servers must use the same folder
                      directly, not replicated copies.
                    </>
                  }
                >
                  <Input mono placeholder="\\fs01\caddy$\storage" value={form.storagePath ?? ''} onChange={(e) => set('storagePath', e.target.value)} />
                </Field>
                {copyNote && (
                  <Callout tone="info">
                    When you save, the existing <span className="mono">certificates</span>, <span className="mono">acme</span>,{' '}
                    <span className="mono">pki</span> and <span className="mono">ocsp</span> folders are copied to the new folder unless they already exist
                    there, so issued certificates and the internal CA root are kept.
                  </Callout>
                )}
              </>
            )}

            {backend === 'redis' && (
              <>
                <PluginRequirement
                  pkg={STORAGE_PLUGINS.redis.pkg}
                  what="Redis storage"
                  installed={hasModule('caddy.storage.redis')}
                  title="The Redis storage module is not in the installed Caddy"
                >
                  Redis storage needs the plugin <span className="mono text-fg">{STORAGE_PLUGINS.redis.pkg}</span> (module{' '}
                  <span className="mono">caddy.storage.redis</span>). The settings cannot be saved until Caddy includes it. Nodes install the primary’s
                  plugins automatically.
                </PluginRequirement>
                <Field label="Servers" required error={fieldError(errors, 'redisAddresses')} hint="host:port of the Redis server (several for a replicated setup).">
                  <ChipInput
                    value={form.redisAddresses}
                    onChange={(v) => set('redisAddresses', v)}
                    placeholder="redis01.corp.local:6379"
                    validate={(v) => (isHostPort(v) ? null : 'use host:port')}
                    disabled={readOnly}
                  />
                </Field>
                <div className="grid gap-4 sm:grid-cols-3">
                  <Field label="Database" error={errors.redisDb}>
                    <NumberInput min={0} max={15} value={form.redisDb} onValueChange={(v) => set('redisDb', v)} />
                  </Field>
                  <Field label="Key prefix" error={errors.redisKeyPrefix} hint="Default caddy.">
                    <Input mono value={form.redisKeyPrefix} onChange={(e) => set('redisKeyPrefix', e.target.value)} />
                  </Field>
                  <Field label="User name" error={errors.redisUsername} hint="Redis 6 ACL user; empty for password-only auth.">
                    <Input mono autoComplete="off" value={form.redisUsername ?? ''} onChange={(e) => set('redisUsername', e.target.value)} />
                  </Field>
                </div>
                <div className="grid gap-4 md:grid-cols-2">
                  <Field label="Password" error={errors.redisPassword}>
                    <SecretInput has={settings.hasRedisPassword} value={form.redisPassword} onChange={(v) => set('redisPassword', v)} disabled={readOnly} />
                  </Field>
                  <Field label="Encryption key" error={errors.redisEncryptionKey} hint="Optional. Encrypts the stored values; every server uses the same key.">
                    <SecretInput
                      has={settings.hasRedisEncryptionKey}
                      value={form.redisEncryptionKey}
                      onChange={(v) => set('redisEncryptionKey', v)}
                      disabled={readOnly}
                    />
                  </Field>
                </div>
                <SwitchField label="TLS" description="Connect to Redis over TLS." checked={form.redisTls} onChange={(v) => set('redisTls', v)} />
                {form.redisTls && (
                  <SwitchField
                    label="Skip certificate verification"
                    description="Accept any server certificate. Only for testing — the connection is not authenticated."
                    checked={form.redisTlsInsecure}
                    onChange={(v) => set('redisTlsInsecure', v)}
                  />
                )}
              </>
            )}

            {backend === 'custom' && (
              <>
                <Field
                  label="Storage module (JSON object)"
                  required
                  error={errors.storageJson}
                  hint={
                    <>
                      Written as Caddy’s <span className="mono">storage</span> value. <span className="mono">module</span> is the name after{' '}
                      <span className="mono">caddy.storage.</span>, e.g. <span className="mono">consul</span>, <span className="mono">s3</span> or{' '}
                      <span className="mono">postgres</span>. Stored encrypted; it may contain credentials.
                    </>
                  }
                >
                  <SecretJsonInput
                    has={settings.hasStorageJson}
                    value={form.storageJson}
                    onChange={(v) => set('storageJson', v)}
                    disabled={readOnly}
                    aria-label="Storage module JSON"
                    placeholder={STORAGE_PLUGINS.consul.example}
                  />
                </Field>
                {!readOnly && (
                  <div className="flex flex-wrap items-center gap-1.5 text-xs text-fg-subtle">
                    <span>Examples:</span>
                    {(['consul', 's3', 'postgres'] as const).map((k) => (
                      <Button key={k} size="xs" onClick={() => set('storageJson', STORAGE_PLUGINS[k].example)} title={`Needs plugin ${STORAGE_PLUGINS[k].pkg}`}>
                        {STORAGE_PLUGINS[k].label}
                      </Button>
                    ))}
                  </div>
                )}
                {customModule && customModule !== 'file_system' && customPlugin && (
                  <PluginRequirement
                    pkg={customPlugin.pkg}
                    what={customPlugin.label}
                    installed={hasModule(`caddy.storage.${customModule}`)}
                    title={`The caddy.storage.${customModule} module is not in the installed Caddy`}
                  >
                    Add the plugin <span className="mono text-fg">{customPlugin.pkg}</span> and rebuild Caddy before saving.
                  </PluginRequirement>
                )}
                {customModule && customModule !== 'file_system' && !customPlugin && !hasModule(`caddy.storage.${customModule}`) && (
                  <Callout tone="warning" title={`The caddy.storage.${customModule} module is not in the installed Caddy`}>
                    Add the plugin that provides it on the{' '}
                    <Link to={`/caddy/plugins?q=${encodeURIComponent(customModule)}`} className="text-accent-text hover:underline">
                      Plugins page
                    </Link>{' '}
                    and rebuild Caddy before saving.
                  </Callout>
                )}
              </>
            )}
          </FormSection>
        </fieldset>
        <div className="mt-5 border-t border-border pt-4">
          {managed ? (
            <p className="text-sm text-fg-subtle">Storage is configured on the primary.</p>
          ) : (
            <SaveBar dirty={dirty} saving={save.isPending} onReset={() => setForm(initial)} readOnly={!isAdmin} />
          )}
        </div>
      </Card>
    </form>
  );
}

/** The "module" of typed storage JSON (null while nothing parseable is typed). */
function customStorageModule(v: string | null | undefined): string | null {
  if (typeof v !== 'string' || v === '\u0000' || !v.trim()) return null;
  try {
    const o = JSON.parse(v) as { module?: unknown };
    return typeof o?.module === 'string' && o.module ? o.module : null;
  } catch {
    return null;
  }
}
