// End-to-end checks of the web UI (web/src) against the mock API (web/mock) in headless Google Chrome.
//
//   cd web && node mock/e2e/run.ts [scenario…]        (Node 22.18+ / 24 runs the TypeScript directly)
//
// Each scenario starts its own mock server (the `npm run dev:mock` dev server with a fresh in-memory state and its
// environment switches) and a fresh browser tab in a fixed time zone and locale, drives the real UI (clicks, typing,
// saving) and checks both what the page shows and what reached the mock API. Output: report.json plus screenshots in
// $CPM_E2E_ARTIFACTS or mock/e2e/artifacts (git-ignored); the exit code is 1 when a check fails. CHROME=<path> selects the
// browser; CPM_E2E_DEBUG=1 logs every DevTools command. Checks that need PowerShell (pwsh) to parse the copied commands
// are skipped, not failed, without it.
//
// How this harness could fail, and the guard for each:
// - Vite or Chrome never become ready → bounded waits that report what was awaited; both are killed in `finally`.
// - A check passes because an element was missing → every page locator (window.__e2e, cdp.ts) throws when nothing matches.
// - React ignores programmatic input → values go through the native setter plus input/change events.
// - Background polling races an assertion → waits poll for the expected state with a timeout, never a fixed sleep.
// - The host's time zone or locale leaks into the expectations → each tab runs in en-US / America/New_York (UTC−4/−5),
//   where a UTC day and the local day differ, and expected labels are computed for that zone explicitly.
// - An interception leaks into a later step → each one is removed right after its step.
// - A scenario's state leaks into the next → one mock server (fresh state) per scenario.
import { spawn, spawnSync, type ChildProcess } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:net';
import { join } from 'node:path';
import type { CaddySettings, RegenerateTokenResult, ServerSummary, SiteHost, TrafficReport } from '../../src/api/types.ts';
import { Browser, type Page, until } from './cdp.ts';

const WEB = join(import.meta.dirname, '..', '..');
const ARTIFACTS = process.env.CPM_E2E_ARTIFACTS || join(WEB, 'mock', 'e2e', 'artifacts');
const TZ = 'America/New_York';
const EXE = 'C:\\Program Files\\Caddy Proxy Manager\\CaddyManager.exe';

interface Check {
  scenario: string;
  ids: string[];
  check: string;
  result: 'pass' | 'fail' | 'skip';
  detail?: string;
}
const checks: Check[] = [];
const screenshots: string[] = [];

// ---------------------------------------------------------------- mock server

function freePort(): Promise<number> {
  return new Promise((resolve, reject) => {
    const srv = createServer();
    srv.listen(0, '127.0.0.1', () => {
      const port = (srv.address() as { port: number }).port;
      srv.close(() => resolve(port));
    });
    srv.on('error', reject);
  });
}

