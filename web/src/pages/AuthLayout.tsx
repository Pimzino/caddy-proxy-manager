import type { ReactNode } from 'react';
import { Logo } from '@/components/layout/Logo';

export function AuthLayout({ title, description, children, footer }: { title: string; description?: ReactNode; children: ReactNode; footer?: ReactNode }) {
  return (
    <div className="flex min-h-dvh flex-col items-center justify-center bg-bg px-4 py-10">
      <div className="w-full max-w-sm">
        <div className="mb-7 flex justify-center">
          <Logo className="h-24" />
        </div>
        <div className="rounded-lg border border-border bg-surface p-6 shadow-xs">
          <h1 className="text-base font-semibold text-fg">{title}</h1>
          {description && <p className="mt-1 text-sm text-fg-subtle">{description}</p>}
          <div className="mt-5">{children}</div>
        </div>
        {footer && <div className="mt-4 text-center text-xs text-fg-subtle">{footer}</div>}
      </div>
    </div>
  );
}
