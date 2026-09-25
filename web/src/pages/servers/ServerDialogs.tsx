// Add / edit server dialogs, the join-token dialog and the per-server action menu (Servers list and detail).
import { useState, type FormEvent, type ReactNode } from 'react';
import { useNavigate } from 'react-router';
import { ExternalLink, KeyRound, Pencil, RefreshCw, Trash2 } from 'lucide-react';
import { ApiError } from '@/api/client';
import { useAddServer, useRegenerateServerToken, useRemoveServer, useSyncServer, useUpdateServer } from '@/api/hooks';
import type { AddServerResult, ServerSummary } from '@/api/types';
import { useAuth } from '@/auth';
import { useFeedback } from '@/components/feedback';
import { Button, Callout, Checkbox, Dialog, Field, Input, useConfirm, useToast, type MenuItem } from '@/components/ui';
import { serverFieldErrors, type FieldErrors } from '@/lib/validation';
import { JoinTokenPanel } from './shared';

function urlError(url: string): string | undefined {
  const v = url.trim();
  if (!v) return 'Enter the address of the node’s management UI.';
  if (!URL.canParse(v)) return 'Enter a full URL, e.g. https://web-proxy02.corp.example.com:8443.';
  const u = new URL(v);
  if (u.protocol !== 'http:' && u.protocol !== 'https:') return 'Use an http:// or https:// URL.';
  if (!u.hostname) return 'The URL needs a host name.';
  return undefined;
}

function ServerForm({
  id,
  name,
  url,
  onName,
  onUrl,
  errors,
  message,
  onSubmit,
  children,
}: {
  id: string;
  name: string;
  url: string;
  onName: (v: string) => void;
  onUrl: (v: string) => void;
  errors: FieldErrors;
  message: string | null;
  onSubmit: (e: FormEvent) => void;
  children?: ReactNode;
}) {
  return (
    <form id={id} onSubmit={onSubmit} noValidate className="flex flex-col gap-4">
      {message && <Callout tone="danger">{message}</Callout>}
      <Field label="Name" required error={errors.name} hint="Shown in this console, e.g. the node’s computer name.">
        <Input value={name} onChange={(e) => onName(e.target.value)} autoComplete="off" maxLength={64} placeholder="WEB-PROXY02" />
      </Field>
      <Field
        label="Management URL"
        required
        error={errors.url}
        hint="Where this server reaches the node’s management UI (its UI port). HTTPS with a self-signed certificate is pinned by fingerprint on first contact."
      >
        <Input value={url} onChange={(e) => onUrl(e.target.value)} mono autoComplete="off" spellCheck={false} placeholder="https://web-proxy02.corp.example.com:8443" />
      </Field>
      {children}
    </form>
  );
}

/** Two-step dialog: name + URL, then the join token (shown once). */
export function AddServerDialog({ onClose }: { onClose: () => void }) {
  const [name, setName] = useState('');
  const [url, setUrl] = useState('');
  const [submitted, setSubmitted] = useState(false);
  const [serverErrors, setServerErrors] = useState<FieldErrors>({});
  const [message, setMessage] = useState<string | null>(null);
  const [result, setResult] = useState<AddServerResult | null>(null);
  const add = useAddServer();
  const feedback = useFeedback();

  const validate = (): FieldErrors => {
    const e: FieldErrors = {};
    if (!name.trim()) e.name = 'Enter a name for the server.';
    const ue = urlError(url);
    if (ue) e.url = ue;
    return e;
  };
  const errors = { ...serverErrors, ...(submitted ? validate() : {}) };

  const submit = (ev: FormEvent) => {
    ev.preventDefault();
    setSubmitted(true);
    setMessage(null);
    if (Object.keys(validate()).length) return;
    add.mutate(
      { name: name.trim(), url: url.trim().replace(/\/+$/, '') },
      {
        onSuccess: setResult,
        onError: (err) => {
          if (err instanceof ApiError && (err.status === 400 || err.status === 409)) {
            if (err.errors) setServerErrors(serverFieldErrors(err.errors));
            else setMessage(err.display);
            return;
          }
          feedback.failed(err, { title: 'Could not add the server' });
        },
      },
    );
  };

  if (result)
    return (
      <Dialog
        open
        onClose={onClose}
        size="lg"
        title={`Join ${result.server.name} to this cluster`}
        description="The server was added. It shows “Waiting to join” until the node uses this token."
        footer={
          <Button variant="primary" onClick={onClose}>
            Done
          </Button>
        }
      >
        <JoinTokenPanel serverName={result.server.name} token={result.joinToken} fingerprint={result.fingerprint} />
      </Dialog>
    );

  return (
    <Dialog
      open
      onClose={onClose}
      dismissible={!add.isPending}
      title="Add server"
      description="Adds a node that this server manages. This server becomes the cluster’s primary."
      footer={
        <>
          <Button onClick={onClose} disabled={add.isPending}>
            Cancel
          </Button>
          <Button type="submit" form="add-server-form" variant="primary" loading={add.isPending}>
            Add and create join token
          </Button>
        </>
      }
    >
      <ServerForm
        id="add-server-form"
        name={name}
        url={url}
        onName={(v) => {
          setName(v);
          setServerErrors({});
        }}
        onUrl={(v) => {
          setUrl(v);
          setServerErrors({});
        }}
        errors={errors}
        message={message}
        onSubmit={submit}
      >
        <Callout tone="info">
          The node must run Caddy Proxy Manager and be reachable from this server on its management port. Its sites, certificates
          and Caddy settings are replaced by this server’s when it joins.
        </Callout>
      </ServerForm>
    </Dialog>
  );
}

