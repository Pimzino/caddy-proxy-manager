// Shared chart parts: width measurement, tooltip, legend keys and the chart card with its table-view twin.
import { useEffect, useRef, useState, type ReactNode } from 'react';
import { ChartLine, Table2 } from 'lucide-react';
import { cn } from '@/lib/cn';
import './charts.css';

/** A data series drawn by a chart. `color` is a CSS colour, normally one of the --viz-* variables. */
export interface ChartSeries {
  id: string;
  label: string;
  color: string;
  values: (number | null)[];
}

/** Width of an element, kept up to date with a ResizeObserver (0 until measured). */
export function useElementWidth<T extends HTMLElement>() {
  const ref = useRef<T>(null);
  const [width, setWidth] = useState(0);
  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    const ro = new ResizeObserver((entries) => {
      const w = Math.floor(entries[0]?.contentRect.width ?? 0);
      setWidth((prev) => (prev === w ? prev : w));
    });
    ro.observe(el);
    return () => ro.disconnect();
  }, []);
  return { ref, width };
}

/** Short stroke of the series colour: the key used in tooltips and line legends. */
export function LineKey({ color }: { color: string }) {
  return <span aria-hidden className="inline-block h-0.5 w-3 shrink-0 rounded-full" style={{ background: color }} />;
}

/** Square swatch: the key for bars and areas. */
export function SwatchKey({ color }: { color: string }) {
  return <span aria-hidden className="inline-block h-2.5 w-2.5 shrink-0 rounded-[3px]" style={{ background: color }} />;
}

export interface TooltipRow {
  key: string;
  color: string;
  label: string;
  value: string;
  kind?: 'line' | 'swatch';
}

/**
 * Hover/focus readout. Values lead (strong), series names follow (secondary); every row is keyed by a
 * short line or swatch of the series colour. Positioned inside the chart's relative container.
 */
export function ChartTooltip({
  x,
  containerWidth,
  top = 4,
  title,
  rows,
  footer,
}: {
  x: number;
  containerWidth: number;
  top?: number;
  title: string;
  rows: TooltipRow[];
  footer?: ReactNode;
}) {
  const flip = x > containerWidth * 0.58;
  return (
    <div
      role="presentation"
      className="pointer-events-none absolute z-10 min-w-36 rounded-md border border-border bg-surface px-2.5 py-2 text-xs shadow-pop"
      style={{ top, left: flip ? undefined : x + 12, right: flip ? containerWidth - x + 12 : undefined }}
    >
      <p className="mb-1 whitespace-nowrap text-fg-subtle">{title}</p>
      <ul className="space-y-0.5">
        {rows.map((r) => (
          <li key={r.key} className="flex items-center gap-2 whitespace-nowrap">
            {r.kind === 'swatch' ? <SwatchKey color={r.color} /> : <LineKey color={r.color} />}
            <span className="font-semibold text-fg tabular-nums">{r.value}</span>
            <span className="text-fg-muted">{r.label}</span>
          </li>
        ))}
      </ul>
      {footer && <div className="mt-1 border-t border-border pt-1 text-fg-muted">{footer}</div>}
    </div>
  );
}

export interface LegendItem {
  label: string;
  color: string;
  kind?: 'line' | 'swatch';
  /** Optional value shown after the label (e.g. latest reading or total). */
  value?: string;
}

export function Legend({ items, className }: { items: LegendItem[]; className?: string }) {
  return (
    <ul className={cn('flex flex-wrap items-center gap-x-4 gap-y-1 text-xs text-fg-muted', className)}>
      {items.map((i) => (
        <li key={i.label} className="flex items-center gap-1.5">
          {i.kind === 'swatch' ? <SwatchKey color={i.color} /> : <LineKey color={i.color} />}
          <span>{i.label}</span>
          {i.value !== undefined && <span className="font-medium text-fg tabular-nums">{i.value}</span>}
        </li>
      ))}
    </ul>
  );
}

