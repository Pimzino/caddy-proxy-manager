import { useEffect, useId, useRef, type ReactNode, type RefObject } from 'react';
import { createPortal } from 'react-dom';
import { X } from 'lucide-react';
import { cn } from '@/lib/cn';
import { Button } from './Button';

// Stack of open dialogs so Escape / focus trapping only affect the top-most one.
const stack: string[] = [];

const FOCUSABLE =
  'a[href], button:not([disabled]), input:not([disabled]):not([type="hidden"]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

export interface DialogProps {
  open: boolean;
  onClose: () => void;
  title: ReactNode;
  description?: ReactNode;
  children?: ReactNode;
  footer?: ReactNode;
  size?: 'sm' | 'md' | 'lg' | 'xl' | '2xl';
  /** "right" renders a full-height side drawer. */
  side?: 'center' | 'right';
  /** Element to focus when opened (defaults to the first field, then the panel). */
  initialFocus?: RefObject<HTMLElement | null>;
  /** When false, Escape and clicking the backdrop do nothing (e.g. while saving). */
  dismissible?: boolean;
  /** Extra content in the header, right of the title. */
  headerExtra?: ReactNode;
  className?: string;
  bodyClassName?: string;
  role?: 'dialog' | 'alertdialog';
}

const sizes = {
  sm: 'max-w-md',
  md: 'max-w-xl',
  lg: 'max-w-3xl',
  xl: 'max-w-5xl',
  '2xl': 'max-w-7xl',
};

export function Dialog(props: DialogProps) {
  if (!props.open) return null;
  return createPortal(<DialogInner {...props} />, document.body);
}

function DialogInner({
  onClose,
  title,
  description,
  children,
  footer,
  size = 'md',
  side = 'center',
  initialFocus,
  dismissible = true,
  headerExtra,
  className,
  bodyClassName,
  role = 'dialog',
}: DialogProps) {
  const id = useId();
  const panelRef = useRef<HTMLDivElement>(null);
  const onCloseRef = useRef(onClose);
  const dismissibleRef = useRef(dismissible);
  useEffect(() => {
    onCloseRef.current = onClose;
    dismissibleRef.current = dismissible;
  });

  useEffect(() => {
    stack.push(id);
    const previouslyFocused = document.activeElement as HTMLElement | null;
    const body = document.body;
    const prevOverflow = body.style.overflow;
    body.style.overflow = 'hidden';

    const panel = panelRef.current;
    const target =
      initialFocus?.current ??
      panel?.querySelector<HTMLElement>('[data-autofocus]') ??
      panel?.querySelector<HTMLElement>('input:not([type="hidden"]):not([disabled]), textarea:not([disabled]), select:not([disabled])') ??
      panel;
    target?.focus({ preventScroll: true });

    const onKeyDown = (e: KeyboardEvent) => {
      if (stack[stack.length - 1] !== id) return;
      if (e.key === 'Escape') {
        e.stopPropagation();
        if (dismissibleRef.current) onCloseRef.current();
        return;
      }
      if (e.key !== 'Tab' || !panelRef.current) return;
      const nodes = Array.from(panelRef.current.querySelectorAll<HTMLElement>(FOCUSABLE)).filter(
        (n) => n.offsetParent !== null || n === document.activeElement,
      );
      if (nodes.length === 0) {
        e.preventDefault();
        panelRef.current.focus();
        return;
      }
      const first = nodes[0];
      const last = nodes[nodes.length - 1];
      if (e.shiftKey && (document.activeElement === first || document.activeElement === panelRef.current)) {
        e.preventDefault();
        last.focus();
      } else if (!e.shiftKey && document.activeElement === last) {
        e.preventDefault();
        first.focus();
      }
    };
    document.addEventListener('keydown', onKeyDown);
    return () => {
      document.removeEventListener('keydown', onKeyDown);
      const i = stack.lastIndexOf(id);
      if (i >= 0) stack.splice(i, 1);
      if (stack.length === 0) body.style.overflow = prevOverflow;
      if (previouslyFocused && document.contains(previouslyFocused)) previouslyFocused.focus({ preventScroll: true });
    };
    // Mount/unmount only: the dialog is unmounted when closed.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const isDrawer = side === 'right';
  return (
    <div className="fixed inset-0 z-50 flex" role="presentation">
      <div
        className="absolute inset-0 animate-fade-in bg-overlay"
        aria-hidden
        onMouseDown={() => {
          if (dismissible) onClose();
        }}
      />
      <div
        className={cn(
          'relative flex w-full',
          isDrawer ? 'justify-end' : 'items-start justify-center overflow-y-auto p-4 sm:items-center sm:p-6',
        )}
        onMouseDown={(e) => {
          if (e.target === e.currentTarget && dismissible) onClose();
        }}
      >
        <div
          ref={panelRef}
          role={role}
          aria-modal="true"
          aria-labelledby={`${id}-title`}
          aria-describedby={description ? `${id}-desc` : undefined}
          tabIndex={-1}
          className={cn(
            'relative flex w-full flex-col border border-border bg-surface shadow-pop focus:outline-none',
            isDrawer
              ? cn('h-full animate-slide-in border-y-0 border-r-0', sizes[size])
              : cn('max-h-[calc(100dvh-2rem)] animate-pop-in rounded-lg sm:max-h-[calc(100dvh-3rem)]', sizes[size]),
            className,
          )}
        >
          <div className="flex shrink-0 items-start gap-3 border-b border-border px-5 py-3.5">
            <div className="min-w-0 flex-1">
              <h2 id={`${id}-title`} className="text-[15px] font-semibold text-fg">
                {title}
              </h2>
              {description && (
                <p id={`${id}-desc`} className="mt-0.5 text-sm text-fg-subtle">
                  {description}
                </p>
              )}
            </div>
            {headerExtra}
            <Button
              variant="ghost"
              size="sm"
              iconOnly
              aria-label="Close"
              onClick={onClose}
              disabled={!dismissible}
              icon={<X size={16} />}
              className="-mr-1.5"
            />
          </div>
          <div className={cn('min-h-0 flex-1 overflow-y-auto px-5 py-4', bodyClassName)}>{children}</div>
          {footer && (
            <div className="flex shrink-0 flex-wrap items-center justify-end gap-2 border-t border-border bg-surface-2/60 px-5 py-3">
              {footer}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
