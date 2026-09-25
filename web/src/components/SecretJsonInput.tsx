import { KeyRound, Pencil, Undo2, Wand2 } from 'lucide-react';
import { Button, Textarea } from '@/components/ui';

/**
 * Write-only multi-line secret (JSON) following the settings wire rule:
 * value undefined = unchanged, "" = clear, other = set. The stored value is never shown.
 * "\u0000" marks "replace requested, nothing typed yet" (same convention as SecretInput).
 */
export function SecretJsonInput({
  has,
  value,
  onChange,
  disabled,
  placeholder,
  rows = 8,
  'aria-label': ariaLabel,
}: {
  has: boolean;
  value: string | null | undefined;
  onChange: (v: string | undefined) => void;
  disabled?: boolean;
  placeholder?: string;
  rows?: number;
  'aria-label'?: string;
}) {
  if (value === undefined || value === null) {
    if (!has && disabled) {
      return (
        <div className="flex h-8 items-center rounded-md border border-border-strong bg-surface-2 px-2.5 text-sm text-fg-subtle">Not configured</div>
      );
    }
    if (has) {
      return (
        <div className="flex flex-wrap items-center gap-2">
          <div className="flex h-8 min-w-0 flex-1 items-center gap-2 rounded-md border border-border-strong bg-surface-2 px-2.5 text-sm text-fg-muted">
            <KeyRound size={13} aria-hidden />
            <span className="truncate">Configured — stored encrypted, not shown</span>
          </div>
          <Button icon={<Pencil size={13} />} onClick={() => onChange('\u0000')} disabled={disabled}>
            Replace
          </Button>
          <Button variant="danger-ghost" onClick={() => onChange('')} disabled={disabled}>
            Clear
          </Button>
        </div>
      );
    }
  }
  if (value === '' && has) {
    return (
      <div className="flex items-center gap-2">
        <div className="flex h-8 min-w-0 flex-1 items-center rounded-md border border-dashed border-danger/40 bg-danger-soft px-2.5 text-sm text-danger">
          Will be removed when you save
        </div>
        <Button icon={<Undo2 size={13} />} onClick={() => onChange(undefined)} disabled={disabled}>
          Undo
        </Button>
      </div>
    );
  }
  const shown = value === '\u0000' || value === undefined || value === null ? '' : value;
  const format = () => {
    try {
      onChange(JSON.stringify(JSON.parse(shown), null, 2));
    } catch {
      /* the field validation explains the problem */
    }
  };
  return (
    <div className="flex flex-col gap-2">
      <Textarea
        mono
        rows={rows}
        spellCheck={false}
        autoComplete="off"
        aria-label={ariaLabel}
        placeholder={placeholder}
        value={shown}
        disabled={disabled}
        autoFocus={value === '\u0000'}
        onChange={(e) => onChange(e.target.value === '' ? (has ? '\u0000' : undefined) : e.target.value)}
      />
      <div className="flex flex-wrap items-center gap-2">
        <Button size="xs" variant="ghost" icon={<Wand2 size={12} />} onClick={format} disabled={disabled || !shown.trim()}>
          Format
        </Button>
        {has && (
          <Button size="xs" variant="ghost" icon={<Undo2 size={12} />} onClick={() => onChange(undefined)} disabled={disabled}>
            Keep current value
          </Button>
        )}
      </div>
    </div>
  );
}
