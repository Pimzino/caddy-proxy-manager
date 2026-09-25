import {
  createContext,
  useContext,
  useId,
  type InputHTMLAttributes,
  type ReactNode,
  type Ref,
  type SelectHTMLAttributes,
  type TextareaHTMLAttributes,
} from 'react';
import { ChevronDown } from 'lucide-react';
import { cn } from '@/lib/cn';

interface FieldCtx {
  id: string;
  describedBy?: string;
  invalid: boolean;
}
const FieldContext = createContext<FieldCtx | null>(null);

export interface FieldProps {
  label?: ReactNode;
  hint?: ReactNode;
  error?: string | null;
  required?: boolean;
  className?: string;
  /** Put label and control side by side (settings forms). */
  inline?: boolean;
  children: ReactNode;
  labelAction?: ReactNode;
}

/** Label + control + hint/error, wiring ids and aria-describedby for the nested control automatically. */
export function Field({ label, hint, error, required, className, children, labelAction }: FieldProps) {
  const id = useId();
  const hintId = hint ? `${id}-hint` : undefined;
  const errorId = error ? `${id}-err` : undefined;
  const describedBy = [errorId, hintId].filter(Boolean).join(' ') || undefined;
  return (
    <FieldContext.Provider value={{ id, describedBy, invalid: !!error }}>
      <div className={cn('flex min-w-0 flex-col gap-1.5', className)}>
        {(label || labelAction) && (
          <div className="flex items-center justify-between gap-2">
            {label && (
              <label htmlFor={id} className="text-sm font-medium text-fg">
                {label}
                {required && (
                  <span className="ml-0.5 text-danger" aria-hidden>
                    *
                  </span>
                )}
              </label>
            )}
            {labelAction}
          </div>
        )}
        {children}
        {error && (
          <p id={errorId} className="text-xs text-danger" role="alert">
            {error}
          </p>
        )}
        {hint && (
          <p id={hintId} className="text-xs text-fg-subtle">
            {hint}
          </p>
        )}
      </div>
    </FieldContext.Provider>
  );
}

export function useFieldProps(id?: string, invalid?: boolean, describedBy?: string) {
  const ctx = useContext(FieldContext);
  return {
    id: id ?? ctx?.id,
    'aria-invalid': invalid || ctx?.invalid || undefined,
    'aria-describedby': [describedBy, ctx?.describedBy].filter(Boolean).join(' ') || undefined,
  };
}

export const controlBase =
  'w-full rounded-md border bg-surface text-fg placeholder:text-fg-subtle/80 shadow-xs ' +
  'border-border-strong transition-colors hover:border-fg-subtle/60 ' +
  'focus:outline-none focus-visible:outline-none focus:border-accent focus:ring-2 focus:ring-ring/25 ' +
  'disabled:cursor-not-allowed disabled:bg-surface-2 disabled:text-fg-muted ' +
  'aria-[invalid=true]:border-danger aria-[invalid=true]:focus:ring-danger/20';

export interface InputProps extends Omit<InputHTMLAttributes<HTMLInputElement>, 'size'> {
  mono?: boolean;
  invalid?: boolean;
  inputSize?: 'sm' | 'md';
  leading?: ReactNode;
  trailing?: ReactNode;
  ref?: Ref<HTMLInputElement>;
}

export function Input({ mono, invalid, inputSize = 'md', leading, trailing, className, id, ref, ...rest }: InputProps) {
  const a11y = useFieldProps(id, invalid, rest['aria-describedby']);
  const input = (
    <input
      ref={ref}
      {...rest}
      {...a11y}
      className={cn(
        controlBase,
        'read-only:bg-surface-2',
        inputSize === 'sm' ? 'h-7 px-2 text-sm' : 'h-8 px-2.5 text-sm',
        mono && 'mono',
        leading ? 'pl-8' : undefined,
        trailing ? 'pr-8' : undefined,
        !leading && !trailing && className,
      )}
    />
  );
  if (!leading && !trailing) return input;
  return (
    <div className={cn('relative', className)}>
      {leading && (
        <span className="pointer-events-none absolute inset-y-0 left-2.5 flex items-center text-fg-subtle">{leading}</span>
      )}
      {input}
      {trailing && <span className="absolute inset-y-0 right-1 flex items-center">{trailing}</span>}
    </div>
  );
}

export interface NumberInputProps extends Omit<InputProps, 'value' | 'onChange' | 'type'> {
  value: number | null | undefined;
  onValueChange: (value: number) => void;
}

/** Integer input that reports NaN while the field is empty so validation can flag it. */
export function NumberInput({ value, onValueChange, ...rest }: NumberInputProps) {
  return (
    <Input
      type="number"
      inputMode="numeric"
      mono
      {...rest}
      value={value === null || value === undefined || Number.isNaN(value) ? '' : value}
      onChange={(e) => onValueChange(e.target.value === '' ? Number.NaN : Number(e.target.value))}
    />
  );
}

export interface TextareaProps extends TextareaHTMLAttributes<HTMLTextAreaElement> {
  mono?: boolean;
  invalid?: boolean;
  ref?: Ref<HTMLTextAreaElement>;
}

export function Textarea({ mono, invalid, className, id, ref, ...rest }: TextareaProps) {
  const a11y = useFieldProps(id, invalid, rest['aria-describedby']);
  return (
    <textarea
      ref={ref}
      {...rest}
      {...a11y}
      className={cn(controlBase, 'read-only:bg-surface-2 min-h-20 px-2.5 py-1.5 text-sm leading-relaxed', mono && 'mono', className)}
    />
  );
}

export interface SelectProps extends Omit<SelectHTMLAttributes<HTMLSelectElement>, 'size'> {
  invalid?: boolean;
  selectSize?: 'sm' | 'md';
  mono?: boolean;
  ref?: Ref<HTMLSelectElement>;
}

export function Select({ invalid, className, id, children, selectSize = 'md', mono, ref, ...rest }: SelectProps) {
  const a11y = useFieldProps(id, invalid, rest['aria-describedby']);
  return (
    <div className={cn('relative', className)}>
      <select
        ref={ref}
        {...rest}
        {...a11y}
        className={cn(
          controlBase,
          'appearance-none pr-7',
          selectSize === 'sm' ? 'h-7 pl-2 text-sm' : 'h-8 pl-2.5 text-sm',
          mono && 'mono',
        )}
      >
        {children}
      </select>
      <ChevronDown
        size={14}
        className="pointer-events-none absolute top-1/2 right-2 -translate-y-1/2 text-fg-subtle"
        aria-hidden
      />
    </div>
  );
}

/** Section of a form with a heading and optional description, laid out as a 2-column settings row on wide screens. */
export function FormSection({
  title,
  description,
  children,
  actions,
}: {
  title: ReactNode;
  description?: ReactNode;
  children: ReactNode;
  actions?: ReactNode;
}) {
  return (
    <section className="grid gap-x-8 gap-y-4 border-b border-border py-6 first:pt-0 last:border-b-0 last:pb-0 xl:grid-cols-[260px_minmax(0,1fr)]">
      <div>
        <h3 className="text-sm font-semibold text-fg">{title}</h3>
        {description && <p className="mt-1 text-sm text-fg-subtle">{description}</p>}
        {actions && <div className="mt-3">{actions}</div>}
      </div>
      <div className="flex min-w-0 flex-col gap-4">{children}</div>
    </section>
  );
}
