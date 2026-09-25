import { useId, useRef, type KeyboardEvent, type ReactNode } from 'react';
import { cn } from '@/lib/cn';

export interface RadioCardOption<T extends string> {
  value: T;
  label: ReactNode;
  description?: ReactNode;
  icon?: ReactNode;
  disabled?: boolean;
}

/** Accessible radio group rendered as selectable cards (arrow keys move the selection). */
export function RadioCards<T extends string>({
  value,
  onChange,
  options,
  columns = 2,
  disabled,
  'aria-label': ariaLabel,
  className,
}: {
  value: T;
  onChange: (value: T) => void;
  options: RadioCardOption<T>[];
  columns?: 1 | 2 | 3 | 4;
  disabled?: boolean;
  'aria-label'?: string;
  className?: string;
}) {
  const group = useId();
  const refs = useRef<(HTMLButtonElement | null)[]>([]);
  const enabled = options.filter((o) => !o.disabled && !disabled);

  const onKeyDown = (e: KeyboardEvent<HTMLButtonElement>, index: number) => {
    const keys = ['ArrowDown', 'ArrowRight', 'ArrowUp', 'ArrowLeft'];
    if (!keys.includes(e.key) || enabled.length === 0) return;
    e.preventDefault();
    const dir = e.key === 'ArrowDown' || e.key === 'ArrowRight' ? 1 : -1;
    let i = index;
    for (let n = 0; n < options.length; n++) {
      i = (i + dir + options.length) % options.length;
      if (!options[i].disabled) break;
    }
    onChange(options[i].value);
    refs.current[i]?.focus();
  };

  const cols = { 1: '', 2: 'sm:grid-cols-2', 3: 'sm:grid-cols-3', 4: 'sm:grid-cols-2 lg:grid-cols-4' }[columns];
  return (
    <div role="radiogroup" aria-label={ariaLabel} className={cn('grid gap-2', cols, className)}>
      {options.map((o, i) => {
        const selected = o.value === value;
        const isDisabled = disabled || o.disabled;
        return (
          <button
            key={o.value}
            ref={(el) => {
              refs.current[i] = el;
            }}
            type="button"
            role="radio"
            aria-checked={selected}
            aria-describedby={o.description ? `${group}-${i}-d` : undefined}
            tabIndex={selected || (!options.some((x) => x.value === value) && i === 0) ? 0 : -1}
            disabled={isDisabled}
            onClick={() => onChange(o.value)}
            onKeyDown={(e) => onKeyDown(e, i)}
            className={cn(
              'flex items-start gap-3 rounded-md border p-3 text-left transition-colors',
              'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring',
              'disabled:cursor-not-allowed disabled:opacity-50',
              selected ? 'border-accent bg-accent-soft ring-1 ring-accent' : 'border-border-strong bg-surface hover:bg-surface-2',
            )}
          >
            <span
              aria-hidden
              className={cn(
                'mt-0.5 flex h-4 w-4 shrink-0 items-center justify-center rounded-full border',
                selected ? 'border-accent' : 'border-border-strong',
              )}
            >
              {selected && <span className="h-2 w-2 rounded-full bg-accent" />}
            </span>
            <span className="min-w-0 flex-1">
              <span className="flex items-center gap-1.5 text-sm font-medium text-fg">
                {o.icon}
                {o.label}
              </span>
              {o.description && (
                <span id={`${group}-${i}-d`} className="mt-0.5 block text-xs text-fg-subtle">
                  {o.description}
                </span>
              )}
            </span>
          </button>
        );
      })}
    </div>
  );
}

/** Compact segmented control (radio group) for filters and small choices. */
export function Segmented<T extends string>({
  value,
  onChange,
  options,
  'aria-label': ariaLabel,
  size = 'md',
}: {
  value: T;
  onChange: (v: T) => void;
  options: { value: T; label: ReactNode }[];
  'aria-label': string;
  size?: 'sm' | 'md';
}) {
  const refs = useRef<(HTMLButtonElement | null)[]>([]);
  return (
    <div role="radiogroup" aria-label={ariaLabel} className="inline-flex w-fit max-w-full self-start rounded-md border border-border-strong bg-surface-2 p-0.5">
      {options.map((o, i) => {
        const selected = o.value === value;
        return (
          <button
            key={o.value}
            ref={(el) => {
              refs.current[i] = el;
            }}
            type="button"
            role="radio"
            aria-checked={selected}
            tabIndex={selected ? 0 : -1}
            onClick={() => onChange(o.value)}
            onKeyDown={(e) => {
              if (e.key !== 'ArrowRight' && e.key !== 'ArrowLeft') return;
              e.preventDefault();
              const next = (i + (e.key === 'ArrowRight' ? 1 : -1) + options.length) % options.length;
              onChange(options[next].value);
              refs.current[next]?.focus();
            }}
            className={cn(
              'rounded-[5px] font-medium transition-colors focus-visible:outline-2 focus-visible:outline-ring',
              size === 'sm' ? 'h-6 px-2 text-xs' : 'h-7 px-2.5 text-sm',
              selected ? 'bg-surface text-fg shadow-xs' : 'text-fg-muted hover:text-fg',
            )}
          >
            {o.label}
          </button>
        );
      })}
    </div>
  );
}
