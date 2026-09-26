import { useEffect, useId, useMemo, useRef, useState, type KeyboardEvent, type ReactNode } from 'react';
import { useLocation } from 'react-router';
import { Check, ChevronDown, ExternalLink, Search } from 'lucide-react';
import { errorMessage } from '@/api/client';
import type { AcmeChallengeType, CaddySettings, CaddySettingsInput, DnsProviderField, DnsProviderInfo } from '@/api/types';
import { SecretInput } from '@/components/SecretInput';
import { Badge, Callout, Checkbox, ChipInput, controlBase, Field, FormSection, Input, NumberInput, RadioCards, Spinner, SwitchField } from '@/components/ui';
import { cn } from '@/lib/cn';
import type { FieldErrors } from '@/lib/validation';
import { hasStoredSecret, isResolverAddress } from './caddyForm';
import { PluginRequirement } from './PluginInstallAction';
import { fieldError } from './shared';

/** Field keys this section displays (for the form's "unplaced errors" summary). */
export const DNS_FIELDS = [
  'defaultAcmeChallenge', 'dnsProvider', 'dnsProviderOptions', 'dnsProviderSecrets', 'dnsPropagationDelaySeconds',
  'dnsPropagationTimeoutSeconds', 'dnsTtlSeconds', 'dnsResolvers',
];

type Set = <K extends keyof CaddySettingsInput>(k: K, v: CaddySettingsInput[K]) => void;

/**
 * Settings › Caddy › "ACME challenge" (SPEC round 3 DNS-01): default challenge, DNS provider from the caddy-dns catalog
 * with typed fields (secrets write-only), propagation/TTL/resolvers, and the plugin rebuild
 * flow when the provider module is not in the installed Caddy.
 */
export function AcmeChallengeSection({
  settings,
  form,
  set,
  errors,
  providers,
  disabled,
}: {
  settings: CaddySettings;
  form: CaddySettingsInput;
  set: Set;
  errors: FieldErrors;
  providers: { data?: DnsProviderInfo[]; isPending: boolean; isError: boolean; error: unknown };
  /** Viewer role or a managed cluster node. */
  disabled: boolean;
}) {
  const location = useLocation();
  const anchor = useRef<HTMLSpanElement>(null);
  const provider = form.dnsProvider ? providers.data?.find((p) => p.name === form.dnsProvider) : undefined;

  useEffect(() => {
    if (location.hash === '#acme-challenge') anchor.current?.scrollIntoView({ block: 'start', behavior: 'smooth' });
  }, [location.hash]);

  const chooseProvider = (name: string | null) => {
    if (name === (form.dnsProvider ?? null)) return;
    set('dnsProvider', name);
    // Values of another provider never carry over (the server clears them too); back to the saved provider restores nothing
    // typed, but its stored secrets count again.
    set('dnsProviderOptions', name && name === settings.dnsProvider ? settings.dnsProviderOptions : {});
    set('dnsProviderSecrets', undefined);
  };

  return (
    <FormSection
      title={
        <span id="acme-challenge" ref={anchor} className="scroll-mt-20">
          ACME challenge
        </span>
      }
      description="How the CA verifies that you control a domain before it issues a certificate. Hosts can override the default in their TLS tab."
    >
      <Field label="Default challenge" error={errors.defaultAcmeChallenge}>
        <RadioCards<AcmeChallengeType>
          aria-label="Default ACME challenge"
          value={form.defaultAcmeChallenge}
          onChange={(v) => set('defaultAcmeChallenge', v)}
          disabled={disabled}
          options={[
            {
              value: 'http',
              label: 'HTTP-01 / TLS-ALPN-01',
              description: 'The CA connects to this server on port 80 or 443. Needs inbound access from the Internet; no DNS credentials.',
            },
            {
              value: 'dns',
              label: 'DNS-01',
              description: 'Caddy publishes a TXT record through your DNS provider’s API. No inbound ports needed; required for wildcard certificates.',
            },
          ]}
        />
      </Field>
      {form.defaultAcmeChallenge === 'http' && form.dnsProvider && (
        <p className="-mt-2 text-xs text-fg-subtle">
          With a DNS provider configured, wildcard names and hosts set to DNS-01 use it; all other hosts keep HTTP-01 / TLS-ALPN-01.
        </p>
      )}

      <Field
        label="DNS provider"
        error={fieldError(errors, 'dnsProvider')}
        required={form.defaultAcmeChallenge === 'dns'}
        hint="Provider modules come from github.com/caddy-dns and must be compiled into Caddy."
      >
        {providers.isError ? (
          <Callout tone="danger" title="Could not load the DNS provider list">
            {errorMessage(providers.error)}
          </Callout>
        ) : (
          <ProviderPicker providers={providers.data} loading={providers.isPending} value={form.dnsProvider ?? null} onChange={chooseProvider} disabled={disabled} />
        )}
      </Field>

      {form.dnsProvider && !provider && providers.data && (
        <Callout tone="warning">
          The configured provider <span className="mono">{form.dnsProvider}</span> is not in the catalog. Choose another provider or remove it.
        </Callout>
      )}

      {provider && (
        <ProviderDetails provider={provider} settings={settings} form={form} set={set} errors={errors} disabled={disabled} />
      )}

      {(form.dnsProvider || form.defaultAcmeChallenge === 'dns') && (
        <AdvancedDns form={form} set={set} errors={errors} disabled={disabled} />
      )}
    </FormSection>
  );
}

