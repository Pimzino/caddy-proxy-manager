// The product documentation shown at /docs. Each page is a Markdown file in the repository's docs/ folder (readable on
// GitHub too); this manifest decides which files are pages, their order in the sidebar, and which console screens link
// to them. Files in docs/ that are not listed here (development notes, test checklists, research) are not published.

export interface DocPageMeta {
  /** URL segment under /docs and the file name in docs/ without ".md". "README" is the docs home (/docs). */
  slug: string;
  /** Sidebar label (the page's own "# " heading is shown as its title). */
  label: string;
  /** Console routes whose "Help" button opens this page (prefix match, longest wins). */
  appRoutes?: string[];
}

export interface DocGroup {
  label: string;
  pages: DocPageMeta[];
}

export const HOME_SLUG = 'README';

/** Files in docs/ that are not product documentation (not bundled; see the glob in content.ts). */
export const unpublishedDocs = ['development', 'vm-test-checklist', 'STYLE'];

export const docGroups: DocGroup[] = [
  {
    label: 'Getting started',
    pages: [
      { slug: HOME_SLUG, label: 'Introduction' },
      { slug: 'installation', label: 'Install, upgrade & remove' },
      { slug: 'getting-started', label: 'First sign-in & tour' },
      { slug: 'dashboard', label: 'Dashboard', appRoutes: ['/'] },
    ],
  },
  {
    label: 'Sites',
    pages: [
      { slug: 'proxy-hosts', label: 'Proxy hosts', appRoutes: ['/hosts/proxy', '/hosts'] },
      { slug: 'redirects', label: 'Redirects', appRoutes: ['/hosts/redirect'] },
      { slug: 'static-sites', label: 'Static sites', appRoutes: ['/hosts/static'] },
      { slug: 'custom-responses', label: 'Custom responses', appRoutes: ['/hosts/response'] },
      { slug: 'host-options', label: 'TLS, access, headers & advanced' },
      { slug: 'streams', label: 'Streams (TCP/UDP)', appRoutes: ['/streams'] },
    ],
  },
  {
    label: 'Security',
    pages: [
      { slug: 'certificates', label: 'Certificates', appRoutes: ['/certificates'] },
      { slug: 'acme', label: 'ACME & DNS challenge' },
      { slug: 'access-lists', label: 'Access lists', appRoutes: ['/access-lists'] },
      { slug: 'users', label: 'Users & roles', appRoutes: ['/users'] },
      { slug: 'directory-sign-in', label: 'Active Directory sign-in' },
      { slug: 'management-ui', label: 'Management UI access' },
      { slug: 'security', label: 'Security & hardening' },
    ],
  },
  {
    label: 'Caddy',
    pages: [
      { slug: 'caddy-service', label: 'Service & updates', appRoutes: ['/caddy/service', '/caddy'] },
      { slug: 'plugins', label: 'Plugins', appRoutes: ['/caddy/plugins'] },
      { slug: 'configuration', label: 'Configuration & revisions', appRoutes: ['/caddy/config'] },
      { slug: 'caddy-settings', label: 'Caddy settings', appRoutes: ['/settings'] },
    ],
  },
  {
    label: 'Monitoring',
    pages: [
      { slug: 'servers', label: 'Servers & resources', appRoutes: ['/servers'] },
      { slug: 'traffic-statistics', label: 'Traffic statistics', appRoutes: ['/traffic'] },
      { slug: 'readiness', label: 'Readiness checks', appRoutes: ['/readiness'] },
      { slug: 'logs', label: 'Logs', appRoutes: ['/logs'] },
      { slug: 'events', label: 'Events', appRoutes: ['/events'] },
      { slug: 'notifications', label: 'Notifications', appRoutes: ['/notifications'] },
      { slug: 'audit-log', label: 'Audit log', appRoutes: ['/audit'] },
    ],
  },
  {
    label: 'Operations',
    pages: [
      { slug: 'backup-restore', label: 'Backup & restore' },
      { slug: 'cluster', label: 'Clustering' },
      { slug: 'updates', label: 'Updates & outbound proxy' },
      { slug: 'cli', label: 'Command line & tray icon' },
      { slug: 'group-policy', label: 'Group Policy' },
      { slug: 'troubleshooting', label: 'Troubleshooting' },
    ],
  },
];

/** Settings tabs (?tab=…) that have their own page; the Settings route itself falls back to Caddy settings. */
const settingsTabs: Record<string, string> = {
  caddy: 'caddy-settings',
  cluster: 'cluster',
  updates: 'updates',
  ui: 'management-ui',
  ldap: 'directory-sign-in',
  backup: 'backup-restore',
};

export const allPages: DocPageMeta[] = docGroups.flatMap((g) => g.pages);

export function docPath(slug: string): string {
  return slug === HOME_SLUG ? '/docs' : `/docs/${slug}`;
}

/** The help page for a console location (used by the "Help" button in the top bar). */
export function helpSlugFor(pathname: string, search = ''): string {
  if (pathname === '/settings') {
    const tab = new URLSearchParams(search).get('tab');
    if (tab && settingsTabs[tab]) return settingsTabs[tab];
  }
  let best: { slug: string; len: number } | null = null;
  for (const page of allPages) {
    for (const route of page.appRoutes ?? []) {
      const match = route === '/' ? pathname === '/' : pathname === route || pathname.startsWith(route + '/');
      if (match && (!best || route.length > best.len)) best = { slug: page.slug, len: route.length };
    }
  }
  return best?.slug ?? HOME_SLUG;
}