export interface ChartTable {
  columns: string[];
  rows: string[][];
  /** Columns (by index) that hold numbers: right-aligned, tabular figures. */
  numeric?: number[];
}

/**
 * Card around a chart: title, optional headline value, legend (always for two or more series) and a
 * "Table" toggle that swaps the chart for its data (the accessible twin of every chart).
 * `dimmed` holds the previous render at reduced opacity while data reloads (no skeleton flash).
 */
export function ChartCard({
  title,
  description,
  value,
  legend,
  table,
  dimmed,
  actions,
  children,
  className,
}: {
  title: ReactNode;
  description?: ReactNode;
  value?: ReactNode;
  legend?: LegendItem[];
  table?: ChartTable;
  dimmed?: boolean;
  actions?: ReactNode;
  children: ReactNode;
  className?: string;
}) {
  const [showTable, setShowTable] = useState(false);
  return (
    <section className={cn('flex min-w-0 flex-col rounded-lg border border-border bg-surface shadow-xs', className)}>
      <header className="flex flex-wrap items-start gap-x-3 gap-y-1 px-4 pt-3">
        <div className="min-w-0 flex-1">
          <h3 className="text-sm font-semibold text-fg">{title}</h3>
          {description && <p className="text-xs text-fg-subtle">{description}</p>}
        </div>
        {value !== undefined && <div className="text-right text-sm font-semibold text-fg">{value}</div>}
        {actions}
        {table && (
          <button
            type="button"
            onClick={() => setShowTable((v) => !v)}
            aria-pressed={showTable}
            title={showTable ? 'Show chart' : 'Show data table'}
            aria-label={showTable ? 'Show chart' : 'Show data table'}
            className="-mr-1.5 inline-flex h-6 w-6 items-center justify-center rounded text-fg-subtle hover:bg-surface-2 hover:text-fg focus-visible:outline-2 focus-visible:outline-ring"
          >
            {showTable ? <ChartLine size={14} /> : <Table2 size={14} />}
          </button>
        )}
      </header>
      {legend && legend.length >= 2 && <Legend items={legend} className="px-4 pt-1.5" />}
      <div className={cn('min-w-0 px-4 pt-2 pb-3 transition-opacity', dimmed && 'opacity-60')}>
        {showTable && table ? <DataTable table={table} /> : children}
      </div>
    </section>
  );
}

function DataTable({ table }: { table: ChartTable }) {
  const numeric = new Set(table.numeric ?? table.columns.map((_, i) => i).slice(1));
  return (
    <div className="max-h-64 overflow-auto rounded-md border border-border" tabIndex={0}>
      <table className="w-full border-collapse text-xs">
        <thead className="sticky top-0 bg-surface-2 text-left">
          <tr>
            {table.columns.map((c, i) => (
              <th key={c} scope="col" className={cn('px-2.5 py-1.5 font-medium text-fg-subtle', numeric.has(i) && 'text-right')}>
                {c}
              </th>
            ))}
          </tr>
        </thead>
        <tbody className="divide-y divide-border">
          {table.rows.map((r, ri) => (
            <tr key={ri}>
              {r.map((cell, ci) => (
                <td key={ci} className={cn('px-2.5 py-1 whitespace-nowrap text-fg', numeric.has(ci) && 'text-right tabular-nums')}>
                  {cell}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

/** Builds the table view of time series: one row per timestamp (newest first), one column per series. */
export function seriesTable(
  x: number[],
  series: ChartSeries[],
  formatX: (ms: number) => string,
  formatValue: (v: number) => string,
  timeHeader = 'Time',
): ChartTable {
  const rows: string[][] = [];
  for (let i = x.length - 1; i >= 0; i--) {
    rows.push([formatX(x[i]), ...series.map((s) => (s.values[i] == null ? '—' : formatValue(s.values[i] as number)))]);
  }
  return { columns: [timeHeader, ...series.map((s) => s.label)], rows };
}
