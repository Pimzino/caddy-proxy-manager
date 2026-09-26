import { createElement, type ReactNode } from 'react';
import type { ReleaseInfo } from '@/api/types';
import { cn } from '@/lib/cn';
import { Markdown } from './Markdown';

/**
 * Release notes of a GitHub release. GitHub's own HTML rendering (`notesHtml`) is preferred because it matches what
 * github.com shows (tables, <details>, task lists); it is third-party input, so it goes through `sanitizeReleaseHtml`,
 * which builds new React elements from an allowlist. Without HTML the Markdown body is rendered by <Markdown>.
 */
export function ReleaseNotes({ release, className }: { release: Pick<ReleaseInfo, 'notes' | 'notesHtml'>; className?: string }) {
  const html = release.notesHtml?.trim() ? sanitizeReleaseHtml(release.notesHtml) : null;
  if (html && html.length) return <div className={cn('release-notes text-sm leading-relaxed text-fg-muted', className)}>{html}</div>;
  if (release.notes?.trim()) return <Markdown source={release.notes} className={cn('text-sm leading-relaxed text-fg-muted', className)} />;
  return <p className={cn('text-sm text-fg-subtle', className)}>No release notes.</p>;
}

// How this can fail, and the guard for each:
// - Script runs while parsing → DOMParser('text/html') documents are inert: scripts never execute, images and frames
//   never load, and handlers are never attached.
// - Markup or handlers survive into the page → nothing of the parsed tree is inserted; React elements are created from
//   the tag allowlist below with only the attributes listed (no class, style, id, on*), and text goes in as text.
// - A dangerous link → only absolute http(s) URLs become <a href>; javascript:, data:, relative and malformed URLs
//   leave just the link text. Every link opens in a new tab without an opener or referrer.
// - External images (blocked by the CSP `img-src 'self' data:` anyway) → rendered as a link to the image, never <img>.
// - Unknown/custom elements (GitHub's <markdown-accessiblity-table>, <g-emoji>) → unwrapped; their text is kept.
// - Pathologically deep nesting → below MAX_DEPTH only the text content is kept (no stack overflow).

const MAX_DEPTH = 48;

const ALLOWED = new Set([
  'p', 'br', 'hr', 'h1', 'h2', 'h3', 'h4', 'h5', 'h6', 'ul', 'ol', 'li', 'strong', 'b', 'em', 'i', 'del', 's', 'code', 'pre',
  'blockquote', 'a', 'table', 'thead', 'tbody', 'tr', 'th', 'td', 'details', 'summary', 'kbd', 'sup', 'sub', 'span', 'div', 'tt',
]);

/** Removed together with everything inside them. */
const DROPPED = new Set([
  'script', 'style', 'iframe', 'frame', 'frameset', 'object', 'embed', 'applet', 'param', 'form', 'input', 'button', 'select',
  'option', 'textarea', 'svg', 'math', 'template', 'noscript', 'link', 'meta', 'base', 'head', 'title', 'audio', 'video',
  'source', 'track', 'canvas', 'dialog',
]);

const VOID = new Set(['br', 'hr']);

const CLASSES: Record<string, string> = {
  p: 'my-2 first:mt-0 last:mb-0',
  h1: 'mt-4 mb-2 text-base font-semibold text-fg first:mt-0',
  h2: 'mt-4 mb-2 text-[15px] font-semibold text-fg first:mt-0',
  h3: 'mt-3 mb-1.5 text-sm font-semibold text-fg first:mt-0',
  h4: 'mt-3 mb-1 text-sm font-semibold text-fg first:mt-0',
  h5: 'mt-3 mb-1 text-sm font-medium text-fg first:mt-0',
  h6: 'mt-3 mb-1 text-sm font-medium text-fg-subtle first:mt-0',
  ul: 'my-2 list-disc space-y-1 pl-5',
  ol: 'my-2 list-decimal space-y-1 pl-5',
  li: 'pl-0.5',
  strong: 'font-semibold text-fg',
  b: 'font-semibold text-fg',
  em: 'italic',
  i: 'italic',
  del: 'line-through',
  s: 'line-through',
  code: 'mono rounded bg-surface-3 px-1 py-px text-[0.9em] text-fg',
  tt: 'mono text-[0.9em]',
  kbd: 'mono rounded border border-border-strong bg-surface-2 px-1 text-[0.85em]',
  pre: 'mono my-2 overflow-x-auto rounded-md bg-code p-3 text-xs leading-relaxed text-fg',
  blockquote: 'my-2 border-l-2 border-border-strong pl-3 text-fg-subtle',
  a: 'text-accent-text hover:underline',
  table: 'w-full border-collapse text-left text-[13px]',
  th: 'border border-border bg-surface-2 px-2.5 py-1.5 font-medium text-fg',
  td: 'border border-border px-2.5 py-1.5 align-top',
  details: 'my-2 rounded-md border border-border px-3 py-2 [&[open]>summary]:mb-2',
  summary: 'cursor-pointer font-medium text-fg select-none',
  hr: 'my-4 border-border',
  sup: 'text-[0.75em]',
  sub: 'text-[0.75em]',
};