function EditServerDialog({ server, onClose }: { server: ServerSummary; onClose: () => void }) {
  const [name, setName] = useState(server.name);
  const [url, setUrl] = useState(server.url ?? '');
  const [repin, setRepin] = useState(false);
  const [submitted, setSubmitted] = useState(false);
  const [serverErrors, setServerErrors] = useState<FieldErrors>({});
  const [message, setMessage] = useState<string | null>(null);
  const update = useUpdateServer();
  const toast = useToast();
  const feedback = useFeedback();
  const https = url.trim().toLowerCase().startsWith('https://');
  const urlChanged = url.trim().replace(/\/+$/, '') !== (server.url ?? '');

  const validate = (): FieldErrors => {
    const e: FieldErrors = {};
    if (!name.trim()) e.name = 'Enter a name for the server.';
    const ue = urlError(url);
    if (ue) e.url = ue;
    return e;
  };
  const errors = { ...serverErrors, ...(submitted ? validate() : {}) };

  const submit = (ev: FormEvent) => {
    ev.preventDefault();
    setSubmitted(true);
    setMessage(null);
    if (Object.keys(validate()).length) return;
    update.mutate(
      { id: server.id, name: name.trim(), url: url.trim().replace(/\/+$/, ''), ...(repin && https && !urlChanged ? { repin: true } : {}) },
      {
        onSuccess: () => {
          toast.success('Server updated');
          onClose();
        },
        onError: (err) => {
          if (err instanceof ApiError && (err.status === 400 || err.status === 409)) {
            if (err.errors) setServerErrors(serverFieldErrors(err.errors));
            else setMessage(err.display);
            return;
          }
          feedback.failed(err, { title: 'Could not update the server' });
        },
      },
    );
  };

  return (
    <Dialog
      open
      onClose={onClose}
      dismissible={!update.isPending}
      title={`Edit ${server.name}`}
      footer={
        <>
          <Button onClick={onClose} disabled={update.isPending}>
            Cancel
          </Button>
          <Button type="submit" form="edit-server-form" variant="primary" loading={update.isPending}>
            Save
          </Button>
        </>
      }
    >
      <ServerForm
        id="edit-server-form"
        name={name}
        url={url}
        onName={(v) => {
          setName(v);
          setServerErrors({});
        }}
        onUrl={(v) => {
          setUrl(v);
          setServerErrors({});
        }}
        errors={errors}
        message={message}
        onSubmit={submit}
      >
        {https && (
          <Checkbox
            checked={repin || urlChanged}
            disabled={urlChanged}
            onChange={setRepin}
            label="Trust the node’s current HTTPS certificate (re-pin)"
            description={
              urlChanged
                ? 'A new URL is always pinned again on first contact.'
                : 'Use this after the node’s certificate was replaced. The fingerprint is taken on the next contact; make sure nobody intercepts the connection.'
            }
          />
        )}
      </ServerForm>
    </Dialog>
  );
}

