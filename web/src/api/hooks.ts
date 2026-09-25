import { keepPreviousData, useMutation, useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query';
import { api, ApiError } from './client';
import type {
  AccessList,
  AccessListInput,
  AccessLogResult,
  AdaptResult,
  ApplyResult,
  AuditEntry,
  BinaryOverview,
  BinarySettings,
  CaddySettings,
  CaddySettingsInput,
  CaddyStatus,
  Certificate,
  CertificateInfo,
  ConfigRevision,
  ConfigRevisionSummary,
  Dashboard,
  DeleteResult,
  EventEntry,
  EventSeverity,
  FixResult,
  HostKind,
  JobInfo,
  JsonDoc,
  LogResult,
  MutationResult,
  NotificationSettings,
  NotificationSettingsInput,
  NotificationTestResult,
  Page,
  PluginPackage,
  ReadinessReport,
  RestoreResult,
  SetupStatus,
  SiteHost,
  SiteHostFields,
  StreamHost,
  StreamHostFields,
  StreamSupport,
  SystemInfo,
  UiSettings,
  UiSettingsInput,
  UiSettingsSaveResult,
  UpstreamHealth,
  UserCreateInput,
  UserDto,
  UserUpdateInput,
} from './types';

export const qk = {
  me: ['me'] as const,
  setupStatus: ['setup-status'] as const,
  hosts: (kind?: HostKind) => (kind ? (['hosts', kind] as const) : (['hosts'] as const)),
  streams: ['streams'] as const,
  streamSupport: ['streams', 'support'] as const,
  accessLists: ['access-lists'] as const,
  certificates: ['certificates'] as const,
  settings: (name: 'caddy' | 'binary' | 'notifications' | 'ui') => ['settings', name] as const,
  config: ['config'] as const,
  caddy: ['caddy'] as const,
  caddyStatus: ['caddy', 'status'] as const,
  binary: ['caddy', 'binary'] as const,
  upstreams: ['caddy', 'upstreams'] as const,
  job: (id: string) => ['jobs', id] as const,
  readiness: ['readiness'] as const,
  systemInfo: ['system', 'info'] as const,
  dashboard: ['dashboard'] as const,
  users: ['users'] as const,
  audit: ['audit'] as const,
  events: ['events'] as const,
  logs: ['logs'] as const,
};

/** Everything that can change when the generated Caddy config changes. */
export function invalidateConfigState(qc: QueryClient) {
  void qc.invalidateQueries({ queryKey: qk.hosts() });
  void qc.invalidateQueries({ queryKey: qk.streams });
  void qc.invalidateQueries({ queryKey: qk.accessLists });
  void qc.invalidateQueries({ queryKey: qk.certificates });
  void qc.invalidateQueries({ queryKey: qk.config });
  void qc.invalidateQueries({ queryKey: qk.dashboard });
  void qc.invalidateQueries({ queryKey: qk.caddyStatus });
  void qc.invalidateQueries({ queryKey: qk.upstreams });
}

// ---------------------------------------------------------------- Setup & auth

export function useSetupStatus() {
  return useQuery({ queryKey: qk.setupStatus, queryFn: () => api.get<SetupStatus>('/api/setup/status'), staleTime: 0 });
}

/** The signed-in user; resolves to null when anonymous (401). */
export function useMe() {
  return useQuery({
    queryKey: qk.me,
    queryFn: async () => {
      try {
        return await api.get<UserDto>('/api/auth/me');
      } catch (err) {
        if (err instanceof ApiError && err.status === 401) return null;
        throw err;
      }
    },
    staleTime: 5 * 60_000,
    retry: false,
  });
}

export function useLogin() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: { email: string; password: string }) => api.post<UserDto>('/api/auth/login', body),
    onSuccess: (user) => {
      qc.clear();
      qc.setQueryData(qk.me, user);
    },
  });
}

