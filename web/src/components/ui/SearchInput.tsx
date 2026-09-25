import { Search, X } from 'lucide-react';
import { cn } from '@/lib/cn';
import { Input } from './Field';

export function SearchInput({
  value,
  onChange,
  placeholder = 'Search…',
  className,
  'aria-label': ariaLabel = 'Search',
}: {
  value: string;
  onChange: (v: string) => void;
  placeholder?: string;
  className?: string;
  'aria-label'?: string;
}) {
  return (
    <Input
      type="search"
      value={value}
      onChange={(e) => onChange(e.target.value)}
      onKeyDown={(e) => {
        if (e.key === 'Escape' && value) {
          e.stopPropagation();
          onChange('');
        }
      }}
      placeholder={placeholder}
      aria-label={ariaLabel}
      className={cn('w-full sm:w-64', className)}
      leading={<Search size={14} />}
      trailing={
        value ? (
          <button
            type="button"
            onClick={() => onChange('')}
            className="flex h-6 w-6 items-center justify-center rounded text-fg-subtle hover:bg-surface-2 hover:text-fg"
            aria-label="Clear search"
          >
            <X size={13} />
          </button>
        ) : undefined
      }
    />
  );
}
