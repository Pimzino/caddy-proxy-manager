// Responsive line/area time-series chart (hand-written SVG): one value axis from zero, hairline grid,
// 2px lines with a 10% area wash, crosshair + tooltip listing every series at the hovered time,
// keyboard support (focus the chart, then ←/→/Home/End), sparing end-of-line value labels.
import { useState, type KeyboardEvent, type PointerEvent } from 'react';
import { ChartTooltip, useElementWidth, type ChartSeries } from './parts';
import { byteTicks, formatTimeTick, labelWidth, linearScale, nearestIndex, timeTicks, valueTicks } from './scale';

export interface TimeSeriesChartProps {
  /** Timestamps (ms since epoch), ascending; one per value of every series. */
  x: number[];
  series: ChartSeries[];
  /** Total height including the x-axis band. */
  height?: number;
  /** Fixed top of the value axis (e.g. 100 for percentages); otherwise derived from the data. */
  yMax?: number;
  /** Byte quantities: ticks on binary-unit boundaries. */
  bytes?: boolean;
  /** Whole-number quantities (counts): value ticks are integers, so rounded labels never repeat or mislead. */
  integer?: boolean;
  /** The time axis is aligned to and labelled in UTC (data bucketed by UTC day); default: the browser's time zone. */
  utc?: boolean;
  /** Draw a 10% wash under each line. */
  area?: boolean;
  /** Visible x range; defaults to the first and last timestamps. */
  xDomain?: [number, number];
  formatValue: (v: number) => string;
  /** Axis tick labels (defaults to formatValue). */
  formatTick?: (v: number) => string;
  /** Tooltip heading for a timestamp. */
  formatX: (ms: number) => string;
  /** Screen-reader summary: what the chart shows ("CPU usage over the last 10 minutes"). */
  ariaLabel: string;
  /** Controlled hover index, to synchronise the crosshair of small multiples. */
  hoverIndex?: number | null;
  onHoverIndex?: (i: number | null) => void;
  /** Value labels at the end of each line (hidden automatically when they would collide). */
  endLabels?: boolean;
  emptyText?: string;
}

const MARGIN_TOP = 8;
const X_AXIS = 22;

