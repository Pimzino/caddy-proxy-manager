// Markdown parser for the product documentation (docs/*.md). Covers the subset the docs use, the GitHub way:
// ATX headings, paragraphs, bullet and numbered lists (nested by indentation), fenced code, GFM tables, block quotes and
// GitHub alerts (> [!NOTE] …), horizontal rules, and inline code, bold, italic, links and bare URLs. Raw HTML is not
// interpreted: it stays literal text. The output is a small tree rendered by DocMarkdown.tsx as React elements.

export type Inline =
  | { t: 'text'; v: string }
  | { t: 'code'; v: string }
  | { t: 'strong'; c: Inline[] }
  | { t: 'em'; c: Inline[] }
  | { t: 'link'; href: string; c: Inline[] }
  | { t: 'image'; src: string; alt: string };

export type AlertKind = 'note' | 'tip' | 'important' | 'warning' | 'caution';
export type Align = 'left' | 'center' | 'right' | null;

export type Block =
  | { t: 'heading'; level: number; c: Inline[]; id: string; text: string }
  | { t: 'para'; c: Inline[] }
  | { t: 'code'; lang: string; v: string }
  | { t: 'list'; ordered: boolean; start: number; items: Block[][] }
  | { t: 'table'; align: Align[]; head: Inline[][]; rows: Inline[][][] }
  | { t: 'quote'; alert: AlertKind | null; c: Block[] }
  | { t: 'hr' };

export interface ParsedDoc {
  title: string;
  blocks: Block[];
}

