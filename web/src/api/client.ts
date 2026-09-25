import type { ProblemDetails, SetupStatus } from './types';

/** Error thrown for any non-2xx API response. Carries the RFC 7807 problem details when available. */
export class ApiError extends Error {
  readonly status: number;
  readonly title: string;
  readonly detail?: string;
  readonly errors?: Record<string, string[]>;

  constructor(status: number, problem: ProblemDetails, fallback: string) {
    const title = problem.title?.trim() || fallback;
    super(problem.detail ? `${title}: ${problem.detail}` : title);
    this.name = 'ApiError';
    this.status = status;
    this.title = title;
    this.detail = problem.detail;
    this.errors = problem.errors;
  }

  /** A single human-readable line for toasts. */
  get display(): string {
    if (this.detail && this.detail !== this.title) return this.detail;
    return this.title;
  }
}

/** Network-level failure (manager unreachable, connection reset...). */
export class NetworkError extends Error {
  constructor(cause: unknown) {
    super('The management service could not be reached. Check that the Caddy Proxy Manager service is running.');
    this.name = 'NetworkError';
    this.cause = cause;
  }
}

export function errorMessage(err: unknown): string {
  if (err instanceof ApiError) return err.display;
  if (err instanceof Error) return err.message;
  return String(err);
}

type UnauthorizedHandler = (target: '/login' | '/setup') => void;
let unauthorizedHandler: UnauthorizedHandler | null = null;
let redirecting = false;

/** Registered by the router so a 401 anywhere sends the user to /login (or /setup on first run). */
export function setUnauthorizedHandler(handler: UnauthorizedHandler | null) {
  unauthorizedHandler = handler;
}

const AUTH_EXEMPT = ['/api/auth/login', '/api/auth/me', '/api/setup'];

async function handleUnauthorized() {
  if (redirecting) return;
  const path = window.location.pathname;
  if (path.startsWith('/login') || path.startsWith('/setup')) return;
  redirecting = true;
  try {
    let target: '/login' | '/setup' = '/login';
    try {
      const res = await fetch('/api/setup/status', { credentials: 'same-origin', headers: { Accept: 'application/json' } });
      if (res.ok) {
        const s = (await res.json()) as SetupStatus;
        if (s.needsSetup) target = '/setup';
      }
    } catch {
      /* fall back to /login */
    }
    if (unauthorizedHandler) unauthorizedHandler(target);
    else window.location.assign(target);
  } finally {
    redirecting = false;
  }
}

export interface RequestOptions {
  method?: string;
  body?: unknown;
  signal?: AbortSignal;
  query?: Record<string, string | number | boolean | null | undefined>;
  /** Response handling. Default: json (204 → null). */
  as?: 'json' | 'text' | 'blob' | 'none';
}

function buildUrl(path: string, query?: RequestOptions['query']): string {
  if (!query) return path;
  const params = new URLSearchParams();
  for (const [k, v] of Object.entries(query)) {
    if (v === undefined || v === null || v === '') continue;
    params.set(k, String(v));
  }
  const qs = params.toString();
  return qs ? `${path}?${qs}` : path;
}

async function parseProblem(res: Response): Promise<ProblemDetails> {
  const type = res.headers.get('content-type') ?? '';
  try {
    if (type.includes('json')) return (await res.json()) as ProblemDetails;
    const text = (await res.text()).trim();
    return text ? { detail: text.slice(0, 2000) } : {};
  } catch {
    return {};
  }
}

function statusFallback(status: number): string {
  switch (status) {
    case 400: return 'Invalid request';
    case 401: return 'Not signed in';
    case 403: return 'You do not have permission to do this';
    case 404: return 'Not found';
    case 409: return 'Conflict';
    case 422: return 'Rejected';
    case 429: return 'Too many attempts. Wait a moment and try again';
    case 503: return 'Service unavailable';
    default: return `Request failed (HTTP ${status})`;
  }
}

export async function request<T>(path: string, opts: RequestOptions = {}): Promise<T> {
  const method = (opts.method ?? 'GET').toUpperCase();
  const headers: Record<string, string> = { Accept: 'application/json, application/problem+json;q=0.9, */*;q=0.1' };
  if (method !== 'GET' && method !== 'HEAD') headers['X-CPM-Request'] = '1';

  let body: BodyInit | undefined;
  if (opts.body instanceof FormData) {
    body = opts.body;
  } else if (opts.body !== undefined) {
    headers['Content-Type'] = 'application/json';
    body = JSON.stringify(opts.body);
  }

  let res: Response;
  try {
    res = await fetch(buildUrl(path, opts.query), {
      method,
      headers,
      body,
      credentials: 'same-origin',
      signal: opts.signal,
    });
  } catch (err) {
    if (err instanceof DOMException && err.name === 'AbortError') throw err;
    throw new NetworkError(err);
  }

  if (!res.ok) {
    const problem = await parseProblem(res);
    if (res.status === 401 && !AUTH_EXEMPT.some((p) => path.startsWith(p))) void handleUnauthorized();
    throw new ApiError(res.status, problem, statusFallback(res.status));
  }

  const as = opts.as ?? 'json';
  if (as === 'none' || res.status === 204) return null as T;
  if (as === 'text') return (await res.text()) as T;
  if (as === 'blob') return (await res.blob()) as T;
  const text = await res.text();
  return (text ? JSON.parse(text) : null) as T;
}

export const api = {
  get: <T>(path: string, query?: RequestOptions['query'], signal?: AbortSignal) => request<T>(path, { query, signal }),
  getText: (path: string, query?: RequestOptions['query']) => request<string>(path, { query, as: 'text' }),
  post: <T>(path: string, body?: unknown) => request<T>(path, { method: 'POST', body: body ?? {} }),
  put: <T>(path: string, body: unknown) => request<T>(path, { method: 'PUT', body }),
  del: <T>(path: string) => request<T>(path, { method: 'DELETE' }),
  upload: <T>(path: string, form: FormData) => request<T>(path, { method: 'POST', body: form }),
};

/** Fetches a file with the session cookie and hands it to the browser as a download. */
export async function downloadFile(path: string, fallbackName: string): Promise<void> {
  let res: Response;
  try {
    res = await fetch(path, { credentials: 'same-origin' });
  } catch (err) {
    throw new NetworkError(err);
  }
  if (!res.ok) {
    const problem = await parseProblem(res);
    if (res.status === 401) void handleUnauthorized();
    throw new ApiError(res.status, problem, statusFallback(res.status));
  }
  const disposition = res.headers.get('content-disposition') ?? '';
  const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition);
  const name = match ? decodeURIComponent(match[1]) : fallbackName;
  saveBlob(await res.blob(), name);
}

export function saveBlob(blob: Blob, filename: string) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  a.rel = 'noopener';
  document.body.appendChild(a);
  a.click();
  a.remove();
  window.setTimeout(() => URL.revokeObjectURL(url), 1000);
}
