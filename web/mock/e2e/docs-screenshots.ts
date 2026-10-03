// Screenshots for the product documentation (docs/images), taken from the real UI against the mock API in headless
// Google Chrome, in the light and the dark theme.
//
//   cd web && node mock/e2e/docs-screenshots.ts [name…]      (Node 22.18+ / 24; needs Google Chrome or CHROME=<path>)
//
// Writes docs/images/<name>.webp (light) and <name>.dark.webp (dark) for every shot below, their pixel sizes to
// docs/images/images.json (the docs viewer reserves the space, so deep links do not jump as images load), removes images no shot
// produces any more (full runs only), and reports sizes to $CPM_E2E_ARTIFACTS or mock/e2e/artifacts/docs-screenshots.json.
// The docs viewer shows the variant matching the reader's theme. Re-run after changing a screen the docs show.
//
// How it could fail, and the guard for each:
// - A tab, row, dialog or section is not found → the page helpers throw; the run stops and names the shot.
// - The capture happens before data or fonts load, or mid-animation → each shot waits for its own ready expression,
//   document.fonts.ready and settled animations.
// - The theme did not apply → asserted from the <html> class before capturing.
// - An empty or off-screen crop → the crop box is checked for size and position.
// - Focus rings, hover states, blinking carets or scrollbars in the image → focus is blurred, the mouse parked outside
//   the crop, carets and scrollbars hidden with an injected style.
// - Stale images after a shot is renamed or removed → a full run deletes every docs/images/*.webp it did not write.
// - Images bloat the bundle (they are embedded in CaddyManager.exe) → a shot over MAX_BYTES fails the run.
import { existsSync, mkdirSync, readdirSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { Browser, type Page, sleep } from './cdp.ts';
import { Mock, WEB } from './mock-server.ts';

const OUT = join(WEB, '..', 'docs', 'images');
const ARTIFACTS = process.env.CPM_E2E_ARTIFACTS || join(WEB, 'mock', 'e2e', 'artifacts');
const WIDTH = 1280;
const HEIGHT = 860;
const SCALE = 1.5;
const QUALITY = 82;
const MAX_BYTES = 350_000;

type Crop = 'window' | 'main' | 'dialog' | { section: string } | { css: string };
interface Shot {
  name: string;
  path: string;
  /** Mock mode: signed in as admin (default), signed out, or first-run setup. */
  env?: 'anon' | 'setup';
  /** Expression that is true once the page shows its data. */
  ready: string;
  /** Script run after the page is ready (may be async), e.g. opening a dialog or a tab. */
  act?: string;
  /** Expression that is true once `act` has taken effect. */
  actReady?: string;
  crop: Crop;
  /** Viewport height for tall sections. */
  height?: number;
}

const dialog = `!!document.querySelector('[aria-modal="true"]')`;
const openRow = (text: string) => `__e2e.one('tr', ${JSON.stringify(text)}).click()`;
const openButton = (text: string) => `__e2e.click('main button', ${JSON.stringify(text)})`;
const dialogTab = (tab: string) => `__e2e.click('[role="tab"]', ${JSON.stringify(tab)}, __e2e.dialog())`;
const pageTab = (tab: string) => `__e2e.click('main [role="tab"]', ${JSON.stringify(tab)})`;
const tabActive = (tab: string) =>
  `[...document.querySelectorAll('[role="tab"][aria-selected="true"]')].some((t) => __e2e.norm(t.textContent) === ${JSON.stringify(tab)})`;

const shots: Shot[] = [
  // Getting started
  { name: 'console-overview', path: '/', ready: `__e2e.has('Recent events')`, crop: 'window' },
  { name: 'sign-in', path: '/login', env: 'anon', ready: `!!document.querySelector('input[type="password"]')`, crop: { css: 'div.w-full.max-w-sm' } },
  { name: 'setup', path: '/setup', env: 'setup', ready: `__e2e.has('Setup token')`, crop: { css: 'div.w-full.max-w-sm' } },
  { name: 'dashboard', path: '/', ready: `__e2e.has('Recent events')`, crop: 'main' },
  // Sites
  { name: 'proxy-hosts-list', path: '/hosts/proxy', ready: `__e2e.has('shop.example.com')`, crop: 'main' },
  { name: 'proxy-host-details', path: '/hosts/proxy', ready: `__e2e.has('grafana.example.com')`, act: openRow('grafana.example.com'), actReady: `${dialog} && __e2e.has('Load balancing')`, crop: 'dialog' },
  { name: 'host-tls-tab', path: '/hosts/proxy', ready: `__e2e.has('app.example.com')`, act: `${openRow('app.example.com')}; await new Promise((r) => setTimeout(r, 300)); ${dialogTab('TLS')}`, actReady: tabActive('TLS'), crop: 'dialog' },
  { name: 'host-access-tab', path: '/hosts/proxy', ready: `__e2e.has('wiki.corp.example.com')`, act: `${openRow('wiki.corp.example.com')}; await new Promise((r) => setTimeout(r, 300)); ${dialogTab('Access')}`, actReady: tabActive('Access'), crop: 'dialog' },
  { name: 'host-locations-tab', path: '/hosts/proxy', ready: `__e2e.has('api.example.com')`, act: `${openRow('api.example.com')}; await new Promise((r) => setTimeout(r, 300)); ${dialogTab('Locations')}`, actReady: tabActive('Locations'), crop: 'dialog' },
  { name: 'host-advanced-tab', path: '/hosts/proxy', ready: `__e2e.has('api.example.com')`, act: `${openRow('api.example.com')}; await new Promise((r) => setTimeout(r, 300)); ${dialogTab('Advanced')}`, actReady: tabActive('Advanced'), crop: 'dialog' },
  { name: 'redirect-details', path: '/hosts/redirect', ready: `__e2e.has('old-shop.example.com')`, act: openRow('old-shop.example.com'), actReady: `${dialog} && __e2e.has('Target URL')`, crop: 'dialog' },
  { name: 'static-site-details', path: '/hosts/static', ready: `__e2e.has('docs.example.com')`, act: openRow('docs.example.com'), actReady: `${dialog} && __e2e.has('Root folder')`, crop: 'dialog' },
  { name: 'custom-response-details', path: '/hosts/response', ready: `__e2e.has('maintenance.example.com')`, act: openRow('maintenance.example.com'), actReady: `${dialog} && __e2e.has('Content type')`, crop: 'dialog' },
  { name: 'streams-list', path: '/streams', ready: `__e2e.has('RDP gateway')`, crop: 'main' },
  { name: 'stream-dialog', path: '/streams', ready: `__e2e.has('RDP gateway')`, act: openButton('Add stream'), actReady: `${dialog} && __e2e.has('Listen port')`, crop: 'dialog' },
  // Security
  { name: 'certificates-list', path: '/certificates', ready: `__e2e.has('Legacy intranet (Sectigo)')`, crop: 'main' },
  { name: 'add-certificate', path: '/certificates', ready: `__e2e.has('Legacy intranet (Sectigo)')`, act: `${openButton('Add certificate')}; await new Promise((r) => setTimeout(r, 300)); ${dialogTab('Windows store')}`, actReady: `${tabActive('Windows store')} && __e2e.has('Follow renewals by subject')`, crop: 'dialog' },
  { name: 'acme-settings', path: '/settings?tab=caddy', ready: `__e2e.has('Certificates (ACME)')`, crop: { section: 'Certificates (ACME)' } },
  { name: 'acme-challenge', path: '/settings?tab=caddy', ready: `__e2e.has('Default challenge')`, crop: { section: 'ACME challenge' }, height: 1300 },
  { name: 'access-lists-list', path: '/access-lists', ready: `__e2e.has('Office networks')`, crop: 'main' },
  { name: 'access-list-dialog', path: '/access-lists', ready: `__e2e.has('Contractors (VPN or login)')`, act: openRow('Contractors (VPN or login)'), actReady: `${dialog} && __e2e.has('Satisfy any')`, crop: 'dialog' },
  { name: 'users-list', path: '/users', ready: `__e2e.has('Priya Shah')`, crop: 'main' },
  { name: 'add-user', path: '/users', ready: `__e2e.has('Priya Shah')`, act: openButton('Add user'), actReady: `${dialog} && __e2e.has('At least 12 characters.')`, crop: 'dialog' },
  { name: 'directory-settings', path: '/settings?tab=ldap', ready: `__e2e.has('Role mapping')`, crop: 'main', height: 1300 },
  { name: 'management-ui-settings', path: '/settings?tab=ui', ready: `__e2e.has('About this installation')`, crop: 'main', height: 1100 },
  // Caddy
  { name: 'caddy-service', path: '/caddy/service', ready: `__e2e.has('Upstream health')`, crop: 'main' },
  { name: 'plugins', path: '/caddy/plugins', ready: `__e2e.has('Package catalog')`, crop: 'main' },
  { name: 'configuration', path: '/caddy/config', ready: `__e2e.has('caddy.json')`, crop: 'main' },
  { name: 'configuration-revisions', path: '/caddy/config', ready: `__e2e.has('caddy.json')`, act: pageTab('Revisions'), actReady: `${tabActive('Revisions')} && __e2e.has('Host updated: api.example.com')`, crop: 'main' },
  { name: 'import-caddyfile', path: '/caddy/config', ready: `__e2e.has('caddy.json')`, act: openButton('Import Caddyfile'), actReady: `${dialog} && __e2e.has('Import a Caddyfile')`, crop: 'dialog' },
  { name: 'caddy-settings-listeners', path: '/settings?tab=caddy', ready: `__e2e.has('Public HTTPS port')`, crop: { section: 'Listeners' } },
  { name: 'caddy-settings-unknown-hosts', path: '/settings?tab=caddy', ready: `__e2e.has('Default site')`, crop: { section: 'Unknown hosts' } },
  {
    name: 'caddy-settings-request-limits',
    path: '/settings?tab=caddy',
    ready: `__e2e.has('Request headers to keep')`,
    act: `(() => {
      __e2e.setLabel('Request header limit (KiB)', '64');
      __e2e.setLabel('Proxy-Status name', 'edge01.example.com');
      const el = __e2e.control('Request headers to keep');
      for (const name of ['SM_USER', 'webhook_*']) {
        __e2e.set(el, name);
        el.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
      }
    })()`,
    actReady: `__e2e.withText('span', 'webhook_*').length > 0`,
    crop: { section: 'Request limits and headers' },
  },
  // Monitoring
  { name: 'servers-list', path: '/servers', ready: `__e2e.has('WEB-PROXY03')`, crop: 'main' },
  { name: 'server-detail', path: '/servers/local', ready: `__e2e.has('Resource usage')`, crop: 'main' },
  { name: 'traffic', path: '/traffic', ready: `__e2e.has('Top hosts')`, crop: 'main' },
  { name: 'readiness', path: '/readiness', ready: `__e2e.has('Group Policy firewall script')`, crop: 'main' },
  { name: 'logs', path: '/logs', ready: `__e2e.has('tls.obtain')`, crop: 'main' },
  { name: 'events', path: '/events', ready: `__e2e.has('Kept for 90 days')`, crop: 'main' },
  { name: 'notifications', path: '/notifications', ready: `__e2e.has('Alert rules')`, crop: 'main' },
  { name: 'audit-log', path: '/audit', ready: `__e2e.has('entries')`, crop: 'main' },
  // Operations
  { name: 'backups', path: '/settings?tab=backup', ready: `__e2e.has('Backups on the server')`, crop: 'main', height: 1300 },
  { name: 'cluster-settings', path: '/settings?tab=cluster', ready: `__e2e.has('Shared Caddy storage')`, crop: 'main', height: 1100 },
  { name: 'add-server', path: '/servers', ready: `__e2e.has('WEB-PROXY03')`, act: openButton('Add server'), actReady: `${dialog} && __e2e.has('Management URL')`, crop: 'dialog' },
  { name: 'updates-settings', path: '/settings?tab=updates', ready: `__e2e.has('Outbound proxy')`, crop: 'main', height: 1100 },
];

// Hides carets and scrollbars; the capture must show the page, not the moment.
const CLEAN = `(() => {
  const s = document.createElement('style');
  s.textContent = '*{caret-color:transparent!important;scrollbar-width:none!important}*::-webkit-scrollbar{display:none!important}';
  document.head.appendChild(s);
  document.activeElement instanceof HTMLElement && document.activeElement.blur();
})()`;
const SETTLED = `document.getAnimations().every((a) => a.playState !== 'running')`;

function cropExpression(crop: Crop): string {
  const box = (el: string) => `(() => { const e = ${el}; if (!e) throw new Error('crop element not found'); const r = e.getBoundingClientRect(); return { x: r.left, y: r.top, width: r.width, height: r.height }; })()`;
  if (crop === 'window') return `({ x: 0, y: 0, width: innerWidth, height: innerHeight })`;
  if (crop === 'dialog') return box(`document.querySelector('[aria-modal="true"]')`);
  if (crop === 'main')
    // The visible part of the scroll panel, trimmed to its content when the page is short.
    return `(() => {
      const m = document.querySelector('main#main'); const c = m.firstElementChild;
      const r = m.getBoundingClientRect(); const bottom = Math.min(r.bottom, c.getBoundingClientRect().bottom + 20);
      return { x: r.left, y: r.top, width: r.width, height: bottom - r.top };
    })()`;
  if ('section' in crop)
    return box(`__e2e.all('section h3').find((h) => __e2e.norm(h.textContent) === ${JSON.stringify(crop.section)})?.closest('section')`);
  return box(`document.querySelector(${JSON.stringify(crop.css)})`);
}

const cropIsFree = (c: Crop) => c === 'window' || c === 'dialog';

async function capture(page: Page, base: string, shot: Shot, theme: 'light' | 'dark'): Promise<{ file: string; bytes: number; width: number; height: number }> {
  const height = shot.height ?? HEIGHT;
  await page.send('Emulation.setDeviceMetricsOverride', { width: WIDTH, height, deviceScaleFactor: SCALE, mobile: false });
  await page.eval(`localStorage.setItem('cpm.theme', ${JSON.stringify(theme)}); localStorage.removeItem('cpm.sidebar')`);
  await page.goto(base + shot.path, shot.ready, `${shot.name}: ${shot.path}`);
  await page.eval(`document.fonts.ready.then(() => true)`);
  if (shot.act) {
    await page.eval(`(async () => { ${shot.act}; })()`);
    await page.waitFor(`${shot.name}: after its action`, shot.actReady ?? 'true');
  }
  if (typeof shot.crop === 'object' && 'section' in shot.crop) {
    await page.eval(`__e2e.all('section h3').find((h) => __e2e.norm(h.textContent) === ${JSON.stringify(shot.crop.section)}).closest('section').scrollIntoView({ block: 'start' })`);
  }
  await page.eval(CLEAN);
  await page.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: WIDTH - 2, y: height - 2 });
  await page.waitFor(`${shot.name}: animations to settle`, SETTLED);
  await sleep(150);
  const dark = await page.eval<boolean>(`document.documentElement.classList.contains('dark')`);
  if (dark !== (theme === 'dark')) throw new Error(`${shot.name}: the ${theme} theme did not apply`);
  const r = await page.eval<{ x: number; y: number; width: number; height: number }>(cropExpression(shot.crop));
  // Sections and single elements get a margin of their surrounding surface, so text does not touch the edge.
  // Settings sections carry their own vertical padding (a margin there would show the next section's heading).
  const padX = typeof shot.crop === 'object' ? 20 : 0;
  const padY = typeof shot.crop === 'object' && 'css' in shot.crop ? 20 : 0;
  // Never reach outside the content panel (into the sidebar or the top bar); pages without one use the window.
  const area = await page.eval<{ left: number; top: number }>(
    `(() => { const m = document.querySelector('main#main'); const r = m ? m.getBoundingClientRect() : { left: 0, top: 0 }; return { left: r.left, top: r.top }; })()`,
  );
  const x = Math.max(cropIsFree(shot.crop) ? 0 : area.left, Math.floor(r.x - padX));
  const y = Math.max(cropIsFree(shot.crop) ? 0 : area.top, Math.floor(r.y - padY));
  const clip = { x, y, width: Math.min(WIDTH - x, Math.ceil(r.width + 2 * padX)), height: Math.min(height - y, Math.ceil(r.height + 2 * padY)) };
  if (clip.width < 200 || clip.height < 100) throw new Error(`${shot.name}: crop too small (${JSON.stringify(clip)})`);
  if (clip.x + clip.width > WIDTH + 1 || clip.y + clip.height > height + 1) throw new Error(`${shot.name}: crop outside the viewport (${JSON.stringify(clip)})`);
  const { data } = (await page.send('Page.captureScreenshot', { format: 'webp', quality: QUALITY, clip: { ...clip, scale: 1 } })) as { data: string };
  const file = join(OUT, `${shot.name}${theme === 'dark' ? '.dark' : ''}.webp`);
  writeFileSync(file, Buffer.from(data, 'base64'));
  const bytes = statSync(file).size;
  if (bytes > MAX_BYTES) throw new Error(`${shot.name} (${theme}): ${bytes} bytes, over the ${MAX_BYTES}-byte limit`);
  return { file, bytes, width: Math.round(clip.width * SCALE), height: Math.round(clip.height * SCALE) };
}

