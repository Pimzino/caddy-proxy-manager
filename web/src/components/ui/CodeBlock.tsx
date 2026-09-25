import { useMemo, type ReactNode } from 'react';
import { Download } from 'lucide-react';
import { saveBlob } from '@/api/client';
import { cn } from '@/lib/cn';
import { Button } from './Button';
import { CopyButton } from './CopyButton';

const JSON_TOKEN = /("(?:\\u[a-fA-F0-9]{4}|\\[^u]|[^\\"])*"(\s*:)?|\b(?:true|false)\b|\bnull\b|-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)/g;

/** Minimal JSON syntax highlighter producing React nodes (no innerHTML). */
function highlightJson(src: string): ReactNode[] {
  const out: ReactNode[] = [];
  let last = 0;
  let key = 0;
  for (const m of src.matchAll(JSON_TOKEN)) {
    const idx = m.index ?? 0;
    if (idx > last) out.push(src.slice(last, idx));
    const tok = m[0];
    let cls = 'json-number';
    if (tok.startsWith('"')) cls = m[2] ? 'json-key' : 'json-string';
    else if (tok === 'true' || tok === 'false') cls = 'json-bool';
    else if (tok === 'null') cls = 'json-null';
    out.push(
      <span key={key++} className={cls}>
        {tok}
      </span>,
    );
    last = idx + tok.length;
  }
  if (last < src.length) out.push(src.slice(last));
  return out;
}

export function CodeBlock({
  code,
  language,
  title,
  filename,
  maxHeight = 480,
  wrap = false,
  actions,
  className,
  copyLabel = 'Copy',
}: {
  code: string;
  language?: 'json' | 'powershell' | 'text' | 'caddyfile';
  title?: ReactNode;
  /** When set, shows a download button saving the code with this filename. */
  filename?: string;
  maxHeight?: number | 'none';
  wrap?: boolean;
  actions?: ReactNode;
  className?: string;
  copyLabel?: string;
}) {
  // Highlighting very large documents costs more than it helps.
  const content = useMemo(
    () => (language === 'json' && code.length < 400_000 ? highlightJson(code) : code),
    [code, language],
  );
  return (
    <div className={cn('overflow-hidden rounded-md border border-border bg-code', className)}>
      <div className="flex items-center gap-2 border-b border-border bg-surface-2/70 px-3 py-1.5">
        <div className="min-w-0 flex-1 truncate text-xs font-medium text-fg-muted">
          {title ?? (language ? language.toUpperCase() : null)}
        </div>
        {actions}
        {filename && (
          <Button
            size="xs"
            variant="ghost"
            icon={<Download size={13} />}
            onClick={() => saveBlob(new Blob([code], { type: 'text/plain;charset=utf-8' }), filename)}
          >
            Download
          </Button>
        )}
        <CopyButton text={code} size="xs" variant="ghost" label={copyLabel} />
      </div>
      <pre
        className={cn(
          'mono overflow-auto p-3 text-[12.5px] leading-[1.6] text-fg',
          wrap ? 'break-all whitespace-pre-wrap' : 'whitespace-pre',
        )}
        style={{ maxHeight: maxHeight === 'none' ? undefined : maxHeight }}
        tabIndex={0}
      >
        <code>{content}</code>
      </pre>
    </div>
  );
}
