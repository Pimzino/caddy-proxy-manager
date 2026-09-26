// Round 3: flat, typographic wordmarks for Caddy Proxy Manager. Runs in a browser page (see generate.ts).
// The letters are drawn, not typed: every glyph is constructed from geometry (arcs, stems, bands, cuts) as a signed
// distance field and rasterised flat with analytic anti-aliasing on <canvas>. Inter is used only for the small
// "Proxy Manager" line, so the wordmark pairs with the UI. No gloss, no bevels, no vector output.

const T = {
  bg: '#f6f7f9', surface: '#ffffff', surface2: '#f1f3f6', border: '#e1e5eb', fg: '#0f172a', fgMuted: '#475569',
  fgSubtle: '#64748b', dBg: '#0b1017', dSurface: '#111822', dBorder: '#243040', dFg: '#e5e9f0', dFgMuted: '#a3aebd',
  dFgSubtle: '#7b8798', dSidebar: '#0e141d',
};

const hex = (h) => [1, 3, 5].map((i) => parseInt(h.slice(i, i + 2), 16) / 255);
const clamp = (x, a = 0, b = 1) => (x < a ? a : x > b ? b : x);
const len = Math.hypot;
const TAU = Math.PI * 2;

// ------------------------------------------------------------------ SDF construction kit (units, y down)
const circle = (x, y, cx, cy, r) => len(x - cx, y - cy) - r;
const ring = (x, y, cx, cy, r, w) => Math.abs(len(x - cx, y - cy) - r) - w / 2; // r = centre line
const outline = (d, w) => Math.abs(d + w / 2) - w / 2; // inner stroke of any filled shape
const inter = (a, b) => Math.max(a, b);
const sub = (a, b) => Math.max(a, -b);
function box(x, y, x0, y0, x1, y1) {
  const qx = Math.abs(x - (x0 + x1) / 2) - (x1 - x0) / 2, qy = Math.abs(y - (y0 + y1) / 2) - (y1 - y0) / 2;
  return len(Math.max(qx, 0), Math.max(qy, 0)) + Math.min(Math.max(qx, qy), 0);
}
/** Box with a separate corner radius for the left and right side. */
function boxLR(x, y, x0, y0, x1, y1, rl, rr) {
  const cx = (x0 + x1) / 2, cy = (y0 + y1) / 2, r = x > cx ? rr : rl;
  const qx = Math.abs(x - cx) - (x1 - x0) / 2 + r, qy = Math.abs(y - cy) - (y1 - y0) / 2 + r;
  return len(Math.max(qx, 0), Math.max(qy, 0)) + Math.min(Math.max(qx, qy), 0) - r;
}
function capsule(x, y, ax, ay, bx, by, w) {
  const px = x - ax, py = y - ay, dx = bx - ax, dy = by - ay;
  const h = clamp((px * dx + py * dy) / (dx * dx + dy * dy));
  return len(px - dx * h, py - dy * h) - w / 2;
}
/** Straight stroke with square (butt) ends. */
function bar(x, y, ax, ay, bx, by, w) {
  const l = len(bx - ax, by - ay), ux = (bx - ax) / l, uy = (by - ay) / l;
  const qx = x - (ax + bx) / 2, qy = y - (ay + by) / 2;
  const rx = Math.abs(ux * qx + uy * qy) - l / 2, ry = Math.abs(-uy * qx + ux * qy) - w / 2;
  return len(Math.max(rx, 0), Math.max(ry, 0)) + Math.min(Math.max(rx, ry), 0);
}
/** Arc with round ends: centre (cx, cy), centre-line radius r, from angle a0 sweeping to a1 (radians, y down). */
function arc(x, y, cx, cy, r, a0, a1, w) {
  let t = Math.atan2(y - cy, x - cx) - a0;
  t = ((t % TAU) + TAU) % TAU;
  if (t <= a1 - a0) return Math.abs(len(x - cx, y - cy) - r) - w / 2;
  const e0 = len(x - cx - r * Math.cos(a0), y - cy - r * Math.sin(a0));
  const e1 = len(x - cx - r * Math.cos(a1), y - cy - r * Math.sin(a1));
  return Math.min(e0, e1) - w / 2;
}
/** Wedge with its apex at (cx, cy), pointing along angle `a`, half-angle `h` — for radial cuts. */
function wedge(x, y, cx, cy, a, h) {
  const px = x - cx, py = y - cy;
  const u = Math.cos(a) * px + Math.sin(a) * py, v = -Math.sin(a) * px + Math.cos(a) * py;
  return Math.max(Math.abs(v) * Math.cos(h) - u * Math.sin(h), -u);
}
/** Signed distance to a line through p→q; positive on the right-hand side of the direction p→q (y down). */
function side(x, y, px, py, qx, qy) {
  const l = len(qx - px, qy - py);
  return ((x - px) * (qy - py) - (y - py) * (qx - px)) / l;
}

