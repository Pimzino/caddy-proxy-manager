import { Suspense, useEffect, useState } from 'react';
import { Link, Outlet, useLocation } from 'react-router';
import { ChevronRight, Menu } from 'lucide-react';
import { useSystemInfo } from '@/api/hooks';
import { LoadingBlock } from '@/components/ui';
import { cn } from '@/lib/cn';
import { readStorage, writeStorage } from '@/lib/storage';
import { findNav } from '@/nav';
import { CaddyStatusPill } from './CaddyStatusPill';
import { ErrorBoundary } from './ErrorBoundary';
import { Sidebar } from './Sidebar';
import { UserMenu } from './UserMenu';

const SIDEBAR_KEY = 'cpm.sidebar';

export function AppShell() {
  const [collapsed, setCollapsed] = useState(() => readStorage(SIDEBAR_KEY) === 'collapsed');
  const [drawerOpen, setDrawerOpen] = useState(false);
  const location = useLocation();
  const nav = findNav(location.pathname);
  const system = useSystemInfo();
  const title = nav?.item.label ?? 'Caddy Proxy Manager';

  useEffect(() => {
    const host = system.data?.machineName;
    document.title = [nav?.item.label, host, 'Caddy Proxy Manager'].filter(Boolean).join(' · ');
  }, [nav?.item.label, system.data?.machineName]);

  useEffect(() => {
    if (!drawerOpen) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setDrawerOpen(false);
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [drawerOpen]);

  const toggleCollapsed = () => {
    setCollapsed((c) => {
      writeStorage(SIDEBAR_KEY, c ? null : 'collapsed');
      return !c;
    });
  };

  return (
    <div className="flex h-dvh overflow-hidden">
      <a
        href="#main"
        className="sr-only z-50 rounded-md bg-surface px-3 py-2 text-sm focus:not-sr-only focus:absolute focus:top-2 focus:left-2"
      >
        Skip to content
      </a>
      <aside
        className={cn('hidden shrink-0 transition-[width] duration-150 lg:block', collapsed ? 'w-14' : 'w-60')}
        aria-label="Sidebar"
      >
        <Sidebar collapsed={collapsed} onToggleCollapsed={toggleCollapsed} version={system.data?.version} />
      </aside>

      {drawerOpen && (
        <div className="fixed inset-0 z-40 lg:hidden" role="dialog" aria-modal="true" aria-label="Navigation">
          <div className="absolute inset-0 animate-fade-in bg-overlay" onClick={() => setDrawerOpen(false)} aria-hidden />
          <div className="relative h-full w-72 max-w-[85vw] animate-slide-left border-r border-border shadow-pop">
            <Sidebar collapsed={false} mobile onNavigate={() => setDrawerOpen(false)} />
          </div>
        </div>
      )}

      <div className="flex min-w-0 flex-1 flex-col">
        <header className="flex h-12 shrink-0 items-center gap-3 border-b border-border bg-surface px-3 sm:px-4">
          <button
            type="button"
            onClick={() => setDrawerOpen(true)}
            className="flex h-8 w-8 items-center justify-center rounded-md text-fg-muted hover:bg-surface-2 hover:text-fg focus-visible:outline-2 focus-visible:outline-ring lg:hidden"
            aria-label="Open navigation"
            aria-expanded={drawerOpen}
          >
            <Menu size={18} />
          </button>
          <nav aria-label="Breadcrumb" className="flex min-w-0 flex-1 items-center gap-1.5 text-sm">
            {nav && nav.group !== 'Overview' && (
              <>
                <span className="hidden text-fg-subtle sm:inline">{nav.group}</span>
                <ChevronRight size={14} className="hidden shrink-0 text-fg-subtle sm:inline" aria-hidden />
              </>
            )}
            <Link to={nav?.item.to ?? '/'} className="truncate font-medium text-fg hover:underline" aria-current="page">
              {title}
            </Link>
          </nav>
          <CaddyStatusPill />
          <UserMenu />
        </header>
        <main id="main" className="min-h-0 flex-1 overflow-y-auto" tabIndex={-1}>
          <div className="mx-auto w-full max-w-[1600px] px-4 py-5 sm:px-6">
            <ErrorBoundary resetKey={location.pathname}>
              <Suspense fallback={<LoadingBlock />}>
                <Outlet />
              </Suspense>
            </ErrorBoundary>
          </div>
        </main>
      </div>
    </div>
  );
}
