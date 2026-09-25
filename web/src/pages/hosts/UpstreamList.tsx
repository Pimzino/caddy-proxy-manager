import { Plus, Trash2 } from 'lucide-react';
import type { Upstream } from '@/api/types';
import { Button, Input, NumberInput, Select } from '@/components/ui';
import { cn } from '@/lib/cn';
import type { FieldErrors } from '@/lib/validation';
import { newUpstream, parseUpstreamUrl } from './hostModel';

/** Editable list of upstream servers (scheme / host / port). Pasting a URL into the host splits it. */
export function UpstreamList({
  value,
  onChange,
  errors,
  prefix,
  disabled,
}: {
  value: Upstream[];
  onChange: (next: Upstream[]) => void;
  errors: FieldErrors;
  prefix: string;
  disabled?: boolean;
}) {
  const update = (i: number, patch: Partial<Upstream>) => onChange(value.map((u, j) => (j === i ? { ...u, ...patch } : u)));
  const listError = errors[prefix];
  return (
    <div className="flex flex-col gap-2">
      {value.length > 0 && (
        <div className="hidden grid-cols-[96px_minmax(0,1fr)_96px_32px] gap-2 text-xs font-medium text-fg-subtle sm:grid">
          <span>Scheme</span>
          <span>Host or IP</span>
          <span>Port</span>
          <span />
        </div>
      )}
      {value.map((u, i) => {
        const hostErr = errors[`${prefix}.${i}.host`];
        const portErr = errors[`${prefix}.${i}.port`];
        return (
          <div key={i} className="flex flex-col gap-1">
            <div className="grid grid-cols-[88px_minmax(0,1fr)_84px_32px] gap-2 sm:grid-cols-[96px_minmax(0,1fr)_96px_32px]">
              <Select
                aria-label={`Upstream ${i + 1} scheme`}
                value={u.scheme}
                disabled={disabled}
                onChange={(e) => {
                  const scheme = e.target.value as Upstream['scheme'];
                  const port = scheme === 'https' && u.port === 80 ? 443 : scheme === 'http' && u.port === 443 ? 80 : u.port;
                  update(i, { scheme, port });
                }}
              >
                <option value="http">http</option>
                <option value="https">https</option>
              </Select>
              <Input
                mono
                aria-label={`Upstream ${i + 1} host`}
                placeholder="10.0.0.20 or app01.corp.local"
                value={u.host}
                disabled={disabled}
                invalid={!!hostErr}
                onChange={(e) => {
                  const parsed = parseUpstreamUrl(e.target.value);
                  update(i, parsed ?? { host: e.target.value });
                }}
              />
              <NumberInput
                aria-label={`Upstream ${i + 1} port`}
                min={1}
                max={65535}
                value={u.port}
                disabled={disabled}
                invalid={!!portErr}
                onValueChange={(port) => update(i, { port })}
              />
              <Button
                variant="ghost"
                iconOnly
                aria-label={`Remove upstream ${i + 1}`}
                icon={<Trash2 size={14} />}
                disabled={disabled}
                onClick={() => onChange(value.filter((_, j) => j !== i))}
              />
            </div>
            {(hostErr || portErr) && (
              <p className="text-xs text-danger" role="alert">
                {[hostErr, portErr].filter(Boolean).join(' ')}
              </p>
            )}
          </div>
        );
      })}
      {listError && (
        <p className="text-xs text-danger" role="alert">
          {listError}
        </p>
      )}
      <div className={cn(value.length === 0 && 'pt-1')}>
        <Button size="sm" icon={<Plus size={14} />} disabled={disabled} onClick={() => onChange([...value, newUpstream()])}>
          Add upstream
        </Button>
      </div>
    </div>
  );
}
