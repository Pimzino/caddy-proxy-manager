import { useId, useMemo, useState, type FormEvent } from 'react';
import { ArrowDown, ArrowUp, ListChecks, MoreHorizontal, Pencil, Plus, Trash2, UserPlus } from 'lucide-react';
import { ApiError, errorMessage } from '@/api/client';
import { useAccessLists, useDeleteAccessList, useSaveAccessList } from '@/api/hooks';
import type { AccessList, AccessListInput, IpRule } from '@/api/types';
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
  SearchInput,
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
} from '@/components/ui';
import { formatDate, pluralize } from '@/lib/format';
import { isValidCidr, type FieldErrors } from '@/lib/validation';

export default function AccessListsPage() {
  const { canOperate } = useAuth();
  const lists = useAccessLists();
  const del = useDeleteAccessList();
  const feedback = useFeedback();
  const confirm = useConfirm();
  const [q, setQ] = useState('');
  const [editor, setEditor] = useState<{ list?: AccessList } | null>(null);

  const filtered = useMemo(() => {
    const n = q.trim().toLowerCase();
    const all = [...(lists.data ?? [])].sort((a, b) => a.name.localeCompare(b.name));
    return n ? all.filter((l) => [l.name, ...l.rules.map((r) => r.cidr), ...l.users.map((u) => u.username)].join(' ').toLowerCase().includes(n)) : all;
  }, [lists.data, q]);

  const remove = async (l: AccessList) => {
    if (l.usedBy > 0) {
      await confirm({
        title: 'Access list in use',
        message: `“${l.name}” is used by ${l.usedBy} host${l.usedBy === 1 ? '' : 's'}. Remove it from those hosts before deleting it.`,
        confirmLabel: 'OK',
        alertOnly: true,
      });
      return;
    }
    const ok = await confirm({
      title: 'Delete access list?',
      message: `“${l.name}” will be deleted. This cannot be undone.`,
      confirmLabel: 'Delete',
      danger: true,
    });
    if (!ok) return;
    del.mutate(l.id, {
      onSuccess: (res) => feedback.applied(res.apply, 'Access list deleted'),
      onError: (err) => feedback.failed(err, { title: 'Could not delete the access list' }),
    });
  };

  return (
    <>
      <PageHeader
        title="Access Lists"
        description="Reusable IP allow/deny rules and basic-authentication users that you attach to hosts."
        actions={
          canOperate && (
            <Button variant="primary" icon={<Plus size={14} />} onClick={() => setEditor({})}>
              Add access list
            </Button>
          )
        }
      />
      <Card>
        <div className="flex flex-wrap items-center gap-3 border-b border-border px-4 py-2.5">
          <SearchInput value={q} onChange={setQ} placeholder="Search names, CIDRs, users…" />
          <span className="ml-auto text-sm text-fg-subtle">{lists.data && pluralize(lists.data.length, 'list')}</span>
        </div>
        {lists.isPending ? (
          <TableSkeleton rows={3} cols={5} />
        ) : lists.isError ? (
          <div className="p-4">
            <Callout tone="danger" title="Could not load access lists">
              {errorMessage(lists.error)}
            </Callout>
          </div>
        ) : (lists.data ?? []).length === 0 ? (
          <EmptyState
            icon={<ListChecks size={18} />}
            title="No access lists yet"
            description="Create a list such as “Office networks only” (allow 10.0.0.0/8, deny all) and attach it to internal hosts."
            action={
              canOperate && (
                <Button variant="primary" icon={<Plus size={14} />} onClick={() => setEditor({})}>
                  Add access list
                </Button>
              )
            }
          />
        ) : filtered.length === 0 ? (
          <EmptyState title="No matches" description={`Nothing matches “${q}”.`} />
        ) : (
          <Table>
            <THead>
              <tr>
                <TH>Name</TH>
                <TH>IP rules</TH>
                <TH>Users</TH>
                <TH>Mode</TH>
                <TH>Used by</TH>
                <TH>Updated</TH>
                <TH className="w-12">
                  <span className="sr-only">Actions</span>
                </TH>
              </tr>
            </THead>
            <TBody>
              {filtered.map((l) => (
                <TR key={l.id} interactive onClick={() => setEditor({ list: l })}>
                  <TD className="font-medium text-fg">{l.name}</TD>
                  <TD>
                    {l.rules.length === 0 ? (
                      <span className="text-fg-subtle">None</span>
                    ) : (
                      <span className="mono text-sm" title={l.rules.map((r) => `${r.action} ${r.cidr}`).join('\n')}>
                        {l.rules
                          .slice(0, 2)
                          .map((r) => `${r.action} ${r.cidr}`)
                          .join(', ')}
                        {l.rules.length > 2 && <span className="text-fg-subtle"> +{l.rules.length - 2}</span>}
                      </span>
                    )}
                  </TD>
                  <TD>{l.users.length === 0 ? <span className="text-fg-subtle">None</span> : l.users.length}</TD>
                  <TD>
                    {l.rules.length > 0 && l.users.length > 0 ? (
                      <Badge tone={l.satisfyAny ? 'info' : 'accent'}>{l.satisfyAny ? 'IP or login' : 'IP and login'}</Badge>
                    ) : (
                      <Badge>{l.users.length > 0 ? 'Login' : 'IP'}</Badge>
                    )}
                  </TD>
                  <TD>{l.usedBy === 0 ? <span className="text-fg-subtle">Not used</span> : `${l.usedBy} host${l.usedBy === 1 ? '' : 's'}`}</TD>
                  <TD className="text-fg-muted">{formatDate(l.updatedAt)}</TD>
                  <TD onClick={(e) => e.stopPropagation()} className="text-right">
                    {canOperate && (
                      <DropdownMenu
                        items={[
                          { label: 'Edit', icon: <Pencil size={14} />, onSelect: () => setEditor({ list: l }) },
                          'separator',
                          { label: 'Delete', icon: <Trash2 size={14} />, danger: true, onSelect: () => void remove(l) },
                        ]}
                        trigger={(p) => (
                          <Button {...p} variant="ghost" size="sm" iconOnly aria-label={`Actions for ${l.name}`} icon={<MoreHorizontal size={16} />} />
                        )}
                      />
                    )}
                  </TD>
                </TR>
              ))}
            </TBody>
          </Table>
        )}
      </Card>
      {editor && <AccessListEditor list={editor.list} readOnly={!canOperate} onClose={() => setEditor(null)} />}
    </>
  );
}