// ---------------------------------------------------------------- provider picker (searchable listbox)

function InstalledBadge({ installed }: { installed: boolean }) {
  return installed ? (
    <Badge tone="success" icon={<Check size={11} />}>
      Installed
    </Badge>
  ) : (
    <Badge tone="neutral">Not installed</Badge>
  );
}

function ProviderPicker({
  providers,
  loading,
  value,
  onChange,
  disabled,
}: {
  providers?: DnsProviderInfo[];
  loading: boolean;
  value: string | null;
  onChange: (name: string | null) => void;
  disabled: boolean;
}) {
  const id = useId();
  const root = useRef<HTMLDivElement>(null);
  const trigger = useRef<HTMLButtonElement>(null);
  const [open, setOpen] = useState(false);
  const [q, setQ] = useState('');
  const [active, setActive] = useState(0);
  const selected = providers?.find((p) => p.name === value);

  // "None" first, then the catalog (server order = popularity) filtered by label, name or package.
  const options = useMemo(() => {
    const n = q.trim().toLowerCase();
    const list: (DnsProviderInfo | null)[] = (providers ?? []).filter((p) => !n || `${p.label} ${p.name} ${p.package}`.toLowerCase().includes(n));
    return n ? list : [null, ...list];
  }, [providers, q]);

  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (root.current && !root.current.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener('mousedown', onDown);
    return () => document.removeEventListener('mousedown', onDown);
  }, [open]);

  useEffect(() => {
    if (open) document.getElementById(`${id}-opt-${active}`)?.scrollIntoView({ block: 'nearest' });
  }, [active, open, id]);

  const show = () => {
    setQ('');
    const i = value ? (providers ?? []).findIndex((p) => p.name === value) + 1 : 0;
    setActive(Math.max(0, i));
    setOpen(true);
  };
  const pick = (p: DnsProviderInfo | null) => {
    onChange(p?.name ?? null);
    setOpen(false);
    trigger.current?.focus();
  };
  const onKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      if (options.length) setActive((a) => (a + (e.key === 'ArrowDown' ? 1 : -1) + options.length) % options.length);
    } else if (e.key === 'Enter') {
      e.preventDefault();
      if (options[active] !== undefined) pick(options[active]);
    } else if (e.key === 'Escape') {
      e.preventDefault();
      e.stopPropagation();
      setOpen(false);
      trigger.current?.focus();
    } else if (e.key === 'Tab') setOpen(false);
  };

  return (
    <div ref={root} className="relative max-w-xl">
      <button
        ref={trigger}
        type="button"
        disabled={disabled || loading}
        aria-haspopup="listbox"
        aria-expanded={open}
        onClick={() => (open ? setOpen(false) : show())}
        className={cn(controlBase, 'flex h-8 items-center gap-2 px-2.5 text-left text-sm')}
      >
        {loading ? (
          <span className="flex items-center gap-2 text-fg-subtle">
            <Spinner size={12} /> Loading providers…
          </span>
        ) : selected ? (
          <>
            <span className="truncate text-fg">{selected.label}</span>
            <span className="mono truncate text-xs text-fg-subtle">{selected.name}</span>
            <span className="ml-auto" />
            <InstalledBadge installed={selected.installed} />
          </>
        ) : value ? (
          <span className="mono truncate">{value}</span>
        ) : (
          <span className="text-fg-subtle">None — no DNS challenge</span>
        )}
        <ChevronDown size={14} className={cn('shrink-0 text-fg-subtle', !selected && 'ml-auto')} aria-hidden />
      </button>
      {open && (
        <div className="absolute z-30 mt-1 w-full animate-fade-in rounded-md border border-border bg-surface shadow-pop">
          <div className="border-b border-border p-2">
            <Input
              autoFocus
              inputSize="sm"
              role="combobox"
              aria-label="Search DNS providers"
              aria-expanded
              aria-controls={`${id}-list`}
              aria-activedescendant={options.length ? `${id}-opt-${active}` : undefined}
              aria-autocomplete="list"
              leading={<Search size={13} />}
              placeholder="Search, e.g. cloudflare, route53, azure…"
              value={q}
              onChange={(e) => {
                setQ(e.target.value);
                setActive(0);
              }}
              onKeyDown={onKeyDown}
            />
          </div>
          <ul id={`${id}-list`} role="listbox" aria-label="DNS providers" className="max-h-72 overflow-y-auto py-1">
            {options.length === 0 && <li className="px-3 py-2 text-sm text-fg-subtle">No provider matches “{q}”.</li>}
            {options.map((p, i) => {
              const isSelected = (p?.name ?? null) === value;
              return (
                <li
                  key={p?.name ?? '__none'}
                  id={`${id}-opt-${i}`}
                  role="option"
                  aria-selected={isSelected}
                  onMouseDown={(e) => e.preventDefault()}
                  onMouseEnter={() => setActive(i)}
                  onClick={() => pick(p)}
                  className={cn('flex cursor-pointer items-center gap-2 px-3 py-1.5 text-sm', i === active && 'bg-surface-2')}
                >
                  <Check size={13} className={cn('shrink-0 text-accent-text', !isSelected && 'invisible')} aria-hidden />
                  {p ? (
                    <>
                      <span className="truncate text-fg">{p.label}</span>
                      <span className="mono truncate text-xs text-fg-subtle">{p.name}</span>
                      <span className="ml-auto" />
                      <InstalledBadge installed={p.installed} />
                    </>
                  ) : (
                    <span className="text-fg-muted">None — no DNS challenge</span>
                  )}
                </li>
              );
            })}
          </ul>
        </div>
      )}
    </div>
  );
}