/** An absolute http(s) URL, or null. */
export function safeHttpUrl(value: string | null | undefined): string | null {
  if (!value) return null;
  try {
    const u = new URL(value.trim());
    return u.protocol === 'https:' || u.protocol === 'http:' ? u.href : null;
  } catch {
    return null;
  }
}

/** Parses untrusted HTML inertly and rebuilds it as React elements from the allowlist. */
export function sanitizeReleaseHtml(html: string): ReactNode[] {
  const doc = new DOMParser().parseFromString(html, 'text/html');
  return convertChildren(doc.body, 0, { pre: false, link: false });
}

/** Elements whose whitespace-only text children are invalid DOM (React warns) and meaningless. */
const NO_TEXT = new Set(['table', 'thead', 'tbody', 'tfoot', 'tr', 'ul', 'ol']);

interface Ctx {
  pre: boolean;
  link: boolean;
}

function convertChildren(parent: Node, depth: number, ctx: Ctx): ReactNode[] {
  const out: ReactNode[] = [];
  const skipBlank = parent.nodeType === Node.ELEMENT_NODE && NO_TEXT.has((parent as Element).localName.toLowerCase());
  parent.childNodes.forEach((child, i) => {
    if (skipBlank && child.nodeType === Node.TEXT_NODE && !child.textContent?.trim()) return;
    const n = convert(child, i, depth, ctx);
    if (n !== null && n !== '') out.push(n);
  });
  return out;
}

function convert(node: Node, key: number, depth: number, ctx: Ctx): ReactNode {
  if (node.nodeType === Node.TEXT_NODE) return node.textContent ?? '';
  if (node.nodeType !== Node.ELEMENT_NODE) return null; // comments, processing instructions
  const el = node as Element;
  const tag = el.localName.toLowerCase();
  if (depth > MAX_DEPTH) return el.textContent ?? '';

  if (tag === 'input') {
    // GitHub task lists: <input type="checkbox" class="task-list-item-checkbox" checked disabled>
    if (el.getAttribute('type')?.toLowerCase() !== 'checkbox') return null;
    const checked = el.hasAttribute('checked');
    return (
      <span key={key} role="img" aria-label={checked ? 'done' : 'not done'} className="mr-1 text-fg">
        {checked ? '☑' : '☐'}
      </span>
    );
  }
  if (DROPPED.has(tag)) return null;
  if (tag === 'img') {
    const alt = el.getAttribute('alt')?.trim() || 'image';
    const src = safeHttpUrl(el.getAttribute('src'));
    return src ? (
      <a key={key} href={src} target="_blank" rel="noopener noreferrer" className={CLASSES.a}>
        [{alt}]
      </a>
    ) : (
      `[${alt}]`
    );
  }

  const children = VOID.has(tag) ? [] : convertChildren(el, depth + 1, { pre: ctx.pre || tag === 'pre', link: ctx.link || tag === 'a' });
  if (!ALLOWED.has(tag)) return children.length ? <span key={key}>{children}</span> : null; // unwrap

  const props: Record<string, unknown> = { key };
  let className: string | undefined = CLASSES[tag];
  if (tag === 'code' && ctx.pre) className = undefined; // the <pre> carries the code block style
  else if (tag === 'code' && ctx.link) className = className?.replace('text-fg', 'text-accent-text');
  // GitHub task lists (<li class="task-list-item">): the checkbox replaces the bullet, as on github.com.
  if (tag === 'li' && el.classList.contains('task-list-item')) className = 'list-none -ml-5';
  if (className) props.className = className;

  if (tag === 'a') {
    const href = safeHttpUrl(el.getAttribute('href'));
    if (!href) return children.length ? <span key={key}>{children}</span> : null;
    props.href = href;
    props.target = '_blank';
    props.rel = 'noopener noreferrer';
  } else if (tag === 'th' || tag === 'td') {
    const align = el.getAttribute('align')?.toLowerCase();
    if (align === 'left' || align === 'center' || align === 'right') props.align = align;
  } else if (tag === 'ol') {
    const start = Number.parseInt(el.getAttribute('start') ?? '', 10);
    if (Number.isFinite(start) && start > 0) props.start = start;
  } else if (tag === 'details') {
    if (el.hasAttribute('open')) props.open = true;
  }

  const element = createElement(tag, props, ...(VOID.has(tag) ? [] : children));
  if (tag === 'table')
    return (
      <div key={key} className="my-3 overflow-x-auto">
        {element}
      </div>
    );
  return element;
}