interface UserRow {
  username: string;
  password: string;
  hasPassword: boolean;
}

function validateList(name: string, rules: IpRule[], users: UserRow[]): FieldErrors {
  const e: FieldErrors = {};
  if (!name.trim()) e.name = 'Enter a name.';
  rules.forEach((r, i) => {
    if (!isValidCidr(r.cidr)) e[`rules.${i}.cidr`] = `“${r.cidr || '(empty)'}” is not an IP address, CIDR range or “all”.`;
  });
  const seen = new Set<string>();
  users.forEach((u, i) => {
    const n = u.username.trim();
    if (!n) e[`users.${i}.username`] = 'Enter a user name.';
    else if (n.includes(':')) e[`users.${i}.username`] = 'User names cannot contain “:”.';
    else if (seen.has(n.toLowerCase())) e[`users.${i}.username`] = 'Duplicate user name.';
    seen.add(n.toLowerCase());
    if (!u.hasPassword && !u.password) e[`users.${i}.password`] = 'Set a password for the new user.';
  });
  if (rules.length === 0 && users.length === 0) e._list = 'Add at least one IP rule or user — an empty list does not restrict anything.';
  return e;
}

function AccessListEditor({ list, readOnly, onClose }: { list?: AccessList; readOnly: boolean; onClose: () => void }) {
  const uid = useId();
  const [name, setName] = useState(list?.name ?? '');
  const [satisfyAny, setSatisfyAny] = useState(list?.satisfyAny ?? false);
  const [passAuth, setPassAuth] = useState(list?.passAuthToUpstream ?? false);
  const [rules, setRules] = useState<IpRule[]>(list?.rules ?? []);
  const [users, setUsers] = useState<UserRow[]>(
    (list?.users ?? []).map((u) => ({ username: u.username, password: '', hasPassword: u.hasPassword ?? true })),
  );
  const [submitted, setSubmitted] = useState(false);
  const [serverError, setServerError] = useState<string | null>(null);
  const save = useSaveAccessList();
  const feedback = useFeedback();
  const errors = submitted ? validateList(name, rules, users) : {};

  const updateRule = (i: number, patch: Partial<IpRule>) => setRules(rules.map((r, j) => (j === i ? { ...r, ...patch } : r)));
  const moveRule = (i: number, dir: -1 | 1) => {
    const j = i + dir;
    if (j < 0 || j >= rules.length) return;
    const next = [...rules];
    [next[i], next[j]] = [next[j], next[i]];
    setRules(next);
  };
  const updateUser = (i: number, patch: Partial<UserRow>) => setUsers(users.map((u, j) => (j === i ? { ...u, ...patch } : u)));

  const hasAllow = rules.some((r) => r.action === 'allow');
  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (readOnly) return;
    setSubmitted(true);
    setServerError(null);
    if (Object.keys(validateList(name, rules, users)).length) return;
    const body: AccessListInput = {
      name: name.trim(),
      satisfyAny,
      passAuthToUpstream: passAuth,
      rules: rules.map((r) => ({ action: r.action, cidr: r.cidr.trim() })),
      users: users.map((u) => (u.password ? { username: u.username.trim(), password: u.password } : { username: u.username.trim() })),
    };
    save.mutate(
      { id: list?.id, list: body },
      {
        onSuccess: (res) => {
          feedback.applied(res.apply, list ? 'Saved and applied' : 'Access list created');
          onClose();
        },
        onError: (err) => {
          if (err instanceof ApiError && (err.status === 400 || err.status === 409)) {
            setServerError(err.errors ? Object.values(err.errors).flat().join(' ') : err.display);
            return;
          }
          feedback.failed(err);
        },
      },
    );
  };

  return (
    <Dialog
      open
      onClose={onClose}
      dismissible={!save.isPending}
      side="right"
      size="lg"
      title={readOnly ? 'Access list' : list ? 'Edit access list' : 'New access list'}
      description={list && list.usedBy > 0 ? `Used by ${list.usedBy} host${list.usedBy === 1 ? '' : 's'} — changes apply immediately.` : undefined}
      footer={
        readOnly ? (
          <Button onClick={onClose}>Close</Button>
        ) : (
          <>
            <Button onClick={onClose} disabled={save.isPending}>
              Cancel
            </Button>
            <Button type="submit" form={`${uid}-form`} variant="primary" loading={save.isPending}>
              {list ? 'Save' : 'Create'}
            </Button>
          </>
        )
      }
    >
      <form id={`${uid}-form`} onSubmit={submit} noValidate>
        <fieldset disabled={readOnly || save.isPending} className="flex min-w-0 flex-col gap-6">
          {serverError && <Callout tone="danger">{serverError}</Callout>}
          {errors._list && <Callout tone="warning">{errors._list}</Callout>}
          <Field label="Name" required error={errors.name}>
            <Input value={name} onChange={(e) => setName(e.target.value)} placeholder="Office networks" />
          </Field>

          <section className="flex flex-col gap-3">
            <div>
              <h3 className="text-sm font-semibold text-fg">IP rules</h3>
              <p className="mt-0.5 text-xs text-fg-subtle">
                Evaluated top to bottom; the first matching rule wins. If no rule matches, the client is{' '}
                <strong className="font-medium text-fg-muted">{hasAllow ? 'denied' : 'allowed'}</strong>
                {hasAllow ? ' (because at least one Allow rule exists).' : ' (no Allow rules).'} Use “all” to match every address.
              </p>
            </div>
            {rules.map((r, i) => {
              const err = errors[`rules.${i}.cidr`];
              return (
                <div key={i} className="flex flex-col gap-1">
                  <div className="grid grid-cols-[28px_104px_minmax(0,1fr)_auto] items-center gap-2">
                    <span className="mono text-center text-xs text-fg-subtle" aria-hidden>
                      {i + 1}
                    </span>
                    <Select
                      aria-label={`Rule ${i + 1} action`}
                      value={r.action}
                      onChange={(e) => updateRule(i, { action: e.target.value as IpRule['action'] })}
                    >
                      <option value="allow">Allow</option>
                      <option value="deny">Deny</option>
                    </Select>
                    <Input
                      mono
                      aria-label={`Rule ${i + 1} address`}
                      placeholder="10.0.0.0/8, 192.168.1.10, 2001:db8::/32 or all"
                      value={r.cidr}
                      invalid={!!err}
                      onChange={(e) => updateRule(i, { cidr: e.target.value })}
                    />
                    <div className="flex">
                      <Button variant="ghost" iconOnly size="md" aria-label={`Move rule ${i + 1} up`} icon={<ArrowUp size={14} />} disabled={i === 0} onClick={() => moveRule(i, -1)} />
                      <Button
                        variant="ghost"
                        iconOnly
                        aria-label={`Move rule ${i + 1} down`}
                        icon={<ArrowDown size={14} />}
                        disabled={i === rules.length - 1}
                        onClick={() => moveRule(i, 1)}
                      />
                      <Button variant="ghost" iconOnly aria-label={`Remove rule ${i + 1}`} icon={<Trash2 size={14} />} onClick={() => setRules(rules.filter((_, j) => j !== i))} />
                    </div>
                  </div>
                  {err && <p className="pl-9 text-xs text-danger">{err}</p>}
                </div>
              );
            })}
            <div className="flex gap-2">
              <Button size="sm" icon={<Plus size={14} />} onClick={() => setRules([...rules, { action: 'allow', cidr: '' }])}>
                Add rule
              </Button>
              {rules.length === 0 && (
                <Button
                  size="sm"
                  variant="ghost"
                  onClick={() =>
                    setRules([
                      { action: 'allow', cidr: '10.0.0.0/8' },
                      { action: 'allow', cidr: '172.16.0.0/12' },
                      { action: 'allow', cidr: '192.168.0.0/16' },
                      { action: 'deny', cidr: 'all' },
                    ])
                  }
                >
                  Private networks only
                </Button>
              )}
            </div>
          </section>

          <section className="flex flex-col gap-3 border-t border-border pt-5">
            <div>
              <h3 className="text-sm font-semibold text-fg">Basic authentication users</h3>
              <p className="mt-0.5 text-xs text-fg-subtle">Browsers prompt for these credentials. Passwords are stored as bcrypt hashes.</p>
            </div>
            {users.map((u, i) => {
              const uErr = errors[`users.${i}.username`];
              const pErr = errors[`users.${i}.password`];
              return (
                <div key={i} className="flex flex-col gap-1">
                  <div className="grid grid-cols-[minmax(0,1fr)_minmax(0,1fr)_32px] gap-2">
                    <Input
                      aria-label={`User ${i + 1} name`}
                      placeholder="user name"
                      autoComplete="off"
                      value={u.username}
                      invalid={!!uErr}
                      onChange={(e) => updateUser(i, { username: e.target.value })}
                    />
                    <Input
                      type="password"
                      aria-label={`User ${i + 1} password`}
                      autoComplete="new-password"
                      placeholder={u.hasPassword ? 'Unchanged' : 'Password'}
                      value={u.password}
                      invalid={!!pErr}
                      onChange={(e) => updateUser(i, { password: e.target.value })}
                    />
                    <Button variant="ghost" iconOnly aria-label={`Remove user ${i + 1}`} icon={<Trash2 size={14} />} onClick={() => setUsers(users.filter((_, j) => j !== i))} />
                  </div>
                  {(uErr || pErr) && <p className="text-xs text-danger">{uErr ?? pErr}</p>}
                </div>
              );
            })}
            <div>
              <Button size="sm" icon={<UserPlus size={14} />} onClick={() => setUsers([...users, { username: '', password: '', hasPassword: false }])}>
                Add user
              </Button>
            </div>
          </section>

          <section className="flex flex-col gap-4 border-t border-border pt-5">
            <h3 className="text-sm font-semibold text-fg">Behaviour</h3>
            <RadioCards
              aria-label="How rules and users combine"
              value={satisfyAny ? 'any' : 'all'}
              onChange={(v) => setSatisfyAny(v === 'any')}
              disabled={readOnly}
              options={[
                { value: 'all', label: 'Satisfy all', description: 'Client must match an Allow rule AND log in.' },
                { value: 'any', label: 'Satisfy any', description: 'Allowed IPs get in directly; others must log in.' },
              ]}
            />
            <SwitchField
              label="Pass credentials to upstream"
              description="Forward the Authorization header to the backend (for apps that also read it)."
              checked={passAuth}
              onChange={setPassAuth}
              disabled={readOnly}
            />
          </section>
        </fieldset>
      </form>
    </Dialog>
  );
}
