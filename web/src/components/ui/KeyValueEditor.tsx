import { Plus, Trash2 } from 'lucide-react';
import { cn } from '@/lib/cn';
import { Button } from './Button';
import { Input, Select } from './Field';

export interface KeyValueRow {
  action?: string;
  name: string;
  value: string;
}

/**
 * Ordered list editor for name/value pairs, optionally with an action column
 * (used for header operations: set / add / delete).
 */
export function KeyValueEditor<R extends KeyValueRow>({
  rows,
  onChange,
  newRow,
  actions,
  nameLabel = 'Name',
  valueLabel = 'Value',
  namePlaceholder,
  valuePlaceholder,
  addLabel = 'Add',
  disabled,
  errors,
  errorPrefix,
  valueDisabledFor = [],
}: {
  rows: R[];
  onChange: (rows: R[]) => void;
  newRow: () => R;
  actions?: { value: string; label: string }[];
  nameLabel?: string;
  valueLabel?: string;
  namePlaceholder?: string;
  valuePlaceholder?: string;
  addLabel?: string;
  disabled?: boolean;
  errors?: Record<string, string>;
  errorPrefix?: string;
  /** Actions for which the value column is not applicable (e.g. "delete"). */
  valueDisabledFor?: string[];
}) {
  const update = (i: number, patch: Partial<R>) => onChange(rows.map((r, j) => (j === i ? { ...r, ...patch } : r)));
  const grid = actions ? 'sm:grid-cols-[110px_minmax(0,1fr)_minmax(0,1.4fr)_32px]' : 'sm:grid-cols-[minmax(0,1fr)_minmax(0,1.4fr)_32px]';
  return (
    <div className="flex flex-col gap-2">
      {rows.length > 0 && (
        <div className={cn('hidden gap-2 text-xs font-medium text-fg-subtle sm:grid', grid)}>
          {actions && <span>Action</span>}
          <span>{nameLabel}</span>
          <span>{valueLabel}</span>
          <span />
        </div>
      )}
      {rows.map((row, i) => {
        const nameErr = errors?.[`${errorPrefix}.${i}.name`];
        const valueErr = errors?.[`${errorPrefix}.${i}.value`];
        const valueNa = !!row.action && valueDisabledFor.includes(row.action);
        return (
          <div key={i} className="flex flex-col gap-1">
            <div className={cn('grid grid-cols-1 gap-2', grid)}>
              {actions && (
                <Select
                  aria-label={`Action for row ${i + 1}`}
                  value={row.action}
                  disabled={disabled}
                  onChange={(e) => update(i, { action: e.target.value } as Partial<R>)}
                >
                  {actions.map((a) => (
                    <option key={a.value} value={a.value}>
                      {a.label}
                    </option>
                  ))}
                </Select>
              )}
              <Input
                mono
                aria-label={`${nameLabel} ${i + 1}`}
                placeholder={namePlaceholder}
                value={row.name}
                disabled={disabled}
                invalid={!!nameErr}
                onChange={(e) => update(i, { name: e.target.value } as Partial<R>)}
              />
              <Input
                mono
                aria-label={`${valueLabel} ${i + 1}`}
                placeholder={valueNa ? 'Not used' : valuePlaceholder}
                value={valueNa ? '' : row.value}
                disabled={disabled || valueNa}
                invalid={!!valueErr}
                onChange={(e) => update(i, { value: e.target.value } as Partial<R>)}
              />
              <Button
                variant="ghost"
                iconOnly
                aria-label={`Remove row ${i + 1}`}
                disabled={disabled}
                icon={<Trash2 size={14} />}
                onClick={() => onChange(rows.filter((_, j) => j !== i))}
              />
            </div>
            {(nameErr || valueErr) && <p className="text-xs text-danger">{nameErr ?? valueErr}</p>}
          </div>
        );
      })}
      <div>
        <Button size="sm" icon={<Plus size={14} />} disabled={disabled} onClick={() => onChange([...rows, newRow()])}>
          {addLabel}
        </Button>
      </div>
    </div>
  );
}
