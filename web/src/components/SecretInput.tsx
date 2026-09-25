import { KeyRound, Undo2 } from 'lucide-react';
import { Button, Input } from '@/components/ui';

/**
 * Write-only secret field (SPEC settings rule): the API never returns secrets, only has<Name>.
 * value: undefined = unchanged, "" = clear, other = set.
 */
export function SecretInput({
  has,
  value,
  onChange,
  disabled,
  placeholder = 'Enter a new value',
  'aria-label': ariaLabel,
}: {
  has: boolean;
  value: string | null | undefined;
  onChange: (v: string | undefined) => void;
  disabled?: boolean;
  placeholder?: string;
  'aria-label'?: string;
}) {
  if (has && (value === undefined || value === null)) {
    return (
      <div className="flex items-center gap-2">
        <div className="flex h-8 min-w-0 flex-1 items-center gap-2 rounded-md border border-border-strong bg-surface-2 px-2.5 text-sm text-fg-muted">
          <KeyRound size={13} aria-hidden />
          <span className="truncate">Stored securely — not shown</span>
        </div>
        <Button size="md" onClick={() => onChange('\u0000')} disabled={disabled}>
          Change
        </Button>
        <Button size="md" variant="danger-ghost" onClick={() => onChange('')} disabled={disabled}>
          Clear
        </Button>
      </div>
    );
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
  // "\u0000" marks "change requested, nothing typed yet".
  const shown = value === '\u0000' || value === undefined || value === null ? '' : value;
  return (
    <div className="flex items-center gap-2">
      <Input
        type="password"
        autoComplete="new-password"
        placeholder={has ? placeholder : 'Not set'}
        aria-label={ariaLabel}
        value={shown}
        disabled={disabled}
        autoFocus={value === '\u0000'}
        onChange={(e) => onChange(e.target.value === '' ? (has ? '\u0000' : undefined) : e.target.value)}
        className="flex-1"
      />
      {has && (
        <Button icon={<Undo2 size={13} />} onClick={() => onChange(undefined)} disabled={disabled}>
          Keep current
        </Button>
      )}
    </div>
  );
}

/** Converts the SecretInput state into the API's write-only field value. */
export function secretPayload(value: string | null | undefined): string | undefined {
  if (value === undefined || value === null || value === '\u0000') return undefined;
  return value;
}
