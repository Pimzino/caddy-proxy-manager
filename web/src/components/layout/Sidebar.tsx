import { NavLink } from 'react-router';
import { PanelLeftClose, PanelLeftOpen, X } from 'lucide-react';
import { useAuth } from '@/auth';
import { cn } from '@/lib/cn';
import { navGroups } from '@/nav';
import { Logo } from './Logo';

export function Sidebar({
  collapsed,
  onToggleCollapsed,
  mobile,
  onNavigate,
  version,
}: {
  collapsed: boolean;
  onToggleCollapsed?: () => void;
  mobile?: boolean;
  onNavigate?: () => void;
  version?: string;
}) {
  const { hasRole } = useAuth();
  const compact = collapsed && !mobile;
  return (
    <div className={cn('flex h-full flex-col bg-sidebar', !mobile && 'border-r border-border')}>
      <div className={cn('flex h-12 shrink-0 items-center gap-2 border-b border-border', compact ? 'justify-center px-2' : 'px-4')}>
        <Logo className="h-6 w-6 shrink-0" />
        {!compact && (
          <div className="min-w-0 flex-1 leading-tight">
            <p className="truncate text-sm font-semibold text-fg">Caddy Proxy Manager</p>
          </div>
        )}
        {mobile && (
          <button
            type="button"
            onClick={onNavigate}
            className="flex h-7 w-7 items-center justify-center rounded-md text-fg-subtle hover:bg-surface-2 hover:text-fg"
            aria-label="Close navigation"
          >
            <X size={16} />
          </button>
        )}
      </div>
      <nav className="flex-1 overflow-y-auto px-2 py-3" aria-label="Main">
        {navGroups.map((group, gi) => {
          const items = group.items.filter((i) => !i.role || hasRole(i.role));
          if (items.length === 0) return null;
          return (
            <div key={group.label} className={cn(gi > 0 && (compact ? 'mt-2 border-t border-border pt-2' : 'mt-4'))}>
              {!compact && (
                <p className="mb-1 px-2 text-[11px] font-semibold tracking-wider text-fg-subtle uppercase">{group.label}</p>
              )}
              <ul className="flex flex-col gap-px">
                {items.map((item) => {
                  const Icon = item.icon;
                  return (
                    <li key={item.to}>
                      <NavLink
                        to={item.to}
                        end={item.end}
                        onClick={onNavigate}
                        title={compact ? item.label : undefined}
                        aria-label={compact ? item.label : undefined}
                        className={({ isActive }) =>
                          cn(
                            'flex h-8 items-center gap-2.5 rounded-md text-sm transition-colors',
                            'focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-ring',
                            compact ? 'justify-center px-0' : 'px-2',
                            isActive
                              ? 'bg-accent-soft font-medium text-accent-text'
                              : 'text-fg-muted hover:bg-surface-2 hover:text-fg',
                          )
                        }
                      >
                        <Icon size={16} className="shrink-0" aria-hidden />
                        {!compact && <span className="truncate">{item.label}</span>}
                      </NavLink>
                    </li>
                  );
                })}
              </ul>
            </div>
          );
        })}
      </nav>
      {!mobile && (
        <div className={cn('flex shrink-0 items-center border-t border-border py-2', compact ? 'justify-center px-2' : 'justify-between px-3')}>
          {!compact && version && <span className="mono truncate text-xs text-fg-subtle">v{version}</span>}
          <button
            type="button"
            onClick={onToggleCollapsed}
            className="flex h-7 w-7 items-center justify-center rounded-md text-fg-subtle hover:bg-surface-2 hover:text-fg focus-visible:outline-2 focus-visible:outline-ring"
            aria-label={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
            title={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
          >
            {collapsed ? <PanelLeftOpen size={16} /> : <PanelLeftClose size={16} />}
          </button>
        </div>
      )}
    </div>
  );
}