// ------------------------------------------------------------------ concepts
// build({ fav }) → { parts: [{ d(x, y), ink: 'ink'|'accent' }], bounds: [x0, y0, x1, y1], text? }
// Units: lowercase x-height or cap height = 1; y = 0 at the top of it, y = 1 on the baseline.
const CONCEPTS = [
  // ---------------------------------------------------------------- 1. Handle
  {
    id: 'handle',
    name: 'Handle',
    blurb: 'A caddy is a carrier with a handle. The two d ascenders join into one: a monoline wordmark you could pick up.',
    accent: ['#2f54eb', '#7b96ff'],
    build({ fav, channel = false, exit = 'line', subPos = 'auto', t = 0.2, subColor = 'muted', w = 0.15, g = 0.075 }) {
      const r = 0.5 - w / 2, asc = -0.36;
      const parts = [], ink = [], acc = [];
      let ox = 0;
      const bowl = (o) => (x, y) => ring(x, y, o + 0.5, 0.5, r, w);
      const stem = (o, top, bottom = 1 - w / 2) => (x, y) => capsule(x, y, o + 1 - w / 2, top, o + 1 - w / 2, bottom, w);
      const letters = fav ? ['d', 'd'] : ['c', 'a', 'd', 'd', 'y'];
      const dStems = [];
      let yMid = 0;
      for (const ch of letters) {
        const o = ox;
        if (ch === 'c') { ink.push((x, y) => arc(x, y, o + 0.5, 0.5, r, 0.72, TAU - 0.72, w)); ox += 0.5 + r * Math.cos(0.72) + w / 2 + t; continue; }
        if (ch === 'a') { ink.push(bowl(o), stem(o, w / 2)); }
        if (ch === 'd') { ink.push(bowl(o), stem(o, asc)); dStems.push(o + 1 - w / 2); }
        if (ch === 'y') {
          yMid = o + 0.5;
          ink.push(
            (x, y) => capsule(x, y, o + w / 2, w / 2, o + w / 2, 0.5, w),
            (x, y) => arc(x, y, o + 0.5, 0.5, r, 0, Math.PI, w),
            (x, y) => capsule(x, y, o + 1 - w / 2, w / 2, o + 1 - w / 2, 1.08, w),
            (x, y) => arc(x, y, o + 0.5, 1.08, r, 0, Math.PI * 0.82, w),
          );
        }
        ox += 1 + t;
      }
      // the handle: one arch springing from both d stems
      const [s1, s2] = dStems, hr = (s2 - s1) / 2;
      acc.push((x, y) => arc(x, y, (s1 + s2) / 2, asc, hr, Math.PI, TAU, w));
      const right = ox - t;
      // optional: Channel's idea — one cut through every letter at mid x-height, leaving through the y
      const cut = channel ? (x, y) => box(x, y, -9, 0.5 - g / 2, 99, 0.5 + g / 2) : () => Infinity;
      parts.push({ d: (x, y) => sub(Math.min(...ink.map((f) => f(x, y))), cut(x, y)), ink: 'ink' });
      // exit: 'line' runs out to the text, 'stub' just pokes out of the y (the dart), 'none' leaves the cut alone
      const exitTo = { line: right + 0.5, stub: right + 0.3, none: null }[exit];
      if (channel && !fav && exitTo) acc.push((x, y) => capsule(x, y, yMid, 0.5, exitTo, 0.5, 0.045));
      parts.push({ d: (x, y) => Math.min(...acc.map((f) => f(x, y))), ink: 'accent' });
      // subPos: 'right' on the channel axis, 'under' below the descender, 'tuck' beside the y's descender
      const pos = subPos === 'auto' ? (channel ? 'right' : 'under') : subPos;
      const base = { str: 'Proxy Manager', cap: 0.3, weight: 500, tracking: 0.004, color: subColor };
      const text = fav ? null
        : pos === 'right' ? { ...base, x: right + 0.64, y: 0.5 + 0.15, extendBounds: true }
        : pos === 'tuck' ? { ...base, x: 0.02, y: 1.66 }
        : { ...base, x: 0.02, y: 2.05 };
      const bottom = fav ? 1.05 : pos === 'under' ? 2.12 : pos === 'tuck' ? 1.74 : 1.62;
      const rightEdge = right + 0.05 + (channel && exit === 'stub' && !fav ? 0.3 : 0);
      return { parts, bounds: [-0.05, asc - hr - w, rightEdge, bottom], text };
    },
  },

  // ---------------------------------------------------------------- 2. Channel
  {
    id: 'channel',
    name: 'Channel',
    blurb: 'Heavy stencil capitals whose breaks all line up: one clear channel runs through the whole name and leaves through the fork of the Y.',
    accent: ['#f0441a', '#ff6b43'],
    build({ fav, handle = false }) {
      const w = 0.26, g = 0.1, t = 0.1, ink = [], dStems = [];
      let ox = 0, yJoin = 0;
      const letters = fav ? (handle ? ['D', 'D'] : ['C']) : ['C', 'A', 'D', 'D', 'Y'];
      for (const ch of letters) {
        const o = ox;
        if (ch === 'C') {
          ink.push((x, y) => sub(ring(x, y, o + 0.5, 0.5, 0.5 - w / 2, w), wedge(x, y, o + 0.5, 0.5, 0, 0.7)));
          ox += 0.5 + 0.5 * Math.cos(0.7) + t;
          continue;
        }
        if (ch === 'A') {
          ink.push((x, y) => {
            // side() is positive inside both diagonals
            const l = side(x, y, o + 0.43, 0, o, 1), rr = side(x, y, o + 1, 1, o + 0.57, 0);
            const outer = Math.max(-l, -rr), inner = Math.max(w * 1.05 - l, w * 1.05 - rr, 0.5 + g / 2 - y);
            return inter(sub(outer, inner), box(x, y, o - 1, 0, o + 2, 1));
          });
          ox += 1 + t;
        }
        if (ch === 'D') { ink.push((x, y) => outline(boxLR(x, y, o, 0, o + 0.9, 1, 0, 0.5), w)); dStems.push(o + w / 2); ox += 0.9 + t; }
        if (ch === 'Y') {
          yJoin = o + 0.5;
          ink.push((x, y) => inter(Math.min(
            bar(x, y, o + 0.04, -0.15, o + 0.5, 0.58, w),
            bar(x, y, o + 0.96, -0.15, o + 0.5, 0.58, w),
            box(x, y, o + 0.5 - w / 2, 0.5, o + 0.5 + w / 2, 1),
          ), box(x, y, o - 1, 0, o + 2, 1)));
          ox += 1 + t;
        }
      }
      const channel = (x, y) => box(x, y, -9, 0.5 - g / 2, 99, 0.5 + g / 2);
      const right = ox - t;
      const lineFrom = fav ? 0.5 : yJoin, lineTo = fav ? 1.25 : right + 0.55;
      const favLine = fav && !handle;
      // optional: Handle's idea — a heavy arch springing from the two D stems
      const [s1, s2] = dStems, hr = (s2 - s1) / 2;
      const arch = handle ? (x, y) => inter(ring(x, y, (s1 + s2) / 2, 0, hr, w), box(x, y, -9, -9, 99, 0.001)) : () => Infinity;
      return {
        parts: [
          { d: (x, y) => sub(Math.min(...ink.map((f) => f(x, y))), channel(x, y)), ink: 'ink' },
          { d: (x, y) => Math.min(arch(x, y), fav && !favLine ? Infinity : box(x, y, lineFrom, 0.5 - 0.022, lineTo, 0.5 + 0.022)), ink: 'accent' },
        ],
        bounds: [-0.05, handle ? -hr - w / 2 - 0.05 : -0.05, fav ? (handle ? right + 0.05 : 1.3) : right + 0.6, 1.05],
        text: fav ? null : { str: 'PROXY MANAGER', x: right + 0.72, y: 0.5 + 0.12, cap: 0.24, weight: 650, tracking: 0.2, color: 'ink', extendBounds: true },
      };
    },
  },

  // ---------------------------------------------------------------- 3. Counters
  {
    id: 'counters',
    name: 'Counters',
    blurb: 'Bold geometric lowercase. The three closed counters, a · d · d, hold three lit dots: healthy upstreams, like the status dots in the app.',
    accent: ['#0fae6e', '#34d399'],
    build({ fav }) {
      const w = 0.3, R = 0.5, c = R - w, t = 0.07, ink = [], dots = [];
      let ox = 0;
      const letters = fav ? ['a'] : ['c', 'a', 'd', 'd', 'y'];
      const bowlStem = (o, top) => (x, y) => sub(Math.min(circle(x, y, o + 0.5, 0.5, R), box(x, y, o + 1 - w, top, o + 1, 1)), circle(x, y, o + 0.5, 0.5, c));
      for (const ch of letters) {
        const o = ox;
        if (ch === 'c') {
          ink.push((x, y) => sub(ring(x, y, o + 0.5, 0.5, R - w / 2, w), wedge(x, y, o + 0.5, 0.5, 0, 0.78)));
          ox += 0.5 + R * Math.cos(0.78) + t;
          continue;
        }
        if (ch === 'a' || ch === 'd') {
          ink.push(bowlStem(o, ch === 'a' ? 0 : -0.56));
          dots.push([o + 0.5, 0.5]);
        }
        if (ch === 'y') {
          ink.push((x, y) => Math.min(
            box(x, y, o, 0, o + w, 0.52),
            inter(ring(x, y, o + 0.5, 0.5, R - w / 2, w), box(x, y, o - 1, 0.48, o + 2, 2)),
            box(x, y, o + 1 - w, 0, o + 1, 1.22),
            inter(ring(x, y, o + 0.5, 1.2, R - w / 2, w), box(x, y, o + 0.22, 1.18, o + 2, 3)),
          ));
        }
        ox += 1 + t;
      }
      const right = ox - t;
      return {
        parts: [
          { d: (x, y) => Math.min(...ink.map((f) => f(x, y))), ink: 'ink' },
          { d: (x, y) => Math.min(...dots.map(([cx, cy]) => circle(x, y, cx, cy, c - 0.06))), ink: 'accent' },
        ],
        bounds: [-0.05, fav ? -0.05 : -0.6, right + 0.05, fav ? 1.05 : 1.75],
        text: fav ? null : { str: 'Proxy Manager', x: right + 0.14, y: 1, cap: 0.3, weight: 500, tracking: 0.004, color: 'muted', extendBounds: true },
      };
    },
  },

  // ---------------------------------------------------------------- 4. Node
  {
    id: 'node',
    name: 'Node',
    blurb: 'Tall, airy capitals drawn with one pen. The Y is what it looks like, a junction, and its centre is a node.',
    accent: ['#e8890c', '#fbbf24'],
    build({ fav }) {
      const w = 0.085, cw = 0.56, t = 0.24, ink = [];
      let ox = 0, node = null;
      const letters = fav ? ['Y'] : ['C', 'A', 'D', 'D', 'Y'];
      for (const ch of letters) {
        const o = ox;
        if (ch === 'C') ink.push((x, y) => sub(outline(boxLR(x, y, o, 0, o + cw, 1, cw / 2, cw / 2), w), box(x, y, o + cw * 0.55, 0.31, o + cw + 1, 0.69)));
        if (ch === 'A') ink.push((x, y) => Math.min(capsule(x, y, o + w / 2, 1 - w / 2, o + cw / 2, w / 2, w), capsule(x, y, o + cw / 2, w / 2, o + cw - w / 2, 1 - w / 2, w)));
        if (ch === 'D') ink.push((x, y) => outline(boxLR(x, y, o, 0, o + cw, 1, 0, cw / 2), w));
        if (ch === 'Y') {
          node = [o + cw / 2, 0.44];
          ink.push((x, y) => Math.min(
            capsule(x, y, o + w / 2, w / 2, node[0], node[1], w),
            capsule(x, y, o + cw - w / 2, w / 2, node[0], node[1], w),
            capsule(x, y, node[0], node[1], node[0], 1 - w / 2, w),
          ));
        }
        ox += cw + t;
      }
      const right = ox - t;
      return {
        parts: [
          { d: (x, y) => Math.min(...ink.map((f) => f(x, y))), ink: 'ink' },
          { d: (x, y) => circle(x, y, node[0], node[1], w * 1.35), ink: 'accent' },
        ],
        bounds: [-0.05, -0.05, right + 0.05, fav ? 1.05 : 1.42],
        text: fav ? null : { str: 'PROXY MANAGER', x: 0, y: 1.36, cap: 0.13, weight: 560, fit: right, color: 'muted' },
      };
    },
  },
];