const only = process.argv.slice(2);
const selected = only.length ? shots.filter((s) => only.includes(s.name)) : shots;
if (only.length && selected.length !== only.length) throw new Error(`Unknown shot(s): ${only.filter((n) => !shots.some((s) => s.name === n)).join(', ')}`);
mkdirSync(OUT, { recursive: true });
// The docs viewer imports the size list; a first run starts without one.
if (!existsSync(join(OUT, 'images.json'))) writeFileSync(join(OUT, 'images.json'), '{}\n');
mkdirSync(ARTIFACTS, { recursive: true });

const written: { name: string; theme: string; bytes: number; width: number; height: number }[] = [];
const browser = await Browser.launch();
try {
  for (const env of [undefined, 'anon', 'setup'] as const) {
    const group = selected.filter((s) => s.env === env);
    if (group.length === 0) continue;
    const mock = await Mock.start(env === 'anon' ? { MOCK_ANON: '1' } : env === 'setup' ? { MOCK_SETUP: '1' } : {});
    const page = await browser.newPage({ timeZone: 'Europe/London', locale: 'en-GB', width: WIDTH, height: HEIGHT });
    try {
      // Any page of the origin, so the theme can be stored before each shot.
      await page.goto(`${mock.base}/login`, 'true', 'the mock server');
      for (const shot of group) {
        for (const theme of ['light', 'dark'] as const) {
          const r = await capture(page, mock.base, shot, theme);
          written.push({ name: shot.name, theme, bytes: r.bytes, width: r.width, height: r.height });
          console.log(`  ✓ ${shot.name}${theme === 'dark' ? '.dark' : ''}.webp  ${r.width}×${r.height}  ${(r.bytes / 1024).toFixed(0)} KB`);
        }
      }
    } finally {
      await page.close();
      mock.stop();
    }
  }
} finally {
  await browser.close();
}