export function useSetup() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: { token: string; email: string; name: string; password: string }) =>
      api.post<UserDto>('/api/setup', body),
    onSuccess: (user) => {
      qc.clear();
      qc.setQueryData(qk.me, user);
      qc.setQueryData(qk.setupStatus, { needsSetup: false, setupTokenPath: '' });
    },
  });
}

export function useLogout() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => api.post<null>('/api/auth/logout'),
    onSettled: () => {
      qc.clear();
      qc.setQueryData(qk.me, null);
    },
  });
}

export function useChangePassword() {
  return useMutation({
    mutationFn: (body: { currentPassword: string; newPassword: string }) =>
      api.post<null>('/api/auth/change-password', body),
  });
}

// ---------------------------------------------------------------- Dashboard & system

export function useDashboard() {
  return useQuery({
    queryKey: qk.dashboard,
    queryFn: () => api.get<Dashboard>('/api/dashboard'),
    refetchInterval: 15_000,
  });
}

export function useSystemInfo() {
  return useQuery({ queryKey: qk.systemInfo, queryFn: () => api.get<SystemInfo>('/api/system/info'), staleTime: 60_000 });
}

export function useRestartManager() {
  return useMutation({ mutationFn: () => api.post<null>('/api/system/restart') });
}

// ---------------------------------------------------------------- Hosts

export function useHosts(kind?: HostKind) {
  return useQuery({
    queryKey: qk.hosts(kind),
    queryFn: () => api.get<SiteHost[]>('/api/hosts', { kind }),
  });
}

export function useSaveHost() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, host }: { id?: string; host: SiteHostFields }) =>
      id ? api.put<MutationResult<SiteHost>>(`/api/hosts/${id}`, host) : api.post<MutationResult<SiteHost>>('/api/hosts', host),
    onSettled: () => invalidateConfigState(qc),
  });
}

export function useToggleHost() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, enabled }: { id: string; enabled: boolean }) =>
      api.post<MutationResult<SiteHost>>(`/api/hosts/${id}/${enabled ? 'enable' : 'disable'}`),
    onSettled: () => invalidateConfigState(qc),
  });
}

export function useDeleteHost() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api.del<DeleteResult>(`/api/hosts/${id}`),
    onSettled: () => invalidateConfigState(qc),
  });
}

// ---------------------------------------------------------------- Streams

export function useStreams() {
  return useQuery({ queryKey: qk.streams, queryFn: () => api.get<StreamHost[]>('/api/streams') });
}

export function useStreamSupport() {
  return useQuery({ queryKey: qk.streamSupport, queryFn: () => api.get<StreamSupport>('/api/streams/support') });
}

export function useSaveStream() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, stream }: { id?: string; stream: StreamHostFields }) =>
      id
        ? api.put<MutationResult<StreamHost>>(`/api/streams/${id}`, stream)
        : api.post<MutationResult<StreamHost>>('/api/streams', stream),
    onSettled: () => invalidateConfigState(qc),
  });
}

export function useDeleteStream() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api.del<DeleteResult>(`/api/streams/${id}`),
    onSettled: () => invalidateConfigState(qc),
  });
}

// ---------------------------------------------------------------- Access lists

export function useAccessLists() {
  return useQuery({ queryKey: qk.accessLists, queryFn: () => api.get<AccessList[]>('/api/access-lists') });
}

export function useSaveAccessList() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, list }: { id?: string; list: AccessListInput }) =>
      id
        ? api.put<MutationResult<AccessList>>(`/api/access-lists/${id}`, list)
        : api.post<MutationResult<AccessList>>('/api/access-lists', list),
    onSettled: () => invalidateConfigState(qc),
  });
}

export function useDeleteAccessList() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api.del<DeleteResult>(`/api/access-lists/${id}`),
    onSettled: () => invalidateConfigState(qc),
  });
}

// ---------------------------------------------------------------- Certificates

export function useCertificates() {
  return useQuery({ queryKey: qk.certificates, queryFn: () => api.get<CertificateInfo[]>('/api/certificates') });
}