const FENCE = /^(\s*)(`{3,}|~{3,})\s*([\w+-]*)/;
const HEADING = /^ {0,3}(#{1,6})\s+(.*?)\s*#*\s*$/;
const HR = /^ {0,3}((\*\s*){3,}|(-\s*){3,}|(_\s*){3,})$/;
const LIST_ITEM = /^( {0,3})([-*+]|\d{1,9}[.)])(\s+)(.*)$/;
const QUOTE = /^ {0,3}>\s?(.*)$/;
const TABLE_SEP = /^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?\s*$/;
const ALERT = /^\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\]\s*$/i;

const indentOf = (line: string) => line.length - line.trimStart().length;
const isBlank = (line: string) => line.trim() === '';

function isTableStart(lines: string[], i: number): boolean {
  return lines[i].includes('|') && i + 1 < lines.length && TABLE_SEP.test(lines[i + 1]) && lines[i + 1].includes('-');
}

/** A line that ends a paragraph because it starts another block. */
function startsBlock(lines: string[], i: number): boolean {
  const l = lines[i];
  return FENCE.test(l) || HEADING.test(l) || HR.test(l) || QUOTE.test(l) || LIST_ITEM.test(l) || isTableStart(lines, i);
}

export function parseMarkdown(source: string): ParsedDoc {
  const lines = source.replace(/\r\n?/g, '\n').replace(/\t/g, '    ').split('\n');
  const ids = new Map<string, number>();
  const blocks = parseBlocks(lines, ids);
  const h1 = blocks.find((b) => b.t === 'heading' && b.level === 1);
  return { title: h1 && h1.t === 'heading' ? h1.text : '', blocks };
}

function parseBlocks(lines: string[], ids: Map<string, number>): Block[] {
  const out: Block[] = [];
  let i = 0;
  while (i < lines.length) {
    const line = lines[i];
    if (isBlank(line)) {
      i++;
      continue;
    }

    const fence = FENCE.exec(line);
    if (fence) {
      const [, indent, marker, lang] = fence;
      const body: string[] = [];
      for (i++; i < lines.length; i++) {
        const l = lines[i];
        if (l.trimStart().startsWith(marker[0].repeat(marker.length)) && l.trim().replace(new RegExp(`\\${marker[0]}`, 'g'), '') === '') break;
        body.push(l.startsWith(indent) ? l.slice(indent.length) : l.trimStart());
      }
      i++; // closing fence (or end of input)
      out.push({ t: 'code', lang: lang.toLowerCase(), v: body.join('\n') });
      continue;
    }

    const h = HEADING.exec(line);
    if (h) {
      const c = parseInline(h[2]);
      const text = plainText(c);
      out.push({ t: 'heading', level: h[1].length, c, text, id: uniqueId(slugify(text), ids) });
      i++;
      continue;
    }

    if (HR.test(line)) {
      out.push({ t: 'hr' });
      i++;
      continue;
    }

    if (QUOTE.test(line)) {
      const inner: string[] = [];
      while (i < lines.length && !isBlank(lines[i])) {
        const q = QUOTE.exec(lines[i]);
        if (q) inner.push(q[1]);
        else if (startsBlock(lines, i)) break;
        else inner.push(lines[i]); // lazy continuation
        i++;
      }
      let alert: AlertKind | null = null;
      const first = ALERT.exec(inner[0]?.trim() ?? '');
      if (first) {
        alert = first[1].toLowerCase() as AlertKind;
        inner.shift();
      }
      out.push({ t: 'quote', alert, c: parseBlocks(inner, ids) });
      continue;
    }

    if (isTableStart(lines, i)) {
      const head = splitRow(lines[i]);
      const align = splitRow(lines[i + 1]).map((cell): Align => {
        const l = cell.startsWith(':');
        const r = cell.endsWith(':');
        return l && r ? 'center' : r ? 'right' : l ? 'left' : null;
      });
      const rows: Inline[][][] = [];
      for (i += 2; i < lines.length && !isBlank(lines[i]) && lines[i].includes('|'); i++) {
        const cells = splitRow(lines[i]);
        rows.push(head.map((_, k) => parseInline(cells[k] ?? '')));
      }
      out.push({ t: 'table', align, head: head.map((c) => parseInline(c)), rows });
      continue;
    }

    const li = LIST_ITEM.exec(line);
    if (li) {
      const ordered = /\d/.test(li[2]);
      const delimiter = li[2].slice(-1);
      const start = ordered ? parseInt(li[2], 10) : 1;
      const items: Block[][] = [];
      while (i < lines.length) {
        const m = LIST_ITEM.exec(lines[i]);
        if (!m || /\d/.test(m[2]) !== ordered || m[2].slice(-1) !== delimiter) break;
        const contentIndent = m[1].length + m[2].length + Math.min(m[3].length, 4);
        const itemLines = [m[4]];
        i++;
        while (i < lines.length) {
          const l = lines[i];
          if (isBlank(l)) {
            // A blank line continues the item only when indented content follows.
            let j = i;
            while (j < lines.length && isBlank(lines[j])) j++;
            if (j < lines.length && indentOf(lines[j]) >= contentIndent) {
              for (; i < j; i++) itemLines.push('');
              continue;
            }
            break;
          }
          if (indentOf(l) >= contentIndent) {
            itemLines.push(l.slice(contentIndent));
            i++;
            continue;
          }
          // Lazy paragraph continuation: an unindented line that does not start a new block.
          if (!startsBlock(lines, i) && itemLines.length > 0 && !isBlank(itemLines[itemLines.length - 1])) {
            itemLines.push(l.trimStart());
            i++;
            continue;
          }
          break;
        }
        items.push(parseBlocks(itemLines, ids));
        // Blank lines between items of the same list.
        let j = i;
        while (j < lines.length && isBlank(lines[j])) j++;
        const next = j < lines.length ? LIST_ITEM.exec(lines[j]) : null;
        if (next && /\d/.test(next[2]) === ordered && next[2].slice(-1) === delimiter) i = j;
        else break;
      }
      out.push({ t: 'list', ordered, start, items });
      continue;
    }

    // Paragraph
    const para: string[] = [line.trim()];
    for (i++; i < lines.length && !isBlank(lines[i]) && !startsBlock(lines, i); i++) para.push(lines[i].trim());
    out.push({ t: 'para', c: parseInline(para.join('\n')) });
  }
  return out;
}

function splitRow(row: string): string[] {
  let s = row.trim();
  if (s.startsWith('|')) s = s.slice(1);
  if (s.endsWith('|') && !s.endsWith('\\|')) s = s.slice(0, -1);
  const cells: string[] = [];
  let cur = '';
  let inCode = false;
  for (let k = 0; k < s.length; k++) {
    const ch = s[k];
    if (ch === '\\' && s[k + 1] === '|') {
      cur += '|';
      k++;
    } else if (ch === '`') {
      inCode = !inCode;
      cur += ch;
    } else if (ch === '|' && !inCode) {
      cells.push(cur.trim());
      cur = '';
    } else cur += ch;
  }
  cells.push(cur.trim());
  return cells;
}

// ------------------------------------------------------------------------------------------------ inline

