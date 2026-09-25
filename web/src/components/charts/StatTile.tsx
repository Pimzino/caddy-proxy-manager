// Stat tile: label · value (semibold, proportional figures) · optional detail line · optional sparkline.
import type { ReactNode } from 'react';
import { cn } from '@/lib/cn';
import { Sparkline } from './Sparkline';

export function StatTile({
  label,
  value,
  detail,
  trend,
  className,
}: {
  label: string;
  value: ReactNode;
  detail?: ReactNode;
  trend?: { values: number[]; x?: number[]; formatValue: (v: number) => string; formatX?: (ms: number) => string; color?: string };
  className?: string;
}) {
  return (
    <div className={cn('flex min-w-0 flex-col rounded-lg border border-border bg-surface px-4 py-3 shadow-xs', className)}>
      <p className="truncate text-xs font-medium text-fg-subtle">{label}</p>
      <p className="mt-0.5 truncate text-xl font-semibold tracking-tight text-fg">{value}</p>
      {detail && <p className="truncate text-xs text-fg-subtle">{detail}</p>}
      {trend && trend.values.length > 1 && (
        <div className="mt-2">
          <Sparkline
            values={trend.values}
            x={trend.x}
            height={28}
            color={trend.color}
            formatValue={trend.formatValue}
            formatX={trend.formatX}
            ariaLabel={`${label} trend`}
          />
        </div>
      )}
    </div>
  );
}