export type CertificateCreate =
  | { method: 'upload'; form: FormData }
  | { method: 'pem'; body: { name: string; certPem: string; keyPem: string } }
  | { method: 'path'; body: { name: string; certPath: string; keyPath: string } };

export function useCreateCertificate() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: CertificateCreate) => {
      switch (input.method) {
        case 'upload':
          return api.upload<MutationResult<Certificate>>('/api/certificates/upload', input.form);
        case 'pem':
          return api.post<MutationResult<Certificate>>('/api/certificates/pem', input.body);
        case 'path':
          return api.post<MutationResult<Certificate>>('/api/certificates/path', input.body);
      }
    },
    onSettled: () => invalidateConfigState(qc),
  });
}

export function useReplaceCertificate() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, form }: { id: string; form: FormData }) =>
      api.upload<MutationResult<Certificate>>(`/api/certificates/${id}/replace`, form),
    onSettled: () => invalidateConfigState(qc),
  });
}

export function useUpdateCertificate() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, name, notes }: { id: string; name: string; notes: string }) =>
      api.put<MutationResult<Certificate> | Certificate>(`/api/certificates/${id}`, { name, notes }),
    onSettled: () => invalidateConfigState(qc),
  });
}

export function useDeleteCertificate() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api.del<DeleteResult>(`/api/certificates/${id}`),
    onSettled: () => invalidateConfigState(qc),
  });
}

// ---------------------------------------------------------------- Caddy settings & config

export function useCaddySettings() {
  return useQuery({ queryKey: qk.settings('caddy'), queryFn: () => api.get<CaddySettings>('/api/settings/caddy') });
}

export function useSaveCaddySettings() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (settings: CaddySettingsInput) =>
      api.put<MutationResult<CaddySettings>>('/api/settings/caddy', settings),
    onSuccess: (res) => qc.setQueryData(qk.settings('caddy'), res.item),
    onSettled: () => {
      void qc.invalidateQueries({ queryKey: qk.settings('caddy') });
      invalidateConfigState(qc);
    },
  });
}

export function useConfigPreview(enabled = true) {
  return useQuery({
    queryKey: [...qk.config, 'preview'],
    queryFn: () => api.get<JsonDoc>('/api/config/preview'),
    enabled,
  });
}

export function useRunningConfig(enabled = true) {
  return useQuery({
    queryKey: [...qk.config, 'running'],
    queryFn: () => api.get<JsonDoc>('/api/config/running'),
    enabled,
    retry: false,
  });
}

export function useApplyConfig() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => api.post<ApplyResult>('/api/config/apply'),
    onSettled: () => invalidateConfigState(qc),
  });
}

export function useRevisions(take = 50) {
  return useQuery({
    queryKey: [...qk.config, 'revisions', take],
    queryFn: () => api.get<ConfigRevisionSummary[]>('/api/config/revisions', { take }),
  });
}

export function useRevision(id: string | null) {
  return useQuery({
    queryKey: [...qk.config, 'revision', id],
    queryFn: () => api.get<ConfigRevision>(`/api/config/revisions/${id}`),
    enabled: !!id,
    staleTime: Infinity,
  });
}

export function useAdaptCaddyfile() {
  return useMutation({
    mutationFn: (caddyfile: string) => api.post<AdaptResult>('/api/config/caddyfile/adapt', { caddyfile }),
  });
}

export function useUpstreams() {
  return useQuery({
    queryKey: qk.upstreams,
    queryFn: () => api.get<UpstreamHealth[]>('/api/caddy/upstreams'),
    refetchInterval: 30_000,
    retry: false,
  });
}

// ---------------------------------------------------------------- Caddy service & binary

export function useCaddyStatus() {
  return useQuery({
    queryKey: qk.caddyStatus,
    queryFn: () => api.get<CaddyStatus>('/api/caddy/status'),
    refetchInterval: 10_000,
    refetchIntervalInBackground: false,
  });
}

