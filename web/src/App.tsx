import { lazy, Suspense, useEffect, type ReactNode } from 'react';
import { createBrowserRouter, Navigate, Outlet, RouterProvider, useNavigate } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ApiError, setUnauthorizedHandler } from '@/api/client';
import { qk } from '@/api/hooks';
import { RequireAuth, RequireRole } from '@/auth';
import { FeedbackProvider } from '@/components/feedback';
import { AppShell } from '@/components/layout/AppShell';
import { ConfirmProvider, LoadingBlock, ToastProvider } from '@/components/ui';
import { ThemeProvider } from '@/lib/theme';

const LoginPage = lazy(() => import('@/pages/LoginPage'));
const SetupPage = lazy(() => import('@/pages/SetupPage'));
const DashboardPage = lazy(() => import('@/pages/DashboardPage'));
const ServersPage = lazy(() => import('@/pages/servers/ServersPage'));
const ServerDetailPage = lazy(() => import('@/pages/servers/ServerDetailPage'));
const TrafficPage = lazy(() => import('@/pages/servers/TrafficPage'));
const HostsPage = lazy(() => import('@/pages/hosts/HostsPage'));
const StreamsPage = lazy(() => import('@/pages/StreamsPage'));
const CertificatesPage = lazy(() => import('@/pages/certificates/CertificatesPage'));
const AccessListsPage = lazy(() => import('@/pages/AccessListsPage'));
const ServicePage = lazy(() => import('@/pages/caddy/ServicePage'));
const PluginsPage = lazy(() => import('@/pages/caddy/PluginsPage'));
const ConfigPage = lazy(() => import('@/pages/caddy/ConfigPage'));
const ReadinessPage = lazy(() => import('@/pages/ReadinessPage'));
const LogsPage = lazy(() => import('@/pages/LogsPage'));
const EventsPage = lazy(() => import('@/pages/EventsPage'));
const NotificationsPage = lazy(() => import('@/pages/NotificationsPage'));
const SettingsPage = lazy(() => import('@/pages/settings/SettingsPage'));
const UsersPage = lazy(() => import('@/pages/UsersPage'));
const AuditPage = lazy(() => import('@/pages/AuditPage'));
const NotFoundPage = lazy(() => import('@/pages/NotFoundPage'));

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 15_000,
      refetchOnWindowFocus: true,
      retry: (count, err) => {
        if (err instanceof ApiError && err.status < 500) return false;
        return count < 2;
      },
    },
    mutations: { retry: false },
  },
});

/** Lets the API client send the user to /login or /setup on 401 without a full reload. */
function UnauthorizedBridge({ children }: { children: ReactNode }) {
  const navigate = useNavigate();
  useEffect(() => {
    setUnauthorizedHandler((target) => {
      const here = window.location.pathname + window.location.search;
      queryClient.setQueryData(qk.me, null);
      navigate(target === '/login' && here !== '/' ? `/login?next=${encodeURIComponent(here)}` : target, { replace: true });
    });
    return () => setUnauthorizedHandler(null);
  }, [navigate]);
  return <>{children}</>;
}

function Root() {
  return (
    <UnauthorizedBridge>
      <Suspense fallback={<LoadingBlock className="min-h-screen" />}>
        <Outlet />
      </Suspense>
    </UnauthorizedBridge>
  );
}

const admin = (el: ReactNode) => <RequireRole role="admin">{el}</RequireRole>;

const router = createBrowserRouter([
  {
    element: <Root />,
    children: [
      { path: '/login', element: <LoginPage /> },
      { path: '/setup', element: <SetupPage /> },
      {
        element: (
          <RequireAuth>
            <AppShell />
          </RequireAuth>
        ),
        children: [
          { index: true, element: <DashboardPage /> },
          { path: 'servers', element: <ServersPage /> },
          { path: 'servers/:id', element: <ServerDetailPage /> },
          { path: 'traffic', element: <TrafficPage /> },
          { path: 'hosts', element: <Navigate to="/hosts/proxy" replace /> },
          { path: 'hosts/:kind', element: <HostsPage /> },
          { path: 'streams', element: <StreamsPage /> },
          { path: 'certificates', element: <CertificatesPage /> },
          { path: 'access-lists', element: <AccessListsPage /> },
          { path: 'caddy', element: <Navigate to="/caddy/service" replace /> },
          { path: 'caddy/service', element: <ServicePage /> },
          { path: 'caddy/plugins', element: <PluginsPage /> },
          { path: 'caddy/config', element: <ConfigPage /> },
          { path: 'readiness', element: <ReadinessPage /> },
          { path: 'logs', element: <LogsPage /> },
          { path: 'events', element: <EventsPage /> },
          { path: 'notifications', element: admin(<NotificationsPage />) },
          { path: 'settings', element: <SettingsPage /> },
          { path: 'users', element: admin(<UsersPage />) },
          { path: 'audit', element: admin(<AuditPage />) },
          { path: '*', element: <NotFoundPage /> },
        ],
      },
    ],
  },
]);

export function App() {
  return (
    <ThemeProvider>
      <QueryClientProvider client={queryClient}>
        <ToastProvider>
          <ConfirmProvider>
            <FeedbackProvider>
              <RouterProvider router={router} />
            </FeedbackProvider>
          </ConfirmProvider>
        </ToastProvider>
      </QueryClientProvider>
    </ThemeProvider>
  );
}
