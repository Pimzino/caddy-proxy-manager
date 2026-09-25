import { createContext, useCallback, useContext, useMemo, useRef, useState, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { AlertTriangle, CheckCircle2, Info, X, XCircle } from 'lucide-react';
import { cn } from '@/lib/cn';

export type ToastTone = 'success' | 'warning' | 'error' | 'info';

interface ToastItem {
  id: number;
  tone: ToastTone;
  title: string;
  description?: ReactNode;
  duration: number;
}

type ToastFn = (title: string, description?: ReactNode, opts?: { duration?: number }) => void;

export interface ToastApi {
  success: ToastFn;
  warning: ToastFn;
  error: ToastFn;
  info: ToastFn;
}

const ToastContext = createContext<ToastApi | null>(null);

const toneStyles: Record<ToastTone, { icon: ReactNode; bar: string; label: string }> = {
  success: { icon: <CheckCircle2 size={16} className="text-success" />, bar: 'bg-success', label: 'Success' },
  warning: { icon: <AlertTriangle size={16} className="text-warning" />, bar: 'bg-warning', label: 'Warning' },
  error: { icon: <XCircle size={16} className="text-danger" />, bar: 'bg-danger', label: 'Error' },
  info: { icon: <Info size={16} className="text-info" />, bar: 'bg-info', label: 'Info' },
};

export function ToastProvider({ children }: { children: ReactNode }) {
  const [items, setItems] = useState<ToastItem[]>([]);
  const nextId = useRef(1);
  const timers = useRef(new Map<number, number>());

  const dismiss = useCallback((id: number) => {
    setItems((list) => list.filter((t) => t.id !== id));
    const timer = timers.current.get(id);
    if (timer) window.clearTimeout(timer);
    timers.current.delete(id);
  }, []);

  const schedule = useCallback(
    (id: number, ms: number) => {
      if (ms <= 0) return;
      timers.current.set(id, window.setTimeout(() => dismiss(id), ms));
    },
    [dismiss],
  );

  const push = useCallback(
    (tone: ToastTone, title: string, description?: ReactNode, opts?: { duration?: number }) => {
      const id = nextId.current++;
      const duration = opts?.duration ?? (tone === 'error' ? 9000 : tone === 'warning' ? 8000 : 4500);
      setItems((list) => [...list.slice(-4), { id, tone, title, description, duration }]);
      schedule(id, duration);
    },
    [schedule],
  );

  const api = useMemo<ToastApi>(
    () => ({
      success: (t, d, o) => push('success', t, d, o),
      warning: (t, d, o) => push('warning', t, d, o),
      error: (t, d, o) => push('error', t, d, o),
      info: (t, d, o) => push('info', t, d, o),
    }),
    [push],
  );

  return (
    <ToastContext.Provider value={api}>
      {children}
      {createPortal(
        <div
          aria-live="polite"
          aria-relevant="additions"
          className="pointer-events-none fixed right-4 bottom-4 z-[70] flex w-[min(380px,calc(100vw-2rem))] flex-col gap-2"
        >
          {items.map((t) => {
            const s = toneStyles[t.tone];
            return (
              <div
                key={t.id}
                role={t.tone === 'error' ? 'alert' : 'status'}
                onMouseEnter={() => {
                  const timer = timers.current.get(t.id);
                  if (timer) window.clearTimeout(timer);
                }}
                onMouseLeave={() => schedule(t.id, 2500)}
                className="pointer-events-auto relative flex animate-pop-in gap-3 overflow-hidden rounded-md border border-border bg-surface py-3 pr-2 pl-4 shadow-pop"
              >
                <span className={cn('absolute inset-y-0 left-0 w-1', s.bar)} aria-hidden />
                <span className="mt-0.5 shrink-0">{s.icon}</span>
                <div className="min-w-0 flex-1">
                  <p className="text-sm font-medium text-fg">
                    <span className="sr-only">{s.label}: </span>
                    {t.title}
                  </p>
                  {t.description && <div className="mt-0.5 text-sm break-words text-fg-muted">{t.description}</div>}
                </div>
                <button
                  type="button"
                  onClick={() => dismiss(t.id)}
                  className="h-6 w-6 shrink-0 rounded text-fg-subtle hover:bg-surface-2 hover:text-fg focus-visible:outline-2 focus-visible:outline-ring"
                  aria-label="Dismiss notification"
                >
                  <X size={14} className="mx-auto" />
                </button>
              </div>
            );
          })}
        </div>,
        document.body,
      )}
    </ToastContext.Provider>
  );
}

export function useToast(): ToastApi {
  const ctx = useContext(ToastContext);
  if (!ctx) throw new Error('useToast must be used inside ToastProvider');
  return ctx;
}
