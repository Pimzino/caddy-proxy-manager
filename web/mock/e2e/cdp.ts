// Minimal Chrome DevTools Protocol driver for the web E2E checks (mock/e2e/run.ts): headless Google Chrome, one tab
// per scenario, Node's built-in WebSocket. No test libraries.
import { spawn, type ChildProcess } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

export const sleep = (ms: number) => new Promise<void>((r) => setTimeout(r, ms));

/** Polls `probe` until it returns a truthy value; throws with `what` (and the last error) after `timeoutMs`. */
export async function until<T>(what: string, probe: () => Promise<T> | T, timeoutMs = 10_000, everyMs = 100): Promise<NonNullable<T>> {
  const end = Date.now() + timeoutMs;
  let last: unknown;
  for (;;) {
    try {
      const v = await probe();
      if (v) return v as NonNullable<T>;
    } catch (err) {
      last = err;
    }
    if (Date.now() > end) throw new Error(`Timed out after ${timeoutMs} ms waiting for ${what}${last ? ` (last error: ${(last as Error).message})` : ''}`);
    await sleep(everyMs);
  }
}

function chromePath(): string {
  const candidates = [
    process.env.CHROME,
    '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
    'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
    'C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe',
    '/usr/bin/google-chrome',
    '/usr/bin/chromium',
  ];
  const found = candidates.find((c) => c && existsSync(c));
  if (!found) throw new Error('Google Chrome was not found. Set CHROME to the browser executable.');
  return found;
}

type Pending = { resolve: (v: Record<string, unknown>) => void; reject: (e: Error) => void; method: string };
type Listener = (params: Record<string, unknown>, sessionId?: string) => void;

/** One browser, driven over its browser-level DevTools WebSocket with flattened sessions. */
export class Browser {
  private ws!: WebSocket;
  private proc!: ChildProcess;
  private profile!: string;
  private seq = 0;
  private pending = new Map<number, Pending>();
  private listeners = new Map<string, Set<Listener>>();

  static async launch(): Promise<Browser> {
    const b = new Browser();
    b.profile = mkdtempSync(join(tmpdir(), 'cpm-e2e-chrome-'));
    b.proc = spawn(
      chromePath(),
      ['--headless=new', '--remote-debugging-port=0', `--user-data-dir=${b.profile}`, '--no-first-run', '--no-default-browser-check', '--disable-gpu', '--disable-extensions', 'about:blank'],
      { stdio: 'ignore' },
    );
    const portFile = join(b.profile, 'DevToolsActivePort');
    const [port, path] = await until('Chrome to start (DevToolsActivePort)', () => (existsSync(portFile) ? readFileSync(portFile, 'utf8').trim().split('\n') : null), 20_000);
    b.ws = new WebSocket(`ws://127.0.0.1:${port}${path}`);
    await new Promise<void>((resolve, reject) => {
      b.ws.addEventListener('open', () => resolve());
      b.ws.addEventListener('error', () => reject(new Error('Could not connect to Chrome DevTools')));
    });
    b.ws.addEventListener('message', (ev) => b.onMessage(String(ev.data)));
    return b;
  }

  private onMessage(raw: string) {
    const msg = JSON.parse(raw) as { id?: number; method?: string; params?: Record<string, unknown>; sessionId?: string; result?: Record<string, unknown>; error?: { message: string } };
    if (msg.id !== undefined) {
      const p = this.pending.get(msg.id);
      if (!p) return;
      this.pending.delete(msg.id);
      if (msg.error) p.reject(new Error(`${p.method}: ${msg.error.message}`));
      else p.resolve(msg.result ?? {});
      return;
    }
    if (msg.method) for (const l of this.listeners.get(msg.method) ?? []) l(msg.params ?? {}, msg.sessionId);
  }

