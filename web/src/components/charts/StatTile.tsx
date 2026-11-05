// Stat tile: label · value (tabular figures) · optional detail line. A number, not a chart: trends live in the charts.
import type { ReactNode } from 'react';
import { cn } from '@/lib/cn';

export function StatTile({ label, value, detail, className }: { label: string; value: ReactNode; detail?: ReactNode; className?: string }) {
  return (
    <div className={cn('flex min-w-0 flex-col rounded-lg border border-border bg-surface px-4 py-3 shadow-xs', className)}>
      <p className="truncate text-xs font-medium text-fg-subtle">{label}</p>
      <p className="mt-1 truncate text-xl font-medium text-fg tabular-nums">{value}</p>
      {detail && <p className="truncate text-xs text-fg-subtle tabular-nums">{detail}</p>}
    </div>
  );
}