// Round 4: Handle and Channel developed together — colour swaps and the two hybrids.
const byId = Object.fromEntries(CONCEPTS.map((c) => [c.id, c]));
const BLUE = byId.handle.accent, VERMILION = byId.channel.accent;
const COMBOS = [
  byId.handle,
  { ...byId.handle, id: 'handle-vermilion', name: 'Handle · vermilion', accent: VERMILION, blurb: 'Handle in Channel’s vermilion.' },
  byId.channel,
  { ...byId.channel, id: 'channel-blue', name: 'Channel · blue', accent: BLUE, blurb: 'Channel in Handle’s blue.' },
  {
    ...byId.handle, id: 'handle-cut', name: 'Handle + Channel (lowercase)', accent: VERMILION, opts: { channel: true },
    blurb: 'The monoline handle wordmark, with Channel’s single cut running through every letter and out of the y into “Proxy Manager”.',
  },
  {
    ...byId.channel, id: 'channel-handle', name: 'Channel + Handle (capitals)', accent: VERMILION, opts: { handle: true },
    blurb: 'The stencil capitals, carried by a heavy handle over the DD. The channel still runs through and out of the Y.',
  },
  {
    ...byId.handle, id: 'handle-cut-blue', name: 'Handle + Channel · blue', accent: BLUE, opts: { channel: true },
    blurb: 'The lowercase hybrid in Handle’s blue.',
  },
  {
    ...byId.channel, id: 'channel-handle-blue', name: 'Channel + Handle · blue', accent: BLUE, opts: { handle: true },
    blurb: 'The capitals hybrid in Handle’s blue.',
  },
];