  send(method: string, params: Record<string, unknown> = {}, sessionId?: string): Promise<Record<string, unknown>> {
    const id = ++this.seq;
    if (process.env.CPM_E2E_DEBUG) console.log(`    cdp → ${method}`);
    return new Promise((resolve, reject) => {
      // A command that never answers (e.g. a paused request nobody continues) fails the step instead of hanging the run.
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`${method}: no answer from Chrome within 30 s`));
      }, 30_000);
      this.pending.set(id, {
        resolve: (v) => (clearTimeout(timer), resolve(v)),
        reject: (e) => (clearTimeout(timer), reject(e)),
        method,
      });
      this.ws.send(JSON.stringify({ id, method, params, ...(sessionId ? { sessionId } : {}) }));
    });
  }

  on(method: string, l: Listener): () => void {
    const set = this.listeners.get(method) ?? new Set<Listener>();
    set.add(l);
    this.listeners.set(method, set);
    return () => set.delete(l);
  }

  async newPage(opts: { timeZone: string; locale?: string; width?: number; height?: number }): Promise<Page> {
    const { targetId } = (await this.send('Target.createTarget', { url: 'about:blank' })) as { targetId: string };
    const { sessionId } = (await this.send('Target.attachToTarget', { targetId, flatten: true })) as { sessionId: string };
    const page = new Page(this, sessionId, targetId);
    await page.send('Page.enable');
    await page.send('Runtime.enable');
    await page.send('Emulation.setDeviceMetricsOverride', { width: opts.width ?? 1440, height: opts.height ?? 1000, deviceScaleFactor: 1, mobile: false });
    await page.send('Emulation.setTimezoneOverride', { timezoneId: opts.timeZone });
    await page.send('Emulation.setLocaleOverride', { locale: opts.locale ?? 'en-US' });
    await page.send('Page.addScriptToEvaluateOnNewDocument', { source: PAGE_HELPERS });
    return page;
  }

  async close() {
    try {
      await Promise.race([this.send('Browser.close'), sleep(3000)]);
    } catch {
      /* already gone */
    }
    this.proc.kill();
    try {
      rmSync(this.profile, { recursive: true, force: true });
    } catch {
      /* Chrome may still hold files for a moment */
    }
  }
}

/**
 * Helpers available in the page as window.__e2e. Every locator throws when nothing matches, so a check can never
 * pass because an element was missing. Values are set through the native setter plus input/change events, which is
 * what React listens to.
 */
const PAGE_HELPERS = `
window.__e2e = {
  norm(s) { return (s || '').replace(/\\s+/g, ' ').trim(); },
  visible(el) { return !!(el && el.getClientRects().length); },
  all(sel, root) { return Array.from((root || document).querySelectorAll(sel)).filter((e) => this.visible(e)); },
  withText(sel, text, root) {
    const hits = this.all(sel, root).filter((e) => this.norm(e.textContent).includes(text));
    return hits.filter((e) => !hits.some((o) => o !== e && e.contains(o)));
  },
  one(sel, text, root) {
    const r = text === undefined ? this.all(sel, root) : this.withText(sel, text, root);
    if (!r.length) throw new Error('No visible ' + sel + (text !== undefined ? ' with text "' + text + '"' : ''));
    return r[0];
  },
  click(sel, text, root) { const el = this.one(sel, text, root); el.scrollIntoView({ block: 'center' }); el.click(); return true; },
  dialog() { const d = this.all('[aria-modal="true"]'); if (!d.length) throw new Error('No open dialog'); return d[d.length - 1]; },
  control(label, root) {
    const labels = this.all('label', root).filter((l) => this.norm(l.textContent).replace(/\\s*\\*$/, '') === label);
    if (!labels.length) throw new Error('No label "' + label + '"');
    const l = labels[0];
    const c = l.htmlFor ? document.getElementById(l.htmlFor) : l.querySelector('input, select, textarea, button');
    if (!c) throw new Error('Label "' + label + '" has no control');
    return c;
  },
  set(el, value) {
    const proto = el instanceof HTMLSelectElement ? HTMLSelectElement.prototype : el instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
    Object.getOwnPropertyDescriptor(proto, 'value').set.call(el, value);
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
    return true;
  },
  setLabel(label, value, root) { return this.set(this.control(label, root), value); },
  dd(label, root) {
    const dt = this.all('dt', root).find((d) => this.norm(d.textContent) === label);
    if (!dt) throw new Error('No <dt> "' + label + '"');
    return this.norm(dt.nextElementSibling && dt.nextElementSibling.textContent);
  },
  has(text) { return this.norm(document.body.innerText).includes(text); },
  card(title) {
    const h = this.all('section h3').find((x) => this.norm(x.textContent) === title);
    if (!h) throw new Error('No chart card "' + title + '"');
    return h.closest('section');
  },
  chart(title) {
    const svg = this.card(title).querySelector('svg[role="img"]');
    if (!svg) throw new Error('No chart in the card "' + title + '"');
    const ticks = Array.from(svg.querySelectorAll('text.viz-tick'));
    const value = ticks.filter((t) => t.getAttribute('text-anchor') === 'end').map((t) => t.textContent);
    const time = ticks.filter((t) => t.getAttribute('text-anchor') === 'middle').map((t) => ({ x: Number(t.getAttribute('x')), label: t.textContent }));
    const line = svg.querySelector('path.viz-mark');
    const xs = line ? Array.from(line.getAttribute('d').matchAll(/[ML](-?[\\d.]+),/g)).map((m) => Number(m[1])) : [];
    return { value, time, xs };
  },
};
`;

