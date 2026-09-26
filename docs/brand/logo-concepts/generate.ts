// Renders the logo with code (no SVG): node docs/brand/logo-concepts/generate.ts [set]  (Node 24; needs Google Chrome)
//
//   assets              → the chosen logo written into the repo: web UI images + favicon.ico, the exe's app.ico,
//                         installer bitmaps, README logos (see renderAssets in typographic.js)
//   teal                → typographic.js, chosen logo in the app's teal (round 6) → out/teal
//   compact             → typographic.js, compact lowercase hybrid (round 5) → out/compact
//   combo               → typographic.js, Handle × Channel variations (round 4) → out/combo
//   type (default)      → typographic.js, flat constructed wordmarks (round 3) → out/type
//
// Loads the script in headless Chrome (web/mock/e2e/cdp.ts) with the UI's fonts inlined, then writes every canvas the
// page produced. ICO and BMP files are encoded here from the raw pixels the page returns.
import { mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { Browser } from '../../../web/mock/e2e/cdp.ts';

const here = dirname(fileURLToPath(import.meta.url));
const repo = join(here, '../../..');
const set = process.argv[2] ?? 'type';
const script = 'typographic.js';
if (!['assets', 'teal', 'compact', 'combo', 'type'].includes(set)) throw new Error(`Unknown set "${set}" (expected assets, teal, compact, combo or type)`);
const fonts = join(repo, 'web/node_modules/@fontsource-variable');
const font = (file: string) => `data:font/woff2;base64,${readFileSync(join(fonts, file)).toString('base64')}`;

const html = `<!doctype html><meta charset="utf-8"><style>
@font-face { font-family: Inter; font-weight: 100 900; src: url(${font('inter/files/inter-latin-wght-normal.woff2')}) format('woff2'); }
@font-face { font-family: 'JetBrains Mono'; font-weight: 100 800; src: url(${font('jetbrains-mono/files/jetbrains-mono-latin-wght-normal.woff2')}) format('woff2'); }
</style><body><script>${readFileSync(join(here, script), 'utf8')}</script>`;

type Raw = { w: number; h: number; rgba: string };
type Asset = { type: 'png'; data: string } | { type: 'ico'; images: Raw[]; png256?: string } | ({ type: 'bmp' } & Raw);

const fromDataUrl = (url: string) => Buffer.from(url.slice(url.indexOf(',') + 1), 'base64');

/** 32-bit BGRA DIB, bottom-up, followed by an all-zero AND mask (transparency comes from alpha). */
function dib({ w, h, rgba }: Raw): Buffer {
  const px = Buffer.from(rgba, 'base64'), maskRow = Math.ceil(w / 32) * 4;
  const out = Buffer.alloc(40 + w * h * 4 + maskRow * h);
  out.writeUInt32LE(40, 0); out.writeInt32LE(w, 4); out.writeInt32LE(h * 2, 8);
  out.writeUInt16LE(1, 12); out.writeUInt16LE(32, 14); out.writeUInt32LE(w * h * 4 + maskRow * h, 20);
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
    const s = (y * w + x) * 4, d = 40 + ((h - 1 - y) * w + x) * 4;
    out[d] = px[s + 2]; out[d + 1] = px[s + 1]; out[d + 2] = px[s]; out[d + 3] = px[s + 3];
  }
  return out;
}
/** Windows .ico: DIB entries for the small sizes, a PNG-compressed 256×256 entry (Vista+). */
function ico(images: Raw[], png256?: string): Buffer {
  const entries = images.map((im) => ({ w: im.w, h: im.h, data: dib(im) }));
  if (png256) entries.push({ w: 256, h: 256, data: fromDataUrl(png256) });
  const head = Buffer.alloc(6 + 16 * entries.length);
  head.writeUInt16LE(0, 0); head.writeUInt16LE(1, 2); head.writeUInt16LE(entries.length, 4);
  let offset = head.length;
  entries.forEach((e, i) => {
    const o = 6 + i * 16;
    head[o] = e.w >= 256 ? 0 : e.w; head[o + 1] = e.h >= 256 ? 0 : e.h;
    head.writeUInt16LE(1, o + 4); head.writeUInt16LE(32, o + 6);
    head.writeUInt32LE(e.data.length, o + 8); head.writeUInt32LE(offset, o + 12);
    offset += e.data.length;
  });
  return Buffer.concat([head, ...entries.map((e) => e.data)]);
}
/** 24-bit bottom-up BMP (what Windows Installer's Bitmap controls expect). */
function bmp({ w, h, rgba }: Raw): Buffer {
  const px = Buffer.from(rgba, 'base64'), row = Math.ceil((w * 3) / 4) * 4, size = 54 + row * h;
  const out = Buffer.alloc(size);
  out.write('BM', 0); out.writeUInt32LE(size, 2); out.writeUInt32LE(54, 10);
  out.writeUInt32LE(40, 14); out.writeInt32LE(w, 18); out.writeInt32LE(h, 22);
  out.writeUInt16LE(1, 26); out.writeUInt16LE(24, 28); out.writeUInt32LE(row * h, 34);
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
    const s = (y * w + x) * 4, d = 54 + (h - 1 - y) * row + x * 3;
    out[d] = px[s + 2]; out[d + 1] = px[s + 1]; out[d + 2] = px[s];
  }
  return out;
}

const outDir = set === 'assets' ? repo : join(here, 'out', set);
const page_ = join(here, '.render.html');
mkdirSync(outDir, { recursive: true });
writeFileSync(page_, html);

const browser = await Browser.launch();
try {
  const page = await browser.newPage({ timeZone: 'UTC' });
  await page.send('Page.navigate', { url: `file://${page_}` });
  await page.waitFor('render page', `document.readyState === 'complete' && typeof window.renderAll === 'function'`, 30_000);
  const started = Date.now();
  const names = await page.eval<string[]>(set === 'assets' ? 'window.renderAssets()' : `window.renderAll(${JSON.stringify(set)})`);
  for (const name of names) {
    const file = join(outDir, name);
    mkdirSync(dirname(file), { recursive: true });
    if (set !== 'assets') {
      writeFileSync(file, fromDataUrl(await page.eval<string>(`window.__out[${JSON.stringify(name)}]`)));
      continue;
    }
    const a = await page.eval<Asset>(`window.__out[${JSON.stringify(name)}]`);
    writeFileSync(file, a.type === 'png' ? fromDataUrl(a.data) : a.type === 'ico' ? ico(a.images, a.png256) : bmp(a));
    console.log(`  ${name}`);
  }
  console.log(`Rendered ${names.length} images in ${((Date.now() - started) / 1000).toFixed(1)} s → ${outDir}`);
} finally {
  await browser.close();
  rmSync(page_, { force: true });
}