// ------------------------------------------------------------------ rasteriser
function paintText(ctx, text, U, ox, oy, colors) {
  const size = (text.cap / 0.727) * U;
  ctx.font = `${text.weight} ${size}px Inter`;
  ctx.textBaseline = 'alphabetic';
  let spacing = (text.tracking ?? 0) * size;
  if (text.fit) {
    ctx.letterSpacing = '0px';
    const natural = ctx.measureText(text.str).width;
    spacing = (text.fit * U - natural) / (text.str.length - 1);
  }
  ctx.letterSpacing = `${spacing}px`;
  ctx.fillStyle = colors[text.color];
  ctx.fillText(text.str, (text.x - ox) * U, (text.y - oy) * U);
}
function textWidth(text, U) {
  const c = document.createElement('canvas').getContext('2d'), size = (text.cap / 0.727) * U;
  c.font = `${text.weight} ${size}px Inter`; c.letterSpacing = `${(text.tracking ?? 0) * size}px`;
  return c.measureText(text.str).width / U;
}

/** Rasterise a concept. `palette`: { ink, accent, muted, bg? } */
function raster(concept, { U = 220, fav = false, palette }) {
  const spec = concept.build({ fav, ...(concept.opts ?? {}) });
  let [x0, y0, x1, y1] = spec.bounds;
  if (spec.text?.extendBounds) x1 = Math.max(x1, spec.text.x + textWidth(spec.text, U) + 0.05);
  const pad = 0.12;
  x0 -= pad; y0 -= pad; x1 += pad; y1 += pad;
  const W = Math.ceil((x1 - x0) * U), H = Math.ceil((y1 - y0) * U);
  const c = document.createElement('canvas');
  c.width = W; c.height = H;
  const ctx = c.getContext('2d'), img = ctx.createImageData(W, H), px = img.data;
  const cols = spec.parts.map((p) => hex(palette[p.ink]));
  for (let j = 0; j < H; j++) {
    const y = y0 + (j + 0.5) / U;
    for (let i = 0; i < W; i++) {
      const x = x0 + (i + 0.5) / U;
      let r = 0, g = 0, b = 0, a = 0;
      spec.parts.forEach((p, n) => {
        const cov = clamp(0.5 - p.d(x, y) * U);
        if (cov <= 0) return;
        const col = cols[n];
        r = col[0] * cov + r * (1 - cov); g = col[1] * cov + g * (1 - cov); b = col[2] * cov + b * (1 - cov); a = cov + a * (1 - cov);
      });
      const k = (j * W + i) * 4;
      if (a > 0) { px[k] = (r / a) * 255; px[k + 1] = (g / a) * 255; px[k + 2] = (b / a) * 255; px[k + 3] = a * 255; }
    }
  }
  ctx.putImageData(img, 0, 0);
  if (spec.text) paintText(ctx, spec.text, U, x0, y0, palette);
  return c;
}
const paletteFor = (concept, dark) => ({
  ink: dark ? T.dFg : T.fg,
  muted: dark ? T.dFgMuted : T.fgMuted,
  accent: concept.accent[dark ? 1 : 0],
  // text in the accent colour uses the UI's accent-text token (lighter teal on dark for legibility)
  accentText: (concept.accentText ?? concept.accent)[dark ? 1 : 0],
});

/** Favicon: the concept's one-letter (or 'dd') form, flat on an ink tile. */
function favicon(concept) {
  const S = 512, c = document.createElement('canvas');
  c.width = c.height = S;
  const ctx = c.getContext('2d');
  ctx.fillStyle = T.fg;
  ctx.beginPath(); ctx.roundRect(0, 0, S, S, S * 0.1875); ctx.fill();
  const glyph = raster(concept, { U: 400, fav: true, palette: { ink: '#f8fafc', muted: '#cbd5e1', accent: concept.accent[1] } });
  ctx.imageSmoothingQuality = 'high';
  const k = Math.min((S * 0.7) / glyph.width, (S * 0.7) / glyph.height);
  ctx.drawImage(glyph, (S - glyph.width * k) / 2, (S - glyph.height * k) / 2, glyph.width * k, glyph.height * k);
  return c;
}

// ------------------------------------------------------------------ composition
function fit(src, w, h) {
  const k = Math.min(w / src.width, h / src.height);
  const tw = Math.max(1, Math.round(src.width * k)), th = Math.max(1, Math.round(src.height * k));
  let c = src;
  while (c.width / 2 >= tw) {
    const n = document.createElement('canvas');
    n.width = Math.round(c.width / 2); n.height = Math.round(c.height / 2);
    const x = n.getContext('2d'); x.imageSmoothingQuality = 'high'; x.drawImage(c, 0, 0, n.width, n.height);
    c = n;
  }
  const n = document.createElement('canvas');
  n.width = tw; n.height = th;
  const x = n.getContext('2d'); x.imageSmoothingQuality = 'high'; x.drawImage(c, 0, 0, tw, th);
  return n;
}
function drawFit(ctx, src, x, y, w, h, align = 'center') {
  const c = fit(src, w, h);
  ctx.drawImage(c, align === 'left' ? x : x + (w - c.width) / 2, y + (h - c.height) / 2);
}
function rounded(ctx, x, y, w, h, r, fill, stroke) {
  ctx.beginPath(); ctx.roundRect(x, y, w, h, r);
  if (fill) { ctx.fillStyle = fill; ctx.fill(); }
  if (stroke) { ctx.strokeStyle = stroke; ctx.lineWidth = 1; ctx.stroke(); }
}
function label(ctx, text, x, y, dark) {
  ctx.letterSpacing = '1.4px'; ctx.font = '600 12px Inter'; ctx.fillStyle = dark ? T.dFgSubtle : T.fgSubtle;
  ctx.fillText(text.toUpperCase(), x, y);
}

