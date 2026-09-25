const dateTimeFmt = new Intl.DateTimeFormat(undefined, {
  year: 'numeric',
  month: 'short',
  day: '2-digit',
  hour: '2-digit',
  minute: '2-digit',
});
const dateFmt = new Intl.DateTimeFormat(undefined, { year: 'numeric', month: 'short', day: '2-digit' });
const timeFmt = new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit' });
const numberFmt = new Intl.NumberFormat();

function toDate(value: string | number | Date | null | undefined): Date | null {
  if (value === null || value === undefined || value === '') return null;
  const d = value instanceof Date ? value : new Date(value);
  return Number.isNaN(d.getTime()) || d.getUTCFullYear() < 1971 ? null : d;
}

export function formatDateTime(value: string | null | undefined): string {
  const d = toDate(value);
  return d ? dateTimeFmt.format(d) : '—';
}

export function formatDate(value: string | null | undefined): string {
  const d = toDate(value);
  return d ? dateFmt.format(d) : '—';
}

export function formatTime(value: string | null | undefined): string {
  const d = toDate(value);
  return d ? timeFmt.format(d) : '—';
}

export function formatNumber(n: number): string {
  return numberFmt.format(n);
}

/** "3 minutes ago" / "in 2 days". `now` is passed in so render stays pure. */
export function formatRelative(value: string | null | undefined, now: number): string {
  const d = toDate(value);
  if (!d) return '—';
  const diff = d.getTime() - now;
  const abs = Math.abs(diff);
  const rtf = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' });
  const units: [Intl.RelativeTimeFormatUnit, number][] = [
    ['year', 365 * 24 * 3600_000],
    ['month', 30 * 24 * 3600_000],
    ['day', 24 * 3600_000],
    ['hour', 3600_000],
    ['minute', 60_000],
  ];
  for (const [unit, ms] of units) {
    if (abs >= ms) return rtf.format(Math.round(diff / ms), unit);
  }
  return abs < 45_000 ? 'just now' : rtf.format(Math.round(diff / 1000), 'second');
}

export function formatDuration(totalSeconds: number): string {
  if (!Number.isFinite(totalSeconds) || totalSeconds < 0) return '—';
  const s = Math.floor(totalSeconds);
  const d = Math.floor(s / 86400);
  const h = Math.floor((s % 86400) / 3600);
  const m = Math.floor((s % 3600) / 60);
  if (d > 0) return `${d}d ${h}h`;
  if (h > 0) return `${h}h ${m}m`;
  if (m > 0) return `${m}m`;
  return `${s}s`;
}

export function formatCompact(n: number): string {
  return new Intl.NumberFormat(undefined, { notation: 'compact', maximumFractionDigits: 1 }).format(n);
}

export function pluralize(n: number, one: string, many = `${one}s`): string {
  return `${formatNumber(n)} ${n === 1 ? one : many}`;
}

export function upstreamUrl(u: { scheme: string; host: string; port: number }): string {
  const host = u.host.includes(':') && !u.host.startsWith('[') ? `[${u.host}]` : u.host;
  return `${u.scheme}://${host}:${u.port}`;
}
