import type { ReactNode } from 'react';
import { Navigate, useLocation } from 'react-router';
import { ShieldAlert } from 'lucide-react';
import { useMe } from '@/api/hooks';
import type { UserDto, UserRole } from '@/api/types';
import { EmptyState, LoadingBlock } from '@/components/ui';
import { errorMessage } from '@/api/client';

const rank: Record<UserRole, number> = { viewer: 0, operator: 1, admin: 2 };

export interface Auth {
  user: UserDto | null;
  /** Operator or admin: may change hosts, certificates, access lists and control Caddy. */
  canOperate: boolean;
  isAdmin: boolean;
  hasRole: (role: UserRole) => boolean;
}

export function useAuth(): Auth {
  const { data } = useMe();
  const user = data ?? null;
  const hasRole = (role: UserRole) => !!user && rank[user.role] >= rank[role];
  return { user, canOperate: hasRole('operator'), isAdmin: hasRole('admin'), hasRole };
}

/** Gate for the signed-in area: waits for /api/auth/me and redirects anonymous users to /login. */
export function RequireAuth({ children }: { children: ReactNode }) {
  const me = useMe();
  const location = useLocation();
  if (me.isPending) return <LoadingBlock className="min-h-screen" label="Loading Caddy Proxy Manager…" />;
  if (me.isError)
    return (
      <div className="flex min-h-screen items-center justify-center p-6">
        <EmptyState
          icon={<ShieldAlert size={18} />}
          title="Cannot reach the management service"
          description={errorMessage(me.error)}
          action={
            <button className="text-sm text-accent-text underline" onClick={() => void me.refetch()}>
              Try again
            </button>
          }
        />
      </div>
    );
  if (!me.data) {
    const next = location.pathname + location.search;
    return <Navigate to={next && next !== '/' ? `/login?next=${encodeURIComponent(next)}` : '/login'} replace />;
  }
  return <>{children}</>;
}

/** Shows an access-denied state instead of the page when the role is insufficient. */
export function RequireRole({ role, children }: { role: UserRole; children: ReactNode }) {
  const { hasRole } = useAuth();
  if (!hasRole(role))
    return (
      <EmptyState
        icon={<ShieldAlert size={18} />}
        title="Access denied"
        description={`This page requires the ${role} role. Ask an administrator if you need access.`}
      />
    );
  return <>{children}</>;
}
