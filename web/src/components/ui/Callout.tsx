import type { ReactNode } from 'react';
import { AlertTriangle, CheckCircle2, Info, XCircle } from 'lucide-react';
import { cn } from '@/lib/cn';

const styles = {
  info: { box: 'border-info/30 bg-info-soft', icon: <Info size={16} className="text-info" /> },
  success: { box: 'border-success/30 bg-success-soft', icon: <CheckCircle2 size={16} className="text-success" /> },
  warning: { box: 'border-warning/35 bg-warning-soft', icon: <AlertTriangle size={16} className="text-warning" /> },
  danger: { box: 'border-danger/30 bg-danger-soft', icon: <XCircle size={16} className="text-danger" /> },
};

export function Callout({
  tone = 'info',
  title,
  children,
  actions,
  className,
}: {
  tone?: keyof typeof styles;
  title?: ReactNode;
  children?: ReactNode;
  actions?: ReactNode;
  className?: string;
}) {
  const s = styles[tone];
  return (
    <div
      className={cn('flex gap-3 rounded-md border px-3.5 py-3', s.box, className)}
      role={tone === 'danger' ? 'alert' : undefined}
    >
      <span className="mt-0.5 shrink-0" aria-hidden>
        {s.icon}
      </span>
      <div className="min-w-0 flex-1 text-sm">
        {title && <p className="font-medium text-fg">{title}</p>}
        {children && <div className={cn('text-fg-muted', title && 'mt-0.5')}>{children}</div>}
      </div>
      {actions && <div className="flex shrink-0 items-start gap-2">{actions}</div>}
    </div>
  );
}
