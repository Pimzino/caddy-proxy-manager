# Documentation style guide

The product documentation is published inside the console at `/docs` (public, no sign-in) and is also read on GitHub.
Each page is a Markdown file in this folder, `docs/<slug>.md`, listed in `web/src/docs/manifest.ts` (sidebar order,
labels, and the console screens whose **Help** button opens the page). Files that are not product documentation
(`development.md`, `vm-test-checklist.md`, this file) are listed in `unpublishedDocs` there and excluded in
`web/src/docs/content.ts`. This file is not published.

## Keeping it true
- The docs describe the product as it behaves **today**. A change that alters anything a user can see or do (a screen,
  label, default, validation rule, role, alert, CLI command, installer behaviour, port, file location) updates the
  affected pages in the same change.
- A new screen or feature gets a page or a section; a new page is added to the manifest (with `appRoutes` when it
  documents a console screen).
- Check with `cd web && node mock/e2e/run.ts docs docs-setup docs-help` (links, anchors, rendering, search, Help mapping).

## Truth
- The code is the source of truth. Only state facts you verified in the code or the running console. If unsure, leave
  it out.
- If a UI hint or an old page says something the code contradicts, document the code's behaviour and fix or report the
  contradiction.
- Never document development-only things: mock mode, CM_WEB_DIR, CM_UI_PORT, DevLogin, SPEC.md, test suites, source file
  names, class names, or code citations. No "file:line".
- No marketing, no filler ("powerful", "seamless", "simply", "just"). No emoji.

## Audience and voice
- Windows server administrators. Second person ("you"), present tense, active voice, short sentences.
- British spelling, matching the UI: behaviour, colour, authorise, licence (noun), centre.
- Exact UI labels in bold, as shown on screen: **Add proxy host**, **Settings › Caddy**, **Force HTTPS**.
  Navigation paths use " › ". Menu items, buttons, tabs, fields: bold. Values the user types, file paths, commands,
  ports and JSON: `code`.
- Say which role is needed (Viewer / Operator / Admin) near the top of each page and on any admin-only action.
- Explain *why* when it helps the reader decide (e.g. why DNS-01 is needed for wildcards), briefly.

## Structure of a page
1. Exactly one `# Title` (first line). Title in sentence case.
2. An intro paragraph (1–3 sentences): what this is and who can use it.
3. `## Sections` in task order (overview → do the common task → reference → edge cases/limits → related pages).
   `###` for sub-sections. Do not skip levels. Heading texts must be unique within the page.
4. Field reference tables: `| Field | Default | Description |` — use the exact label in the Field column (bold not
   needed in tables). Keep cells to one or two sentences.
5. Numbered lists for procedures (one action per step); bullets for unordered facts.
6. End with `## Related` — a bullet list of links to other pages, only when useful.

## Markdown subset (the in-app renderer supports exactly this — nothing else)
- Headings `#`…`####`, paragraphs, `**bold**`, `*italic*`, `` `code` ``, links `[text](target)`, bare https:// URLs.
- Bullet (`-`) and numbered (`1.`) lists; nest with 2 spaces (bullets) or 3 spaces (numbered).
- Tables (GFM pipes, header separator row). Escape a literal pipe in a cell as `\|`.
- Fenced code blocks with a language: ```powershell, ```json, ```text, ```caddyfile.
- Callouts, GitHub style (use sparingly, max ~3 per page):
  > [!NOTE] / > [!TIP] / > [!IMPORTANT] / > [!WARNING] / > [!CAUTION]
  followed by `> text` lines.
- Horizontal rule `---` (rarely).
- Screenshots: `![Alt text describing what the screen shows](images/<name>.webp)` on a line of its own (see below).
- NO raw HTML, NO other images, NO footnotes, NO task lists, NO front matter.
- Put wildcards and anything with `*` or `_` in backticks (`*.example.com`, `SM_USER`) so they are not read as italics.

## Screenshots
- Screenshots are generated, never captured by hand: `cd web && node mock/e2e/docs-screenshots.ts [name…]` drives the
  real UI against the mock API and writes `docs/images/<name>.webp` (light), `<name>.dark.webp` (dark) and their sizes
  to `docs/images/images.json`. The viewer shows the variant for the reader's theme. The list of shots (page, what to
  open, what to crop) is at the top of that script.
- After changing a screen that a page shows, regenerate its shots (or run the script without names to redo all; a full
  run also deletes images no shot produces).
- To add one: add a shot to the script, run it with the shot's name, and reference `images/<name>.webp` from the page,
  after the paragraph that introduces the screen. Use one screenshot per screen or dialog, not one per field.
- Alt text says what the screen shows, e.g. "The TLS tab of the host editor with the certificate options".

## Links
- Link to other pages with relative file links: `[Certificates](certificates.md)`, `[DNS challenge](acme.md#dns-01)`.
  Anchor = heading text lower-cased, punctuation removed, spaces → hyphens (GitHub rules).
- Only link to published pages (slug = file name):
  README (Introduction), installation, getting-started, dashboard, proxy-hosts, redirects, static-sites,
  custom-responses, host-options, streams, certificates, acme, access-lists, users, directory-sign-in, management-ui,
  security, caddy-service, plugins, configuration, caddy-settings, servers, traffic-statistics, readiness, logs, events,
  notifications, audit-log, backup-restore, cluster, updates, cli, group-policy, troubleshooting.
- External links only to official vendor docs (caddyserver.com, learn.microsoft.com, letsencrypt.org, github.com of the
  relevant project).

## What belongs on which page (link to the others instead of repeating them)
- installation: requirements, MSI install (interactive + silent), what gets installed, services, data folder layout,
  ports table, firewall rules, first start / Caddy download / offline, upgrade, repair, uninstall.
- getting-started: setup token, create first admin, sign in (local + directory), console tour (sidebar groups, top bar:
  Caddy status pill, manager update pill, user menu: theme, change password, sign out, Help button → docs), first proxy host walkthrough.
- dashboard: every card on the Dashboard.
- proxy-hosts / redirects / static-sites / custom-responses: list page + Details tab of each kind + kind-specific
  behaviour. host-options: the tabs shared by all kinds (TLS, Access, Headers, Locations (proxy only), Advanced) and
  row actions (Edit, Disable/Enable, Duplicate, Delete), how saving applies config and what happens when Caddy rejects it.
- streams: Streams page.
- certificates: Certificates page, all certificate sources, internal CA root, renewal/sync, expiry.
- acme: ACME settings (email, CA, EAB, disabling challenges), challenge types, DNS-01 providers and credentials,
  advanced propagation/resolvers, wildcard certs, ACME-DNS.
- access-lists, users (roles matrix, Users page, passwords, change password, sessions), directory-sign-in (LDAP tab),
  management-ui (Settings › Management UI tab + publishing the UI through Caddy + recovery), security (security model,
  what is encrypted, headers, admin API, hardening checklist).
- caddy-service (Service & Updates page), plugins, configuration (Configuration page tabs, revisions, Caddyfile mode,
  Import Caddyfile), caddy-settings (Settings › Caddy tab reference, except the ACME section → link to acme).
- servers (Servers list + server detail metrics; cluster joining is in cluster), traffic-statistics, readiness
  (all checks + fixes + GPO script panel), logs, events, notifications (incl. alert rules), audit-log.
- backup-restore, cluster (incl. Settings › Cluster tab and shared storage), updates (Settings › Updates tab, Caddy
  auto-update, outbound proxy, manager updates), cli (all commands + tray icon), group-policy, troubleshooting.
