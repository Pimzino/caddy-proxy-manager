import type { ButtonHTMLAttributes, ReactNode, Ref } from 'react';
import { cn } from '@/lib/cn';
import { Spinner } from './Spinner';

export type ButtonVariant = 'primary' | 'secondary' | 'ghost' | 'danger' | 'danger-ghost' | 'link';
export type ButtonSize = 'xs' | 'sm' | 'md';

const variants: Record<ButtonVariant, string> = {
  primary: 'bg-accent text-accent-fg border-transparent hover:bg-accent-hover shadow-xs',
  secondary: 'bg-surface text-fg border-border-strong hover:bg-surface-2 shadow-xs',
  ghost: 'bg-transparent text-fg-muted border-transparent hover:bg-surface-2 hover:text-fg',
  danger: 'bg-danger text-white border-transparent hover:bg-danger-hover shadow-xs dark:text-[#1a0808]',
  'danger-ghost': 'bg-transparent text-danger border-transparent hover:bg-danger-soft',
  link: 'bg-transparent text-accent-text border-transparent hover:underline px-0! h-auto!',
};

const sizes: Record<ButtonSize, string> = {
  xs: 'h-6 px-2 text-xs gap-1',
  sm: 'h-7 px-2.5 text-sm gap-1.5',
  md: 'h-8 px-3 text-sm gap-1.5',
};

const iconSizes: Record<ButtonSize, string> = { xs: 'h-6 w-6', sm: 'h-7 w-7', md: 'h-8 w-8' };

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: ButtonVariant;
  size?: ButtonSize;
  loading?: boolean;
  icon?: ReactNode;
  /** Square icon-only button. Requires aria-label. */
  iconOnly?: boolean;
  ref?: Ref<HTMLButtonElement>;
}

export function Button({
  variant = 'secondary',
  size = 'md',
  loading = false,
  icon,
  iconOnly = false,
  className,
  children,
  disabled,
  type = 'button',
  ref,
  ...rest
}: ButtonProps) {
  return (
    <button
      ref={ref}
      type={type}
      disabled={disabled || loading}
      aria-busy={loading || undefined}
      className={cn(
        'inline-flex shrink-0 items-center justify-center rounded-md border font-medium whitespace-nowrap select-none',
        'transition-colors duration-100 disabled:opacity-50 disabled:cursor-not-allowed',
        'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring',
        variants[variant],
        iconOnly ? cn(iconSizes[size], 'p-0') : sizes[size],
        className,
      )}
      {...rest}
    >
      {loading ? <Spinner size={size === 'xs' ? 12 : 14} /> : icon}
      {children}
    </button>
  );
}