function board(concept, light, dark, icon) {
  const W = 1800, H = 1060, c = document.createElement('canvas');
  c.width = W; c.height = H;
  const ctx = c.getContext('2d');
  ctx.imageSmoothingQuality = 'high';
  ctx.fillStyle = T.bg; ctx.fillRect(0, 0, W, H);
  ctx.letterSpacing = '-0.4px'; ctx.font = '650 26px Inter'; ctx.fillStyle = T.fg; ctx.fillText(concept.name, 56, 72);
  ctx.letterSpacing = '0px'; ctx.font = '400 16px Inter'; ctx.fillStyle = T.fgMuted; ctx.fillText(concept.blurb, 56, 102);
  rounded(ctx, 56, 136, 836, 500, 8, T.surface, T.border);
  label(ctx, 'Light', 84, 172);
  drawFit(ctx, light, 120, 210, 708, 380);
  rounded(ctx, 908, 136, 836, 500, 8, T.dSurface, T.dBorder);
  label(ctx, 'Dark', 936, 172, true);
  drawFit(ctx, dark, 972, 210, 708, 380);

  const sidebar = (x, isDark) => {
    rounded(ctx, x, 668, 540, 236, 8, isDark ? T.dSidebar : T.surface, isDark ? T.dBorder : T.border);
    label(ctx, `Sidebar header · ${isDark ? 'dark' : 'light'} (2×)`, x + 28, 704, isDark);
    ctx.fillStyle = isDark ? T.dBorder : T.border; ctx.fillRect(x + 1, 872, 538, 2);
    drawFit(ctx, isDark ? dark : light, x + 32, 736, 420, 112, 'left');
  };
  sidebar(56, false);
  sidebar(620, true);

  rounded(ctx, 1184, 668, 560, 236, 8, T.surface, T.border);
  label(ctx, 'Favicon · 64 / 32 / 16 · 16px at 4×', 1212, 704);
  ctx.drawImage(fit(icon, 64, 64), 1212, 752);
  ctx.drawImage(fit(icon, 32, 32), 1300, 768);
  const f16 = fit(icon, 16, 16);
  ctx.drawImage(f16, 1356, 776);
  ctx.imageSmoothingEnabled = false; ctx.drawImage(f16, 1400, 752, 64, 64); ctx.imageSmoothingEnabled = true;
  rounded(ctx, 1492, 764, 224, 40, 8, T.surface2, T.border);
  ctx.drawImage(f16, 1506, 776);
  ctx.letterSpacing = '0px'; ctx.font = '450 13px Inter'; ctx.fillStyle = T.fg; ctx.fillText('Caddy Proxy Manager', 1530, 789);

  const [la, da] = concept.accent;
  ctx.font = '400 13px JetBrains Mono'; ctx.fillStyle = T.fgSubtle;
  ctx.fillText(`wordmark/${concept.id} · letters constructed from geometry, rasterised flat · ink #0f172a / #e5e9f0 · accent ${la} / ${da}`, 56, 1010);
  return c;
}

function overview(items) {
  const W = 1800, rowH = 280, H = rowH * items.length, c = document.createElement('canvas');
  c.width = W; c.height = H;
  const ctx = c.getContext('2d');
  ctx.imageSmoothingQuality = 'high';
  ctx.fillStyle = T.surface; ctx.fillRect(0, 0, W / 2, H);
  ctx.fillStyle = T.dBg; ctx.fillRect(W / 2, 0, W / 2, H);
  items.forEach(({ concept, light, dark }, n) => {
    const y = n * rowH;
    drawFit(ctx, light, 90, y + 50, W / 2 - 180, rowH - 100);
    drawFit(ctx, dark, W / 2 + 90, y + 50, W / 2 - 180, rowH - 100);
    ctx.letterSpacing = '1.4px'; ctx.font = '600 12px Inter';
    ctx.fillStyle = T.fgSubtle; ctx.fillText(`${concept.name} · ${(light.width / light.height).toFixed(1)} : 1`.toUpperCase(), 28, y + 32);
    ctx.fillStyle = T.dFgSubtle; ctx.fillText(concept.name.toUpperCase(), W / 2 + 28, y + 32);
    if (n) { ctx.fillStyle = T.border; ctx.fillRect(0, y, W / 2, 1); ctx.fillStyle = T.dBorder; ctx.fillRect(W / 2, y, W / 2, 1); }
  });
  return c;
}

// Round 5: the lowercase hybrid made compact — shorter (or no) exit line, "Proxy Manager" beneath, tighter tracking.
const HC = { ...byId.handle, accent: VERMILION };
const COMPACT = [
  { ...HC, id: 'wide', name: 'Previous · for comparison', opts: { channel: true }, blurb: 'Round 4 lowercase hybrid: cut, exit line, “Proxy Manager” on the line.' },
  { ...HC, id: 'stub-tuck', name: 'A · Dart + tucked', opts: { channel: true, exit: 'stub', subPos: 'tuck', t: 0.15 }, blurb: 'The cut leaves the y as a short dart; “Proxy Manager” tucks in under “cadd”, beside the y’s tail.' },
  { ...HC, id: 'cut-tuck', name: 'B · Cut only + tucked', opts: { channel: true, exit: 'none', subPos: 'tuck', t: 0.15 }, blurb: 'No exit line at all: the cut alone carries the idea. The most compact.' },
  { ...HC, id: 'stub-under', name: 'C · Dart + beneath', opts: { channel: true, exit: 'stub', subPos: 'under', t: 0.15 }, blurb: 'Dart out of the y; “Proxy Manager” on its own line below the whole word.' },
  { ...HC, id: 'stub-tuck-blue', name: 'A · blue', accent: BLUE, opts: { channel: true, exit: 'stub', subPos: 'tuck', t: 0.15 }, blurb: 'Version A in blue.' },
  { ...HC, id: 'cut-tuck-blue', name: 'B · blue', accent: BLUE, opts: { channel: true, exit: 'none', subPos: 'tuck', t: 0.15 }, blurb: 'Version B in blue.' },
];

// Round 6: the chosen logo (B · cut only + tucked) in the app's own teal (web/src/index.css --accent / --accent-text).
const TEAL = ['#0f766e', '#14b8a6'], TEAL_TEXT = ['#0f766e', '#2dd4bf'];
const B = { channel: true, exit: 'none', subPos: 'tuck', t: 0.15 };
const TEALS = [
  { ...HC, id: 'teal-ink', name: 'Teal handle · black “Proxy Manager”', accent: TEAL, opts: { ...B, subColor: 'ink' }, blurb: 'Handle in the app’s teal; “Proxy Manager” in the same ink as “caddy”.' },
  { ...HC, id: 'teal-teal', name: 'Teal handle · teal “Proxy Manager”', accent: TEAL, accentText: TEAL_TEXT, opts: { ...B, subColor: 'accentText' }, blurb: 'Handle and “Proxy Manager” both in the app’s teal.' },
  { ...HC, id: 'teal-muted', name: 'Teal handle · grey “Proxy Manager” (reference)', accent: TEAL, opts: B, blurb: 'The chosen layout as before, muted grey subtitle, for comparison.' },
];

