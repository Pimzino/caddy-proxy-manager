import type { ReactNode } from 'react';
import { cn } from '@/lib/cn';

export function Card({ children, className }: { children: ReactNode; className?: string }) {
  return <div className={cn('rounded-lg border border-border bg-surface shadow-xs', className)}>{children}</div>;
}

export function CardHeader({
  title,
  description,
  actions,
  icon,
  className,
}: {
  title: ReactNode;
  description?: ReactNode;
  actions?: ReactNode;
  icon?: ReactNode;
  className?: string;
}) {
  return (
    <div className={cn('flex flex-wrap items-center gap-3 border-b border-border px-4 py-3', className)}>
      {icon && <span className="text-fg-subtle">{icon}</span>}
      <div className="min-w-0 flex-1">
        <h2 className="text-sm font-semibold text-fg">{title}</h2>
        {description && <p className="mt-0.5 text-xs text-fg-subtle">{description}</p>}
      </div>
      {actions && <div className="flex flex-wrap items-center gap-2">{actions}</div>}
    </div>
  );
}

export function CardBody({ children, className }: { children: ReactNode; className?: string }) {
  return <div className={cn('p-4', className)}>{children}</div>;
}

/** Page heading row with description and actions. */
export function PageHeader({
  title,
  description,
  actions,
}: {
  title: ReactNode;
  description?: ReactNode;
  actions?: ReactNode;
}) {
  return (
    <div className="mb-5 flex flex-wrap items-end justify-between gap-3">
      <div className="min-w-0">
        <h1 className="text-lg font-semibold tracking-tight text-fg">{title}</h1>
        {description && <p className="mt-0.5 max-w-3xl text-sm text-fg-subtle">{description}</p>}
      </div>
      {actions && <div className="flex flex-wrap items-center gap-2">{actions}</div>}
    </div>
  );
}

/** Label/value pairs for detail views. */
export function DescriptionList({
  items,
  className,
  columns = 1,
}: {
  items: { label: ReactNode; value: ReactNode; mono?: boolean; hidden?: boolean }[];
  className?: string;
  columns?: 1 | 2 | 3;
}) {
  const cols = { 1: '', 2: 'md:grid-cols-2', 3: 'md:grid-cols-2 xl:grid-cols-3' }[columns];
  return (
    <dl className={cn('grid gap-x-6 gap-y-2.5', cols, className)}>
      {items
        .filter((i) => !i.hidden)
        .map((item, i) => (
          <div key={i} className="grid min-w-0 grid-cols-[minmax(96px,34%)_minmax(0,1fr)] items-baseline gap-3">
            <dt className="text-sm text-fg-subtle">{item.label}</dt>
            <dd className={cn('min-w-0 text-sm break-words text-fg', item.mono && 'mono')}>{item.value}</dd>
          </div>
        ))}
    </dl>
  );
}
