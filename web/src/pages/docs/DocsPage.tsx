import { useEffect, useMemo, useRef, useState } from 'react';
import { Link, NavLink, useLocation, useNavigate, useParams } from 'react-router';
import { ArrowLeft, ArrowRight, BookOpen, ChevronRight, FileQuestion, Menu, Monitor, Moon, Search, Sun, X } from 'lucide-react';
import { LogoMark } from '@/components/layout/Logo';
import { buttonClasses } from '@/components/ui';
import { cn } from '@/lib/cn';
import { useTheme, type ThemePreference } from '@/lib/theme';
import { DocMarkdown } from '@/docs/DocMarkdown';
import { pageBySlug, pages, search, type DocPage, type SearchHit } from '@/docs/content';
import { docGroups, docPath, HOME_SLUG } from '@/docs/manifest';

/**
 * The product documentation at /docs and /docs/:slug. Public: it is rendered outside the signed-in area and never calls
 * the API, so it also works before setup, when signed out, and when the management service cannot be reached.
 */
export default function DocsPage() {
  const { slug = HOME_SLUG } = useParams();
  const page = pageBySlug.get(slug);
  const location = useLocation();
  const [drawerOpen, setDrawerOpen] = useState(false);
  const [searchOpen, setSearchOpen] = useState(false);

  useEffect(() => {
    document.title = page ? `${page.title} · Documentation · Caddy Proxy Manager` : 'Page not found · Documentation · Caddy Proxy Manager';
  }, [page]);

  // Scroll to the anchor after navigating (the content is rendered synchronously), else to the top.
  useEffect(() => {
    const id = decodeURIComponent(location.hash.slice(1));
    const el = id ? document.getElementById(id) : null;
    if (el) el.scrollIntoView();
    else window.scrollTo(0, 0);
  }, [location.pathname, location.hash]);

  // "/" or Ctrl/Cmd+K opens the search.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const typing = e.target instanceof HTMLElement && e.target.closest('input, textarea, select, [contenteditable="true"]');
      if ((e.key === 'k' && (e.ctrlKey || e.metaKey)) || (e.key === '/' && !typing && !e.ctrlKey && !e.metaKey && !e.altKey)) {
        e.preventDefault();
        setSearchOpen(true);
      }
      if (e.key === 'Escape') setDrawerOpen(false);
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, []);

  return (
    <div className="min-h-dvh bg-bg">
      <a
        href="#doc-content"
        className="sr-only z-50 rounded-md bg-surface px-3 py-2 text-sm focus:not-sr-only focus:fixed focus:top-2 focus:left-2"
      >
        Skip to content
      </a>
      <header className="sticky top-0 z-30 border-b border-border bg-surface/95 backdrop-blur supports-[backdrop-filter]:bg-surface/85">
        <div className="mx-auto flex h-14 max-w-[1440px] items-center gap-3 px-4 sm:px-6">
          <button
            type="button"
            onClick={() => setDrawerOpen(true)}
            className="flex h-8 w-8 items-center justify-center rounded-md text-fg-muted hover:bg-surface-2 hover:text-fg lg:hidden"
            aria-label="Open documentation navigation"
            aria-expanded={drawerOpen}
          >
            <Menu size={18} />
          </button>
          <Link to="/docs" className="flex shrink-0 items-center gap-2.5 rounded-md" aria-label="Documentation home">
            <LogoMark className="h-8 w-8" />
            <span className="hidden flex-col leading-tight sm:flex">
              <span className="text-sm font-semibold text-fg">Caddy Proxy Manager</span>
              <span className="text-xs text-fg-subtle">Documentation</span>
            </span>
          </Link>
          <div className="flex min-w-0 flex-1 justify-center">
            <button
              type="button"
              onClick={() => setSearchOpen(true)}
              className="flex h-9 w-full max-w-md items-center gap-2 rounded-md border border-border bg-surface-2/60 px-3 text-left text-sm text-fg-subtle transition-colors hover:border-border-strong hover:text-fg-muted"
              aria-label="Search the documentation"
              aria-keyshortcuts="Control+K /"
            >
              <Search size={15} className="shrink-0" aria-hidden />
              <span className="flex-1 truncate">Search the docs…</span>
              <kbd className="mono hidden rounded border border-border bg-surface px-1.5 text-[11px] text-fg-subtle sm:inline">Ctrl K</kbd>
            </button>
          </div>
          <ThemeButton />
          <Link to="/" className={cn(buttonClasses({ variant: 'primary', size: 'sm' }), 'shrink-0')}>
            <span className="hidden sm:inline">Open console</span>
            <span className="sm:hidden">Console</span>
          </Link>
        </div>
      </header>

      {drawerOpen && (
        <div className="fixed inset-0 z-40 lg:hidden" role="dialog" aria-modal="true" aria-label="Documentation navigation">
          <div className="absolute inset-0 animate-fade-in bg-overlay" onClick={() => setDrawerOpen(false)} aria-hidden />
          <div className="relative flex h-full w-80 max-w-[85vw] animate-slide-left flex-col border-r border-border bg-sidebar shadow-pop">
            <div className="flex h-14 shrink-0 items-center justify-between border-b border-border px-4">
              <span className="text-sm font-semibold text-fg">Documentation</span>
              <button
                type="button"
                onClick={() => setDrawerOpen(false)}
                className="flex h-8 w-8 items-center justify-center rounded-md text-fg-subtle hover:bg-surface-2 hover:text-fg"
                aria-label="Close navigation"
              >
                <X size={16} />
              </button>
            </div>
            <div className="flex-1 overflow-y-auto px-3 py-4">
              <DocsNav current={slug} onNavigate={() => setDrawerOpen(false)} />
            </div>
          </div>
        </div>
      )}

      <div className="mx-auto flex max-w-[1440px] px-4 sm:px-6">
        <aside className="hidden w-64 shrink-0 lg:block" aria-label="Documentation">
          <div className="sticky top-14 max-h-[calc(100dvh-3.5rem)] overflow-y-auto py-6 pr-4">
            <DocsNav current={slug} />
          </div>
        </aside>
        <main id="doc-content" className="min-w-0 flex-1 py-8 lg:pl-10 xl:pr-4" tabIndex={-1}>
          {page ? <Article page={page} /> : <NotFound slug={slug} />}
        </main>
        {page && <Toc page={page} />}
      </div>

      {searchOpen && <SearchDialog onClose={() => setSearchOpen(false)} />}
    </div>
  );
}