export function useCaddyAction() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (action: 'start' | 'stop' | 'restart' | 'service/install' | 'service/uninstall') =>
      api.post<CaddyStatus>(`/api/caddy/${action}`),
    onSuccess: (status) => qc.setQueryData(qk.caddyStatus, status),
    onSettled: () => {
      void qc.invalidateQueries({ queryKey: qk.caddy });
      void qc.invalidateQueries({ queryKey: qk.dashboard });
    },
  });
}

export function useBinaryOverview() {
  return useQuery({
    queryKey: qk.binary,
    queryFn: () => api.get<BinaryOverview>('/api/caddy/binary'),
    staleTime: 60_000,
    refetchInterval: 5 * 60_000,
  });
}

export function useCheckForUpdates() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => api.post<BinaryOverview>('/api/caddy/binary/check'),
    onSuccess: (o) => qc.setQueryData(qk.binary, o),
    onSettled: () => void qc.invalidateQueries({ queryKey: qk.dashboard }),
  });
}

export function useInstallBinary() {
  return useMutation({
    mutationFn: (version?: string) => api.post<JobInfo>('/api/caddy/binary/install', version ? { version } : {}),
  });
}

export function usePluginCatalog(q: string) {
  return useQuery({
    queryKey: [...qk.caddy, 'plugins-catalog', q],
    queryFn: ({ signal }) => api.get<PluginPackage[]>('/api/caddy/plugins/catalog', { q }, signal),
    staleTime: 10 * 60_000,
    placeholderData: keepPreviousData,
    retry: false,
  });
}

export function useSavePlugins() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (plugins: string[]) => api.put<BinaryOverview>('/api/caddy/plugins', { plugins }),
    onSuccess: (o) => qc.setQueryData(qk.binary, o),
    onSettled: () => {
      void qc.invalidateQueries({ queryKey: qk.settings('binary') });
      void qc.invalidateQueries({ queryKey: qk.binary });
    },
  });
}

export function useBinarySettings(enabled = true) {
  return useQuery({
    queryKey: qk.settings('binary'),
    queryFn: () => api.get<BinarySettings>('/api/settings/binary'),
    enabled,
  });
}

export function useSaveBinarySettings() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (s: BinarySettings) => api.put<BinarySettings>('/api/settings/binary', s),
    onSettled: () => {
      void qc.invalidateQueries({ queryKey: qk.settings('binary') });
      void qc.invalidateQueries({ queryKey: qk.binary });
    },
  });
}

export function useJob(id: string | null) {
  return useQuery({
    queryKey: qk.job(id ?? ''),
    queryFn: () => api.get<JobInfo>(`/api/jobs/${id}`),
    enabled: !!id,
    // Poll while running. Stop on errors: jobs live in memory, so a manager restart (e.g. during an update) forgets them (404).
    refetchInterval: (q) => (q.state.status === 'error' ? false : q.state.data?.state === 'running' || !q.state.data ? 1000 : false),
    retry: (count, err) => !(err instanceof ApiError && err.status === 404) && count < 3,
    // Keep following a running update even if the tab is in the background.
    refetchIntervalInBackground: true,
  });
}

// ---------------------------------------------------------------- Readiness

export function useReadiness() {
  return useQuery({
    queryKey: qk.readiness,
    queryFn: () => api.get<ReadinessReport | null>('/api/readiness'),
  });
}

export function useRunReadiness() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => api.post<ReadinessReport>('/api/readiness/run'),
    onSuccess: (r) => qc.setQueryData(qk.readiness, r),
    onSettled: () => void qc.invalidateQueries({ queryKey: qk.dashboard }),
  });
}

export function useFixReadiness() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (checkId: string) => api.post<FixResult>(`/api/readiness/fix/${encodeURIComponent(checkId)}`),
    onSuccess: (r) => qc.setQueryData(qk.readiness, r.report),
    onSettled: () => void qc.invalidateQueries({ queryKey: qk.dashboard }),
  });
}

export function useGpoScript(enabled: boolean) {
  return useQuery({
    queryKey: [...qk.readiness, 'gpo-script'],
    queryFn: () => api.getText('/api/readiness/gpo-script'),
    enabled,
    staleTime: 5 * 60_000,
  });
}

