import { useState, type FormEvent } from 'react';
import { BookUser, MoreHorizontal, Pencil, Trash2, UserPlus, Users } from 'lucide-react';
import { ApiError, errorMessage } from '@/api/client';
import { useDeleteUser, useSaveUser, useUsers } from '@/api/hooks';
import type { UserDto, UserRole } from '@/api/types';
import { useAuth } from '@/auth';
import { useFeedback } from '@/components/feedback';
import {
  Badge,
  Button,
  Callout,
  Card,
  Dialog,
  DropdownMenu,
  EmptyState,
  Field,
  Input,
  PageHeader,
  RadioCards,
  StatusDot,
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
import { formatDateTime, formatRelative } from '@/lib/format';
import { useNow } from '@/lib/useNow';
import { isValidEmail, MIN_PASSWORD_LENGTH, serverFieldErrors, type FieldErrors } from '@/lib/validation';

const roleOptions: { value: UserRole; label: string; description: string }[] = [
  { value: 'viewer', label: 'Viewer', description: 'Read-only access to everything except admin pages.' },
  { value: 'operator', label: 'Operator', description: 'Manage hosts, certificates, access lists; start/stop Caddy.' },
  { value: 'admin', label: 'Admin', description: 'Everything, including users, settings, updates and readiness fixes.' },
];

const isDirectory = (u?: UserDto) => u?.externalSource === 'ldap';

export default function UsersPage() {
  const { user: me } = useAuth();
  const users = useUsers();
  const del = useDeleteUser();
  const feedback = useFeedback();
  const confirm = useConfirm();
  const toast = useToast();
  const now = useNow(60_000);
  const [editor, setEditor] = useState<{ user?: UserDto } | null>(null);
  const admins = (users.data ?? []).filter((u) => u.role === 'admin' && !u.disabled).length;

  const remove = async (u: UserDto) => {
    const ok = await confirm({
      title: 'Delete user?',
      message: `${u.name} (${u.email}) will lose access immediately. Audit entries keep their name.`,
      confirmLabel: 'Delete user',
      danger: true,
    });
    if (!ok) return;
    del.mutate(u.id, {
      onSuccess: () => toast.success('User deleted'),
      onError: (err) => feedback.failed(err, { title: 'Could not delete the user' }),
    });
  };

  return (
    <>
      <PageHeader
        title="Users"
        description="People who can sign in to this console. Local accounts use a password (at least 12 characters); directory accounts are created at their first sign-in when Settings › Directory is enabled."
        actions={
          <Button variant="primary" icon={<UserPlus size={14} />} onClick={() => setEditor({})}>
            Add user
          </Button>
        }
      />
      <Card>
        {users.isPending ? (
          <TableSkeleton rows={4} cols={5} />
        ) : users.isError ? (
          <div className="p-4">
            <Callout tone="danger" title="Could not load users">
              {errorMessage(users.error)}
            </Callout>
          </div>
        ) : users.data.length === 0 ? (
          <EmptyState icon={<Users size={18} />} title="No users" />
        ) : (
          <Table>
            <THead>
              <tr>
                <TH>Name</TH>
                <TH>E-mail</TH>
                <TH>Role</TH>
                <TH>Status</TH>
                <TH>Last sign-in</TH>
                <TH>Created</TH>
                <TH className="w-12">
                  <span className="sr-only">Actions</span>
                </TH>
              </tr>
            </THead>
            <TBody>
              {[...users.data]
                .sort((a, b) => a.name.localeCompare(b.name))
                .map((u) => {
                  const self = u.id === me?.id;
                  const lastAdmin = u.role === 'admin' && !u.disabled && admins <= 1;
                  return (
                    <TR key={u.id} interactive onClick={() => setEditor({ user: u })}>
                      <TD className="font-medium text-fg">
                        <span className="inline-flex flex-wrap items-center gap-1.5">
                          {u.name}
                          {self && <span className="text-xs font-normal text-fg-subtle">(you)</span>}
                          {isDirectory(u) && (
                            <Badge tone="info" icon={<BookUser size={11} aria-hidden />} title="Signs in with the directory (LDAP); role from group membership">
                              Directory
                            </Badge>
                          )}
                        </span>
                      </TD>
                      <TD className="text-fg-muted">{u.email}</TD>
                      <TD>
                        <Badge tone={u.role === 'admin' ? 'accent' : u.role === 'operator' ? 'info' : 'neutral'} className="capitalize">
                          {u.role}
                        </Badge>
                      </TD>
                      <TD>{u.disabled ? <StatusDot tone="neutral" label="Disabled" /> : <StatusDot tone="success" label="Active" />}</TD>
                      <TD className="whitespace-nowrap text-fg-muted" title={formatDateTime(u.lastLoginAt)}>
                        {u.lastLoginAt ? formatRelative(u.lastLoginAt, now) : 'Never'}
                      </TD>
                      <TD className="whitespace-nowrap text-fg-muted">{formatDateTime(u.createdAt)}</TD>
                      <TD onClick={(e) => e.stopPropagation()} className="text-right">
                        <DropdownMenu
                          items={[
                            { label: 'Edit', icon: <Pencil size={14} />, onSelect: () => setEditor({ user: u }) },
                            'separator',
                            {
                              label: self ? 'Delete (yourself)' : lastAdmin ? 'Delete (last admin)' : 'Delete',
                              icon: <Trash2 size={14} />,
                              danger: true,
                              disabled: self || lastAdmin,
                              onSelect: () => void remove(u),
                            },
                          ]}
                          trigger={(p) => <Button {...p} variant="ghost" size="sm" iconOnly aria-label={`Actions for ${u.name}`} icon={<MoreHorizontal size={16} />} />}
                        />
                      </TD>
                    </TR>
                  );
                })}
            </TBody>
          </Table>
        )}
      </Card>
      {editor && (
        <UserEditor
          user={editor.user}
          isSelf={editor.user?.id === me?.id}
          isLastAdmin={editor.user?.role === 'admin' && !editor.user.disabled && admins <= 1}
          onClose={() => setEditor(null)}
        />
      )}
    </>
  );
}

function UserEditor({ user, isSelf, isLastAdmin, onClose }: { user?: UserDto; isSelf: boolean; isLastAdmin: boolean; onClose: () => void }) {
  const [name, setName] = useState(user?.name ?? '');
  const [email, setEmail] = useState(user?.email ?? '');
  const [role, setRole] = useState<UserRole>(user?.role ?? 'viewer');
  const [disabled, setDisabled] = useState(user?.disabled ?? false);
  const [password, setPassword] = useState('');
  const [submitted, setSubmitted] = useState(false);
  const [serverErrors, setServerErrors] = useState<FieldErrors>({});
  const [serverMessage, setServerMessage] = useState<string | null>(null);
  const save = useSaveUser();
  const toast = useToast();
  const feedback = useFeedback();
  const directory = isDirectory(user);
  const locked = isSelf || isLastAdmin;

  const validate = (): FieldErrors => {
    const e: FieldErrors = {};
    if (!name.trim()) e.name = 'Enter a name.';
    if (!isValidEmail(email)) e.email = 'Enter a valid e-mail address.';
    if (!user && password.length < MIN_PASSWORD_LENGTH) e.password = `Use at least ${MIN_PASSWORD_LENGTH} characters.`;
    if (user && password && password.length < MIN_PASSWORD_LENGTH) e.password = `Use at least ${MIN_PASSWORD_LENGTH} characters, or leave empty.`;
    return e;
  };
  const errors = { ...serverErrors, ...(submitted ? validate() : {}) };

  const submit = (ev: FormEvent) => {
    ev.preventDefault();
    setSubmitted(true);
    setServerMessage(null);
    if (Object.keys(validate()).length) return;
    const onError = (err: unknown) => {
      if (err instanceof ApiError && (err.status === 400 || err.status === 409)) {
        if (err.errors) setServerErrors(serverFieldErrors(err.errors));
        else if (err.status === 409 && /mail/i.test(err.detail ?? '')) setServerErrors({ email: err.detail ?? err.title });
        else setServerMessage(err.display);
        return;
      }
      feedback.failed(err);
    };
    const onSuccess = () => {
      toast.success(user ? 'User updated' : 'User created');
      onClose();
    };
    if (user)
      save.mutate(
        { id: user.id, body: { name: name.trim(), email: email.trim(), role, disabled, ...(password ? { password } : {}) } },
        { onSuccess, onError },
      );
    else save.mutate({ body: { name: name.trim(), email: email.trim(), role, password } }, { onSuccess, onError });
  };

  return (
    <Dialog
      open
      onClose={onClose}
      dismissible={!save.isPending}
      title={user ? 'Edit user' : 'Add user'}
      footer={
        <>
          <Button onClick={onClose} disabled={save.isPending}>
            Cancel
          </Button>
          <Button type="submit" form="user-form" variant="primary" loading={save.isPending}>
            {user ? 'Save' : 'Create user'}
          </Button>
        </>
      }
    >
      <form id="user-form" onSubmit={submit} noValidate className="flex flex-col gap-4">
        {serverMessage && <Callout tone="danger">{serverMessage}</Callout>}
        {directory && (
          <Callout tone="info" title="Directory account">
            This user signs in with their directory (LDAP) password and has no local password. The role follows directory group membership
            and is updated at every sign-in. Disable the account to block access to this console.
          </Callout>
        )}
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="Name" required error={errors.name}>
            <Input value={name} onChange={(e) => { setName(e.target.value); setServerErrors({}); }} autoComplete="off" />
          </Field>
          <Field label="E-mail" required error={errors.email} hint="Used to sign in and for alert e-mails if listed as a recipient.">
            <Input type="email" value={email} onChange={(e) => { setEmail(e.target.value); setServerErrors({}); }} autoComplete="off" />
          </Field>
        </div>
        <Field label="Role" hint={locked ? (isSelf ? 'You cannot change your own role.' : 'This is the last active admin.') : directory ? 'Assigned from directory groups at each sign-in (Settings › Directory).' : undefined}>
          <RadioCards aria-label="Role" columns={3} value={role} onChange={setRole} options={roleOptions} disabled={locked || directory} />
        </Field>
        {!directory && (
          <Field
            label={user ? 'New password' : 'Password'}
            required={!user}
            error={errors.password}
            hint={user ? 'Leave empty to keep the current password. Changing it signs the user out everywhere.' : `At least ${MIN_PASSWORD_LENGTH} characters.`}
          >
            <Input type="password" autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} />
          </Field>
        )}
        {user && (
          <SwitchField
            label="Disabled"
            description={locked ? 'You cannot disable yourself or the last admin.' : 'Disabled users cannot sign in; active sessions end immediately.'}
            checked={disabled}
            onChange={setDisabled}
            disabled={locked}
          />
        )}
      </form>
    </Dialog>
  );
}