function DocsNav({ current, onNavigate }: { current: string; onNavigate?: () => void }) {
  return (
    <nav aria-label="Documentation pages" className="flex flex-col gap-6">
      {docGroups.map((g) => (
        <div key={g.label}>
          <p className="mb-1.5 px-2 text-[11px] font-semibold tracking-wider text-fg-subtle uppercase">{g.label}</p>
          <ul className="flex flex-col gap-px border-l border-border">
            {g.pages.map((p) => {
              const active = p.slug === current;
              return (
                <li key={p.slug}>
                  <NavLink
                    to={docPath(p.slug)}
                    end
                    onClick={onNavigate}
                    aria-current={active ? 'page' : undefined}
                    className={cn(
                      '-ml-px flex min-h-8 items-center border-l-2 py-1 pr-2 pl-3 text-sm transition-colors',
                      active
                        ? 'border-accent font-medium text-accent-text'
                        : 'border-transparent text-fg-muted hover:border-border-strong hover:text-fg',
                    )}
                  >
                    {p.label}
                  </NavLink>
                </li>
              );
            })}
          </ul>
        </div>
      ))}
    </nav>
  );
}

function Article({ page }: { page: DocPage }) {
  const index = pages.indexOf(page);
  const prev = index > 0 ? pages[index - 1] : null;
  const next = index < pages.length - 1 ? pages[index + 1] : null;
  const body = useMemo(() => page.doc.blocks.filter((b) => !(b.t === 'heading' && b.level === 1)), [page]);
  return (
    <article className="mx-auto max-w-3xl" aria-labelledby="doc-title">
      <nav aria-label="Breadcrumb" className="mb-3 flex items-center gap-1.5 text-xs text-fg-subtle">
        <BookOpen size={13} aria-hidden />
        <Link to="/docs" className="hover:text-fg">
          Docs
        </Link>
        <ChevronRight size={12} aria-hidden />
        <span>{page.group}</span>
      </nav>
      <h1 id="doc-title" className="text-[1.875rem] leading-tight font-semibold tracking-tight text-fg">
        {page.title}
      </h1>
      <DocMarkdown blocks={body} />
      <nav aria-label="Previous and next pages" className="mt-14 grid gap-3 border-t border-border pt-6 sm:grid-cols-2">
        {prev ? (
          <Link
            to={docPath(prev.slug)}
            className="group flex flex-col rounded-lg border border-border bg-surface px-4 py-3 transition-colors hover:border-accent/50"
          >
            <span className="flex items-center gap-1 text-xs text-fg-subtle">
              <ArrowLeft size={12} aria-hidden /> Previous
            </span>
            <span className="mt-0.5 text-sm font-medium text-fg group-hover:text-accent-text">{prev.label}</span>
          </Link>
        ) : (
          <span />
        )}
        {next && (
          <Link
            to={docPath(next.slug)}
            className="group flex flex-col items-end rounded-lg border border-border bg-surface px-4 py-3 text-right transition-colors hover:border-accent/50"
          >
            <span className="flex items-center gap-1 text-xs text-fg-subtle">
              Next <ArrowRight size={12} aria-hidden />
            </span>
            <span className="mt-0.5 text-sm font-medium text-fg group-hover:text-accent-text">{next.label}</span>
          </Link>
        )}
      </nav>
    </article>
  );
}

