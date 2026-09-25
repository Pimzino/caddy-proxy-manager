import type { ReactNode } from 'react';
import { cn } from '@/lib/cn';

export type Tone = 'neutral' | 'accent' | 'success' | 'warning' | 'danger' | 'info';

const tones: Record<Tone, string> = {
  neutral: 'bg-neutral-soft text-fg-muted border-border',
  accent: 'bg-accent-soft text-accent-text border-accent/30',
  success: 'bg-success-soft text-success border-success/25',
  warning: 'bg-warning-soft text-warning border-warning/30',
  danger: 'bg-danger-soft text-danger border-danger/25',
  info: 'bg-info-soft text-info border-info/25',
};

export function Badge({
  tone = 'neutral',
  children,
  icon,
  mono,
  className,
  title,
}: {
  tone?: Tone;
  children: ReactNode;
  icon?: ReactNode;
  mono?: boolean;
  className?: string;
  title?: string;
}) {
  return (
    <span
      title={title}
      className={cn(
        'inline-flex h-5 max-w-full shrink-0 items-center gap-1 rounded border px-1.5 text-xs font-medium whitespace-nowrap',
        tones[tone],
        mono && 'mono',
        className,
      )}
    >
      {icon}
      <span className="truncate">{children}</span>
    </span>
  );
}

/** Monospace chip for technical values (domains, IPs, ports). */
export function Chip({ children, className, title }: { children: ReactNode; className?: string; title?: string }) {
  return (
    <span
      title={title}
      className={cn(
        'mono inline-flex h-5 max-w-full items-center truncate rounded border border-border bg-surface-2 px-1.5 text-xs text-fg',
        className,
      )}
    >
      {children}
    </span>
  );
}