// ------------------------------------------------------------------ product assets (the chosen logo: teal-teal)
// Everything the app, the exe, the installer and the README ship with. Returned as PNG data URLs or raw RGBA
// (for ICO/BMP, which generate.ts encodes).
const LOGO = TEALS.find((c) => c.id === 'teal-teal');
const rgba = (c) => {
  const d = c.getContext('2d').getImageData(0, 0, c.width, c.height).data;
  let bin = '';
  for (let i = 0; i < d.length; i += 0x8000) bin += String.fromCharCode(...d.subarray(i, i + 0x8000));
  return { w: c.width, h: c.height, rgba: btoa(bin) };
};
/**
 * The square mark (favicon, exe / installer icon, collapsed sidebar): "cpm" in the wordmark's letters, the p and m
 * uprights rising into the teal handle the way the d's do in the wordmark (option K of the 'cpm' set).
 */
function mark(S, { square = false } = {}) { return cpmTile(CPM_VARIANTS.find((v) => v.id === 'lc-stems'), S, { square }); }

/** The earlier "dd" mark, kept only so the 'cpm' comparison board can still show it. */
function ddMark(S, { square = false } = {}) {
  const c = document.createElement('canvas');
  c.width = c.height = S;
  const ctx = c.getContext('2d');
  ctx.fillStyle = T.fg;
  ctx.beginPath(); ctx.roundRect(0, 0, S, S, square ? 0 : S * 0.1875); ctx.fill();
  const small = S <= 20, w = S <= 32 ? 0.22 : S <= 64 ? 0.19 : 0.17;
  const glyphConcept = { ...LOGO, opts: { ...LOGO.opts, channel: !small, w, g: w * 0.5 } };
  const glyph = raster(glyphConcept, { U: 600, fav: true, palette: { ink: '#f8fafc', muted: '#cbd5e1', accent: '#14b8a6', accentText: '#2dd4bf' } });
  const box = S * (S <= 32 ? 0.86 : 0.78);
  ctx.imageSmoothingQuality = 'high';
  const g = fit(glyph, box, box);
  ctx.drawImage(g, Math.round((S - g.width) / 2), Math.round((S - g.height) / 2));
  return c;
}
function lockup(dark, U) { return raster(LOGO, { U, palette: paletteFor(LOGO, dark) }); }
function whiteCanvas(W, H) {
  const c = document.createElement('canvas');
  c.width = W; c.height = H;
  const x = c.getContext('2d'); x.fillStyle = '#ffffff'; x.fillRect(0, 0, W, H);
  return c;
}

window.renderAssets = async () => {
  await document.fonts.load('500 20px Inter');
  const out = {};
  const png = (path, c) => (out[path] = { type: 'png', data: c.toDataURL('image/png') });
  // web UI (imported through Vite so the files get content-hashed names; web/dist is embedded in the exe)
  png('web/src/assets/brand/logo-light.png', lockup(false, 110));
  png('web/src/assets/brand/logo-dark.png', lockup(true, 110));
  png('web/src/assets/brand/mark.png', mark(128));
  png('web/src/assets/brand/apple-touch-icon.png', mark(180, { square: true }));
  out['web/src/assets/brand/favicon.ico'] = { type: 'ico', images: [16, 32, 48].map((s) => rgba(mark(s))) };
  // Windows exe icon (ApplicationIcon) and the installer's Add/Remove Programs icon
  out['src/CaddyManager/app.ico'] = {
    type: 'ico',
    images: [16, 20, 24, 32, 40, 48, 64].map((s) => rgba(mark(s))),
    png256: mark(256).toDataURL('image/png'),
  };
  // installer wizard: 493×312 welcome/finish background (graphic in the left 164 px) and 493×58 top banner
  const dlg = whiteCanvas(493, 312), dx = dlg.getContext('2d');
  dx.fillStyle = T.fg; dx.fillRect(0, 0, 164, 312);
  dx.imageSmoothingQuality = 'high';
  const dl = fit(lockup(true, 110), 132, 200);
  dx.drawImage(dl, Math.round((164 - dl.width) / 2), 40);
  out['installer/assets/dialog.bmp'] = { type: 'bmp', ...rgba(dlg) };
  const ban = whiteCanvas(493, 58), bx = ban.getContext('2d');
  bx.drawImage(mark(40), 493 - 40 - 10, 9);
  out['installer/assets/banner.bmp'] = { type: 'bmp', ...rgba(ban) };
  // README
  png('docs/brand/logo-light.png', lockup(false, 170));
  png('docs/brand/logo-dark.png', lockup(true, 170));
  window.__out = out;
  return Object.keys(out);
};

