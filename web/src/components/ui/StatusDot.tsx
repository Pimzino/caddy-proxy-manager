import { cn } from '@/lib/cn';
import type { Tone } from './Badge';

const dot: Record<Tone, string> = {
  neutral: 'bg-neutral',
  accent: 'bg-accent',
  success: 'bg-success',
  warning: 'bg-warning',
  danger: 'bg-danger',
  info: 'bg-info',
};

/** Coloured dot that is always paired with a text label (colour is never the only signal). */
export function StatusDot({
  tone,
  label,
  pulse,
  hideLabel,
  className,
}: {
  tone: Tone;
  label: string;
  pulse?: boolean;
  hideLabel?: boolean;
  className?: string;
}) {
  return (
    <span className={cn('inline-flex items-center gap-1.5 text-sm', className)} title={hideLabel ? label : undefined}>
      <span className="relative flex h-2 w-2 shrink-0" aria-hidden>
        {pulse && <span className={cn('absolute inline-flex h-full w-full animate-ping rounded-full opacity-50', dot[tone])} />}
        <span className={cn('relative inline-flex h-2 w-2 rounded-full', dot[tone])} />
      </span>
      <span className={hideLabel ? 'sr-only' : undefined}>{label}</span>
    </span>
  );
}
