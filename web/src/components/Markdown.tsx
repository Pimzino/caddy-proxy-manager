import type { ReactNode } from 'react';

/** Renders a safe subset of Markdown (release notes): headings, lists, code, bold, links. No HTML injection. */
export function Markdown({ source, className }: { source: string; className?: string }) {
  const blocks: ReactNode[] = [];
  const lines = source.replace(/\r\n/g, '\n').split('\n');
  let list: string[] = [];
  let code: string[] | null = null;
  let key = 0;

  const flushList = () => {
    if (list.length === 0) return;
    blocks.push(
      <ul key={key++} className="my-2 list-disc space-y-1 pl-5">
        {list.map((item, i) => (
          <li key={i}>{inline(item)}</li>
        ))}
      </ul>,
    );
    list = [];
  };

  for (const line of lines) {
    if (code) {
      if (line.trim().startsWith('```')) {
        blocks.push(
          <pre key={key++} className="mono my-2 overflow-auto rounded-md bg-code p-2 text-xs">
            {code.join('\n')}
          </pre>,
        );
        code = null;
      } else code.push(line);
      continue;
    }
    if (line.trim().startsWith('```')) {
      flushList();
      code = [];
      continue;
    }
    const h = /^(#{1,6})\s+(.*)$/.exec(line);
    if (h) {
      flushList();
      blocks.push(
        <p key={key++} className="mt-3 mb-1 font-semibold text-fg first:mt-0">
          {inline(h[2])}
        </p>,
      );
      continue;
    }
    const li = /^\s*[-*+]\s+(.*)$/.exec(line);
    if (li) {
      list.push(li[1]);
      continue;
    }
    flushList();
    if (line.trim()) blocks.push(<p key={key++} className="my-1.5">{inline(line)}</p>);
  }
  flushList();
  if (code) blocks.push(<pre key={key++} className="mono my-2 overflow-auto rounded-md bg-code p-2 text-xs">{code.join('\n')}</pre>);
  return <div className={className}>{blocks}</div>;
}

const INLINE = /(`[^`]+`|\*\*[^*]+\*\*|\[[^\]]+\]\((https?:\/\/[^)\s]+)\)|https?:\/\/[^\s)]+)/g;

function inline(text: string): ReactNode[] {
  const out: ReactNode[] = [];
  let last = 0;
  let k = 0;
  for (const m of text.matchAll(INLINE)) {
    const idx = m.index ?? 0;
    if (idx > last) out.push(text.slice(last, idx));
    const tok = m[0];
    if (tok.startsWith('`')) out.push(<code key={k++} className="mono rounded bg-surface-3 px-1 text-[0.9em]">{tok.slice(1, -1)}</code>);
    else if (tok.startsWith('**')) out.push(<strong key={k++} className="font-semibold text-fg">{tok.slice(2, -2)}</strong>);
    else if (tok.startsWith('[')) {
      const label = tok.slice(1, tok.indexOf(']'));
      out.push(
        <a key={k++} href={m[2]} target="_blank" rel="noopener noreferrer" className="text-accent-text hover:underline">
          {label}
        </a>,
      );
    } else
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