// ------------------------------------------------------------------ 'cpm' set: options for a "CPM" square mark
// Monoline letters in the wordmark's construction (round caps, stroke w at x-height / cap height 1, the cut at mid
// height), teal accent, on an ink tile. K ('lc-stems') is the shipped mark (see mark()); the rest are the options.
function cpmLetters(letters, w, t) {
  const r = 0.5 - w / 2, ink = [], acc = [], stems = {};
  let ox = 0, bottom = 1;
  letters.forEach(([ch, teal], n) => {
    const o = ox, into = teal ? acc : ink;
    if (ch === 'c' || ch === 'C') {
      into.push((x, y) => arc(x, y, o + 0.5, 0.5, r, 0.72, TAU - 0.72, w));
      ox += 0.5 + r * Math.cos(0.72) + w / 2;
    }
    if (ch === 'p') {
      stems.p = o + w / 2;
      into.push((x, y) => ring(x, y, o + 0.5, 0.5, r, w), (x, y) => capsule(x, y, o + w / 2, w / 2, o + w / 2, 1.42, w));
      bottom = Math.max(bottom, 1.42 + w / 2); ox += 1;
    }
    if (ch === 'm') {
      const a = 0.27, x0 = o + w / 2, x1 = x0 + 2 * a, x2 = x1 + 2 * a, cy = w / 2 + a;
      stems.mLeft = x0; stems.mRight = x2;
      into.push(
        (x, y) => capsule(x, y, x0, w / 2, x0, 1 - w / 2, w),
        (x, y) => arc(x, y, x0 + a, cy, a, Math.PI, TAU, w),
        (x, y) => arc(x, y, x1 + a, cy, a, Math.PI, TAU, w),
        (x, y) => capsule(x, y, x1, cy, x1, 1 - w / 2, w),
        (x, y) => capsule(x, y, x2, cy, x2, 1 - w / 2, w),
      );
      ox += 4 * a + w;
    }
    if (ch === 'P') {
      const bb = 0.58, br = (bb - w / 2) / 2, bx = 0.66 - w / 2 - br, cy = w / 2 + br;
      into.push(
        (x, y) => capsule(x, y, o + w / 2, w / 2, o + w / 2, 1 - w / 2, w),
        (x, y) => capsule(x, y, o + w / 2, w / 2, o + bx, w / 2, w),
        (x, y) => capsule(x, y, o + w / 2, bb, o + bx, bb, w),
        (x, y) => arc(x, y, o + bx, cy, br, -Math.PI / 2, Math.PI / 2, w),
      );
      ox += bx + br + w / 2;
    }
    if (ch === 'M') {
      const mw = 0.92, pts = [[w / 2, 1 - w / 2], [w / 2, w / 2], [mw / 2, 0.66], [mw - w / 2, w / 2], [mw - w / 2, 1 - w / 2]];
      for (let i = 1; i < pts.length; i++) {
        const [ax, ay] = pts[i - 1], [bx, by] = pts[i];
        into.push((x, y) => capsule(x, y, o + ax, ay, o + bx, by, w));
      }
      ox += mw;
    }
    // condensed capitals: stadium-shaped C, D-bowl P, narrow M (about half the width of the round forms)
    if (ch === 'Cn') {
      const cw = 0.6;
      into.push((x, y) => sub(outline(boxLR(x, y, o, 0, o + cw, 1, cw / 2, cw / 2), w), box(x, y, o + cw * 0.5, 0.32, o + cw + 1, 0.68)));
      ox += cw;
    }
    if (ch === 'Pn') {
      const cw = 0.56, bb = 0.6;
      stems.p = o + w / 2;
      into.push(
        (x, y) => capsule(x, y, o + w / 2, w / 2, o + w / 2, 1 - w / 2, w),
        (x, y) => outline(boxLR(x, y, o, 0, o + cw, bb, 0, bb / 2), w),
      );
      ox += cw;
    }
    if (ch === 'Mn') {
      const mw = 0.7, pts = [[w / 2, 1 - w / 2], [w / 2, w / 2], [mw / 2, 0.64], [mw - w / 2, w / 2], [mw - w / 2, 1 - w / 2]];
      stems.mLeft = o + w / 2; stems.mRight = o + mw - w / 2;
      for (let i = 1; i < pts.length; i++) {
        const [ax, ay] = pts[i - 1], [bx, by] = pts[i];
        into.push((x, y) => capsule(x, y, o + ax, ay, o + bx, by, w));
      }
      ox += mw;
    }
    if (n < letters.length - 1) ox += t;
  });
  return { ink, acc, stems, right: ox, bottom };
}
/** One option: letters [[char, teal?]], plus a teal handle over the letters and/or a teal channel in the cut. */
function cpmVariant(id, name, note, letters, { handle = false, channel = false, stemHandle = null, asc = -0.3, t = 0.16 } = {}) {
  return {
    id, name, note,
    build({ w = 0.17, cut = true }) {
      const L = cpmLetters(letters, w, t), g = w * 0.55;
      const band = (x, y) => box(x, y, -9, 0.5 - g / 2, 99, 0.5 + g / 2);
      const ink = [...L.ink], handles = [];
      let top = 0;
      if (stemHandle) {
        // like the wordmark's d ascenders: the P stem and one M stem rise past cap height and meet in one teal arch
        const s1 = L.stems.p, s2 = stemHandle === 'right' ? L.stems.mRight : L.stems.mLeft, hr = (s2 - s1) / 2;
        ink.push((x, y) => capsule(x, y, s1, w / 2, s1, asc, w), (x, y) => capsule(x, y, s2, w / 2, s2, asc, w));
        handles.push((x, y) => arc(x, y, (s1 + s2) / 2, asc, hr, Math.PI, TAU, w));
        top = asc - hr - w / 2;
      } else if (handle) {
        const cx = L.right / 2, hr = L.right * 0.26, cy = -0.12;
        handles.push((x, y) => arc(x, y, cx, cy, hr, Math.PI, TAU, w));
        top = cy - hr - w / 2;
      }
      const minOf = (fs) => (fs.length ? (x, y) => Math.min(...fs.map((f) => f(x, y))) : () => Infinity);
      const inkD = minOf(ink), accLetters = minOf(L.acc), handleD = minOf(handles);
      const parts = [
        { d: (x, y) => (cut ? sub(inkD(x, y), band(x, y)) : inkD(x, y)), ink: 'ink' },
        // teal letters take the cut; the handle sits above it
        { d: (x, y) => Math.min(cut ? sub(accLetters(x, y), band(x, y)) : accLetters(x, y), handleD(x, y)), ink: 'accent' },
      ];
      if (channel && cut) parts.push({ d: (x, y) => box(x, y, -0.08, 0.5 - g * 0.22, L.right + 0.08, 0.5 + g * 0.22), ink: 'accent' });
      return { parts, bounds: [-0.1, Math.min(top, -0.02) - 0.02, L.right + 0.1, L.bottom + 0.02] };
    },
  };
}
const CPM_VARIANTS = [
  cpmVariant('lc-stems', 'K · cpm, handle from p and m uprights', 'p and m uprights rise slightly and meet in a teal arch', [['c'], ['p'], ['m']], { stemHandle: 'left', asc: -0.2 }),
  cpmVariant('lc-stems-dd', 'K2 · as K, dd ascender height', 'uprights rise as high as the dd ascenders', [['c'], ['p'], ['m']], { stemHandle: 'left', asc: -0.36 }),
  cpmVariant('lc-handle', 'A · cpm + handle', 'lowercase, cut, teal carry handle over the letters', [['c'], ['p'], ['m']], { handle: true }),
  cpmVariant('lc-teal-c', 'B · cpm, teal c', 'lowercase, cut, the c (for caddy) in teal', [['c', true], ['p'], ['m']]),
  cpmVariant('uc-teal-c', 'C · CPM, teal C', 'capitals, cut, the C in teal', [['C', true], ['P'], ['M']], { t: 0.12 }),
  cpmVariant('uc-handle', 'D · CPM + handle', 'capitals, cut, teal carry handle', [['C'], ['P'], ['M']], { handle: true, t: 0.12 }),
  cpmVariant('lc-channel', 'E · cpm + teal channel', 'lowercase, the cut carries a thin teal line', [['c'], ['p'], ['m']], { channel: true }),
  cpmVariant('cn-teal-c', 'F · CPM condensed, teal C', 'narrow capitals, cut, teal C (≈2× letter height)', [['Cn', true], ['Pn'], ['Mn']], { t: 0.13 }),
  cpmVariant('cn-handle', 'G · CPM condensed + handle', 'narrow capitals, cut, teal carry handle', [['Cn'], ['Pn'], ['Mn']], { handle: true, t: 0.13 }),
];
const CPM_PALETTE = { ink: '#f8fafc', muted: '#cbd5e1', accent: '#14b8a6', accentText: '#2dd4bf' };
/** Square mark for an option: heavier stroke at small sizes, no cut at ≤ 20 px. `square` fills the whole tile. */
function cpmTile(v, S, { square = false } = {}) {
  const c = document.createElement('canvas');
  c.width = c.height = S;
  const ctx = c.getContext('2d');
  ctx.fillStyle = T.fg;
  ctx.beginPath(); ctx.roundRect(0, 0, S, S, square ? 0 : S * 0.1875); ctx.fill();
  const w = S <= 20 ? 0.26 : S <= 32 ? 0.22 : S <= 64 ? 0.19 : 0.16;
  const boxS = S * (S <= 32 ? 0.9 : 0.8);
  // ~2× supersampled for the tile size (glyphs are about 3.6 units wide)
  const glyph = raster({ build: () => v.build({ w, cut: S > 20 }) }, { U: Math.max(40, (boxS * 2) / 2.0), palette: CPM_PALETTE });
  ctx.imageSmoothingQuality = 'high';
  const g = fit(glyph, boxS, boxS);
  ctx.drawImage(g, Math.round((S - g.width) / 2), Math.round((S - g.height) / 2));
  return c;
}
function cpmBoard(rows) {
  const W = 1800, head = 110, rowH = 200, H = head + rowH * rows.length + 40, c = document.createElement('canvas');
  c.width = W; c.height = H;
  const ctx = c.getContext('2d');
  ctx.imageSmoothingQuality = 'high';
  ctx.fillStyle = T.bg; ctx.fillRect(0, 0, W / 2, H);
  ctx.fillStyle = T.dBg; ctx.fillRect(W / 2, 0, W / 2, H);
  ctx.letterSpacing = '-0.4px'; ctx.font = '650 24px Inter'; ctx.fillStyle = T.fg;
  ctx.fillText('Square mark options: "CPM"', 40, 52);
  ctx.letterSpacing = '0px'; ctx.font = '400 14px Inter'; ctx.fillStyle = T.fgMuted;
  ctx.fillText('Each at 512 (shown 150), 64, 32, 16 and 16 px at 4× · light background left, dark right · the ≤ 20 px sizes drop the cut', 40, 80);
  const cols = (x0, dark) => {
    ctx.letterSpacing = '1.2px'; ctx.font = '600 11px Inter'; ctx.fillStyle = dark ? T.dFgSubtle : T.fgSubtle;
    [['512', 0], ['64', 190], ['32', 290], ['16', 360], ['16 @ 4×', 420]].forEach(([t, dx]) => ctx.fillText(t, x0 + dx, head - 8));
  };
  cols(40, false); cols(W / 2 + 40, true);
  rows.forEach(({ v, tiles }, n) => {
    const y = head + n * rowH + 20;
    for (const [x0, dark] of [[40, false], [W / 2 + 40, true]]) {
      ctx.drawImage(fit(tiles[512], 150, 150), x0, y);
      ctx.drawImage(tiles[64], x0 + 190, y + 43);
      ctx.drawImage(tiles[32], x0 + 290, y + 59);
      ctx.drawImage(tiles[16], x0 + 360, y + 67);
      ctx.imageSmoothingEnabled = false; ctx.drawImage(tiles[16], x0 + 420, y + 43, 64, 64); ctx.imageSmoothingEnabled = true;
      ctx.letterSpacing = '-0.2px'; ctx.font = '600 16px Inter'; ctx.fillStyle = dark ? T.dFg : T.fg;
      ctx.fillText(v.name, x0 + 520, y + 64);
      ctx.letterSpacing = '0px'; ctx.font = '400 13px Inter'; ctx.fillStyle = dark ? T.dFgMuted : T.fgMuted;
      ctx.fillText(v.note, x0 + 520, y + 88);
    }
  });
  return c;
}
window.renderCpm = async () => {
  await document.fonts.load('600 20px Inter');
  const out = {}, rows = [];
  const all = [...CPM_VARIANTS, { id: 'previous-dd', name: 'Previous · dd + handle', note: 'the earlier mark, for comparison', tile: (S) => ddMark(S) }];
  for (const v of all) {
    const tiles = {};
    for (const S of [512, 64, 32, 24, 20, 16]) tiles[S] = v.tile ? v.tile(S) : cpmTile(v, S);
    rows.push({ v, tiles });
    for (const S of [512, 64, 32, 24, 20, 16]) out[`${v.id}/mark-${S}.png`] = tiles[S].toDataURL('image/png');
  }
  out['overview.png'] = cpmBoard(rows).toDataURL('image/png');
  window.__out = out;
  return Object.keys(out);
};

window.renderAll = async (set = 'type') => {
  if (set === 'cpm') return window.renderCpm();
  const list = { combo: COMBOS, compact: COMPACT, teal: TEALS }[set] ?? CONCEPTS;
  await document.fonts.load('600 20px Inter');
  await document.fonts.load('400 20px JetBrains Mono');
  const out = {}, items = [];
  for (const concept of list) {
    const light = raster(concept, { palette: paletteFor(concept, false) });
    const dark = raster(concept, { palette: paletteFor(concept, true) });
    const icon = favicon(concept);
    items.push({ concept, light, dark });
    out[`${concept.id}/wordmark-light.png`] = light.toDataURL('image/png');
    out[`${concept.id}/wordmark-dark.png`] = dark.toDataURL('image/png');
    out[`${concept.id}/icon-256.png`] = fit(icon, 256, 256).toDataURL('image/png');
    for (const s of [64, 32, 16]) out[`${concept.id}/favicon-${s}.png`] = fit(icon, s, s).toDataURL('image/png');
    out[`${concept.id}/board.png`] = board(concept, light, dark, icon).toDataURL('image/png');
  }
  out['overview.png'] = overview(items).toDataURL('image/png');
  window.__out = out;
  return Object.keys(out);
};