/**
 * Actions on a server (row menu on the Servers page, header menu on the detail page) and the dialogs
 * they open. Role gating: Sync now = operator; Regenerate token, Edit, Remove = admin.
 */
export function useServerActions(opts: { onRemoved?: () => void } = {}) {
  const { canOperate, isAdmin } = useAuth();
  const navigate = useNavigate();
  const confirm = useConfirm();
  const toast = useToast();
  const feedback = useFeedback();
  const sync = useSyncServer();
  const regen = useRegenerateServerToken();
  const remove = useRemoveServer();
  const [editing, setEditing] = useState<ServerSummary | null>(null);
  const [token, setToken] = useState<{ server: ServerSummary; token: string } | null>(null);

  const syncNow = (s: ServerSummary) =>
    sync.mutate(s.id, {
      onSuccess: (r) =>
        r.sync?.inSync
          ? toast.success(`${s.name} is in sync`)
          : toast.warning(`${s.name} is not in sync yet`, r.sync?.lastError ?? 'The node reported an older revision; it retries automatically.'),
      onError: (err) => feedback.failed(err, { title: `Could not sync ${s.name}` }),
    });

  const regenerate = async (s: ServerSummary) => {
    const ok = await confirm({
      title: 'Create a new join token?',
      message: `The current join token of ${s.name} stops working. A node that already joined keeps working only if it is re-joined with the new token.`,
      confirmLabel: 'Create new token',
      danger: true,
    });
    if (!ok) return;
    regen.mutate(s.id, {
      onSuccess: (r) => setToken({ server: s, token: r.joinToken }),
      onError: (err) => feedback.failed(err, { title: 'Could not create a join token' }),
    });
  };

  const removeServer = async (s: ServerSummary) => {
    const ok = await confirm({
      title: `Remove ${s.name}?`,
      message: (
        <>
          This server stops managing <strong className="font-medium text-fg">{s.name}</strong> and asks it to leave the cluster. The
          node keeps serving its last configuration as a standalone server. Its statistics are no longer shown here.
        </>
      ),
      confirmLabel: 'Remove server',
      danger: true,
    });
    if (!ok) return;
    remove.mutate(s.id, {
      onSuccess: () => {
        toast.success(`${s.name} removed`);
        opts.onRemoved?.();
      },
      onError: (err) => feedback.failed(err, { title: `Could not remove ${s.name}` }),
    });
  };

  const menuItems = (s: ServerSummary, { includeOpen = true } = {}): MenuItem[] => {
    const node = !s.isLocal;
    return [
      { label: 'Open', icon: <ExternalLink size={14} />, onSelect: () => navigate(`/servers/${encodeURIComponent(s.id)}`), hidden: !includeOpen },
      { label: 'Sync now', icon: <RefreshCw size={14} />, onSelect: () => syncNow(s), hidden: !node || !canOperate, disabled: s.status === 'pending' },
      { label: 'Regenerate join token', icon: <KeyRound size={14} />, onSelect: () => void regenerate(s), hidden: !node || !isAdmin },
      { label: 'Edit', icon: <Pencil size={14} />, onSelect: () => setEditing(s), hidden: !node || !isAdmin },
      ...(node && isAdmin ? (['separator'] as MenuItem[]) : []),
      { label: 'Remove', icon: <Trash2 size={14} />, danger: true, onSelect: () => void removeServer(s), hidden: !node || !isAdmin },
    ];
  };

  const dialogs = (
    <>
      {editing && <EditServerDialog server={editing} onClose={() => setEditing(null)} />}
      {token && (
        <Dialog
          open
          onClose={() => setToken(null)}
          size="lg"
          title={`New join token for ${token.server.name}`}
          footer={
            <Button variant="primary" onClick={() => setToken(null)}>
              Done
            </Button>
          }
        >
          <JoinTokenPanel serverName={token.server.name} token={token.token} />
        </Dialog>
      )}
    </>
  );

  return { menuItems, dialogs, syncNow, syncing: sync.isPending ? sync.variables : undefined };
}
