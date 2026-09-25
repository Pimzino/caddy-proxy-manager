import { useId, type ReactNode } from 'react';
import { cn } from '@/lib/cn';

export function Checkbox({
  checked,
  onChange,
  label,
  description,
  disabled,
  className,
}: {
  checked: boolean;
  onChange: (checked: boolean) => void;
  label: ReactNode;
  description?: ReactNode;
  disabled?: boolean;
  className?: string;
}) {
  const id = useId();
  return (
    <div className={cn('flex items-start gap-2', className)}>
      <input
        id={id}
        type="checkbox"
        checked={checked}
        disabled={disabled}
        onChange={(e) => onChange(e.target.checked)}
        aria-describedby={description ? `${id}-d` : undefined}
        className="mt-0.5 h-4 w-4 shrink-0 rounded border-border-strong accent-[var(--accent)] disabled:cursor-not-allowed"
      />
      <div className="min-w-0">
        <label htmlFor={id} className={cn('text-sm text-fg', disabled ? 'opacity-60' : 'cursor-pointer')}>
          {label}
        </label>
        {description && (
          <p id={`${id}-d`} className="text-xs text-fg-subtle">
            {description}
          </p>
        )}
      </div>
    </div>
  );
}