class Mock {
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
      await until('the mock API', async () => (await fetch(`${m.base}/api/auth/me`)).ok, 30_000, 250);
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

// ---------------------------------------------------------------- scenario plumbing

async function scenario(name: string, env: Record<string, string>, body: (ctx: { mock: Mock; page: Page; browser: Browser }) => Promise<void>) {
  const only = process.argv.slice(2);
  if (only.length && !only.includes(name)) return;
  console.log(`\n▶ ${name}`);
  const mock = await Mock.start(env);
  const browser = await Browser.launch();
  const page = await browser.newPage({ timeZone: TZ });
  try {
    await body({ mock, page, browser });
  } catch (err) {
    checks.push({ scenario: name, ids: [], check: 'scenario completed', result: 'fail', detail: (err as Error).stack ?? String(err) });
    console.log(`  ✗ scenario aborted: ${(err as Error).message}`);
    try {
      screenshots.push(await page.screenshot(ARTIFACTS, `${name}-failure`));
    } catch {
      /* no page */
    }
  } finally {
    await browser.close();
    mock.stop();
  }
}

/** Records one check; `fn` throws (or returns a string explaining the failure) when it does not hold. */
async function check(scenarioName: string, ids: string[], what: string, fn: () => Promise<string | void> | string | void) {
  try {
    const problem = await fn();
    if (problem) throw new Error(problem);
    checks.push({ scenario: scenarioName, ids, check: what, result: 'pass' });
    console.log(`  ✓ [${ids.join(', ')}] ${what}`);
  } catch (err) {
    checks.push({ scenario: scenarioName, ids, check: what, result: 'fail', detail: (err as Error).message });
    console.log(`  ✗ [${ids.join(', ')}] ${what}\n      ${(err as Error).message}`);
  }
}

function skip(scenarioName: string, ids: string[], what: string, why: string) {
  checks.push({ scenario: scenarioName, ids, check: what, result: 'skip', detail: why });
  console.log(`  - [${ids.join(', ')}] ${what} (skipped: ${why})`);
}

function eq(actual: unknown, expected: unknown, what: string): string | void {
  const a = JSON.stringify(actual);
  const e = JSON.stringify(expected);
  if (a !== e) return `${what}: expected ${e}, got ${a}`;
}

const js = (v: unknown) => JSON.stringify(v);
async function shot(page: Page, name: string) {
  screenshots.push(await page.screenshot(ARTIFACTS, name));
}

/** Command elements of each PowerShell command in `script`, parsed by PowerShell itself (null when pwsh is missing). */
function pwshCommands(script: string): { invocation: string; elements: string[] }[] | null {
  const ps = `
$errs = $null; $toks = $null
$ast = [System.Management.Automation.Language.Parser]::ParseInput($env:CPM_SCRIPT, [ref]$toks, [ref]$errs)
if ($errs.Count) { throw ($errs | ForEach-Object Message) -join '; ' }
$cmds = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true)
ConvertTo-Json -Depth 4 -Compress @($cmds | ForEach-Object { [pscustomobject]@{ invocation = "$($_.InvocationOperator)"; elements = @($_.CommandElements | ForEach-Object { $v = $_.SafeGetValue(); if ($null -eq $v) { $_.Extent.Text } else { "$v" } }) } })`;
  const r = spawnSync('pwsh', ['-NoProfile', '-NonInteractive', '-Command', ps], { env: { ...process.env, CPM_SCRIPT: script }, encoding: 'utf8' });
  if (r.error) return null;
  if (r.status !== 0) throw new Error(`pwsh could not parse the command: ${r.stderr}`);
  return JSON.parse(r.stdout) as { invocation: string; elements: string[] }[];
}

/** Arguments of a Windows command line (CommandLineToArgvW rules for quotes; the commands contain no backslash-quote). */
function argv(line: string): string[] {
  const out: string[] = [];
  let cur = '';
  let quoted = false;
  let any = false;
  for (const ch of line) {
    if (ch === '"') {
      quoted = !quoted;
      any = true;
    } else if (/\s/.test(ch) && !quoted) {
      if (any || cur) out.push(cur);
      cur = '';
      any = false;
    } else cur += ch;
  }
  if (any || cur) out.push(cur);
  return out;
}

// ---------------------------------------------------------------- scenarios

/** WEB-5 / TEL-6 (UTC day buckets) and WEB-4 (integer count axes) on the Traffic page. */
async function trafficScenario() {
  const S = 'traffic';
  await scenario(S, {}, async ({ mock, page }) => {
    const month = await mock.api<TrafficReport>('GET', '/api/servers/local/traffic?range=month');
    await page.goto(`${mock.base}/traffic?range=month`, `!!__e2e.card('Unique clients').querySelector('path.viz-mark')`, 'the 30-day traffic view');
    await shot(page, 'traffic-30d-new-york');

    const newest = Date.parse(month.series[month.series.length - 1].at);
    const utcLabel = `${new Intl.DateTimeFormat('en-US', { weekday: 'short', month: 'short', day: 'numeric', timeZone: 'UTC' }).format(newest)} (UTC)`;
    const localLabel = new Intl.DateTimeFormat('en-US', { weekday: 'short', month: 'short', day: 'numeric', timeZone: TZ }).format(newest);

    await check(S, ['WEB-5', 'TEL-6'], 'day buckets start at 00:00 UTC in the API (precondition)', () =>
      month.series.every((p) => p.at.endsWith('T00:00:00.000Z') || p.at.endsWith('T00:00:00Z')) ? undefined : `bucket starts: ${month.series.slice(-2).map((p) => p.at).join(', ')}`,
    );
    await check(S, ['WEB-5', 'TEL-6'], 'the 30-day view states that days are UTC calendar days', async () =>
      (await page.eval<boolean>(`__e2e.has('Days are UTC calendar days (00:00–24:00 UTC)')`)) ? undefined : 'no UTC note',
    );
    await check(S, ['WEB-5', 'TEL-6'], `newest 30-day bucket is labelled with its UTC date "${utcLabel}" (the local date west of UTC is "${localLabel}")`, async () => {
      await page.eval(`(() => { const card = __e2e.card('Requests'); __e2e.click('button[aria-label="Show data table"]', undefined, card); })()`);
      const first = await page.waitFor<string>('the data table', `(() => { const card = __e2e.card('Requests'); const td = card.querySelector('tbody tr td'); return td && td.textContent; })()`);
      await page.eval(`__e2e.click('button[aria-label="Show chart"]', undefined, __e2e.card('Requests'))`);
      return eq(first, utcLabel, 'first row of the Requests data table');
    });
    await check(S, ['WEB-5', 'TEL-6'], 'every 30-day x-axis tick sits on a bucket start and names that bucket’s UTC date', async () => {
      const c = await page.eval<{ time: { x: number; label: string }[]; xs: number[] }>(`__e2e.chart('Unique clients')`);
      if (c.time.length < 2 || c.xs.length !== month.series.length) return `ticks ${js(c.time)}, points ${c.xs.length}`;
      const md = new Intl.DateTimeFormat('en-US', { month: 'short', day: 'numeric', timeZone: 'UTC' });
      for (const t of c.time) {
        const i = c.xs.findIndex((x) => Math.abs(x - t.x) < 0.6);
        if (i < 0) return `tick "${t.label}" at x=${t.x} is between buckets (bucket x: ${c.xs.slice(0, 4).join(', ')}, …)`;
        const expected = md.format(Date.parse(month.series[i].at));
        if (t.label !== expected) return `tick at bucket ${month.series[i].at} reads "${t.label}", expected "${expected}"`;
      }
    });
    await check(S, ['WEB-5', 'TEL-6'], 'the 24-hour view states that times are local (America/New_York)', async () => {
      await page.goto(`${mock.base}/traffic`, `!!__e2e.card('Requests').querySelector('path.viz-mark')`, 'the 24-hour traffic view');
      const ok = await page.eval<boolean>(`__e2e.has('Times in your time zone (America/New_York, UTC−0')`);
      return ok ? undefined : 'no local time zone note';
    });

    // WEB-4: a quiet server. The 1-hour report is rewritten to at most 2 requests and 10 clients per minute.
    const stop = await page.intercept('*/api/servers/local/traffic?range=hour*', 'Response', (r) => {
      if (!r.body) return null;
      const rep = JSON.parse(r.body) as TrafficReport;
      rep.series = rep.series.map((p, i) => ({ ...p, requests: i % 3, uniqueClients: i % 11, status4xx: 0, status5xx: 0 }));
      return { status: 200, body: JSON.stringify(rep) };
    });
    await page.goto(`${mock.base}/traffic?range=hour`, `!!document.querySelector('svg[role="img"]')`, 'the 1-hour traffic view');
    await page.waitFor('the Requests chart', `!!__e2e.card('Requests').querySelector('path.viz-mark')`);
    await stop();
    await shot(page, 'traffic-1h-low-counts');
    await check(S, ['WEB-4'], 'requests axis (peak 2/min) reads 0, 1, 2', async () => eq((await page.eval<{ value: string[] }>(`__e2e.chart('Requests')`)).value, ['0', '1', '2'], 'value ticks'));
    await check(S, ['WEB-4'], 'unique clients axis (peak 10/min) reads 0, 5, 10', async () =>
      eq((await page.eval<{ value: string[] }>(`__e2e.chart('Unique clients')`)).value, ['0', '5', '10'], 'value ticks'),
    );
    await check(S, ['WEB-4'], 'status class columns (peak 2/min) read 0, 1, 2', async () =>
      eq((await page.eval<{ value: string[] }>(`__e2e.chart('Responses by status class')`)).value, ['0', '1', '2'], 'value ticks'),
    );
  });
}

/** WEB-1: the DNS challenge has no CNAME delegation any more — neither the host editor nor Settings › Caddy offers it. */
async function hostsScenario() {
  const S = 'hosts';
  await scenario(S, {}, async ({ mock, page }) => {
    const find = async () => (await mock.api<SiteHost[]>('GET', '/api/hosts')).find((h) => h.domains.includes('shop.example.com'))!;
    await check(S, ['WEB-1'], 'precondition: shop.example.com uses the DNS challenge', async () => eq((await find()).acmeChallenge, 'dns', 'acmeChallenge'));

    await page.goto(`${mock.base}/hosts/proxy`, `__e2e.has('shop.example.com')`, 'the proxy hosts list');
    await page.eval(`__e2e.one('tr', 'shop.example.com').click()`);
    await page.waitFor('the host editor', `!!__e2e.dialog() && __e2e.withText('[role="tab"]', 'TLS').length > 0`);
    await page.eval(`__e2e.click('[role="tab"]', 'TLS', __e2e.dialog())`);
    await page.waitFor('the ACME challenge field', `!!__e2e.control('ACME challenge', __e2e.dialog())`);
    await shot(page, 'host-dns-challenge-no-delegation');
    await check(S, ['WEB-1'], 'the TLS tab of a DNS-01 host shows no delegation select and no CNAME records', async () => {
      const shown = await page.eval<boolean>(
        `__e2e.has('Challenge delegation') || __e2e.has('_acme-challenge.shop.example.com') || __e2e.has('Check DNS')`,
      );
      return shown ? 'delegation UI still shown' : undefined;
    });
    await page.eval(`__e2e.click('button[type="submit"]', 'Save', __e2e.dialog())`);
    await page.waitFor('the editor to close after saving', `!document.querySelector('[aria-modal="true"]')`, 10_000);
    await check(S, ['WEB-1'], 'the saved host carries no delegation fields', async () => {
      const saved = (await find()) as unknown as Record<string, unknown>;
      return eq(['dnsDelegation' in saved, 'dnsOverrideDomain' in saved, saved.acmeChallenge], [false, false, 'dns'], 'saved host');
    });

    await page.goto(`${mock.base}/settings`, `__e2e.has('ACME challenge')`, 'Settings › Caddy');
    await shot(page, 'settings-acme-challenge-no-delegation');
    await check(S, ['WEB-1'], 'Settings › Caddy shows no challenge delegation panel', async () =>
      (await page.eval<boolean>(`__e2e.has('Challenge delegation') || __e2e.has('Default delegation name')`)) ? 'delegation panel still shown' : undefined,
    );
    await check(S, ['WEB-1'], 'the delegation-check endpoint is gone', async () => {
      try {
        await mock.api('POST', '/api/dns/delegation-check', { domains: ['shop.example.com'] });
        return 'POST /api/dns/delegation-check still answers';
      } catch {
        return undefined;
      }
    });
  });
}

/** WEB-2 control (no DNS-01: both challenges off is refused) and WEB-10 (membership card follows a storage save). */
async function settingsScenario() {
  const S = 'settings';
  await scenario(S, {}, async ({ mock, page }) => {
    await page.goto(`${mock.base}/settings`, `__e2e.has('Disable HTTP-01 challenge')`, 'Settings › Caddy');
    await page.eval(`__e2e.control('Disable HTTP-01 challenge').click(); __e2e.control('Disable TLS-ALPN-01 challenge').click()`);
    await page.eval(`__e2e.click('button[type="submit"]', 'Save')`);
    await check(S, ['WEB-2'], 'without DNS-01, disabling both HTTP-01 and TLS-ALPN-01 is still refused before saving', async () => {
      // The warning turns into an error (a role="alert" danger callout) when the save is refused.
      await page.waitFor('the challenge error', `__e2e.withText('[role="alert"]', 'TLS-ALPN-01').length > 0`);
      const s = await mock.api<CaddySettings>('GET', '/api/settings/caddy');
      return eq([s.disableHttpChallenge, s.disableTlsAlpnChallenge], [false, false], 'stored challenges');
    });
    await check(S, ['WEB-2'], 'the error says DNS-01 as the default challenge is the alternative', async () =>
      (await page.eval<boolean>(`__e2e.has('Keep HTTP-01 or TLS-ALPN-01 enabled, or make DNS-01 (with a DNS provider) the default challenge')`)) ? undefined : 'old wording',
    );
    await shot(page, 'settings-no-challenge-left');

    await page.goto(`${mock.base}/settings?tab=cluster`, `__e2e.has('Cluster membership') && __e2e.has('Storage backend')`, 'Settings › Cluster');
    await check(S, ['WEB-10'], 'precondition: the membership card shows local storage', async () => eq(await page.eval(`__e2e.dd('Caddy storage')`), 'Local folder', 'Caddy storage'));
    await page.eval(`__e2e.click('[role="radio"]', 'Shared folder')`);
    await page.waitFor('the folder field', `!!__e2e.control('Folder')`);
    await page.eval(`__e2e.setLabel('Folder', '\\\\\\\\fs01\\\\caddy$\\\\storage')`);
    await page.eval(`__e2e.click('button[type="submit"]', 'Save')`);
    await until('the storage save', async () => (await mock.api<CaddySettings>('GET', '/api/settings/caddy')).storageBackend === 'fileSystem', 10_000);
    await check(S, ['WEB-10'], 'after saving, the membership card shows the shared folder without a reload', async () => {
      await page.waitFor('the membership card to follow', `__e2e.dd('Caddy storage') === 'Shared folder'`, 5_000);
    });
    await shot(page, 'settings-cluster-shared-storage');
  });
}

/** WEB-2: a DNS-only configuration saves, on a standalone/primary server and on a managed node (node-local fields). */
async function dnsOnlyScenarios() {
  for (const role of ['primary', 'node'] as const) {
    const S = `dns-only-${role}`;
    await scenario(S, { MOCK_DNS_ONLY: '1', MOCK_CLUSTER_ROLE: role }, async ({ mock, page }) => {
      const port = role === 'node' ? '8081' : '8080';
      await page.goto(`${mock.base}/settings`, `__e2e.has('Listeners') && !!__e2e.control('HTTP port')`, 'Settings › Caddy');
      await page.eval(`__e2e.setLabel('HTTP port', '${port}')`);
      await page.eval(`__e2e.click('button[type="submit"]', 'Save')`);
      await check(S, ['WEB-2'], `DNS-01 by default with HTTP-01 and TLS-ALPN-01 disabled: changing the HTTP port saves${role === 'node' ? ' on a managed node' : ''}`, async () => {
        await until('the saved port', async () => (await mock.api<CaddySettings>('GET', '/api/settings/caddy')).httpPort === Number(port), 8_000);
        const blocked = await page.eval<boolean>(`__e2e.has('Keep HTTP-01 or TLS-ALPN-01 enabled')`);
        return blocked ? 'the challenge error is shown' : undefined;
      });
      await shot(page, `settings-dns-only-${role}`);
      if (role === 'node') {
        await page.goto(`${mock.base}/`, `__e2e.has('Run readiness checks')`, 'the dashboard');
        await check(S, ['WEB-8'], 'the dashboard of a managed node has no “Add proxy host” shortcut (readiness checks stay)', async () =>
          (await page.eval<boolean>(`__e2e.withText('button', 'Add proxy host').length > 0`)) ? '“Add proxy host” is shown' : undefined,
        );
      }
    });
  }
}

/** WEB-3 (stale data on failed refresh), WEB-6/WEB-9/SEC-3/CL-8 (key rotation and join commands), WEB-7/WEB-8 (dashboard). */
async function serversScenario() {
  const S = 'servers';
  const online = 'n-7f3a91c2e4b5';
  const offline = 'n-2b8e4d6f1a09';
  await scenario(S, {}, async ({ mock, page }) => {
    /** Row menu → the join token / key action → confirm; waits for the dialog that shows the new token. */
    const rotate = async (name: string) => {
      await page.eval(`__e2e.click('button[aria-label="Actions for ${name}"]')`);
      await page.eval(`__e2e.click('[role="menuitem"]', 'join token')`);
      await page.waitFor('the confirmation', `!!__e2e.dialog() && __e2e.all('button', __e2e.dialog()).length > 1`);
      await page.eval(`__e2e.all('button', __e2e.dialog()).filter((b) => /Rotate key|Create new token/.test(b.textContent))[0].click()`);
      await page.waitFor('the new token', `!!__e2e.dialog().querySelector('pre code')`);
    };

    await page.goto(`${mock.base}/`, `__e2e.has('Run readiness checks')`, 'the dashboard');
    await check(S, ['WEB-8'], 'the dashboard of a primary keeps “Add proxy host”', async () => ((await page.eval<boolean>(`__e2e.withText('button', 'Add proxy host').length > 0`)) ? undefined : 'missing'));

    // Rotate the key of an online node: acknowledged.
    await page.goto(`${mock.base}/servers`, `__e2e.has('WEB-PROXY02') && __e2e.has('WEB-PROXY03')`, 'the Servers page');
    await rotate('WEB-PROXY02');
    await check(S, ['SEC-3', 'CL-8', 'WEB-9'], 'rotating an online node reports that it confirmed the new key', async () => {
      await page.waitFor('the rotation result', `__e2e.has('WEB-PROXY02 now uses the new key')`, 3_000);
    });
    await shot(page, 'servers-key-rotated');
    const codes = await page.eval<string[]>(`Array.from(__e2e.dialog().querySelectorAll('pre code')).map((c) => c.textContent)`);
    const token = codes[0];
    await check(S, ['SEC-3', 'CL-8', 'WEB-9'], 'the result shows the new token for re-joining', () => (/^cpmj1\.[A-Za-z0-9_-]+$/.test(token) ? undefined : `token block: ${token}`));
    await check(S, ['WEB-6', 'CL-8'], 'PowerShell commands: stop the service, join by full path with the quoted token, start the service', () =>
      eq(codes[1].split('\n'), ['net stop CaddyProxyManager', `& '${EXE}' cluster join '${token}'`, 'net start CaddyProxyManager'], 'commands'),
    );
    const parsed = pwshCommands(codes[1]);
    if (parsed === null) skip(S, ['WEB-6'], 'PowerShell parses the copied commands as intended', 'pwsh is not installed');
    else
      await check(S, ['WEB-6'], 'PowerShell parses the copied commands as net stop / & <exe> cluster join <token> / net start', () =>
        eq(parsed, [
          { invocation: 'Unknown', elements: ['net', 'stop', 'CaddyProxyManager'] },
          { invocation: 'Ampersand', elements: [EXE, 'cluster', 'join', token] },
          { invocation: 'Unknown', elements: ['net', 'start', 'CaddyProxyManager'] },
        ], 'parsed commands'),
      );
    await check(S, ['WEB-6', 'CL-8'], 'Command Prompt commands quote the exe path and the token with double quotes', async () => {
      await page.eval(`__e2e.click('[role="radio"]', 'Command Prompt', __e2e.dialog())`);
      const cmd = await page.waitFor<string>('the Command Prompt variant', `(() => { const c = __e2e.dialog().querySelectorAll('pre code')[1].textContent; return c.includes('"') && c; })()`, 3_000);
      const lines = cmd.split('\n');
      return eq(lines, ['net stop CaddyProxyManager', `"${EXE}" cluster join "${token}"`, 'net start CaddyProxyManager'], 'commands') ?? eq(argv(lines[1]), [EXE, 'cluster', 'join', token], 'arguments of the join line');
    });
    await shot(page, 'servers-join-command-cmd');
    await page.eval(`__e2e.click('button', 'Done', __e2e.dialog())`);

    // Rotate the key of an offline node: pending.
    await rotate('WEB-PROXY03');
    await check(S, ['SEC-3', 'CL-8', 'WEB-9'], 'rotating an unreachable node says the rotation is pending and the old key still works there', async () => {
      await page.waitFor('the pending result', `__e2e.has('Key rotation pending — WEB-PROXY03 could not be reached')`);
    });
    await shot(page, 'servers-key-rotation-pending');
    await page.eval(`__e2e.click('button', 'Done', __e2e.dialog())`);
    await check(S, ['SEC-3'], 'the Servers list marks the unreachable node “Key rotation pending”', async () => {
      await page.waitFor('the badge', `__e2e.withText('tr', 'WEB-PROXY03').some((r) => r.textContent.includes('Key rotation pending'))`, 5_000);
    });
    await check(S, ['SEC-3'], 'POST /api/servers/{id}/token of the mock: {joinToken, rotated} and keyRotationPending in the list', async () => {
      const r = await mock.api<RegenerateTokenResult>('POST', `/api/servers/${online}/token`);
      const list = await mock.api<ServerSummary[]>('GET', '/api/servers');
      return (
        eq([typeof r.joinToken, r.rotated], ['string', true], 'online node result') ??
        eq([list.find((s) => s.id === online)?.keyRotationPending ?? false, list.find((s) => s.id === offline)?.keyRotationPending], [false, true], 'keyRotationPending')
      );
    });
    await page.eval(`__e2e.click('button[aria-label="Actions for WEB-PROXY03"]')`);
    await page.eval(`__e2e.click('[role="menuitem"]', 'Remove')`);
    await check(S, ['SEC-3'], 'removing an unreachable node warns that it keeps trusting the key until `cluster leave` runs on it', async () => {
      await page.waitFor('the removal warning', `__e2e.has('keeps trusting this cluster’s key until you run CaddyManager.exe cluster leave')`);
    });
    await page.eval(`__e2e.click('button', 'Cancel', __e2e.dialog())`);

    // WEB-3: failed background refreshes keep the page.
    await page.goto(`${mock.base}/servers/${online}`, `__e2e.has('Resource usage') && __e2e.has('WEB-PROXY02')`, 'the server detail page');
    let stop = await page.intercept(`*/api/servers/${online}`, 'Request', () => ({ status: 503, body: JSON.stringify({ title: 'Service Unavailable', status: 503, detail: 'The manager is restarting.' }) }));
    await check(S, ['WEB-3'], 'a failing refresh of the detail page shows a warning and keeps the server (no “Server not found”)', async () => {
      await page.waitFor('the refresh warning', `__e2e.has('Could not refresh this server')`, 45_000);
      const lost = await page.eval<boolean>(`__e2e.has('Server not found') || !__e2e.has('Resource usage')`);
      return lost ? 'the detail page was replaced' : undefined;
    });
    await shot(page, 'server-detail-refresh-failed');
    await stop();
    await check(S, ['WEB-3'], 'the warning clears once the server answers again', async () => {
      await page.waitFor('the recovery', `!__e2e.has('Could not refresh this server')`, 30_000);
    });

    await page.goto(`${mock.base}/servers`, `__e2e.has('WEB-PROXY02')`, 'the Servers page');
    stop = await page.intercept('*/api/servers', 'Request', () => ({ status: 503, body: JSON.stringify({ title: 'Service Unavailable', status: 503 }) }));
    await check(S, ['WEB-3'], 'a failing refresh of the Servers list keeps the table and shows a warning', async () => {
      await page.waitFor('the list warning', `__e2e.has('Could not refresh the server list')`, 45_000);
      const lost = await page.eval<boolean>(`__e2e.has('Could not load servers') || __e2e.withText('tr', 'WEB-PROXY02').length === 0`);
      return lost ? 'the table was replaced' : undefined;
    });
    await shot(page, 'servers-refresh-failed');
    await stop();

    // WEB-7: a node that has not joined yet is neither online nor in sync.
    await mock.api('DELETE', `/api/servers/${offline}`);
    await mock.api('POST', '/api/servers', { name: 'WEB-PROXY04', url: 'https://web-proxy04.corp.example.com:8443' });
    await page.goto(`${mock.base}/`, `__e2e.has('Servers')`, 'the dashboard');
    await check(S, ['WEB-7'], 'the dashboard Servers card counts a node waiting to join as not online and names it', async () => {
      await page.waitFor('the Servers card', `__e2e.has('WEB-PROXY04 waiting to join')`, 10_000);
      const card = await page.eval<string>(`__e2e.norm(__e2e.one('a[href="/servers"]', 'online').textContent)`);
      return card.includes('2/3 online') ? undefined : `card: ${card}`;
    });
    await shot(page, 'dashboard-waiting-to-join');
  });
}

/**
 * Branding: the raster logo (docs/brand/logo-concepts) replaced the old inline SVG everywhere in the UI.
 * Failure modes checked: an image that 404s or fails to decode (naturalWidth 0), both theme variants showing at
 * once (or neither), the collapsed sidebar still trying to fit the lockup, the sign-in page without the logo, the
 * favicon link pointing at a missing or non-ICO file, and a leftover SVG logo/favicon.
 */
async function brandingScenario() {
  const S = 'branding';
  await scenario(S, {}, async ({ mock, page }) => {
    // one entry per <img> of the logo that is actually displayed
    const shown = `[...document.querySelectorAll('img[alt="Caddy Proxy Manager"]')].filter((i) => i.getClientRects().length)
      .map((i) => ({ src: new URL(i.src).pathname, ok: i.complete && i.naturalWidth > 0, h: Math.round(i.getBoundingClientRect().height) }))`;
    type Shown = { src: string; ok: boolean; h: number }[];
    const setTheme = async (theme: 'light' | 'dark', sidebar: 'collapsed' | null, path: string, ready: string, what: string, logoVisible = true) => {
      await page.eval(`localStorage.setItem('cpm.theme', ${js(theme)}); ${sidebar ? `localStorage.setItem('cpm.sidebar', 'collapsed')` : `localStorage.removeItem('cpm.sidebar')`}`);
      await page.goto(`${mock.base}${path}`, ready, what);
      if (logoVisible) await page.waitFor(`${what}: logo images decoded`, `(${shown}).length > 0 && (${shown}).every((i) => i.ok)`);
    };
    const oneLockup = (variant: 'light' | 'dark', minH: number) => async () => {
      const imgs = await page.eval<Shown>(shown);
      if (imgs.length !== 1) return `expected exactly one visible logo, got ${js(imgs)}`;
      if (!/\/logo-(light|dark)(-[\w-]+)?\.png$/.test(imgs[0].src)) return `not the lockup PNG: ${imgs[0].src}`;
      if (!imgs[0].src.includes(`logo-${variant}`)) return `expected the ${variant} lockup, got ${imgs[0].src}`;
      if (imgs[0].h < minH) return `lockup only ${imgs[0].h}px tall (want ≥ ${minH}px so “Proxy Manager” stays legible)`;
    };
    const sidebarReady = `!!document.querySelector('aside img[alt="Caddy Proxy Manager"]')`;

    await page.goto(`${mock.base}/`, sidebarReady, 'the dashboard');
    await setTheme('light', null, '/', sidebarReady, 'the dashboard (light)');
    await shot(page, 'branding-sidebar-light');
    await check(S, ['WEB-BRAND'], 'light theme: the sidebar shows only the light lockup, loaded, ≥ 64px tall', oneLockup('light', 64));

    await setTheme('dark', null, '/', sidebarReady, 'the dashboard (dark)');
    await shot(page, 'branding-sidebar-dark');
    await check(S, ['WEB-BRAND'], 'dark theme: the sidebar shows only the dark lockup, loaded, ≥ 64px tall', oneLockup('dark', 64));

    await setTheme('light', 'collapsed', '/', sidebarReady, 'the dashboard (collapsed sidebar)');
    await shot(page, 'branding-sidebar-collapsed');
    await check(S, ['WEB-BRAND'], 'collapsed sidebar: the square mark replaces the lockup', async () => {
      const imgs = await page.eval<Shown>(shown);
      if (imgs.length !== 1 || !/\/mark(-[\w-]+)?\.png$/.test(imgs[0].src)) return `expected only the mark, got ${js(imgs)}`;
    });

    // phone width: the navigation drawer shows the lockup with the close button beside it
    await page.send('Emulation.setDeviceMetricsOverride', { width: 390, height: 844, deviceScaleFactor: 2, mobile: true });
    await setTheme('light', null, '/', `!!document.querySelector('button[aria-label="Open navigation"]')`, 'the dashboard (phone)', false);
    await page.eval(`document.querySelector('button[aria-label="Open navigation"]').click()`);
    await page.waitFor('the navigation drawer', `!!document.querySelector('button[aria-label="Close navigation"]') && (${shown}).length > 0 && (${shown}).every((i) => i.ok) && document.getAnimations().every((x) => x.playState !== 'running')`);
    await shot(page, 'branding-drawer-phone');
    await check(S, ['WEB-BRAND'], 'phone drawer: the lockup and the close button sit side by side without overlapping', async () => {
      const r = await page.eval<{ logo: number[]; close: number[] } | null>(`(() => {
        const logo = [...document.querySelectorAll('img[alt="Caddy Proxy Manager"]')].find((i) => i.getClientRects().length && !i.closest('aside'));
        const close = document.querySelector('button[aria-label="Close navigation"]');
        if (!logo || !close) return null;
        const a = logo.getBoundingClientRect(), b = close.getBoundingClientRect();
        return { logo: [a.left, a.right], close: [b.left, b.right] };
      })()`);
      if (!r) return 'drawer logo or close button not found';
      if (r.logo[1] > r.close[0]) return `logo (right edge ${r.logo[1]}) overlaps the close button (left edge ${r.close[0]})`;
    });
    await page.send('Emulation.setDeviceMetricsOverride', { width: 1440, height: 1000, deviceScaleFactor: 1, mobile: false });

    await check(S, ['WEB-BRAND'], 'the favicon link serves an ICO (Vite-processed, so hashed in builds), and no SVG logo is left', async () => {
      const r = await page.eval<{ href: string; status: number; magic: number[]; svgs: number; apple: number }>(`(async () => {
        const href = document.querySelector('link[rel="icon"]').getAttribute('href');
        const res = await fetch(href);
        const buf = new Uint8Array(await res.arrayBuffer());
        const apple = (await fetch(document.querySelector('link[rel="apple-touch-icon"]').getAttribute('href'))).status;
        return { href, status: res.status, magic: [...buf.slice(0, 4)], svgs: document.querySelectorAll('svg[viewBox="0 0 32 32"] rect.fill-accent, link[type="image/svg+xml"]').length, apple };
      })()`);
      if (r.status !== 200) return `favicon ${r.href} → HTTP ${r.status}`;
      if (js(r.magic) !== js([0, 0, 1, 0])) return `favicon is not an ICO (first bytes ${js(r.magic)})`;
      if (!/favicon(-[\w-]+)?\.ico$/.test(r.href) || r.href === '/favicon.ico') return `favicon is not the Vite-processed asset: ${r.href}`;
      if (r.apple !== 200) return `apple-touch-icon → HTTP ${r.apple}`;
      if (r.svgs) return `${r.svgs} SVG logo element(s)/favicon link(s) still present`;
    });

    // sign-in page: sign out first (the mock starts signed in)
    await mock.api('POST', '/api/auth/logout');
    const loginReady = `!!document.querySelector('input[type="password"]')`;
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(theme, null, '/login', loginReady, `the sign-in page (${theme})`);
      await shot(page, `branding-login-${theme}`);
      await check(S, ['WEB-BRAND'], `sign-in page (${theme}): the ${theme} lockup is shown above the form, ≥ 80px tall`, oneLockup(theme, 80));
    }
  });
}

// ---------------------------------------------------------------- main

const started = new Date();
try {
  await trafficScenario();
  await hostsScenario();
  await settingsScenario();
  await dnsOnlyScenarios();
  await serversScenario();
  await brandingScenario();
} finally {
  mkdirSync(ARTIFACTS, { recursive: true });
  const failed = checks.filter((c) => c.result === 'fail').length;
  const report = {
    test: 'web-ui-e2e',
    utc: started.toISOString(),
    durationMs: Date.now() - started.getTime(),
    node: process.version,
    browser: 'headless Google Chrome (CDP)',
    timeZone: TZ,
    passed: checks.filter((c) => c.result === 'pass').length,
    failed,
    skipped: checks.filter((c) => c.result === 'skip').length,
    checks,
    screenshots: screenshots.map((s) => s.slice(ARTIFACTS.length + 1)),
  };
  const file = join(ARTIFACTS, 'report.json');
  writeFileSync(file, JSON.stringify(report, null, 2));
  console.log(`\n${report.passed} passed, ${failed} failed, ${report.skipped} skipped — ${file}`);
  process.exitCode = failed ? 1 : 0;
}