// ---------------------------------------------------------------- selected provider: plugin state + typed fields

function ProviderDetails({
  provider,
  settings,
  form,
  set,
  errors,
  disabled,
}: {
  provider: DnsProviderInfo;
  settings: CaddySettings;
  form: CaddySettingsInput;
  set: Set;
  errors: FieldErrors;
  disabled: boolean;
}) {
  const setOption = (name: string, v: string) => set('dnsProviderOptions', { ...form.dnsProviderOptions, [name]: v });
  const setSecret = (name: string, v: string | undefined) => {
    const next = { ...(form.dnsProviderSecrets ?? {}) };
    if (v === undefined) delete next[name];
    else next[name] = v;
    set('dnsProviderSecrets', Object.keys(next).length ? next : undefined);
  };

  return (
    <div className="flex flex-col gap-4 rounded-md border border-border p-4">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
        <span className="text-sm font-medium text-fg">{provider.label}</span>
        <InstalledBadge installed={provider.installed} />
        <span className="mono text-xs text-fg-subtle">{provider.module}</span>
        <a href={provider.docsUrl} target="_blank" rel="noopener noreferrer" className="ml-auto inline-flex items-center gap-0.5 text-xs text-accent-text hover:underline">
          Documentation <ExternalLink size={11} aria-hidden />
        </a>
      </div>
      {provider.notes && <p className="-mt-2 text-xs text-fg-subtle">{provider.notes}</p>}

      <PluginRequirement
        pkg={provider.package}
        what={`${provider.label} DNS`}
        installed={provider.installed}
        title={`The ${provider.label} module is not in the installed Caddy`}
      >
        Caddy needs the plugin <span className="mono text-fg">{provider.package}</span> to use this provider. Until Caddy is rebuilt with it,
        certificates that use DNS-01 cannot be obtained: settings and hosts that use the DNS challenge are refused until it is installed.
      </PluginRequirement>

      <div className="grid gap-4 md:grid-cols-2">
        {provider.fields.map((f) => (
          <ProviderField
            key={f.name}
            field={f}
            value={f.secret ? form.dnsProviderSecrets?.[f.name] : form.dnsProviderOptions?.[f.name]}
            stored={f.secret && hasStoredSecret(settings, form, f.name)}
            error={errors[`${f.secret ? 'dnsProviderSecrets' : 'dnsProviderOptions'}.${f.name}`]}
            onChange={(v) => (f.secret ? setSecret(f.name, v) : setOption(f.name, v ?? ''))}
            disabled={disabled}
          />
        ))}
      </div>
      {provider.fields.some((f) => f.secret) && (
        <p className="text-xs text-fg-subtle">Secret fields are stored encrypted and never shown again; they are masked in configuration views and Caddy errors.</p>
      )}
    </div>
  );
}

