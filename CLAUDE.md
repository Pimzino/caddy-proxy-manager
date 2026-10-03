# Project rules

## Windows only

Caddy Proxy Manager ships only for Windows (64-bit Windows 10 1809+ / Server 2019+). macOS is just a development machine.

- Implement OS-specific features (performance counters, services, firewall, certificate store, ...) for Windows only.
  Do not write Linux or macOS implementations.
- On other operating systems the code only has to build and not crash: return empty values (0 / null / "not
  available") and carry on.
- If you need non-Windows behaviour to see something working during development, get it from tests (fakes,
  fixtures), throwaway sample data or scratch scripts kept outside the product code. Never add dev-only platform
  code paths to the codebase.
- Tests that need real Windows behaviour skip elsewhere with a clear reason and run on the `windows-latest` CI job
  (or the Windows test VM).

## Product documentation stays true

The console serves the product documentation at `/docs` (public, no sign-in). Its pages are the Markdown files in
`docs/`, listed in `web/src/docs/manifest.ts`; `docs/STYLE.md` has the rules for writing them.

- Every change that alters what a user can see or do — a screen, label, default, validation rule, role, alert, CLI
  command, installer behaviour, port or file location — updates the affected `docs/*.md` pages in the same change.
  Check the pages that mention the feature (search `docs/` for the label), not just the obvious one.
- New screens or features get a page or a section. A new page goes into the manifest, with `appRoutes` when it
  documents a console screen (that is where the top bar's **Help** button leads).
- Screenshots in `docs/images` are generated from the UI: after changing a screen the docs show, regenerate its
  shots with `cd web && node mock/e2e/docs-screenshots.ts <name>…` (shot list at the top of the script).
- Write from the code and the running console, never from memory or older docs. When a UI hint and the code disagree,
  document the code's behaviour and fix or report the hint.
- Run the docs checks before finishing: `cd web && node mock/e2e/run.ts docs docs-setup docs-help` (every page renders,
  links and anchors resolve, search, Help-button mapping, signed-out access, phone layout).

## Caddy version tracking

Last Caddy release the product was tested with: **v2.11.7** (`CaddyVersion.Tested` in
`src/CaddyManager.Platform/Binary/CaddyVersion.cs`; CI downloads exactly that tag).

- At the start of any work, check for newer stable Caddy releases
  (`gh api repos/caddyserver/caddy/releases --jq '.[] | select(.prerelease|not) | .tag_name' | head -5`). If one is
  newer than the version above, say so and read the full release notes of every release since that version.
- The app supports Caddy's full feature set. From those notes, list every new feature the app does not offer yet and
  every behaviour change that affects it, and implement them all as proper fields (not only through the raw JSON
  fields); remove support for anything Caddy removed. Do not leave a feature out on your own judgement: if one cannot be
  built (it does not work as documented, for example), say so with the evidence. Then test against the new release.
- After testing, update the version in this section together with `CaddyVersion.Tested`, the `.dev/bin` binary and
  every place that states the tested version (docs, tests that run the real binary). Keep this section to the version and these rules;
  no feature lists here.