export function TimeSeriesChart({
  x,
  series,
  height = 180,
  yMax,
  bytes,
  integer,
  utc,
  area = true,
  xDomain,
  formatValue,
  formatTick,
  formatX,
  ariaLabel,
  hoverIndex,
  onHoverIndex,
  endLabels = true,
  emptyText = 'No data yet',
}: TimeSeriesChartProps) {
  const { ref, width } = useElementWidth<HTMLDivElement>();
  const [localHover, setLocalHover] = useState<number | null>(null);
  const [pointerInside, setPointerInside] = useState(false);
  const controlled = onHoverIndex !== undefined;
  const hover = controlled ? (hoverIndex ?? null) : localHover;
  const setHover = (i: number | null) => (controlled ? onHoverIndex(i) : setLocalHover(i));

  const tickFmt = formatTick ?? formatValue;
  let dataMax = 0;
  for (const s of series) for (const v of s.values) if (v != null && v > dataMax) dataMax = v;
  const axisMax = yMax ?? dataMax;
  const nice = bytes ? byteTicks(axisMax, 4) : valueTicks(axisMax, 4, integer);
  // yMax is a hard ceiling (100 %, total memory): end the axis exactly there instead of at the next round number.
  const yTicks = yMax && yMax > 0 && nice.top > yMax ? [...nice.ticks.filter((t) => t < yMax * 0.9), yMax] : nice.ticks;
  const top = yMax && yMax > 0 && nice.top > yMax ? yMax : nice.top;

  const lastIdx = (s: ChartSeries) => {
    for (let i = s.values.length - 1; i >= 0; i--) if (s.values[i] != null) return i;
    return -1;
  };
  const endValues = series.map((s) => {
    const i = lastIdx(s);
    return i >= 0 ? (s.values[i] as number) : null;
  });

  const left = Math.max(28, ...yTicks.map((t) => labelWidth(tickFmt(t)))) + 8;
  const plotH = height - MARGIN_TOP - X_AXIS;
  const yScale = linearScale([0, top || 1], [MARGIN_TOP + plotH, MARGIN_TOP]);

  // End labels: shown when they fit without colliding (never stacked or nudged away from their lines).
  const endLabelTexts = endValues.map((v) => (v == null ? '' : formatValue(v)));
  let showEnd = endLabels && x.length > 1 && series.length <= 4;
  if (showEnd) {
    const ys = endValues.map((v) => (v == null ? null : yScale(v))).filter((v): v is number => v !== null).sort((a, b) => a - b);
    for (let i = 1; i < ys.length; i++) if (ys[i] - ys[i - 1] < 13) showEnd = false;
  }
  const right = showEnd ? Math.max(...endLabelTexts.map(labelWidth)) + 14 : 10;
  const plotW = Math.max(10, width - left - right);
  const [x0, x1] = xDomain ?? [x[0] ?? 0, x[x.length - 1] ?? 1];
  const xScale = linearScale([x0, x1 === x0 ? x0 + 1 : x1], [left, left + plotW]);
  const { ticks: xTicks, step } = timeTicks(x0, x1, plotW, undefined, utc);

  const hasData = x.length > 0 && series.some((s) => s.values.some((v) => v != null));

  const pathsFor = (s: ChartSeries) => {
    const lines: string[] = [];
    const areas: string[] = [];
    const dots: [number, number][] = [];
    let seg: [number, number][] = [];
    const flush = () => {
      if (seg.length === 1) dots.push(seg[0]);
      else if (seg.length > 1) {
        const d = seg.map(([px, py], i) => `${i ? 'L' : 'M'}${px.toFixed(1)},${py.toFixed(1)}`).join('');
        lines.push(d);
        const base = yScale(0).toFixed(1);
        areas.push(`${d}L${seg[seg.length - 1][0].toFixed(1)},${base}L${seg[0][0].toFixed(1)},${base}Z`);
      }
      seg = [];
    };
    s.values.forEach((v, i) => {
      if (v == null || x[i] < x0 || x[i] > x1) flush();
      else seg.push([xScale(x[i]), yScale(v)]);
    });
    flush();
    return { lines, areas, dots };
  };

  const onPointerMove = (e: PointerEvent<SVGRectElement>) => {
    const rect = (e.currentTarget.ownerSVGElement as SVGSVGElement).getBoundingClientRect();
    const px = e.clientX - rect.left;
    const t = x0 + ((px - left) / plotW) * (x1 - x0);
    const i = nearestIndex(x, t);
    if (i !== hover) setHover(i < 0 ? null : i);
  };

  const onKeyDown = (e: KeyboardEvent<SVGSVGElement>) => {
    if (!x.length) return;
    const cur = hover ?? x.length - 1;
    let next: number | null = null;
    if (e.key === 'ArrowLeft') next = Math.max(0, cur - 1);
    else if (e.key === 'ArrowRight') next = Math.min(x.length - 1, cur + 1);
    else if (e.key === 'Home') next = 0;
    else if (e.key === 'End') next = x.length - 1;
    else if (e.key === 'Escape') {
      setHover(null);
      return;
    }
    if (next === null) return;
    e.preventDefault();
    setPointerInside(true);
    setHover(next);
  };

  const summary = `${ariaLabel}. ${series
    .map((s, i) => (endValues[i] == null ? `${s.label}: no data` : `${s.label}: latest ${formatValue(endValues[i] as number)}`))
    .join('; ')}.`;

  const hx = hover !== null && x[hover] !== undefined ? xScale(x[hover]) : null;

  return (
    <div ref={ref} className="relative w-full" style={{ height }}>
      {width > 0 && !hasData && (
        <div className="flex h-full items-center justify-center rounded-md border border-dashed border-border text-xs text-fg-subtle">
          {emptyText}
        </div>
      )}
      {width > 0 && hasData && (
        <svg
          width={width}
          height={height}
          role="img"
          aria-label={summary}
          tabIndex={0}
          onKeyDown={onKeyDown}
          onFocus={() => {
            setPointerInside(true);
            if (hover === null) setHover(x.length - 1);
          }}
          onBlur={() => {
            setPointerInside(false);
            setHover(null);
          }}
          className="block overflow-visible focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring"
        >
          {/* grid + value axis */}
          {yTicks.map((t) => (
            <g key={t}>
              <line x1={left} x2={left + plotW} y1={yScale(t)} y2={yScale(t)} stroke={t === 0 ? 'var(--viz-axis)' : 'var(--viz-grid)'} strokeWidth={1} shapeRendering="crispEdges" />
              <text x={left - 8} y={yScale(t)} dy="0.32em" textAnchor="end" className="viz-tick">
                {tickFmt(t)}
              </text>
            </g>
          ))}
          {/* time axis */}
          {xTicks.map((t) => {
            const px = xScale(t);
            if (px < left - 0.5 || px > left + plotW + 0.5) return null;
            return (
              <text key={t} x={px} y={MARGIN_TOP + plotH + 15} textAnchor="middle" className="viz-tick">
                {formatTimeTick(t, step, utc)}
              </text>
            );
          })}
          {/* marks */}
          {series.map((s) => {
            const { lines, areas, dots } = pathsFor(s);
            return (
              <g key={s.id}>
                {area && areas.map((d, i) => <path key={i} d={d} fill={s.color} fillOpacity={0.1} stroke="none" />)}
                {lines.map((d, i) => (
                  <path key={i} d={d} fill="none" stroke={s.color} strokeWidth={2} strokeLinejoin="round" strokeLinecap="round" className="viz-mark" />
                ))}
                {dots.map(([cx, cy], i) => (
                  <circle key={i} cx={cx} cy={cy} r={2.5} fill={s.color} />
                ))}
              </g>
            );
          })}
          {/* end labels + end dots */}
          {showEnd &&
            series.map((s, si) => {
              const i = lastIdx(s);
              if (i < 0 || endValues[si] == null) return null;
              const cx = xScale(x[i]);
              const cy = yScale(endValues[si] as number);
              return (
                <g key={s.id}>
                  <circle cx={cx} cy={cy} r={4} fill={s.color} stroke="var(--viz-surface)" strokeWidth={2} />
                  <text x={left + plotW + 8} y={cy} dy="0.32em" className="viz-label">
                    {endLabelTexts[si]}
                  </text>
                </g>
              );
            })}
          {/* crosshair */}
          {hx !== null && hover !== null && (
            <g pointerEvents="none">
              <line x1={hx} x2={hx} y1={MARGIN_TOP} y2={MARGIN_TOP + plotH} stroke="var(--fg-subtle)" strokeWidth={1} shapeRendering="crispEdges" />
              {series.map((s) =>
                s.values[hover] == null ? null : (
                  <circle key={s.id} cx={hx} cy={yScale(s.values[hover] as number)} r={4} fill={s.color} stroke="var(--viz-surface)" strokeWidth={2} />
                ),
              )}
            </g>
          )}
          {/* hit layer: the whole plot, so the pointer only has to be near a time */}
          <rect
            x={left}
            y={MARGIN_TOP}
            width={plotW}
            height={plotH}
            fill="transparent"
            onPointerEnter={() => setPointerInside(true)}
            onPointerMove={onPointerMove}
            onPointerLeave={() => {
              setPointerInside(false);
              setHover(null);
            }}
          />
        </svg>
      )}
      {hx !== null && hover !== null && pointerInside && (
        <ChartTooltip
          x={hx}
          containerWidth={width}
          title={formatX(x[hover])}
          rows={series.map((s) => ({
            key: s.id,
            color: s.color,
            label: s.label,
            value: s.values[hover] == null ? '—' : formatValue(s.values[hover] as number),
          }))}
        />
      )}
    </div>
  );
}
