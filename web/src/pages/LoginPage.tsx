import { useState, type FormEvent } from 'react';
import { Navigate, useNavigate, useSearchParams } from 'react-router';
import { ApiError, errorMessage } from '@/api/client';
import { useLogin, useMe, useSetupStatus } from '@/api/hooks';
import { Button, Callout, Field, Input, LoadingBlock } from '@/components/ui';
import { AuthLayout } from './AuthLayout';

function safeNext(next: string | null): string {
  // Only allow same-origin absolute paths (no protocol-relative or external redirects).
  if (next && next.startsWith('/') && !next.startsWith('//') && !next.startsWith('/login') && !next.startsWith('/setup'))
    return next;
  return '/';
}

export default function LoginPage() {
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const setup = useSetupStatus();
  const me = useMe();
  const login = useLogin();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const next = safeNext(params.get('next'));

  if (setup.data?.needsSetup) return <Navigate to="/setup" replace />;
  if (me.data) return <Navigate to={next} replace />;
  if (setup.isPending || me.isPending) return <LoadingBlock className="min-h-dvh" />;

  const submit = (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    if (!email.trim() || !password) {
      setError('Enter your e-mail address and password.');
      return;
    }
    login.mutate(
      { email: email.trim(), password },
      {
        onSuccess: () => navigate(next, { replace: true }),
        onError: (err) => {
          if (err instanceof ApiError && err.status === 401) setError('The e-mail address or password is incorrect.');
          else if (err instanceof ApiError && err.status === 429)
            setError('Too many sign-in attempts. Wait a minute before trying again.');
          else setError(errorMessage(err));
        },
      },
    );
  };

  return (
    <AuthLayout
      title="Sign in"
      description="Use your Caddy Proxy Manager account."
      footer={
        <>
          Forgot the admin password? On the server, in an elevated prompt run{' '}
          <span className="mono whitespace-nowrap">CaddyManager.exe reset-password --email &lt;e-mail&gt;</span>
        </>
      }
    >
      <form onSubmit={submit} className="flex flex-col gap-4" noValidate>
        {error && <Callout tone="danger">{error}</Callout>}
        <Field label="E-mail">
          <Input
            type="email"
            autoComplete="username"
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            autoFocus
            required
          />
        </Field>
        <Field label="Password">
          <Input
            type="password"
            autoComplete="current-password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            required
          />
        </Field>
        <Button type="submit" variant="primary" loading={login.isPending} className="mt-1 w-full">
          Sign in
        </Button>
      </form>
    </AuthLayout>
  );
}
