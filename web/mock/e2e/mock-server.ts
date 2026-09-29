// The mock API dev server (web/mock, `vite --mode mock`) started on a free port with its own in-memory state. Shared by
// the UI end-to-end checks (run.ts) and the documentation screenshots (docs-screenshots.ts).
import { spawn, type ChildProcess } from 'node:child_process';
import { createServer } from 'node:net';
import { join } from 'node:path';
import { until } from './cdp.ts';

export const WEB = join(import.meta.dirname, '..', '..');

export function freePort(): Promise<number> {
  return new Promise((resolve, reject) => {
    const srv = createServer();
    srv.listen(0, '127.0.0.1', () => {
      const port = (srv.address() as { port: number }).port;
      srv.close(() => resolve(port));
    });
    srv.on('error', reject);
  });
}

export class Mock {
  readonly base: string;
  private proc: ChildProcess;
  private output = '';

  private constructor(base: string, proc: ChildProcess) {
    this.base = base;
    this.proc = proc;
    proc.stdout?.on('data', (d) => (this.output += String(d)));
    proc.stderr?.on('data', (d) => (this.output += String(d)));
  }

  static async start(env: Record<string, string>): Promise<Mock> {
    const port = await freePort();
    const proc = spawn(process.execPath, [join(WEB, 'node_modules', 'vite', 'bin', 'vite.js'), '--mode', 'mock', '--host', '127.0.0.1', '--port', String(port), '--strictPort'], {
      cwd: WEB,
      env: { ...process.env, MOCK_LATENCY: '0', ...env },
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    const m = new Mock(`http://127.0.0.1:${port}`, proc);
    try {
      // Any answer means the server is up (signed out, MOCK_ANON/MOCK_SETUP answer 401).
      await until('the mock API', async () => (await fetch(`${m.base}/api/auth/me`)).status < 500, 30_000, 250);
    } catch (err) {
      proc.kill();
      throw new Error(`${(err as Error).message}\n${m.output}`, { cause: err });
    }
    return m;
  }

  async api<T>(method: string, path: string, body?: unknown): Promise<T> {
    const res = await fetch(this.base + path, {
      method,
      // X-CPM-Request: the anti-CSRF header every state-changing API call carries (like src/api/client.ts).
      headers: { 'X-CPM-Request': '1', ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    const text = await res.text();
    if (!res.ok) throw new Error(`${method} ${path} → ${res.status} ${text}`);
    return (text ? JSON.parse(text) : null) as T;
  }

  stop() {
    this.proc.kill();
  }
}
