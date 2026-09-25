import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from 'react';
import { ApiError, errorMessage } from '@/api/client';
import type { ApplyResult } from '@/api/types';
import { serverFieldErrors, type FieldErrors } from '@/lib/validation';
import { Button, CodeBlock, Dialog, useToast } from './ui';

interface ErrorDialogState {
  title: string;
  message?: ReactNode;
  detail?: string;
}

interface Feedback {
  /** Toast (or dialog) for an ApplyResult returned by a config mutation. */
  applied: (apply: ApplyResult | undefined, successTitle?: string, warningTitle?: string) => void;
  /**
   * Standard handling for a failed mutation:
   * 422 → dialog with Caddy's error text; 400 with field errors → onFieldErrors; otherwise a toast.
   */
  failed: (err: unknown, opts?: { title?: string; onFieldErrors?: (errors: FieldErrors) => void }) => void;
  showError: (state: ErrorDialogState) => void;
}

const FeedbackContext = createContext<Feedback | null>(null);

export function FeedbackProvider({ children }: { children: ReactNode }) {
  const toast = useToast();
  const [dialog, setDialog] = useState<ErrorDialogState | null>(null);

  const applied = useCallback<Feedback['applied']>(
    (apply, successTitle = 'Saved and applied', warningTitle) => {
      if (!apply) {
        toast.success(successTitle.replace(' and applied', ''));
        return;
      }
      const warnings = apply.warnings ?? [];
      const warningList =
        warnings.length > 0 ? (
          <ul className="mt-1 list-disc space-y-0.5 pl-4">
            {warnings.map((w, i) => (
              <li key={i}>{w}</li>
            ))}
          </ul>
        ) : null;
      if (!apply.success) {
        setDialog({
          title: 'Configuration not applied',
          message: 'The change was saved, but Caddy did not accept the resulting configuration.',
          detail: apply.error ?? 'No error details were returned.',
        });
        return;
      }
      if (apply.writtenOnly) {
        // The server explains why (not running / binary missing / validated or not); only fall back to a generic line.
        toast.warning(
          'Saved — Caddy is not running',
          warningList ?? 'The configuration was written to disk. It will be loaded when Caddy starts.',
        );
        return;
      }
      if (warnings.length > 0) {
        toast.warning(warningTitle ?? `${successTitle} with warnings`, warningList);
        return;
      }
      toast.success(successTitle);
    },
    [toast],
  );

  const failed = useCallback<Feedback['failed']>(
    (err, opts) => {
      if (err instanceof ApiError) {
        if (err.status === 401) return; // redirected to sign-in
        if (err.status === 422) {
          setDialog({
            title: err.title && err.title !== 'Rejected' ? err.title : 'Caddy rejected the configuration',
            message:
              'The change was not saved and the previous configuration is still active. Caddy reported the following error:',
            detail: err.detail ?? err.title,
          });
          return;
        }
        if (err.status === 400 && err.errors && Object.keys(err.errors).length > 0) {
          const fieldErrors = serverFieldErrors(err.errors);
          if (opts?.onFieldErrors) {
            opts.onFieldErrors(fieldErrors);
            toast.error(opts.title ?? 'Please correct the highlighted fields', err.detail);
          } else {
            toast.error(
              opts?.title ?? err.title,
              <ul className="list-disc pl-4">
                {Object.values(fieldErrors).map((m, i) => (
                  <li key={i}>{m}</li>
                ))}
              </ul>,
            );
          }
          return;
        }
        if (err.status === 403) {
          toast.error('Permission denied', err.detail ?? 'Your role does not allow this action.');
          return;
        }
        toast.error(opts?.title ?? err.title, err.detail && err.detail !== err.title ? err.detail : undefined);
        return;
      }
      toast.error(opts?.title ?? 'Something went wrong', errorMessage(err));
    },
    [toast],
  );

  const value = useMemo(() => ({ applied, failed, showError: setDialog }), [applied, failed]);

  return (
    <FeedbackContext.Provider value={value}>
      {children}
      <Dialog
        open={!!dialog}
        onClose={() => setDialog(null)}
        title={dialog?.title ?? ''}
        size="lg"
        role="alertdialog"
        footer={
          <Button variant="primary" onClick={() => setDialog(null)} data-autofocus>
            Close
          </Button>
        }
      >
        {dialog?.message && <p className="mb-3 text-sm text-fg-muted">{dialog.message}</p>}
        {dialog?.detail && <CodeBlock code={dialog.detail} title="Error" wrap maxHeight={360} />}
      </Dialog>
    </FeedbackContext.Provider>
  );
}

export function useFeedback(): Feedback {
  const ctx = useContext(FeedbackContext);
  if (!ctx) throw new Error('useFeedback must be used inside FeedbackProvider');
  return ctx;
}