// ---------------------------------------------------------------- Notifications & UI settings

export function useNotificationSettings() {
  return useQuery({
    queryKey: qk.settings('notifications'),
    queryFn: () => api.get<NotificationSettings>('/api/settings/notifications'),
  });
}

export function useSaveNotificationSettings() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (s: NotificationSettingsInput) => api.put<unknown>('/api/settings/notifications', s),
    onSettled: () => void qc.invalidateQueries({ queryKey: qk.settings('notifications') }),
  });
}

export function useTestNotifications() {
  return useMutation({
    mutationFn: () => api.post<NotificationTestResult>('/api/settings/notifications/test', {}),
  });
}

export function useUiSettings(enabled = true) {
  return useQuery({
    queryKey: qk.settings('ui'),
    queryFn: () => api.get<UiSettings>('/api/settings/ui'),
    enabled,
  });
}

export function useSaveUiSettings() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (s: UiSettingsInput) => api.put<UiSettingsSaveResult>('/api/settings/ui', s),
    onSuccess: (r) => qc.setQueryData(qk.settings('ui'), r.item),
    onSettled: () => void qc.invalidateQueries({ queryKey: qk.settings('ui') }),
  });
}

// ---------------------------------------------------------------- Users, audit, events

export function useUsers(enabled = true) {
  return useQuery({ queryKey: qk.users, queryFn: () => api.get<UserDto[]>('/api/users'), enabled });
}

export function useSaveUser() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (v: { id: string; body: UserUpdateInput } | { id?: undefined; body: UserCreateInput }) =>
      v.id ? api.put<UserDto>(`/api/users/${v.id}`, v.body) : api.post<UserDto>('/api/users', v.body),
    onSettled: () => {
      void qc.invalidateQueries({ queryKey: qk.users });
      void qc.invalidateQueries({ queryKey: qk.me });
    },
  });
}

export function useDeleteUser() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api.del<null>(`/api/users/${id}`),
    onSettled: () => void qc.invalidateQueries({ queryKey: qk.users }),
  });
}

export function useAudit(params: { skip: number; take: number; q: string }) {
  return useQuery({
    queryKey: [...qk.audit, params],
    queryFn: () => api.get<Page<AuditEntry>>('/api/audit', params),
    placeholderData: keepPreviousData,
  });
}

export function useEvents(params: { skip: number; take: number; severity?: EventSeverity | '' }) {
  return useQuery({
    queryKey: [...qk.events, params],
    queryFn: () => api.get<Page<EventEntry>>('/api/events', params),
    placeholderData: keepPreviousData,
    refetchInterval: 30_000,
  });
}

// ---------------------------------------------------------------- Logs & backup

export function useCaddyLog(params: { lines: number; q: string }, refetchInterval: number | false) {
  return useQuery({
    queryKey: [...qk.logs, 'caddy', params],
    queryFn: () => api.get<LogResult>('/api/logs/caddy', params),
    refetchInterval,
    placeholderData: keepPreviousData,
  });
}

export function useAccessLog(params: { host: string; lines: number }, refetchInterval: number | false) {
  return useQuery({
    queryKey: [...qk.logs, 'access', params],
    queryFn: () => api.get<AccessLogResult>('/api/logs/access', params),
    refetchInterval,
    placeholderData: keepPreviousData,
  });
}

export function useManagerLog(params: { lines: number; q: string }, refetchInterval: number | false, enabled: boolean) {
  return useQuery({
    queryKey: [...qk.logs, 'manager', params],
    queryFn: () => api.get<LogResult>('/api/logs/manager', params),
    refetchInterval,
    enabled,
    placeholderData: keepPreviousData,
  });
}

export function useRestoreBackup() {
  return useMutation({
    mutationFn: (file: File) => {
      const form = new FormData();
      form.append('file', file);
      return api.upload<RestoreResult>('/api/backup/restore', form);
    },
  });
}
