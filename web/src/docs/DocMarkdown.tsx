import { Fragment, useEffect, useState, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { Link } from 'react-router';
import { AlertTriangle, CircleAlert, ImageOff, Info, Lightbulb, Link2, OctagonAlert, X } from 'lucide-react';
import { CodeBlock } from '@/components/ui';
import { cn } from '@/lib/cn';
import { docImage } from './images';
import { docPath, HOME_SLUG } from './manifest';
import type { AlertKind, Block, Inline } from './markdown';

/**
 * Where a link in a doc page goes: another page ("certificates.md#pfx" → /docs/certificates#pfx), an anchor on this
 * page, a console route (/settings), or an external site (new tab).
 */
export function resolveHref(href: string): { to: string; external: boolean } {
  if (/^[a-z][a-z0-9+.-]*:/i.test(href)) return { to: href, external: true };
  if (href.startsWith('#')) return { to: href, external: false };
  const md = /^(?:\.\/)?([\w-]+)\.md(#.*)?$/.exec(href);
  if (md) return { to: docPath(md[1] === 'README' ? HOME_SLUG : md[1]) + (md[2] ?? ''), external: false };
  return { to: href, external: false };
}

function InlineNodes({ nodes }: { nodes: Inline[] }) {
  return (
    <>
      {nodes.map((n, i) => {
        switch (n.t) {
          case 'text':
            return <Fragment key={i}>{n.v}</Fragment>;
          case 'code':
            return (
              <code key={i} className="mono rounded border border-border bg-surface-2 px-1 py-px text-[0.86em] text-fg [overflow-wrap:anywhere]">
                {n.v}
              </code>
            );
          case 'strong':
            return (
              <strong key={i} className="font-semibold text-fg">
                <InlineNodes nodes={n.c} />
              </strong>
            );
          case 'em':
            return (
              <em key={i}>
                <InlineNodes nodes={n.c} />
              </em>
            );
          case 'image':
            return <DocImage key={i} src={n.src} alt={n.alt} />;
          case 'link': {
            const { to, external } = resolveHref(n.href);
            const cls = 'font-medium text-accent-text underline decoration-accent-text/30 underline-offset-2 hover:decoration-accent-text';
            if (external)
              return (
                <a key={i} href={to} target="_blank" rel="noopener noreferrer" className={cn(cls, 'break-words')}>
                  <InlineNodes nodes={n.c} />
                </a>
              );
            if (to.startsWith('#'))
              return (
                <a key={i} href={to} className={cls}>
                  <InlineNodes nodes={n.c} />
                </a>
              );
            return (
              <Link key={i} to={to} className={cls}>
                <InlineNodes nodes={n.c} />
              </Link>
            );
          }
        }
      })}
    </>
  );
}

const alerts: Record<AlertKind, { title: string; icon: ReactNode; box: string }> = {
  note: { title: 'Note', icon: <Info size={16} className="text-info" />, box: 'border-info/35 bg-info-soft' },
  tip: { title: 'Tip', icon: <Lightbulb size={16} className="text-success" />, box: 'border-success/35 bg-success-soft' },
  important: { title: 'Important', icon: <CircleAlert size={16} className="text-accent-text" />, box: 'border-accent/35 bg-accent-soft' },
  warning: { title: 'Warning', icon: <AlertTriangle size={16} className="text-warning" />, box: 'border-warning/40 bg-warning-soft' },
  caution: { title: 'Caution', icon: <OctagonAlert size={16} className="text-danger" />, box: 'border-danger/35 bg-danger-soft' },
};

function codeLanguage(lang: string): 'json' | 'powershell' | 'text' | 'caddyfile' {
  if (lang === 'json' || lang === 'jsonc') return 'json';
  if (lang === 'powershell' || lang === 'ps1' || lang === 'pwsh') return 'powershell';
  if (lang === 'caddyfile' || lang === 'caddy') return 'caddyfile';
  return 'text';
}

const codeTitles: Record<string, string> = {
  powershell: 'PowerShell',
  ps1: 'PowerShell',
  pwsh: 'PowerShell',
  json: 'JSON',
  caddyfile: 'Caddyfile',
  cmd: 'Command Prompt',
  bat: 'Command Prompt',
  text: 'Text',
};

function Heading({ level, id, children }: { level: number; id: string; children: ReactNode }) {
  const anchor = (
    <a
      href={`#${id}`}
      className="ml-2 inline-flex translate-y-px items-center text-fg-subtle opacity-0 transition-opacity group-hover:opacity-100 focus-visible:opacity-100"
      aria-label="Link to this section"
    >
      <Link2 size={level === 2 ? 16 : 14} aria-hidden />
    </a>
  );
  const common = 'group scroll-mt-20 text-fg';
  if (level === 1) return <h1 id={id} className={cn(common, 'text-[1.75rem] leading-tight font-semibold tracking-tight')}>{children}</h1>;
  if (level === 2)
    return (
      <h2 id={id} className={cn(common, 'mt-11 mb-3 border-b border-border pb-2 text-xl font-semibold tracking-tight')}>
        {children}
        {anchor}
      </h2>
    );
  if (level === 3)
    return (
      <h3 id={id} className={cn(common, 'mt-8 mb-2 text-[1.0625rem] font-semibold')}>
        {children}
        {anchor}
      </h3>
    );
  return (
    <h4 id={id} className={cn(common, 'mt-6 mb-1.5 text-[0.9375rem] font-semibold')}>
      {children}
      {anchor}
    </h4>
  );
}

function Blocks({ blocks, nested = false }: { blocks: Block[]; nested?: boolean }) {
  return (
    <>
      {blocks.map((b, i) => {
        switch (b.t) {
          case 'heading':
            return (
              <Heading key={i} level={b.level} id={b.id}>
                <InlineNodes nodes={b.c} />
              </Heading>
            );
          case 'para':
            if (b.c.length === 1 && b.c[0].t === 'image') return <DocImage key={i} src={b.c[0].src} alt={b.c[0].alt} figure />;
            return (
              <p key={i} className={cn(nested ? 'my-1.5' : 'my-3.5')}>
                <InlineNodes nodes={b.c} />
              </p>
            );
          case 'code':
            return (
              <CodeBlock
                key={i}
                code={b.v}
                language={codeLanguage(b.lang)}
                title={codeTitles[b.lang] ?? (b.lang ? b.lang.toUpperCase() : 'Text')}
                maxHeight="none"
                className="my-4"
              />
            );
          case 'list': {
            const items = b.items.map((item, k) => (
              <li key={k} className="pl-1 marker:text-fg-subtle">
                <Blocks blocks={item} nested />
              </li>
            ));
            return b.ordered ? (
              <ol key={i} start={b.start} className={cn('list-decimal pl-6', nested ? 'my-1.5' : 'my-3.5')}>
                {items}
              </ol>
            ) : (
              <ul key={i} className={cn('list-disc pl-6', nested ? 'my-1.5' : 'my-3.5')}>
                {items}
              </ul>
            );
          }
          case 'table':
            return (
              <div key={i} className="my-5 overflow-x-auto rounded-md border border-border">
                <table className="w-full border-collapse text-left text-[0.8125rem] leading-relaxed">
                  <thead className="bg-surface-2">
                    <tr>
                      {b.head.map((c, k) => (
                        <th
                          key={k}
                          scope="col"
                          className="border-b border-border px-3 py-2 align-bottom font-semibold whitespace-nowrap text-fg"
                          style={{ textAlign: b.align[k] ?? undefined }}
                        >
                          <InlineNodes nodes={c} />
                        </th>
                      ))}
                    </tr>
                  </thead>
                  <tbody>
                    {b.rows.map((r, j) => (
                      <tr key={j} className="border-b border-border last:border-b-0 even:bg-surface-2/40">
                        {r.map((c, k) => (
                          <td key={k} className="px-3 py-2 align-top" style={{ textAlign: b.align[k] ?? undefined }}>
                            <InlineNodes nodes={c} />
                          </td>
                        ))}
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            );
          case 'quote': {
            if (!b.alert)
              return (
                <blockquote key={i} className="my-4 border-l-2 border-border-strong pl-4 text-fg-muted">
                  <Blocks blocks={b.c} nested />
                </blockquote>
              );
            const a = alerts[b.alert];
            return (
              <aside key={i} className={cn('my-5 flex gap-3 rounded-md border px-4 py-3', a.box)} aria-label={a.title}>
                <span className="mt-[3px] shrink-0" aria-hidden>
                  {a.icon}
                </span>
                <div className="min-w-0 flex-1">
                  <p className="font-semibold text-fg">{a.title}</p>
                  <div className="text-fg-muted [&>*:first-child]:mt-0.5 [&>*:last-child]:mb-0">
                    <Blocks blocks={b.c} nested />
                  </div>
                </div>
              </aside>
            );
          }
          case 'hr':
            return <hr key={i} className="my-8 border-border" />;
        }
      })}
    </>
  );
}

/**
 * A screenshot: the light or dark variant follows the theme (the hidden one is not downloaded: lazy images that are
 * display:none are never fetched). Selecting it opens it at full size.
 */
function DocImage({ src, alt, figure = false }: { src: string; alt: string; figure?: boolean }) {
  const [open, setOpen] = useState(false);
  const img = docImage(src);
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setOpen(false);
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [open]);
  if (!img)
    return (
      <span className="my-4 flex items-center gap-2 rounded-md border border-dashed border-border px-3 py-2 text-sm text-fg-subtle">
        <ImageOff size={15} aria-hidden /> Missing image: {src}
      </span>
    );
  const pair = (cls: string) => (
    <>
      <img src={img.light} alt={alt} width={img.width} height={img.height} loading="lazy" decoding="async" className={cn(cls, 'dark:hidden')} />
      <img src={img.dark} alt={alt} width={img.width} height={img.height} loading="lazy" decoding="async" className={cn(cls, 'hidden dark:block')} />
    </>
  );
  const button = (
    <button
      type="button"
      onClick={() => setOpen(true)}
      className="block w-full cursor-zoom-in overflow-hidden rounded-lg border border-border bg-surface shadow-xs transition-shadow hover:shadow-pop focus-visible:outline-2 focus-visible:outline-ring"
      aria-label={`${alt} (open full size)`}
    >
      {pair('block h-auto w-full')}
    </button>
  );
  return (
    <>
      {figure ? <figure className="my-6">{button}</figure> : button}
      {open &&
        createPortal(
          <div
            className="fixed inset-0 z-50 flex animate-fade-in cursor-zoom-out items-center justify-center bg-overlay p-4 sm:p-8"
            role="dialog"
            aria-modal="true"
            aria-label={alt}
            onClick={() => setOpen(false)}
          >
            <button
              type="button"
              className="absolute top-3 right-3 flex h-9 w-9 items-center justify-center rounded-md bg-surface text-fg shadow-pop"
              aria-label="Close"
              autoFocus
            >
              <X size={18} />
            </button>
            {pair('max-h-full max-w-full rounded-lg border border-border object-contain shadow-pop')}
          </div>,
          document.body,
        )}
    </>
  );
}

/** A documentation page body. The first-level heading is rendered by the caller (with the breadcrumb). */
export function DocMarkdown({ blocks }: { blocks: Block[] }) {
  return (
    <div className="text-[0.9375rem] leading-7 break-words text-fg-muted">
      <Blocks blocks={blocks} />
    </div>
  );
}