/** "On this page": the page's level-2 and level-3 headings, highlighting the one being read. */
function Toc({ page }: { page: DocPage }) {
  const headings = useMemo(
    () => page.doc.blocks.flatMap((b) => (b.t === 'heading' && (b.level === 2 || b.level === 3) ? [{ id: b.id, text: b.text, level: b.level }] : [])),
    [page],
  );
  const [active, setActive] = useState<string | null>(null);

  useEffect(() => {
    const els = headings.map((h) => document.getElementById(h.id)).filter((e): e is HTMLElement => !!e);
    if (els.length === 0) return;
    const update = () => {
      // The last heading above a line 120px below the top bar is the section being read.
      let current: string | null = els[0].id;
      for (const el of els) {
        if (el.getBoundingClientRect().top <= 120) current = el.id;
        else break;
      }
      if (window.innerHeight + window.scrollY >= document.documentElement.scrollHeight - 4) current = els[els.length - 1].id;
      setActive(current);
    };
    update();
    window.addEventListener('scroll', update, { passive: true });
    window.addEventListener('resize', update);
    return () => {
      window.removeEventListener('scroll', update);
      window.removeEventListener('resize', update);
    };
  }, [headings]);

  if (headings.length < 2) return <div className="hidden w-56 shrink-0 xl:block" />;
  return (
    <aside className="hidden w-56 shrink-0 xl:block" aria-label="On this page">
      <div className="sticky top-14 max-h-[calc(100dvh-3.5rem)] overflow-y-auto py-8 pl-4">
        <p className="mb-2 text-[11px] font-semibold tracking-wider text-fg-subtle uppercase">On this page</p>
        <ul className="flex flex-col gap-0.5 border-l border-border text-[13px]">
          {headings.map((h) => (
            <li key={h.id}>
              <a
                href={`#${h.id}`}
                aria-current={active === h.id ? 'location' : undefined}
                className={cn(
                  '-ml-px block border-l-2 py-1 leading-snug transition-colors',
                  h.level === 3 ? 'pl-6' : 'pl-3',
                  active === h.id ? 'border-accent text-accent-text' : 'border-transparent text-fg-subtle hover:text-fg',
                )}
              >
                {h.text}
              </a>
            </li>
          ))}
        </ul>
      </div>
    </aside>
  );
}

function NotFound({ slug }: { slug: string }) {
  return (
    <div className="mx-auto flex max-w-xl flex-col items-center py-20 text-center">
      <FileQuestion size={28} className="text-fg-subtle" aria-hidden />
      <h1 id="doc-title" className="mt-4 text-xl font-semibold text-fg">
        Page not found
      </h1>
      <p className="mt-2 text-sm text-fg-muted">
        There is no documentation page called <code className="mono">{slug}</code>. It may have been renamed.
      </p>
      <Link to="/docs" className={cn(buttonClasses({ variant: 'secondary', size: 'sm' }), 'mt-5')}>
        Documentation home
      </Link>
    </div>
  );
}

const themeOrder: ThemePreference[] = ['system', 'light', 'dark'];
const themeLabel: Record<ThemePreference, string> = { system: 'System theme', light: 'Light theme', dark: 'Dark theme' };

function ThemeButton() {
  const { preference, setPreference } = useTheme();
  const next = themeOrder[(themeOrder.indexOf(preference) + 1) % themeOrder.length];
  const Icon = preference === 'system' ? Monitor : preference === 'light' ? Sun : Moon;
  return (
    <button
      type="button"
      onClick={() => setPreference(next)}
      className="flex h-8 w-8 shrink-0 items-center justify-center rounded-md text-fg-muted hover:bg-surface-2 hover:text-fg"
      aria-label={`${themeLabel[preference]} (switch to ${themeLabel[next].toLowerCase()})`}
      title={themeLabel[preference]}
    >
      <Icon size={16} aria-hidden />
    </button>
  );
}

