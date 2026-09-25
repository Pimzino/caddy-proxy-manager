import { useId, type ReactNode } from 'react';
import { cn } from '@/lib/cn';

export interface SwitchProps {
  checked: boolean;
  onChange: (checked: boolean) => void;
  disabled?: boolean;
  size?: 'sm' | 'md';
  id?: string;
  'aria-label'?: string;
  'aria-labelledby'?: string;
  'aria-describedby'?: string;
  className?: string;
}

export function Switch({ checked, onChange, disabled, size = 'md', className, ...aria }: SwitchProps) {
  const track = size === 'sm' ? 'h-4 w-7' : 'h-5 w-9';
  const knob = size === 'sm' ? 'h-3 w-3' : 'h-4 w-4';
  const shift = size === 'sm' ? 'translate-x-3' : 'translate-x-4';
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      disabled={disabled}
      onClick={() => onChange(!checked)}
      className={cn(
        'relative inline-flex shrink-0 items-center rounded-full border border-transparent p-px transition-colors',
        'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring disabled:cursor-not-allowed disabled:opacity-50',
        checked ? 'bg-accent' : 'bg-border-strong',
        track,
        className,
      )}
      {...aria}
    >
      <span
        aria-hidden
        className={cn(
          'inline-block rounded-full bg-white shadow-sm transition-transform duration-150',
          knob,
          checked ? shift : 'translate-x-0',
        )}
      />
    </button>
  );
}

/** Switch with a label and optional description, the whole row being clickable. */
export function SwitchField({
  label,
  description,
  checked,
  onChange,
  disabled,
  className,
}: {
  label: ReactNode;
  description?: ReactNode;
  checked: boolean;
  onChange: (checked: boolean) => void;
  disabled?: boolean;
  className?: string;
}) {
  const id = useId();
  return (
    <div className={cn('flex items-start justify-between gap-4', className)}>
      <div className="min-w-0">
        <label
          id={`${id}-l`}
          htmlFor={id}
          className={cn('text-sm font-medium text-fg', disabled ? 'cursor-not-allowed opacity-60' : 'cursor-pointer')}
        >
          {label}
        </label>
        {description && (
          <p id={`${id}-d`} className="mt-0.5 text-xs text-fg-subtle">
            {description}
          </p>
        )}
      </div>
      <Switch
        id={id}
        checked={checked}
        onChange={onChange}
        disabled={disabled}
        aria-labelledby={`${id}-l`}
        aria-describedby={description ? `${id}-d` : undefined}
        className="mt-0.5"
      />
    </div>
  );
}