function ProviderField({
  field,
  value,
  stored,
  error,
  onChange,
  disabled,
}: {
  field: DnsProviderField;
  value: string | undefined;
  stored: boolean;
  error?: string;
  onChange: (v: string | undefined) => void;
  disabled: boolean;
}) {
  const label: ReactNode = (
    <>
      {field.label} <span className="mono text-xs font-normal text-fg-subtle">{field.name}</span>
    </>
  );
  if (field.type === 'boolean')
    return (
      <SwitchField
        className="md:col-span-2"
        label={label}
        description={field.help ?? undefined}
        checked={value === 'true'}
        onChange={(v) => onChange(v ? 'true' : '')}
        disabled={disabled}
      />
    );
  const hint = field.type === 'duration' ? [field.help, 'In seconds.'].filter(Boolean).join(' ') : (field.help ?? undefined);
  return (
    <Field label={label} required={field.required} error={error} hint={hint}>
      {field.secret ? (
        <SecretInput has={stored} value={value} onChange={onChange} disabled={disabled} placeholder={field.placeholder ?? 'Enter a new value'} aria-label={field.label} />
      ) : field.type === 'number' || field.type === 'duration' ? (
        <NumberInput
          min={field.type === 'duration' ? 0 : undefined}
          placeholder={field.placeholder ?? undefined}
          value={value === undefined || value === '' ? null : Number(value)}
          onValueChange={(v) => onChange(Number.isNaN(v) ? '' : String(v))}
          disabled={disabled}
          className="max-w-48"
        />
      ) : (
        <Input mono autoComplete="off" spellCheck={false} placeholder={field.placeholder ?? undefined} value={value ?? ''} onChange={(e) => onChange(e.target.value)} disabled={disabled} />
      )}
    </Field>
  );
}

