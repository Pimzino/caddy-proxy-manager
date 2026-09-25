// Meter: a thin bar whose fill carries severity (accent → warning → danger); the track is a light step
// of the same colour so the state reads across the whole bar. Always paired with a text value.
import { cn } from '@/lib/cn';
import './charts.css';

export type MeterLevel = 'normal' | 'warning' | 'danger';

const fills: Record<MeterLevel, string> = {
  normal: 'var(--accent)',
  warning: 'var(--warning)',
  danger: 'var(--danger)',
};

export function meterLevel(percent: number, warnAt = 80, dangerAt = 92): MeterLevel {
  return percent >= dangerAt ? 'danger' : percent >= warnAt ? 'warning' : 'normal';
}

export function Meter({
  value,
  max = 100,
  label,
  valueText,
  warnAt = 80,
  dangerAt = 92,
  size = 'md',
  className,
}: {
  value: number;
  max?: number;
  /** Accessible name ("CPU", "Disk C:"). */
  label: string;
  /** Human-readable value for assistive technology ("62% — 5.1 of 8 GB"). */
  valueText?: string;
  /** Thresholds in percent of max. */
  warnAt?: number;
  dangerAt?: number;
  size?: 'sm' | 'md';
  className?: string;
}) {
  const pct = max > 0 ? Math.max(0, Math.min(100, (value / max) * 100)) : 0;
  const level = meterLevel(pct, warnAt, dangerAt);
  const fill = fills[level];
  return (
    <div
      role="meter"
      aria-label={label}
      aria-valuemin={0}
      aria-valuemax={max}
      aria-valuenow={Math.round(value * 100) / 100}
      aria-valuetext={valueText ?? `${Math.round(pct)}%`}
      className={cn('w-full overflow-hidden rounded-full', size === 'sm' ? 'h-1.5' : 'h-2', className)}
      style={{ background: `color-mix(in srgb, ${fill} 18%, transparent)` }}
    >
      <div className="h-full rounded-full transition-[width] duration-300" style={{ width: `${pct}%`, background: fill }} />
    </div>
  );
}

/** Compact meter with its value beside it, for table cells. */
export function MiniMeter({ percent, label, text }: { percent: number; label: string; text: string }) {
  const level = meterLevel(percent);
  return (
    <div className="flex min-w-24 items-center gap-2">
      <Meter value={percent} label={label} valueText={text} size="sm" className="w-14 shrink-0" />
      <span className={cn('text-xs tabular-nums', level === 'normal' ? 'text-fg-muted' : level === 'warning' ? 'text-warning' : 'text-danger')}>{text}</span>
    </div>
  );
}
