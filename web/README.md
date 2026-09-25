# Caddy Proxy Manager — Web UI

React 19 + TypeScript + Vite SPA. Built to `web/dist` and embedded in `CaddyManager.exe` by the host project.

| Command | Purpose |
|---|---|
| `npm ci` | Install dependencies |
| `npm run dev` | Dev server on :5173, proxies `/api` to `http://localhost:5081` (override with `CPM_API`) |
| `npm run dev:mock` | Dev server with the in-memory mock API from `mock/` (no backend needed) |
| `npm run build` | Type-check and build to `dist/` |
| `npm run typecheck` / `npm run lint` | Static checks |

Mock mode signs you in as `admin@example.com`. Environment switches: `MOCK_ROLE=viewer|operator`,
`MOCK_ANON=1` (start signed out; any fixture e-mail + any password signs in, the password `wrong` fails),
`MOCK_SETUP=1` (first-run setup; token `mock-token`), `MOCK_LATENCY=ms`.
In mock mode, custom routes containing an unknown handler (e.g. `[{"handle":[{"handler":"bogus"}]}]`)
are rejected with a Caddy-style 422 to exercise the error dialog. The mock is a Vite dev-server plugin
and is never part of a production build.

API types in `src/api/types.ts` mirror `src/CaddyManager.Core` (camelCase properties and enum values, nulls omitted).
