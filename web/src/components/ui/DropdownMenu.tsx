import { useEffect, useId, useRef, useState, type KeyboardEvent, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { cn } from '@/lib/cn';

export type MenuItem =
  | {
      label: ReactNode;
      icon?: ReactNode;
      onSelect: () => void;
      danger?: boolean;
      disabled?: boolean;
      hidden?: boolean;
      shortcut?: string;
    }
  | 'separator';

interface Position {
  focus: 'first' | 'last';
  top?: number;
  bottom?: number;
  left?: number;
  right?: number;
}

export interface DropdownTriggerProps {
  'aria-haspopup': 'menu';
  'aria-expanded': boolean;
  'aria-controls': string | undefined;
  onClick: (e: React.MouseEvent<HTMLElement>) => void;
  onKeyDown: (e: KeyboardEvent<HTMLElement>) => void;
  ref: (el: HTMLElement | null) => void;
}

/** Menu button (WAI-ARIA menu pattern). Rendered in a portal with fixed positioning so tables never clip it. */
export function DropdownMenu({
  trigger,
  items,
  align = 'end',
  header,
  width = 200,
}: {
  trigger: (props: DropdownTriggerProps) => ReactNode;
  items: MenuItem[];
  align?: 'start' | 'end';
  header?: ReactNode;
  width?: number;
}) {
  const id = useId();
  const [pos, setPos] = useState<Position | null>(null);
  // Element kept in state (not a ref) because trigger props are created during render.
  const [triggerEl, setTriggerEl] = useState<HTMLElement | null>(null);
  const menuRef = useRef<HTMLDivElement>(null);
  const visible = items.filter((i) => i === 'separator' || !i.hidden);
  const open = pos !== null;

  const openMenu = (focus: 'first' | 'last' = 'first') => {
    const el = triggerEl;
    if (!el) return;
    const r = el.getBoundingClientRect();
    const estimated = visible.length * 32 + (header ? 56 : 8);
    const p: Position = { focus };
    if (r.bottom + estimated + 8 > window.innerHeight && r.top > estimated) p.bottom = window.innerHeight - r.top + 4;
    else p.top = r.bottom + 4;
    if (align === 'end') p.right = Math.max(8, window.innerWidth - r.right);
    else p.left = Math.min(r.left, window.innerWidth - width - 8);
    setPos(p);
  };
  const close = (restoreFocus = true) => {
    setPos(null);
    if (restoreFocus) triggerEl?.focus();
  };

  const focusLast = pos?.focus === 'last';
  useEffect(() => {
    if (!open) return;
    const nodes = menuRef.current?.querySelectorAll<HTMLElement>('[role="menuitem"]:not([disabled])');
    if (nodes && nodes.length) (focusLast ? nodes[nodes.length - 1] : nodes[0]).focus();
    const onDown = (e: MouseEvent) => {
      const t = e.target as Node;
      if (menuRef.current?.contains(t) || triggerEl?.contains(t)) return;
      setPos(null);
    };
    const onScroll = (e: Event) => {
      if (menuRef.current?.contains(e.target as Node)) return;
      setPos(null);
    };
    const onResize = () => setPos(null);
    document.addEventListener('mousedown', onDown);
    window.addEventListener('scroll', onScroll, true);
    window.addEventListener('resize', onResize);
    return () => {
      document.removeEventListener('mousedown', onDown);
      window.removeEventListener('scroll', onScroll, true);
      window.removeEventListener('resize', onResize);
    };
  }, [open, triggerEl, focusLast]);

  const onMenuKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    const nodes = Array.from(menuRef.current?.querySelectorAll<HTMLElement>('[role="menuitem"]:not([disabled])') ?? []);
    const i = nodes.indexOf(document.activeElement as HTMLElement);
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      nodes[(i + 1) % nodes.length]?.focus();
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      nodes[(i - 1 + nodes.length) % nodes.length]?.focus();
    } else if (e.key === 'Home') {
      e.preventDefault();
      nodes[0]?.focus();
    } else if (e.key === 'End') {
      e.preventDefault();
      nodes[nodes.length - 1]?.focus();
    } else if (e.key === 'Escape') {
      e.preventDefault();
      e.stopPropagation();
      close();
    } else if (e.key === 'Tab') {
      close(false);
    } else if (e.key.length === 1 && /\S/.test(e.key)) {
      const ch = e.key.toLowerCase();
      const start = i + 1;
      for (let n = 0; n < nodes.length; n++) {
        const node = nodes[(start + n) % nodes.length];
        if (node.textContent?.trim().toLowerCase().startsWith(ch)) {
          node.focus();
          break;
        }
      }
    }
  };

  const triggerProps: DropdownTriggerProps = {
    'aria-haspopup': 'menu',
    'aria-expanded': open,
    'aria-controls': open ? `${id}-menu` : undefined,
    onClick: (e) => {
      e.stopPropagation();
      if (open) close(false);
      else openMenu();
    },
    onKeyDown: (e) => {
      if (e.key === 'ArrowDown') {
        e.preventDefault();
        openMenu('first');
      } else if (e.key === 'ArrowUp') {
        e.preventDefault();
        openMenu('last');
      }
    },
    ref: setTriggerEl,
  };

  return (
    <>
      {trigger(triggerProps)}
      {open &&
        createPortal(
          <div
            ref={menuRef}
            id={`${id}-menu`}
            role="menu"
            tabIndex={-1}
            onKeyDown={onMenuKeyDown}
            onClick={(e) => e.stopPropagation()}
            style={{ top: pos.top, bottom: pos.bottom, left: pos.left, right: pos.right, width, position: 'fixed' }}
            className="z-[60] animate-pop-in overflow-hidden rounded-md border border-border bg-surface py-1 shadow-pop"
          >
            {header && <div className="border-b border-border px-3 py-2">{header}</div>}
            {visible.map((item, i) =>
              item === 'separator' ? (
                <div key={`sep-${i}`} role="separator" className="my-1 h-px bg-border" />
              ) : (
                <button
                  key={i}
                  type="button"
                  role="menuitem"
                  tabIndex={-1}
                  disabled={item.disabled}
                  onClick={() => {
                    close();
                    item.onSelect();
                  }}
                  className={cn(
                    'flex h-8 w-full items-center gap-2 px-3 text-left text-sm outline-none',
                    'disabled:cursor-not-allowed disabled:opacity-50',
                    item.danger
                      ? 'text-danger hover:bg-danger-soft focus:bg-danger-soft'
                      : 'text-fg hover:bg-surface-2 focus:bg-surface-2',
                  )}
                >
                  {item.icon && <span className={cn('shrink-0', !item.danger && 'text-fg-subtle')}>{item.icon}</span>}
                  <span className="flex-1 truncate">{item.label}</span>
                  {item.shortcut && <span className="text-xs text-fg-subtle">{item.shortcut}</span>}
                </button>
              ),
            )}
          </div>,
          document.body,
        )}
    </>
  );
}
