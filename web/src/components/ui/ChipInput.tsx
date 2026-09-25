import { useState, type ClipboardEvent, type KeyboardEvent } from 'react';
import { X } from 'lucide-react';
import { cn } from '@/lib/cn';
import { controlBase, useFieldProps } from './Field';

/**
 * Tag-style input for lists of technical values (domains, CIDRs, e-mail recipients).
 * Enter, comma, space or Tab commit the current text; pasting a list splits it; Backspace removes the last chip.
 */
export function ChipInput({
  value,
  onChange,
  placeholder,
  validate,
  normalize = (s) => s.trim(),
  disabled,
  id,
  invalid,
  mono = true,
  'aria-label': ariaLabel,
  'aria-describedby': describedBy,
}: {
  value: string[];
  onChange: (next: string[]) => void;
  placeholder?: string;
  /** Returns an error message for an invalid entry. */
  validate?: (item: string) => string | null;
  normalize?: (s: string) => string;
  disabled?: boolean;
  id?: string;
  invalid?: boolean;
  mono?: boolean;
  'aria-label'?: string;
  'aria-describedby'?: string;
}) {
  const a11y = useFieldProps(id, invalid, describedBy);
  const [draft, setDraft] = useState('');
  const [error, setError] = useState<string | null>(null);

  const commit = (raw: string): boolean => {
    const parts = raw
      .split(/[\s,;]+/)
      .map(normalize)
      .filter(Boolean);
    if (parts.length === 0) return true;
    const next = [...value];
    for (const p of parts) {
      const err = validate?.(p) ?? null;
      if (err) {
        setError(`${p}: ${err}`);
        return false;
      }
      if (!next.some((v) => v.toLowerCase() === p.toLowerCase())) next.push(p);
    }
    setError(null);
    onChange(next);
    setDraft('');
    return true;
  };

  const onKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Enter' || e.key === ',' || e.key === ' ' || e.key === ';') {
      if (draft.trim()) {
        e.preventDefault();
        commit(draft);
      } else if (e.key !== 'Enter') e.preventDefault();
    } else if (e.key === 'Tab' && draft.trim()) {
      if (!commit(draft)) e.preventDefault();
    } else if (e.key === 'Backspace' && !draft && value.length) {
      onChange(value.slice(0, -1));
    }
  };

  const onPaste = (e: ClipboardEvent<HTMLInputElement>) => {
    const text = e.clipboardData.getData('text');
    if (/[\s,;]/.test(text.trim())) {
      e.preventDefault();
      commit(draft + text);
    }
  };

  const errorId = a11y.id ? `${a11y.id}-chip-err` : undefined;
  const isInvalid = !!a11y['aria-invalid'];
  return (
    <div>
      <div
        className={cn(
          controlBase,
          'flex min-h-8 flex-wrap items-center gap-1 px-1.5 py-1 focus-within:border-accent focus-within:ring-2 focus-within:ring-ring/25',
          (isInvalid || error) && 'border-danger',
          disabled && 'cursor-not-allowed bg-surface-2',
        )}
        onMouseDown={(e) => {
          if (e.target === e.currentTarget) {
            e.preventDefault();
            (e.currentTarget.querySelector('input') as HTMLInputElement | null)?.focus();
          }
        }}
      >
        {value.map((v, i) => (
          <span
            key={v}
            className={cn(
              'inline-flex h-6 max-w-full items-center gap-1 rounded border border-border bg-surface-2 pr-0.5 pl-1.5 text-xs text-fg',
              mono && 'mono',
            )}
          >
            <span className="truncate">{v}</span>
            {!disabled && (
              <button
                type="button"
                onClick={() => onChange(value.filter((_, j) => j !== i))}
                className="flex h-4 w-4 items-center justify-center rounded text-fg-subtle hover:bg-surface-3 hover:text-fg focus-visible:outline-2 focus-visible:outline-ring"
                aria-label={`Remove ${v}`}
              >
                <X size={12} />
              </button>
            )}
          </span>
        ))}
        <input
          id={a11y.id}
          value={draft}
          disabled={disabled}
          aria-label={ariaLabel}
          aria-invalid={isInvalid || !!error || undefined}
          aria-describedby={[error ? errorId : undefined, a11y['aria-describedby']].filter(Boolean).join(' ') || undefined}
          onChange={(e) => {
            setDraft(e.target.value);
            if (error) setError(null);
          }}
          onKeyDown={onKeyDown}
          onPaste={onPaste}
          onBlur={() => {
            if (draft.trim()) commit(draft);
          }}
          placeholder={value.length === 0 ? placeholder : undefined}
          className={cn(
            'h-6 min-w-[8rem] flex-1 bg-transparent px-1 text-sm text-fg outline-none placeholder:text-fg-subtle/80 disabled:cursor-not-allowed',
            mono && 'mono',
          )}
        />
      </div>
      {error && (
        <p id={errorId} className="mt-1 text-xs text-danger" role="alert">
          {error}
        </p>
      )}
    </div>
  );
}