// ---------------------------------------------------------------- advanced DNS options

function AdvancedDns({ form, set, errors, disabled }: { form: CaddySettingsInput; set: Set; errors: FieldErrors; disabled: boolean }) {
  const hasValues =
    form.dnsPropagationDelaySeconds != null ||
    form.dnsPropagationTimeoutSeconds != null ||
    form.dnsTtlSeconds != null ||
    form.dnsResolvers.length > 0;
  const hasErrors = ['dnsPropagationDelaySeconds', 'dnsPropagationTimeoutSeconds', 'dnsTtlSeconds', 'dnsResolvers'].some((k) => fieldError(errors, k));
  const [open, setOpen] = useState(hasValues);
  const skipCheck = form.dnsPropagationTimeoutSeconds === -1;
  const num = (v: number) => (Number.isNaN(v) ? null : v);

  return (
    <details open={open || hasErrors} onToggle={(e) => setOpen(e.currentTarget.open)} className="group rounded-md border border-border">
      <summary className="flex cursor-pointer list-none items-center gap-2 px-4 py-2.5 text-sm font-medium text-fg select-none [&::-webkit-details-marker]:hidden">
        <ChevronDown size={14} className="-rotate-90 text-fg-subtle transition-transform group-open:rotate-0" aria-hidden />
        Advanced
        <span className="font-normal text-fg-subtle">propagation, TTL, resolvers</span>
        {hasValues && !open && <Badge className="ml-auto">Customised</Badge>}
      </summary>
      <div className="flex flex-col gap-4 border-t border-border p-4">
        <div className="grid gap-4 sm:grid-cols-3">
          <Field label="Propagation delay (s)" error={errors.dnsPropagationDelaySeconds} hint="Wait before the first propagation check. Default 0.">
            <NumberInput min={0} placeholder="0" value={form.dnsPropagationDelaySeconds} onValueChange={(v) => set('dnsPropagationDelaySeconds', num(v))} disabled={disabled} />
          </Field>
          <Field label="Propagation timeout (s)" error={errors.dnsPropagationTimeoutSeconds} hint="How long to wait for the TXT record to appear. Default 120.">
            <NumberInput
              min={1}
              placeholder="120"
              value={skipCheck ? null : form.dnsPropagationTimeoutSeconds}
              onValueChange={(v) => set('dnsPropagationTimeoutSeconds', num(v))}
              disabled={disabled || skipCheck}
            />
          </Field>
          <Field label="TTL (s)" error={errors.dnsTtlSeconds} hint="TTL of the challenge record. Empty = provider default.">
            <NumberInput min={0} placeholder="Provider default" value={form.dnsTtlSeconds} onValueChange={(v) => set('dnsTtlSeconds', num(v))} disabled={disabled} />
          </Field>
        </div>
        <Checkbox
          checked={skipCheck}
          onChange={(v) => set('dnsPropagationTimeoutSeconds', v ? -1 : null)}
          disabled={disabled}
          label="Skip the propagation check"
          description="Caddy asks the CA to validate right after creating the record (after the delay above). Use when the check cannot reach your authoritative DNS servers."
        />
        <Field
          label="Resolvers"
          error={fieldError(errors, 'dnsResolvers')}
          hint="DNS servers Caddy queries to check propagation, e.g. 1.1.1.1:53. Set them when the local DNS answers for the zone itself (split-horizon DNS)."
        >
          <ChipInput
            value={form.dnsResolvers}
            onChange={(v) => set('dnsResolvers', v)}
            placeholder="1.1.1.1:53"
            validate={(v) => (isResolverAddress(v) ? null : 'not an IP address or host[:port]')}
            disabled={disabled}
          />
        </Field>
      </div>
    </details>
  );
}
