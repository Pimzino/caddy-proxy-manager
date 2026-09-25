import { useState, type FormEvent } from 'react';
import { Navigate, useNavigate } from 'react-router';
import { ApiError, errorMessage } from '@/api/client';
import { useSetup, useSetupStatus } from '@/api/hooks';
import { Button, Callout, Field, Input, LoadingBlock } from '@/components/ui';
import { isValidEmail, MIN_PASSWORD_LENGTH, serverFieldErrors } from '@/lib/validation';
import { AuthLayout } from './AuthLayout';

export default function SetupPage() {
  const status = useSetupStatus();
  const setup = useSetup();
  const navigate = useNavigate();
  const [form, setForm] = useState({ token: '', name: '', email: '', password: '', confirm: '' });
  const [submitted, setSubmitted] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [serverErrors, setServerErrors] = useState<Record<string, string>>({});

  if (status.isPending) return <LoadingBlock className="min-h-dvh" />;
  if (status.data && !status.data.needsSetup) return <Navigate to="/login" replace />;

  const tokenPath = status.data?.setupTokenPath || 'C:\\ProgramData\\CaddyProxyManager\\setup-token.txt';
  const errors = {
    token: !form.token.trim() ? 'Paste the setup token from the server.' : null,
    name: !form.name.trim() ? 'Enter your name.' : null,
    email: !isValidEmail(form.email) ? 'Enter a valid e-mail address.' : null,
    password: form.password.length < MIN_PASSWORD_LENGTH ? `Use at least ${MIN_PASSWORD_LENGTH} characters.` : null,
    confirm: form.confirm !== form.password ? 'The passwords do not match.' : null,
  };
  const show = (k: keyof typeof errors) => (submitted ? errors[k] : null) ?? serverErrors[k] ?? null;
  const set = (k: keyof typeof form) => (e: React.ChangeEvent<HTMLInputElement>) => setForm({ ...form, [k]: e.target.value });

  const submit = (e: FormEvent) => {
    e.preventDefault();
    setSubmitted(true);
    setError(null);
    setServerErrors({});
    if (Object.values(errors).some(Boolean)) return;
    setup.mutate(
      { token: form.token.trim(), name: form.name.trim(), email: form.email.trim(), password: form.password },
      {
        onSuccess: () => navigate('/', { replace: true }),
        onError: (err) => {
          if (err instanceof ApiError && err.status === 403) {
            navigate('/login', { replace: true });
            return;
          }
          if (err instanceof ApiError && err.errors) setServerErrors(serverFieldErrors(err.errors));
          setError(
            err instanceof ApiError && err.status === 400 && !err.errors
              ? 'The setup token is not valid. Copy it again from the file on the server.'
              : errorMessage(err),
          );
        },
      },
    );
  };

  return (
    <AuthLayout
      title="Welcome — create the administrator"
      description="This one-time setup creates the first admin account for this server."
    >
      <form onSubmit={submit} className="flex flex-col gap-4" noValidate>
        <Callout tone="info" title="Setup token">
          Open <span className="mono break-all text-fg">{tokenPath}</span> on the server (or check the service log) and paste
          the token below. The file is deleted once setup completes.
        </Callout>
        {error && <Callout tone="danger">{error}</Callout>}
        <Field label="Setup token" error={show('token')} required>
          <Input mono value={form.token} onChange={set('token')} autoComplete="off" spellCheck={false} autoFocus />
        </Field>
        <Field label="Name" error={show('name')} required>
          <Input value={form.name} onChange={set('name')} autoComplete="name" />
        </Field>
        <Field label="E-mail" error={show('email')} required>
          <Input type="email" value={form.email} onChange={set('email')} autoComplete="username" />
        </Field>
        <Field label="Password" hint={`At least ${MIN_PASSWORD_LENGTH} characters.`} error={show('password')} required>
          <Input type="password" value={form.password} onChange={set('password')} autoComplete="new-password" />
        </Field>
        <Field label="Confirm password" error={show('confirm')} required>
          <Input type="password" value={form.confirm} onChange={set('confirm')} autoComplete="new-password" />
        </Field>
        <Button type="submit" variant="primary" loading={setup.isPending} className="mt-1 w-full">
          Create administrator
        </Button>
      </form>
    </AuthLayout>
  );
}