const PUNCT = /[!"#$%&'()*+,\-./:;<=>?@[\\\]^_`{|}~]/;
const URL_AT = /^https?:\/\/[^\s<>()]*[^\s<>().,;:!?'"*_]/;

export function parseInline(src: string): Inline[] {
  const out: Inline[] = [];
  let text = '';
  const flush = () => {
    if (text) out.push({ t: 'text', v: text });
    text = '';
  };
  let i = 0;
  while (i < src.length) {
    const ch = src[i];
    if (ch === '\\' && i + 1 < src.length && PUNCT.test(src[i + 1])) {
      text += src[i + 1];
      i += 2;
      continue;
    }
    if (ch === '`') {
      let run = 1;
      while (src[i + run] === '`') run++;
      const fence = '`'.repeat(run);
      const end = src.indexOf(fence, i + run);
      if (end > 0) {
        flush();
        let code = src.slice(i + run, end).replace(/\n/g, ' ');
        if (code.startsWith(' ') && code.endsWith(' ') && code.trim()) code = code.slice(1, -1);
        out.push({ t: 'code', v: code });
        i = end + run;
        continue;
      }
      text += fence;
      i += run;
      continue;
    }
    if (ch === '!' && src[i + 1] === '[') {
      const close = matchBracket(src, i + 1);
      if (close > 0 && src[close + 1] === '(') {
        const end = src.indexOf(')', close + 2);
        if (end > 0) {
          flush();
          out.push({ t: 'image', src: src.slice(close + 2, end).trim().split(/\s+/)[0], alt: src.slice(i + 2, close) });
          i = end + 1;
          continue;
        }
      }
    }
    if (ch === '[') {
      const close = matchBracket(src, i);
      if (close > 0 && src[close + 1] === '(') {
        const end = src.indexOf(')', close + 2);
        if (end > 0) {
          const target = src.slice(close + 2, end).trim().split(/\s+/)[0];
          flush();
          out.push({ t: 'link', href: target, c: parseInline(src.slice(i + 1, close)) });
          i = end + 1;
          continue;
        }
      }
    }
    if (ch === '*' && src[i + 1] === '*') {
      const end = src.indexOf('**', i + 2);
      if (end > i + 2) {
        flush();
        out.push({ t: 'strong', c: parseInline(src.slice(i + 2, end)) });
        i = end + 2;
        continue;
      }
    }
    if ((ch === '*' || ch === '_') && src[i + 1] !== ch && src[i + 1] !== ' ' && src[i + 1] !== undefined) {
      const prev = src[i - 1];
      const intraword = ch === '_' && prev !== undefined && /\w/.test(prev);
      if (!intraword) {
        let end = i + 1;
        while ((end = src.indexOf(ch, end)) > 0) {
          const after = src[end + 1];
          if (src[end - 1] !== ' ' && src[end - 1] !== '\\' && !(ch === '_' && after !== undefined && /\w/.test(after))) break;
          end++;
        }
        if (end > i + 1) {
          flush();
          out.push({ t: 'em', c: parseInline(src.slice(i + 1, end)) });
          i = end + 1;
          continue;
        }
      }
    }
    if (ch === 'h' && (i === 0 || !/\w/.test(src[i - 1]))) {
      const m = URL_AT.exec(src.slice(i));
      if (m) {
        flush();
        out.push({ t: 'link', href: m[0], c: [{ t: 'text', v: m[0] }] });
        i += m[0].length;
        continue;
      }
    }
    text += ch === '\n' ? ' ' : ch;
    i++;
  }
  flush();
  return out;
}

function matchBracket(src: string, open: number): number {
  let depth = 0;
  for (let k = open; k < src.length; k++) {
    const ch = src[k];
    if (ch === '\\') {
      k++;
      continue;
    }
    if (ch === '`') {
      const end = src.indexOf('`', k + 1);
      if (end > 0) k = end;
      continue;
    }
    if (ch === '[') depth++;
    else if (ch === ']' && --depth === 0) return k;
  }
  return -1;
}

export function plainText(nodes: Inline[]): string {
  return nodes.map((n) => (n.t === 'text' || n.t === 'code' ? n.v : n.t === 'image' ? '' : plainText(n.c))).join('');
}

/** GitHub-compatible heading anchor: lower case, punctuation removed, spaces to hyphens. */
export function slugify(text: string): string {
  return text
    .trim()
    .toLowerCase()
    .replace(/[^\p{L}\p{N}\s_-]/gu, '')
    .replace(/\s/g, '-');
}

function uniqueId(base: string, ids: Map<string, number>): string {
  const n = ids.get(base);
  if (n === undefined) {
    ids.set(base, 1);
    return base;
  }
  ids.set(base, n + 1);
  return `${base}-${n}`;
}

/** The text of a block tree, for the search index. */
export function blockText(blocks: Block[]): string {
  const parts: string[] = [];
  const walk = (bs: Block[]) => {
    for (const b of bs) {
      if (b.t === 'para' || b.t === 'heading') parts.push(plainText(b.c));
      else if (b.t === 'code') parts.push(b.v);
      else if (b.t === 'list') b.items.forEach(walk);
      else if (b.t === 'quote') walk(b.c);
      else if (b.t === 'table') {
        parts.push(b.head.map(plainText).join(' '));
        for (const r of b.rows) parts.push(r.map(plainText).join(' '));
      }
    }
  };
  walk(blocks);
  return parts.join('\n');
}
