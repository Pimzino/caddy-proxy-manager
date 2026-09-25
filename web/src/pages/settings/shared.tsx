import type { ReactNode } from 'react';
import { Save } from 'lucide-react';
import { errorMessage } from '@/api/client';
import { Button, Callout, LoadingBlock } from '@/components/ui';
import type { FieldErrors } from '@/lib/validation';

/** Renders a settings query: spinner, error callout, or the form with the loaded data. */
export function Loader<T>({
  query,
  children,
}: {
  query: { isPending: boolean; isError: boolean; error: unknown; data?: T };
  children: (data: T) => ReactNode;
}) {
  if (query.isPending) return <LoadingBlock />;
  if (query.isError || query.data === undefined)
    return (
      <Callout tone="danger" title="Could not load settings">
        {errorMessage(query.error)}
      </Callout>
    );
  return <>{children(query.data)}</>;
}

export function SaveBar({
  dirty,
  saving,
  onReset,
  readOnly,
  extra,
}: {
  dirty: boolean;
  saving: boolean;
  onReset: () => void;
  readOnly?: boolean;
  /** Additional actions left of Discard (e.g. "Test sign-in"). */
  extra?: ReactNode;
}) {
  if (readOnly) return <p className="text-sm text-fg-subtle">Only administrators can change these settings.</p>;
  return (
    <div className="flex flex-wrap items-center justify-end gap-2">
      {dirty && <span className="mr-auto text-sm text-fg-subtle">Unsaved changes</span>}
      {extra}
      <Button onClick={onReset} disabled={!dirty || saving}>
        Discard
      </Button>
      <Button type="submit" variant="primary" icon={<Save size={14} />} loading={saving} disabled={!dirty}>
        Save
      </Button>
    </div>
  );
}

/** The message for a field, including item errors such as "trustedProxies.1" for list fields. */
export function fieldError(errors: FieldErrors, key: string): string | undefined {
  const msgs = Object.entries(errors)
    .filter(([k]) => k === key || k.startsWith(`${key}.`))
    .map(([, m]) => m);
  return msgs.length ? msgs.join(' ') : undefined;
}

/** Server errors whose key no field on the form displays. */
export function UnplacedErrors({ errors, fields }: { errors: FieldErrors; fields: string[] }) {
  const unplaced = Object.entries(errors).filter(([k]) => !fields.some((f) => k === f || k.startsWith(`${f}.`)));
  if (unplaced.length === 0) return null;
  return (
    <Callout tone="danger" className="mb-4" title="The settings were not saved">
      <ul className="list-disc pl-4">
        {unplaced.map(([k, m]) => (
          <li key={k}>{m}</li>
        ))}
      </ul>
    </Callout>
  );
}
