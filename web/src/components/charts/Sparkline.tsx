// Sparkline: a small trend line for stat tiles (no axes). The line is the series colour at 2px with a
// light wash, the latest point is marked; hovering (or focusing) shows the value at that point.
import { useState, type KeyboardEvent, type PointerEvent } from 'react';
import { ChartTooltip, useElementWidth } from './parts';
import { linearScale, nearestIndex } from './scale';

export function Sparkline({
  values,
  x,
  height = 36,
  color = 'var(--viz-1)',
  formatValue,
  formatX,
  ariaLabel,
  interactive = true,
  focusable = interactive,
}: {
  values: number[];
  /** Timestamps for the tooltip heading (optional). */
  x?: number[];
  height?: number;
  color?: string;
  formatValue: (v: number) => string;
  formatX?: (ms: number) => string;
  ariaLabel: string;
  /** Hover readout (crosshair + tooltip). */
  interactive?: boolean;
  /** Keyboard focus stop (turn off inside links, where the link is the focus target). */
  focusable?: boolean;
}) {
  const { ref, width } = useElementWidth<HTMLDivElement>();
  const [hover, setHover] = useState<number | null>(null);
  const n = values.length;
  const max = Math.max(0, ...values);
  const pad = 4;
  const xs = linearScale([0, Math.max(1, n - 1)], [pad, Math.max(pad + 1, width - pad)]);
  const ys = linearScale([0, max || 1], [height - pad, pad]);
  const pts = values.map((v, i) => [xs(i), ys(v)] as const);
  const line = pts.map(([px, py], i) => `${i ? 'L' : 'M'}${px.toFixed(1)},${py.toFixed(1)}`).join('');
  const areaPath = n > 1 ? `${line}L${pts[n - 1][0].toFixed(1)},${height - pad}L${pts[0][0].toFixed(1)},${height - pad}Z` : '';
  const last = n ? values[n - 1] : 0;
  const peak = n ? Math.max(...values) : 0;
  const summary = `${ariaLabel}: ${n} points, latest ${formatValue(last)}, peak ${formatValue(peak)}.`;

  const onMove = (e: PointerEvent<SVGSVGElement>) => {
    const rect = e.currentTarget.getBoundingClientRect();
    const idx = nearestIndex(values.map((_, i) => xs(i)), e.clientX - rect.left);
    setHover(idx < 0 ? null : idx);
  };
  const onKeyDown = (e: KeyboardEvent<SVGSVGElement>) => {
    if (!n) return;
    const cur = hover ?? n - 1;
    const next = e.key === 'ArrowLeft' ? Math.max(0, cur - 1) : e.key === 'ArrowRight' ? Math.min(n - 1, cur + 1) : null;
    if (next === null) return;
    e.preventDefault();
    setHover(next);
  };

  return (
    <div ref={ref} className="relative w-full" style={{ height }}>
      {width > 0 && n > 0 && (
        <svg
          width={width}
          height={height}
          role="img"
          aria-label={summary}
          tabIndex={focusable ? 0 : undefined}
          onPointerMove={interactive ? onMove : undefined}
          onPointerLeave={() => setHover(null)}
          onKeyDown={focusable ? onKeyDown : undefined}
          onFocus={() => focusable && setHover(n - 1)}
          onBlur={() => setHover(null)}
          className="block overflow-visible focus-visible:outline-2 focus-visible:outline-ring"
        >
          {areaPath && <path d={areaPath} fill={color} fillOpacity={0.1} />}
          {n > 1 && <path d={line} fill="none" stroke={color} strokeWidth={2} strokeLinejoin="round" strokeLinecap="round" className="viz-mark" />}
          <circle cx={pts[n - 1][0]} cy={pts[n - 1][1]} r={3} fill={color} stroke="var(--viz-surface)" strokeWidth={1.5} />
          {hover !== null && pts[hover] && (
            <>
              <line x1={pts[hover][0]} x2={pts[hover][0]} y1={0} y2={height} stroke="var(--fg-subtle)" strokeWidth={1} />
              <circle cx={pts[hover][0]} cy={pts[hover][1]} r={3.5} fill={color} stroke="var(--viz-surface)" strokeWidth={2} />
            </>
          )}
        </svg>
      )}
      {hover !== null && pts[hover] && (
        <ChartTooltip
          x={pts[hover][0]}
          containerWidth={width}
          top={-8}
          title={x && formatX ? formatX(x[hover]) : `Point ${hover + 1} of ${n}`}
          rows={[{ key: 'v', color, label: '', value: formatValue(values[hover]) }]}
        />
      )}
    </div>
  );
}