if (!only.length) {
  const keep = new Set(written.map((w) => `${w.name}${w.theme === 'dark' ? '.dark' : ''}.webp`));
  for (const f of readdirSync(OUT)) {
    if (f.endsWith('.webp') && !keep.has(f)) {
      rmSync(join(OUT, f));
      console.log(`  − removed stale ${f}`);
    }
  }
}
// Pixel sizes of every image, merged with the previous run for partial runs; the viewer uses them for width/height.
const sizesFile = join(OUT, 'images.json');
const sizes: Record<string, [number, number]> = !only.length || !existsSync(sizesFile) ? {} : JSON.parse(readFileSync(sizesFile, 'utf8'));
for (const w of written) sizes[`${w.name}${w.theme === 'dark' ? '.dark' : ''}.webp`] = [w.width, w.height];
const sorted = Object.fromEntries(Object.entries(sizes).sort(([a], [b]) => a.localeCompare(b)));
writeFileSync(sizesFile, `{\n${Object.entries(sorted).map(([k, v]) => `  ${JSON.stringify(k)}: ${JSON.stringify(v)}`).join(',\n')}\n}\n`);
const total = written.reduce((n, w) => n + w.bytes, 0);
writeFileSync(join(ARTIFACTS, 'docs-screenshots.json'), JSON.stringify({ utc: new Date().toISOString(), scale: SCALE, quality: QUALITY, totalBytes: total, images: written }, null, 2));
console.log(`\n${written.length} images, ${(total / 1024 / 1024).toFixed(2)} MB → ${OUT}`);
