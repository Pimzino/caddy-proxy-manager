import type { ReactNode } from 'react';

/**
 * Renders a safe subset of Markdown (release notes without GitHub's HTML rendering): headings, bullet and numbered
 * lists, fenced code, GFM tables, bold, inline code and links (labels may contain inline code). Inline HTML such as
 * <details>/<summary> is reduced to its text. Everything is emitted as React text, never as HTML.
 */
export function Markdown({ source, className }: { source: string; className?: string }) {
  const blocks: ReactNode[] = [];
  const lines = source.replace(/\r\n/g, '\n').split('\n');
  let list: { ordered: boolean; items: string[] } | null = null;
  let key = 0;

  const flushList = () => {
    if (!list) return;
    const items = list.items.map((item, i) => <li key={i}>{inline(item)}</li>);
    blocks.push(
      list.ordered ? (
        <ol key={key++} className="my-2 list-decimal space-y-1 pl-5">
          {items}
        </ol>
      ) : (
        <ul key={key++} className="my-2 list-disc space-y-1 pl-5">
          {items}
        </ul>
      ),
    );
    list = null;
  };

  for (let i = 0; i < lines.length; i++) {
    const raw = lines[i];
    if (raw.trim().startsWith('```')) {
      flushList();
      const code: string[] = [];
      for (i++; i < lines.length && !lines[i].trim().startsWith('```'); i++) code.push(lines[i]);
      blocks.push(
        <pre key={key++} className="mono my-2 overflow-auto rounded-md bg-code p-2 text-xs">
          {code.join('\n')}
        </pre>,
      );
      continue;
    }
    // GFM table: a header row, a separator row (| --- | :-: |), then body rows.
    if (raw.trim().startsWith('|') && i + 1 < lines.length && /^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?\s*$/.test(lines[i + 1])) {
      flushList();
      const head = cells(raw);
      const rows: string[][] = [];
      for (i += 2; i < lines.length && lines[i].trim().startsWith('|'); i++) rows.push(cells(lines[i]));
      i--;
      blocks.push(
        <div key={key++} className="my-3 overflow-x-auto">
          <table className="w-full border-collapse text-left text-[13px]">
            <thead>
              <tr>
                {head.map((c, j) => (
                  <th key={j} className="border border-border bg-surface-2 px-2.5 py-1.5 font-medium text-fg">
                    {inline(c)}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {rows.map((r, j) => (
                <tr key={j}>
                  {head.map((_, k) => (
                    <td key={k} className="border border-border px-2.5 py-1.5 align-top">
                      {inline(r[k] ?? '')}
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>,
      );
      continue;
    }
    const isSummary = /<summary\b/i.test(raw);
    const line = raw.replace(HTML_TAG, '');
    if (!line.trim() && raw.trim()) continue; // a line of only tags (<details>, </details>, …)
    const h = /^(#{1,6})\s+(.*)$/.exec(line);
    if (h || isSummary) {
      flushList();
      blocks.push(
        <p key={key++} className="mt-3 mb-1 font-semibold text-fg first:mt-0">
          {inline(h ? h[2] : line.trim())}
        </p>,
      );
      continue;
    }
    const li = /^\s*[-*+]\s+(.*)$/.exec(line);
    const oli = /^\s*\d+[.)]\s+(.*)$/.exec(line);
    if (li || oli) {
      const ordered = !li;
      if (list && list.ordered !== ordered) flushList();
      list ??= { ordered, items: [] };
      list.items.push((li ?? oli)![1]);
      continue;
    }
    flushList();
    if (line.trim()) blocks.push(<p key={key++} className="my-1.5">{inline(line)}</p>);
  }
  flushList();
  return <div className={className}>{blocks}</div>;
}

/** Inline HTML that GitHub release bodies use; stripped to its text (inline code like `<server>` is not matched). */
const HTML_TAG = /<\/?(details|summary|div|p|br|span|sup|sub|kbd|b|i|em|strong|tt|img|a)\b[^>]*>/gi;

function cells(row: string): string[] {
  return row
    .trim()
    .replace(/^\|/, '')
    .replace(/\|$/, '')
    .split('|')
    .map((c) => c.trim());
}

const INLINE = /(`[^`]+`|\*\*[^*]+\*\*|\[((?:`[^`]*`|[^\]])+)\]\((https?:\/\/[^)\s]+)\)|https?:\/\/[^\s)<]+)/g;

function inline(text: string, inLink = false): ReactNode[] {
  const out: ReactNode[] = [];
  let last = 0;
  let k = 0;
  for (const m of text.matchAll(INLINE)) {
    const idx = m.index ?? 0;
    const tok = m[0];
    const isLink = tok.startsWith('[') || /^https?:/.test(tok);
    if (inLink && isLink) continue; // no links inside link labels
    if (idx > last) out.push(text.slice(last, idx));
    if (tok.startsWith('`')) out.push(<code key={k++} className="mono rounded bg-surface-3 px-1 text-[0.9em]">{tok.slice(1, -1)}</code>);
    else if (tok.startsWith('**')) out.push(<strong key={k++} className="font-semibold text-fg">{inline(tok.slice(2, -2), inLink)}</strong>);
    else if (tok.startsWith('['))
      out.push(
        <a key={k++} href={m[3]} target="_blank" rel="noopener noreferrer" className="text-accent-text hover:underline">
          {inline(m[2], true)}
        </a>,
      );
    else
      out.push(
        <a key={k++} href={tok} target="_blank" rel="noopener noreferrer" className="break-all text-accent-text hover:underline">
          {tok}
        </a>,
      );
    last = idx + tok.length;
  }
  if (last < text.length) out.push(text.slice(last));
  return out;
}
