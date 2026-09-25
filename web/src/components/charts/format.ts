// Number formatting for chart labels, tooltips and stat tiles. General helpers (formatBytes, formatNumber,
// formatCompact) live in lib/format.ts; these are the short forms that fit axes and tiles.
import { formatCompact, formatNumber } from '@/lib/format';

const UNITS = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'];

/** 1536 → "1.5 KB", 0 → "0 B" (binary units, like formatBytes but compact for axes and tiles). */
export function formatBytesShort(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes < 0) return '—';
  let v = bytes;
  let i = 0;
  while (v >= 1024 && i < UNITS.length - 1) {
    v /= 1024;
    i++;
  }
  if (i === 0) return `${Math.round(v)} B`;
  return `${v >= 100 ? v.toFixed(0) : v >= 10 ? v.toFixed(1).replace(/\.0$/, '') : v.toFixed(2).replace(/\.?0+$/, '')} ${UNITS[i]}`;
}

/** Bytes per second → "1.2 MB/s". */
export function formatByteRate(bytesPerSec: number): string {
  return `${formatBytesShort(bytesPerSec)}/s`;
}

/** 12.345 → "12.3%" (one decimal below 10, none above). */
export function formatPercent(v: number, digits?: number): string {
  if (!Number.isFinite(v)) return '—';
  const d = digits ?? (Math.abs(v) < 10 && v !== 0 ? 1 : 0);
  return `${v.toFixed(d)}%`;
}

/** Ratio (0..1) → percentage text with enough precision for small error rates ("0.04%"). */
export function formatRatio(r: number): string {
  if (!Number.isFinite(r)) return '—';
  const p = r * 100;
  if (p === 0) return '0%';
  if (p < 0.1) return `${p.toFixed(2)}%`;
  if (p < 10) return `${p.toFixed(1)}%`;
  return `${p.toFixed(0)}%`;
}

/** Milliseconds → "850 ms" / "1.24 s" / "0.4 ms". */
export function formatMs(ms: number): string {
  if (!Number.isFinite(ms) || ms < 0) return '—';
  if (ms < 1) return `${ms.toFixed(2)} ms`;
  if (ms < 10) return `${ms.toFixed(1)} ms`;
  if (ms < 1000) return `${Math.round(ms)} ms`;
  return `${(ms / 1000).toFixed(ms < 10_000 ? 2 : 1)} s`;
}

/** Counts: exact below 10,000, compact above ("12.9K"). */
export function formatCount(n: number): string {
  if (!Number.isFinite(n)) return '—';
  return Math.abs(n) < 10_000 ? formatNumber(Math.round(n)) : formatCompact(n);
}

/** Rates with up to one decimal ("3.4", "1,250"). */
export function formatRate(n: number): string {
  if (!Number.isFinite(n)) return '—';
  if (n === 0) return '0';
  if (Math.abs(n) < 10) return n.toFixed(1).replace(/\.0$/, '');
  return formatCount(n);
}