export class Page {
  readonly browser: Browser;
  readonly sessionId: string;
  readonly targetId: string;

  constructor(browser: Browser, sessionId: string, targetId: string) {
    this.browser = browser;
    this.sessionId = sessionId;
    this.targetId = targetId;
  }

  send(method: string, params: Record<string, unknown> = {}) {
    return this.browser.send(method, params, this.sessionId);
  }

  /** Evaluates an expression in the page (promises awaited, result by value). Throws the page's exception. */
  async eval<T = unknown>(expression: string): Promise<T> {
    const r = (await this.send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true })) as {
      result: { value?: T };
      exceptionDetails?: { exception?: { description?: string }; text: string };
    };
    if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description?.split('\n')[0] ?? r.exceptionDetails.text);
    return r.result.value as T;
  }

  /** Waits until the page expression is truthy and returns its value. */
  waitFor<T = unknown>(what: string, expression: string, timeoutMs = 10_000): Promise<T> {
    return until(what, () => this.eval<T>(expression), timeoutMs);
  }

  async goto(url: string, ready: string, readyWhat: string) {
    await this.send('Page.navigate', { url });
    await this.waitFor(readyWhat, `document.readyState === 'complete' && !!window.__e2e && (${ready})`, 30_000);
  }

  async screenshot(dir: string, name: string): Promise<string> {
    mkdirSync(dir, { recursive: true });
    const { data } = (await this.send('Page.captureScreenshot', { format: 'png' })) as { data: string };
    const file = join(dir, `${name}.png`);
    writeFileSync(file, Buffer.from(data, 'base64'));
    return file;
  }

  /**
   * Intercepts requests matching `urlPattern` (Fetch domain wildcards) until the returned function is called.
   * `respond` gets the request (and, at the response stage, the original status and body) and returns the reply to
   * fulfil with, or null to let it through unchanged.
   */
  async intercept(
    urlPattern: string,
    stage: 'Request' | 'Response',
    respond: (req: { url: string; method: string; status?: number; body?: string }) => { status: number; body: string } | null,
  ): Promise<() => Promise<void>> {
    const off = this.browser.on('Fetch.requestPaused', (p, sid) => {
      if (sid !== this.sessionId) return;
      void (async () => {
        const requestId = p.requestId as string;
        const request = p.request as { url: string; method: string };
        try {
          let body: string | undefined;
          const status = p.responseStatusCode as number | undefined;
          if (stage === 'Response' && status !== undefined) {
            const r = (await this.send('Fetch.getResponseBody', { requestId })) as { body: string; base64Encoded: boolean };
            body = r.base64Encoded ? Buffer.from(r.body, 'base64').toString('utf8') : r.body;
          }
          let reply: { status: number; body: string } | null;
          try {
            reply = respond({ url: request.url, method: request.method, status, body });
          } catch (err) {
            console.log(`    interception of ${request.url} failed, passing it through: ${(err as Error).message}`);
            reply = null;
          }
          if (!reply) {
            await this.send(stage === 'Response' ? 'Fetch.continueResponse' : 'Fetch.continueRequest', { requestId });
            return;
          }
          await this.send('Fetch.fulfillRequest', {
            requestId,
            responseCode: reply.status,
            responseHeaders: [{ name: 'Content-Type', value: reply.status >= 400 ? 'application/problem+json' : 'application/json; charset=utf-8' }],
            body: Buffer.from(reply.body, 'utf8').toString('base64'),
          });
        } catch {
          /* the page navigated away */
        }
      })();
    });
    // Only the app's API calls (fetch), never the page itself or its modules.
    await this.send('Fetch.enable', { patterns: [{ urlPattern, requestStage: stage, resourceType: 'Fetch' }] });
    return async () => {
      off();
      await this.send('Fetch.disable');
    };
  }

  async close() {
    await this.browser.send('Target.closeTarget', { targetId: this.targetId });
  }
}
