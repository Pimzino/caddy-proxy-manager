// Stacked columns over time buckets (hand-written SVG): thin columns (≤ 24px) growing from one baseline,
// a 2px surface gap between segments and between neighbouring columns, 4px rounded data end on the top
// segment only. Each column is its own hit target (the whole band), with a tooltip listing every segment.
import { useState, type KeyboardEvent } from 'react';
import { ChartTooltip, useElementWidth, type ChartSeries } from './parts';
import { formatTimeTick, labelWidth, linearScale, timeTicks, valueTicks } from './scale';

const MARGIN_TOP = 8;
const X_AXIS = 22;
const GAP = 2;

export function StackedColumnChart({
  x,
  series,
  height = 180,
  bucketMs,
  formatValue,
  formatX,
  ariaLabel,
  totalLabel = 'Total',
  emptyText = 'No data yet',
}: {
  /** Bucket start times (ms), ascending and evenly spaced. */
  x: number[];
  /** Segments from the baseline up (first series sits on the axis). */
  series: ChartSeries[];
  height?: number;
  /** Bucket width in ms (defaults to the spacing of `x`). */
  bucketMs?: number;
  formatValue: (v: number) => string;
  formatX: (ms: number) => string;
  ariaLabel: string;
  totalLabel?: string;
  emptyText?: string;
}) {
  const { ref, width } = useElementWidth<HTMLDivElement>();
  const [hover, setHover] = useState<number | null>(null);

  const n = x.length;
  const totals = x.map((_, i) => series.reduce((sum, s) => sum + (s.values[i] ?? 0), 0));
  const { ticks, top } = valueTicks(Math.max(0, ...totals), 4);
  const left = Math.max(28, ...ticks.map((t) => labelWidth(formatValue(t)))) + 8;
  const right = 10;
  const plotW = Math.max(10, width - left - right);
  const plotH = height - MARGIN_TOP - X_AXIS;
  const yScale = linearScale([0, top || 1], [MARGIN_TOP + plotH, MARGIN_TOP]);
  const bucket = bucketMs ?? (n > 1 ? x[1] - x[0] : 60_000);
  const x0 = x[0] ?? 0;
  const x1 = (x[n - 1] ?? 0) + bucket;
  const band = n > 0 ? plotW / n : plotW;
  const barW = Math.max(1, Math.min(24, band * 0.72, band - GAP));
  const colX = (i: number) => left + i * band + (band - barW) / 2;
  const xScale = linearScale([x0, x1], [left, left + plotW]);
  const { ticks: xTicks, step } = timeTicks(x0, x1, plotW);
  const hasData = totals.some((t) => t > 0);

  const onKeyDown = (e: KeyboardEvent<SVGSVGElement>) => {
    if (!n) return;
    const cur = hover ?? n - 1;
    let next: number | null = null;
    if (e.key === 'ArrowLeft') next = Math.max(0, cur - 1);
    else if (e.key === 'ArrowRight') next = Math.min(n - 1, cur + 1);
    else if (e.key === 'Home') next = 0;
    else if (e.key === 'End') next = n - 1;
    else if (e.key === 'Escape') return setHover(null);
    if (next === null) return;
    e.preventDefault();
    setHover(next);
  };

  // Segment path: square at the bottom, 4px rounded corners on the data end (top segment only).
  const segment = (cx: number, yTop: number, yBottom: number, rounded: boolean) => {
    const h = yBottom - yTop;
    const r = rounded ? Math.min(4, barW / 2, h) : 0;
    if (r <= 0.5) return `M${cx},${yBottom}V${yTop}H${cx + barW}V${yBottom}Z`;
    return `M${cx},${yBottom}V${yTop + r}Q${cx},${yTop} ${cx + r},${yTop}H${cx + barW - r}Q${cx + barW},${yTop} ${cx + barW},${yTop + r}V${yBottom}Z`;
  };

  const sums = series.map((s) => s.values.reduce<number>((a, v) => a + (v ?? 0), 0));
  const summary = `${ariaLabel}. ${series.map((s, i) => `${s.label}: ${formatValue(sums[i])}`).join('; ')}.`;

  return (
    <div ref={ref} className="relative w-full" style={{ height }}>
      {width > 0 && !hasData && (
        <div className="flex h-full items-center justify-center rounded-md border border-dashed border-border text-xs text-fg-subtle">{emptyText}</div>
      )}
      {width > 0 && hasData && (
        <svg
          width={width}
          height={height}
          role="img"
          aria-label={summary}
          tabIndex={0}
          onKeyDown={onKeyDown}
          onFocus={() => setHover((h) => h ?? n - 1)}
          onBlur={() => setHover(null)}
          onPointerLeave={() => setHover(null)}
          className="block focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring"
        >
          {ticks.map((t) => (
            <g key={t}>
              <line x1={left} x2={left + plotW} y1={yScale(t)} y2={yScale(t)} stroke={t === 0 ? 'var(--viz-axis)' : 'var(--viz-grid)'} strokeWidth={1} shapeRendering="crispEdges" />
              <text x={left - 8} y={yScale(t)} dy="0.32em" textAnchor="end" className="viz-tick">
                {formatValue(t)}
              </text>
            </g>
          ))}
          {xTicks.map((t) => {
            const px = xScale(t);
            if (px < left - 0.5 || px > left + plotW + 0.5) return null;
            return (
              <text key={t} x={px} y={MARGIN_TOP + plotH + 15} textAnchor="middle" className="viz-tick">
                {formatTimeTick(t, step)}
              </text>
            );
          })}
          {x.map((_, i) => {
            const cx = colX(i);
            const topSeries = series.reduce((last, s, si) => ((s.values[i] ?? 0) > 0 ? si : last), -1);
            const dim = hover !== null && hover !== i;
            return (
              <g key={i} opacity={dim ? 0.55 : 1}>
                {series.map((s, si) => {
                  const v = s.values[i] ?? 0;
                  if (v <= 0) return null;
                  const yTop = yScale(totalsUpTo(series, i, si));
                  const yBottom = yScale(totalsUpTo(series, i, si - 1));
                  // 2px surface gap above every segment that has another one on top of it.
                  const gapTop = si < topSeries && yBottom - yTop > GAP + 1 ? GAP : 0;
                  return <path key={s.id} d={segment(cx, yTop + gapTop, yBottom, si === topSeries)} fill={s.color} />;
                })}
              </g>
            );
          })}
          {/* hit targets: the whole band of each column */}
          {x.map((_, i) => (
            <rect
              key={i}
              x={left + i * band}
              y={MARGIN_TOP}
              width={band}
              height={plotH}
              fill="transparent"
              onPointerEnter={() => setHover(i)}
            />
          ))}
        </svg>
      )}
      {hover !== null && x[hover] !== undefined && (
        <ChartTooltip
          x={colX(hover) + barW / 2}
          containerWidth={width}
          title={formatX(x[hover])}
          rows={[...series].reverse().map((s) => ({
            key: s.id,
            color: s.color,
            label: s.label,
            value: formatValue(s.values[hover] ?? 0),
            kind: 'swatch' as const,
          }))}
          footer={
            <>
              <span className="font-semibold text-fg tabular-nums">{formatValue(totals[hover])}</span> {totalLabel}
            </>
          }
        />
      )}
    </div>
  );
}

function totalsUpTo(series: ChartSeries[], i: number, si: number): number {
  let sum = 0;
  for (let k = 0; k <= si; k++) sum += series[k].values[i] ?? 0;
  return sum;
}
