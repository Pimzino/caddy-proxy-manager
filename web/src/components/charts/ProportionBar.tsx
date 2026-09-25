// Part-to-whole bar: one horizontal stacked bar (2px surface gaps, rounded outer ends) with a legend that
// carries every value and share as text, so the colours are never the only way to read it.
import { useState } from 'react';
import { formatCount } from './format';
import { ChartTooltip, SwatchKey, useElementWidth } from './parts';

export interface ProportionItem {
  id: string;
  label: string;
  value: number;
  color: string;
}

export function ProportionBar({
  items,
  ariaLabel,
  formatValue = formatCount,
}: {
  items: ProportionItem[];
  ariaLabel: string;
  formatValue?: (v: number) => string;
}) {
  const { ref, width } = useElementWidth<HTMLDivElement>();
  const [hover, setHover] = useState<number | null>(null);
  const total = items.reduce((s, i) => s + i.value, 0);
  const share = (v: number) => (total > 0 ? (v / total) * 100 : 0);
  const shareText = (v: number) => {
    const p = share(v);
    return p === 0 ? '0%' : p < 0.1 ? '<0.1%' : p < 10 ? `${p.toFixed(1)}%` : `${Math.round(p)}%`;
  };
  const visible = items.map((it, idx) => ({ ...it, idx })).filter((i) => i.value > 0);
  // Centre of each visible segment, for the tooltip.
  let acc = 0;
  const centres = new Map<number, number>();
  for (const v of visible) {
    const w = (v.value / (total || 1)) * width;
    centres.set(v.idx, acc + w / 2);
    acc += w;
  }
  const summary = `${ariaLabel}: ${items.map((i) => `${i.label} ${formatValue(i.value)} (${shareText(i.value)})`).join(', ')}.`;

  return (
    <div>
      <div ref={ref} className="relative" onPointerLeave={() => setHover(null)}>
        <div role="img" aria-label={summary} className="flex h-3 w-full gap-0.5 overflow-hidden rounded bg-surface-2">
          {visible.map((v) => (
            <div
              key={v.id}
              className="h-full min-w-0.5 first:rounded-l last:rounded-r"
              style={{ flexGrow: v.value, flexBasis: 0, background: v.color, opacity: hover !== null && hover !== v.idx ? 0.55 : 1 }}
              onPointerEnter={() => setHover(v.idx)}
            />
          ))}
        </div>
        {hover !== null && centres.has(hover) && (
          <ChartTooltip
            x={centres.get(hover) as number}
            containerWidth={width}
            top={18}
            title={items[hover].label}
            rows={[{ key: 'v', color: items[hover].color, label: shareText(items[hover].value), value: formatValue(items[hover].value), kind: 'swatch' }]}
          />
        )}
      </div>
      <ul className="mt-2 flex flex-wrap gap-x-4 gap-y-1 text-xs">
        {items.map((i, idx) => (
          <li
            key={i.id}
            className="flex items-center gap-1.5"
            onPointerEnter={() => i.value > 0 && setHover(idx)}
            onPointerLeave={() => setHover(null)}
          >
            <SwatchKey color={i.color} />
            <span className="text-fg-muted">{i.label}</span>
            <span className="font-medium text-fg tabular-nums">{formatValue(i.value)}</span>
            <span className="text-fg-subtle tabular-nums">{shareText(i.value)}</span>
          </li>
        ))}
      </ul>
    </div>
  );
}
