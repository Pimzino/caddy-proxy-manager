# Development

## Architecture

```
CaddyManager.exe (ASP.NET Core 10, Windows service)
├── src/CaddyManager.Core      contracts: models, DTOs, service interfaces, LiteDB store, secret protection, jobs
├── src/CaddyManager.Config    Caddy JSON generator, admin API client, apply/revisions, hosts/certs/access-list API
├── src/CaddyManager.Platform  Caddy service host, binary download/update/plugins, readiness + GPO script, CLI, system API
├── src/CaddyManager.Ops       auth/users/LDAP, audit, events + notifications, monitor, dashboard, logs, backups
├── src/CaddyManager           host: composes modules, Kestrel, embedded SPA
└── web/                       React 19 + TypeScript + Tailwind SPA (embedded into the exe at build time)
```

The manager generates Caddy's **JSON** config from its database and applies it with `POST /load` on Caddy's admin API
(validated, atomic, rolled back by Caddy on error); the last good config is written to `caddy.json`, which the
`Caddy` service boots from. `SPEC.md` is the full functional/API specification.

## Prerequisites

.NET 10 SDK, Node.js 22+ (24 recommended). On Windows additionally nothing else — the WiX SDK is restored via NuGet.

## Run locally (macOS / Linux / Windows)

```bash
cd web && npm ci && npm run build && cd ..
mkdir -p .dev/bin   # put a caddy binary for your OS here (dev mode copies it on first start)
CM_UI_PORT=5081 dotnet run --project src/CaddyManager
```

On non-Windows the manager runs Caddy as a child process and uses `./.devdata` for data. The first-run token is in
`.devdata/setup-token.txt`. For UI work: `cd web && npm run dev` (proxies `/api` to :5081) or `npm run dev:mock`
(no backend needed).

## Tests

```bash
dotnet test CaddyManager.sln --filter "Category!=Network"
cd web && npm run typecheck && npm run lint && npm run build
```

Config and Platform tests use a real Caddy binary (`.dev/bin/caddy` or `CM_TEST_CADDY`) when present; Windows-only
tests run on the `windows-latest` CI runner.

## Build the Windows release

On Windows: `.\build.ps1` → `artifacts\publish\CaddyManager.exe`, `artifacts\CaddyProxyManager-<ver>-x64.msi`
and the zip. Cross-publishing the exe also works from macOS/Linux:
`dotnet publish src/CaddyManager -c Release -r win-x64 -o artifacts/publish`. CI:
`.github/workflows/build.yml` (optional Authenticode signing via `SIGNING_CERT_BASE64` / `SIGNING_CERT_PASSWORD`).
