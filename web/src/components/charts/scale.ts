// Scale and tick helpers for the hand-written SVG charts (no chart library).

export type Scale = (v: number) => number;

/** Linear mapping of `domain` onto `range` (a zero-width domain maps to the range start). */
export function linearScale([d0, d1]: [number, number], [r0, r1]: [number, number]): Scale {
  const span = d1 - d0;
  if (span === 0) return () => r0;
  return (v) => r0 + ((v - d0) / span) * (r1 - r0);
}

/**
 * A "nice" step (1, 2, 2.5 or 5 × 10^n) that splits `span` into about `count` intervals. With `integer` (counts) the
 * step is a whole number: at least 1 and never 2.5 (1, 2 or 5 × 10^n), so no tick falls between two integers.
 */
function niceStep(span: number, count: number, integer = false): number {
  const raw = span / Math.max(1, count);
  if (integer && raw <= 1) return 1;
  const pow = 10 ** Math.floor(Math.log10(raw));
  const f = raw / pow;
  const nice = f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 && !integer ? 2.5 : f <= 5 ? 5 : 10;
  return nice * pow;
}

/**
 * Ticks from 0 to a rounded maximum ≥ `max` (value axes of these charts always start at zero). `integer` keeps every
 * tick a whole number, for axes whose labels are rounded to integers (request counts, connections).
 */
export function valueTicks(max: number, count = 4, integer = false): { ticks: number[]; top: number } {
  if (!Number.isFinite(max) || max <= 0) return { ticks: [0, 1], top: 1 };
  const step = niceStep(max, count, integer);
  const top = Math.ceil(max / step - 1e-9) * step;
  const ticks: number[] = [];
  for (let v = 0; v <= top + step / 2; v += step) ticks.push(Number(v.toPrecision(12)));
  return { ticks, top };
}

/** Like valueTicks, but for byte quantities: steps are powers of two times 1, 2 or 5 of a binary unit. */
export function byteTicks(max: number, count = 4): { ticks: number[]; top: number } {
  if (!Number.isFinite(max) || max <= 0) return { ticks: [0, 1024], top: 1024 };
  let unit = 1;
  while (max / unit >= 1024 && unit < 1024 ** 4) unit *= 1024;
  const { ticks, top } = valueTicks(max / unit, count);
  return { ticks: ticks.map((t) => t * unit), top: top * unit };
}

const MIN = 60_000;
const HOUR = 60 * MIN;
const DAY = 24 * HOUR;
const TIME_STEPS = [10_000, 30_000, MIN, 2 * MIN, 5 * MIN, 10 * MIN, 15 * MIN, 30 * MIN, HOUR, 2 * HOUR, 3 * HOUR, 6 * HOUR, 12 * HOUR, DAY, 2 * DAY, 7 * DAY];

/**
 * Time ticks for the x axis: a step from TIME_STEPS so that ticks are at least `minGapPx` apart, aligned to wall-clock
 * boundaries: hours and days of the browser's time zone, or of UTC with `utc` (data bucketed by UTC day, whose buckets
 * start at 00:00 UTC).
 */
export function timeTicks(from: number, to: number, widthPx: number, minGapPx = 84, utc = false): { ticks: number[]; step: number } {
  const span = to - from;
  if (span <= 0 || widthPx <= 0) return { ticks: [], step: MIN };
  const maxTicks = Math.max(2, Math.floor(widthPx / minGapPx));
  const step = TIME_STEPS.find((s) => span / s <= maxTicks) ?? Math.ceil(span / maxTicks / DAY) * DAY;
  const ticks: number[] = [];
  if (step >= DAY) {
    const days = step / DAY;
    const d = new Date(from);
    if (utc) {
      d.setUTCHours(0, 0, 0, 0);
      if (d.getTime() < from) d.setUTCDate(d.getUTCDate() + 1);
      for (; d.getTime() <= to; d.setUTCDate(d.getUTCDate() + days)) ticks.push(d.getTime());
    } else {
      // Local midnight (handles DST shifts better than fixed 24 h arithmetic).
      d.setHours(0, 0, 0, 0);
      if (d.getTime() < from) d.setDate(d.getDate() + 1);
      for (; d.getTime() <= to; d.setDate(d.getDate() + days)) ticks.push(d.getTime());
    }
    return { ticks, step };
  }
  const offset = utc ? 0 : new Date(from).getTimezoneOffset() * MIN; // align hours to local time
  for (let t = Math.ceil((from - offset) / step) * step + offset; t <= to; t += step) ticks.push(t);
  return { ticks, step };
}

const hm = new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit' });
const hms = new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit' });
const md = new Intl.DateTimeFormat(undefined, { month: 'short', day: 'numeric' });
const hmUtc = new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit', timeZone: 'UTC' });
const mdUtc = new Intl.DateTimeFormat(undefined, { month: 'short', day: 'numeric', timeZone: 'UTC' });

/** Label for an x-axis tick at the given step size (`utc`: a tick placed by timeTicks(…, utc), labelled in UTC). */
export function formatTimeTick(ms: number, step: number, utc = false): string {
  const date = utc ? mdUtc : md;
  if (step >= DAY) return date.format(ms);
  const d = new Date(ms);
  const midnight = utc ? d.getUTCHours() === 0 && d.getUTCMinutes() === 0 : d.getHours() === 0 && d.getMinutes() === 0;
  if (step >= HOUR && midnight) return date.format(ms);
  if (utc) return hmUtc.format(ms);
  return step < MIN ? hms.format(ms) : hm.format(ms);
}

/** Index of the entry of the ascending array `xs` nearest to `v`. */
export function nearestIndex(xs: number[], v: number): number {
  if (xs.length === 0) return -1;
  let lo = 0;
  let hi = xs.length - 1;
  while (hi - lo > 1) {
    const mid = (lo + hi) >> 1;
    if (xs[mid] < v) lo = mid;
    else hi = mid;
  }
  return Math.abs(xs[lo] - v) <= Math.abs(xs[hi] - v) ? lo : hi;
}

/** Rough rendered width of an 11px label (used to size margins without measuring the DOM). */
export function labelWidth(text: string): number {
  return Math.ceil(text.length * 6.4) + 2;
}
