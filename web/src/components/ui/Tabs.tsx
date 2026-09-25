import { useId, useRef, type KeyboardEvent, type ReactNode } from 'react';
import { cn } from '@/lib/cn';

export interface TabItem<T extends string> {
  value: T;
  label: ReactNode;
  icon?: ReactNode;
  /** Number shown as a small badge (e.g. validation errors). */
  badge?: number;
  badgeTone?: 'danger' | 'neutral';
  hidden?: boolean;
}

/** WAI-ARIA tabs (automatic activation, roving tabindex, arrow/Home/End keys). */
export function Tabs<T extends string>({
  value,
  onChange,
  items,
  className,
  'aria-label': ariaLabel,
  idBase,
}: {
  value: T;
  onChange: (value: T) => void;
  items: TabItem<T>[];
  className?: string;
  'aria-label': string;
  /** Use the same idBase on TabPanel to link tab and panel. */
  idBase?: string;
}) {
  const auto = useId();
  const base = idBase ?? auto;
  const visible = items.filter((i) => !i.hidden);
  const refs = useRef<(HTMLButtonElement | null)[]>([]);

  const onKeyDown = (e: KeyboardEvent, index: number) => {
    let next = -1;
    if (e.key === 'ArrowRight') next = (index + 1) % visible.length;
    else if (e.key === 'ArrowLeft') next = (index - 1 + visible.length) % visible.length;
    else if (e.key === 'Home') next = 0;
    else if (e.key === 'End') next = visible.length - 1;
    if (next < 0) return;
    e.preventDefault();
    onChange(visible[next].value);
    refs.current[next]?.focus();
  };

  return (
    <div
      role="tablist"
      aria-label={ariaLabel}
      className={cn('flex items-end gap-1 overflow-x-auto overflow-y-hidden shadow-[inset_0_-1px_0_var(--border)]', className)}
    >
      {visible.map((t, i) => {
        const selected = t.value === value;
        return (
          <button
            key={t.value}
            ref={(el) => {
              refs.current[i] = el;
            }}
            id={`${base}-tab-${t.value}`}
            role="tab"
            type="button"
            aria-selected={selected}
            aria-controls={`${base}-panel-${t.value}`}
            tabIndex={selected ? 0 : -1}
            onClick={() => onChange(t.value)}
            onKeyDown={(e) => onKeyDown(e, i)}
            className={cn(
              'inline-flex h-9 shrink-0 items-center gap-1.5 border-b-2 px-2.5 text-sm font-medium whitespace-nowrap transition-colors',
              'focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-ring',
              selected ? 'border-accent text-fg' : 'border-transparent text-fg-muted hover:border-border-strong hover:text-fg',
            )}
          >
            {t.icon}
            {t.label}
            {!!t.badge && (
              <span
                className={cn(
                  'ml-0.5 inline-flex h-4 min-w-4 items-center justify-center rounded-full px-1 text-[10px] font-semibold',
                  t.badgeTone === 'neutral' ? 'bg-surface-3 text-fg-muted' : 'bg-danger text-white dark:text-[#1a0808]',
                )}
                aria-label={t.badgeTone === 'neutral' ? `${t.badge} items` : `${t.badge} errors`}
              >
                {t.badge}
              </span>
            )}
          </button>
        );
      })}
    </div>
  );
}

export function TabPanel({
  idBase,
  value,
  active,
  children,
  className,
}: {
  idBase: string;
  value: string;
  active: boolean;
  children: ReactNode;
  className?: string;
}) {
  if (!active) return null;
  return (
    <div
      role="tabpanel"
      id={`${idBase}-panel-${value}`}
      aria-labelledby={`${idBase}-tab-${value}`}
      tabIndex={0}
      className={cn('focus-visible:outline-none', className)}
    >
      {children}
    </div>
  );
}