function SearchDialog({ onClose }: { onClose: () => void }) {
  const [query, setQuery] = useState('');
  const [selected, setSelected] = useState(0);
  const navigate = useNavigate();
  const inputRef = useRef<HTMLInputElement>(null);
  const listRef = useRef<HTMLUListElement>(null);
  const hits = useMemo(() => search(query), [query]);

  useEffect(() => {
    const prev = document.activeElement as HTMLElement | null;
    inputRef.current?.focus();
    const body = document.body;
    const overflow = body.style.overflow;
    body.style.overflow = 'hidden';
    return () => {
      body.style.overflow = overflow;
      prev?.focus?.();
    };
  }, []);

  useEffect(() => {
    listRef.current?.querySelector<HTMLElement>(`[data-index="${selected}"]`)?.scrollIntoView({ block: 'nearest' });
  }, [selected]);

  const open = (hit: SearchHit) => {
    onClose();
    navigate(docPath(hit.section.slug) + (hit.section.id ? `#${hit.section.id}` : ''));
  };

  return (
    <div className="fixed inset-0 z-50 flex items-start justify-center px-4 pt-[12vh]" role="dialog" aria-modal="true" aria-label="Search the documentation">
      <div className="absolute inset-0 animate-fade-in bg-overlay" onClick={onClose} aria-hidden />
      <div className="relative flex max-h-[70vh] w-full max-w-xl animate-pop-in flex-col overflow-hidden rounded-lg border border-border bg-surface shadow-pop">
        <div className="flex items-center gap-2 border-b border-border px-3">
          <Search size={16} className="shrink-0 text-fg-subtle" aria-hidden />
          <input
            ref={inputRef}
            type="search"
            value={query}
            onChange={(e) => {
              setQuery(e.target.value);
              setSelected(0);
            }}
            onKeyDown={(e) => {
              if (e.key === 'Escape') {
                e.stopPropagation();
                onClose();
              } else if (e.key === 'ArrowDown') {
                e.preventDefault();
                setSelected((s) => Math.min(s + 1, hits.length - 1));
              } else if (e.key === 'ArrowUp') {
                e.preventDefault();
                setSelected((s) => Math.max(s - 1, 0));
              } else if (e.key === 'Enter' && hits[selected]) {
                e.preventDefault();
                open(hits[selected]);
              }
            }}
            placeholder="Search the docs, e.g. wildcard certificate"
            aria-label="Search the documentation"
            aria-controls="doc-search-results"
            aria-activedescendant={hits[selected] ? `doc-hit-${selected}` : undefined}
            className="h-12 min-w-0 flex-1 bg-transparent text-[0.9375rem] text-fg outline-none placeholder:text-fg-subtle [&::-webkit-search-cancel-button]:hidden"
            role="combobox"
            aria-expanded={hits.length > 0}
            aria-autocomplete="list"
          />
          <kbd className="mono rounded border border-border px-1.5 text-[11px] text-fg-subtle">Esc</kbd>
        </div>
        {query.trim() === '' ? (
          <p className="px-4 py-6 text-center text-sm text-fg-subtle">Type to search every page of the documentation.</p>
        ) : hits.length === 0 ? (
          <p className="px-4 py-6 text-center text-sm text-fg-subtle">No results for “{query}”.</p>
        ) : (
          <ul id="doc-search-results" ref={listRef} role="listbox" aria-label="Results" className="overflow-y-auto p-2">
            {hits.map((hit, i) => (
              <li
                key={`${hit.section.slug}#${hit.section.id}`}
                id={`doc-hit-${i}`}
                data-index={i}
                role="option"
                aria-selected={i === selected}
                onMouseMove={() => setSelected(i)}
                onClick={() => open(hit)}
                className={cn('cursor-pointer rounded-md px-3 py-2', i === selected ? 'bg-accent-soft' : 'hover:bg-surface-2')}
              >
                <p className="flex items-center gap-1.5 text-sm font-medium text-fg">
                  <span className="truncate">{hit.section.pageTitle}</span>
                  {hit.section.heading && (
                    <>
                      <ChevronRight size={12} className="shrink-0 text-fg-subtle" aria-hidden />
                      <span className={cn('truncate', i === selected && 'text-accent-text')}>{hit.section.heading}</span>
                    </>
                  )}
                </p>
                {hit.snippet && <p className="mt-0.5 line-clamp-2 text-xs text-fg-subtle">{hit.snippet}</p>}
              </li>
            ))}
          </ul>
        )}
        <div className="flex items-center gap-3 border-t border-border px-3 py-2 text-[11px] text-fg-subtle">
          <span>↑↓ to choose</span>
          <span>Enter to open</span>
          <span className="ml-auto">{query.trim() ? `${hits.length} result${hits.length === 1 ? '' : 's'}` : ''}</span>
        </div>
      </div>
    </div>
  );
}
