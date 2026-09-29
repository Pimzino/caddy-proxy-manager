import { allPages, docGroups, type DocPageMeta } from './manifest';
import { blockText, parseMarkdown, type Block, type ParsedDoc } from './markdown';

// Every page listed in the manifest, read from the repository's docs/ folder at build time (bundled into the lazily
// loaded docs chunk; nothing is fetched at runtime, so the docs work signed out and when the API is down).
// The negated patterns keep the files in `unpublishedDocs` (manifest.ts) out of the bundle; keep both lists in sync.
const sources = import.meta.glob<string>(
  ['../../../docs/*.md', '!../../../docs/development.md', '!../../../docs/vm-test-checklist.md', '!../../../docs/STYLE.md'],
  { query: '?raw', import: 'default', eager: true },
);

// A new docs/*.md file must be added to the manifest (published) or to unpublishedDocs and the patterns above.
const unlisted = Object.keys(sources)
  .map((k) => k.replace('../../../docs/', '').replace(/\.md$/, ''))
  .filter((slug) => !allPages.some((p) => p.slug === slug));
if (import.meta.env.DEV && unlisted.length)
  throw new Error(`docs/${unlisted.join('.md, docs/')}.md is neither in the docs manifest nor excluded as unpublished (web/src/docs/manifest.ts).`);

export interface DocPage extends DocPageMeta {
  group: string;
  title: string;
  doc: ParsedDoc;
}

export interface SearchSection {
  slug: string;
  pageTitle: string;
  group: string;
  /** Heading of the section ("" for the text before the first sub-heading). */
  heading: string;
  /** Anchor of the section heading ("" for the top of the page). */
  id: string;
  text: string;
}

function sourceFor(slug: string): string | undefined {
  return sources[`../../../docs/${slug}.md`];
}

export const pages: DocPage[] = docGroups.flatMap((g) =>
  g.pages.map((meta) => {
    const src = sourceFor(meta.slug);
    const doc = src !== undefined ? parseMarkdown(src) : { title: meta.label, blocks: [] as Block[] };
    return { ...meta, group: g.label, title: doc.title || meta.label, doc };
  }),
);

/** Manifest entries whose Markdown file is missing (reported by the docs E2E check and shown as an empty page). */
export const missingPages = allPages.filter((p) => sourceFor(p.slug) === undefined).map((p) => p.slug);

export const pageBySlug = new Map(pages.map((p) => [p.slug, p]));

/** The page split at its level-2 and level-3 headings. */
export const sections: SearchSection[] = pages.flatMap((p) => {
  const out: SearchSection[] = [];
  let current: SearchSection = { slug: p.slug, pageTitle: p.title, group: p.group, heading: '', id: '', text: '' };
  let body: Block[] = [];
  const close = () => {
    current.text = blockText(body);
    out.push(current);
    body = [];
  };
  for (const b of p.doc.blocks) {
    if (b.t === 'heading' && b.level === 1) continue;
    if (b.t === 'heading' && (b.level === 2 || b.level === 3)) {
      close();
      current = { slug: p.slug, pageTitle: p.title, group: p.group, heading: b.text, id: b.id, text: '' };
    } else body.push(b);
  }
  close();
  return out;
});

export interface SearchHit {
  section: SearchSection;
  score: number;
  snippet: string;
}

const norm = (s: string) => s.toLowerCase().normalize('NFKD').replace(/[̀-ͯ]/g, '');

/** Every query word must occur in the section (title, heading or text); headings and titles weigh more. */
export function search(query: string, limit = 30): SearchHit[] {
  const words = norm(query).split(/\s+/).filter(Boolean);
  if (words.length === 0) return [];
  const hits: SearchHit[] = [];
  for (const s of sections) {
    const title = norm(s.pageTitle);
    const heading = norm(s.heading);
    const text = norm(s.text);
    let score = 0;
    let ok = true;
    for (const w of words) {
      const inTitle = title.includes(w);
      const inHeading = heading.includes(w);
      const inText = text.includes(w);
      if (!inTitle && !inHeading && !inText) {
        ok = false;
        break;
      }
      if (inHeading) score += heading.split(/\W+/).includes(w) ? 12 : 8;
      if (inTitle) score += s.heading ? 3 : 10;
      if (inText) score += Math.min(5, text.split(w).length - 1);
    }
    if (!ok) continue;
    if (heading.includes(norm(query.trim()))) score += 10;
    hits.push({ section: s, score, snippet: snippet(s.text, words) });
  }
  hits.sort((a, b) => b.score - a.score || a.section.pageTitle.localeCompare(b.section.pageTitle));
  return hits.slice(0, limit);
}

function snippet(text: string, words: string[]): string {
  const flat = text.replace(/\s+/g, ' ').trim();
  const lower = norm(flat);
  let at = -1;
  for (const w of words) {
    at = lower.indexOf(w);
    if (at >= 0) break;
  }
  if (at < 0) return flat.slice(0, 140);
  const start = Math.max(0, at - 50);
  return (start > 0 ? '…' : '') + flat.slice(start, start + 160).trim() + (start + 160 < flat.length ? '…' : '');
}
