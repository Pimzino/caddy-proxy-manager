import { useState, type FormEvent } from 'react';
import { useChangePassword } from '@/api/hooks';
import { ApiError } from '@/api/client';
import { Button, Callout, Dialog, Field, Input, useToast } from '@/components/ui';
import { MIN_PASSWORD_LENGTH } from '@/lib/validation';

export function ChangePasswordDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  return (
    <Dialog open={open} onClose={onClose} title="Change password" description="Your other sessions (other browsers or devices) are signed out; this one stays signed in." size="sm">
      <ChangePasswordForm onDone={onClose} />
    </Dialog>
  );
}

function ChangePasswordForm({ onDone }: { onDone: () => void }) {
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');
  const [confirm, setConfirm] = useState('');
  const [submitted, setSubmitted] = useState(false);
  const [serverError, setServerError] = useState<string | null>(null);
  const change = useChangePassword();
  const toast = useToast();

  const errors = {
    current: !current ? 'Enter your current password.' : null,
    next:
      next.length < MIN_PASSWORD_LENGTH
        ? `Use at least ${MIN_PASSWORD_LENGTH} characters.`
        : next === current
          ? 'The new password must be different from the current one.'
          : null,
    confirm: confirm !== next ? 'The passwords do not match.' : null,
  };
  const valid = !errors.current && !errors.next && !errors.confirm;

  const submit = (e: FormEvent) => {
    e.preventDefault();
    setSubmitted(true);
    setServerError(null);
    if (!valid) return;
    change.mutate(
      { currentPassword: current, newPassword: next },
      {
        onSuccess: () => {
          toast.success('Password changed');
          onDone();
        },
        onError: (err) => {
          const msg =
            err instanceof ApiError
              ? err.errors
                ? Object.values(err.errors).flat().join(' ')
                : err.display
              : 'The password could not be changed.';
          setServerError(msg);
        },
      },
    );
  };

  return (
    <form onSubmit={submit} className="flex flex-col gap-4" noValidate>
      {serverError && <Callout tone="danger">{serverError}</Callout>}
      <Field label="Current password" error={submitted ? errors.current : null} required>
        <Input type="password" autoComplete="current-password" value={current} onChange={(e) => setCurrent(e.target.value)} />
      </Field>
      <Field label="New password" hint={`At least ${MIN_PASSWORD_LENGTH} characters.`} error={submitted ? errors.next : null} required>
        <Input type="password" autoComplete="new-password" value={next} onChange={(e) => setNext(e.target.value)} />
      </Field>
      <Field label="Confirm new password" error={submitted ? errors.confirm : null} required>
        <Input type="password" autoComplete="new-password" value={confirm} onChange={(e) => setConfirm(e.target.value)} />
      </Field>
      <div className="flex justify-end gap-2 pt-1">
        <Button onClick={onDone}>Cancel</Button>
        <Button type="submit" variant="primary" loading={change.isPending}>
          Change password
        </Button>
      </div>
    </form>
  );
}
