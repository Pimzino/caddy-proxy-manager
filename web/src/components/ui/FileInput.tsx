import type { InputHTMLAttributes } from 'react';
import { cn } from '@/lib/cn';
import { useFieldProps } from './Field';

/** Native file input styled to match the console (keeps full keyboard and screen reader support). */
export function FileInput({
  onFile,
  className,
  id,
  ...rest
}: Omit<InputHTMLAttributes<HTMLInputElement>, 'type' | 'onChange'> & { onFile: (file: File | null) => void }) {
  const a11y = useFieldProps(id, undefined, rest['aria-describedby']);
  return (
    <input
      type="file"
      {...rest}
      {...a11y}
      onChange={(e) => onFile(e.target.files?.[0] ?? null)}
      className={cn(
        'block w-full rounded-md border border-dashed border-border-strong bg-surface-2/50 p-1.5 text-sm text-fg-muted',
        'file:mr-3 file:h-7 file:cursor-pointer file:rounded-md file:border file:border-border-strong file:bg-surface file:px-2.5 file:text-sm file:font-medium file:text-fg hover:file:bg-surface-2',
        'focus-visible:outline-2 focus-visible:outline-ring aria-[invalid=true]:border-danger',
        className,
      )}
    />
  );
}
