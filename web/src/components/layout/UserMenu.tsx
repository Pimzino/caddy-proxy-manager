import { useState } from 'react';
import { useNavigate } from 'react-router';
import { Check, ChevronDown, KeyRound, LogOut, Monitor, Moon, Sun } from 'lucide-react';
import { useLogout } from '@/api/hooks';
import { useAuth } from '@/auth';
import { Badge, DropdownMenu } from '@/components/ui';
import { useTheme, type ThemePreference } from '@/lib/theme';
import { ChangePasswordDialog } from './ChangePasswordDialog';

const themeItems: { value: ThemePreference; label: string; icon: typeof Sun }[] = [
  { value: 'system', label: 'System theme', icon: Monitor },
  { value: 'light', label: 'Light theme', icon: Sun },
  { value: 'dark', label: 'Dark theme', icon: Moon },
];

export function UserMenu() {
  const { user } = useAuth();
  const { preference, setPreference, resolved } = useTheme();
  const logout = useLogout();
  const navigate = useNavigate();
  const [pwOpen, setPwOpen] = useState(false);
  if (!user) return null;
  const initials =
    user.name
      .split(/\s+/)
      .filter(Boolean)
      .slice(0, 2)
      .map((p) => p[0]?.toUpperCase())
      .join('') || user.email[0]?.toUpperCase();

  return (
    <>
      <DropdownMenu
        width={240}
        header={
          <div className="min-w-0">
            <p className="truncate text-sm font-medium text-fg">{user.name}</p>
            <p className="truncate text-xs text-fg-subtle">{user.email}</p>
            <Badge tone={user.role === 'admin' ? 'accent' : 'neutral'} className="mt-1.5 capitalize">
              {user.role}
            </Badge>
          </div>
        }
        items={[
          ...themeItems.map((t) => ({
            label: t.label,
            icon: preference === t.value ? <Check size={14} className="text-accent-text" /> : <t.icon size={14} />,
            onSelect: () => setPreference(t.value),
          })),
          'separator' as const,
          { label: 'Change password', icon: <KeyRound size={14} />, onSelect: () => setPwOpen(true) },
          {
            label: 'Sign out',
            icon: <LogOut size={14} />,
            onSelect: () =>
              logout.mutate(undefined, {
                onSettled: () => navigate('/login', { replace: true }),
              }),
          },
        ]}
        trigger={(props) => (
          <button
            type="button"
            {...props}
            aria-label={`User menu for ${user.name} (${resolved} theme)`}
            className="inline-flex h-8 items-center gap-2 rounded-md px-1.5 text-sm text-fg hover:bg-surface-2 focus-visible:outline-2 focus-visible:outline-ring"
          >
            <span className="flex h-6 w-6 items-center justify-center rounded-full bg-accent-soft text-[11px] font-semibold text-accent-text">
              {initials}
            </span>
            <span className="hidden max-w-[10rem] truncate lg:inline">{user.name}</span>
            <ChevronDown size={14} className="text-fg-subtle" aria-hidden />
          </button>
        )}
      />
      <ChangePasswordDialog open={pwOpen} onClose={() => setPwOpen(false)} />
    </>
  );
}
