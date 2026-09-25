import { createContext, useCallback, useContext, useRef, useState, type ReactNode } from 'react';
import { AlertTriangle } from 'lucide-react';
import { Button } from './Button';
import { Dialog } from './Dialog';

export interface ConfirmOptions {
  title: string;
  message?: ReactNode;
  confirmLabel?: string;
  cancelLabel?: string;
  danger?: boolean;
  /** Informational: show only the confirm button (resolves true). */
  alertOnly?: boolean;
}

type ConfirmFn = (opts: ConfirmOptions) => Promise<boolean>;
const ConfirmContext = createContext<ConfirmFn | null>(null);

/** Promise-based confirmation dialog: `if (await confirm({...})) doIt()`. */
export function ConfirmProvider({ children }: { children: ReactNode }) {
  const [opts, setOpts] = useState<ConfirmOptions | null>(null);
  const resolver = useRef<((v: boolean) => void) | null>(null);
  const confirmRef = useRef<HTMLButtonElement>(null);

  const confirm = useCallback<ConfirmFn>((o) => {
    resolver.current?.(false);
    setOpts(o);
    return new Promise<boolean>((resolve) => {
      resolver.current = resolve;
    });
  }, []);

  const finish = (v: boolean) => {
    resolver.current?.(v);
    resolver.current = null;
    setOpts(null);
  };

  return (
    <ConfirmContext.Provider value={confirm}>
      {children}
      <Dialog
        open={!!opts}
        onClose={() => finish(false)}
        title={opts?.title ?? ''}
        size="sm"
        role="alertdialog"
        initialFocus={confirmRef}
        footer={
          <>
            {!opts?.alertOnly && <Button onClick={() => finish(false)}>{opts?.cancelLabel ?? 'Cancel'}</Button>}
            <Button ref={confirmRef} variant={opts?.danger ? 'danger' : 'primary'} onClick={() => finish(true)}>
              {opts?.confirmLabel ?? 'Confirm'}
            </Button>
          </>
        }
      >
        <div className="flex gap-3">
          {opts?.danger && (
            <span className="flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-danger-soft text-danger">
              <AlertTriangle size={16} aria-hidden />
            </span>
          )}
          <div className="text-sm text-fg-muted">{opts?.message}</div>
        </div>
      </Dialog>
    </ConfirmContext.Provider>
  );
}

export function useConfirm(): ConfirmFn {
  const ctx = useContext(ConfirmContext);
  if (!ctx) throw new Error('useConfirm must be used inside ConfirmProvider');
  return ctx;
}
