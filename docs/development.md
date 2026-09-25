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

Config and Platform tests use a real Caddy binary (`.dev/bin/caddy` or `CM_TEST_CADDY`) when present. Use the
release the product is verified with, `CaddyVersion.Tested` in `src/CaddyManager.Platform/Binary/CaddyVersion.cs`
(CI downloads exactly that tag with `.github/scripts/get-caddy.ps1`, which fails when the release has no
`caddy_<ver>_checksums.txt` or the zip does not match its SHA-512 entry, and `CaddyPinE2ETests` fails if the binary
differs; `GetCaddyScriptE2ETests` runs the script with `pwsh` against a local mock of the GitHub API). The informational *caddy-latest* CI job runs the same suites against the newest Caddy release; read its
release notes before bumping the constant. On macOS, run tests with `DYLD_LIBRARY_PATH=/opt/homebrew/lib` for the
QUIC/HTTP/3 tests.

End-to-end tests (real Caddy, real sockets, real processes) write a JSON artifact per test to `CPM_E2E_ARTIFACTS`
(CI uploads `TestResults/e2e`) or `e2e-artifacts/` next to the test binaries. Windows-only suites
(`Category=WindowsE2E`, `WindowsService`) run on the elevated `windows-latest` runner: throw-away services against
the real Service Control Manager, firewall rules in a unique group, the WinHTTP proxy, the Event Log source, DPAPI,
PowerShell facts. They restore what they change. `CaddyServiceStartPendingE2ETests` reproduces Caddy's
START_PENDING race (caddy PR #8012) with a stub service (`caddy-stub` in `ServiceProbe.cs`) and runs 10 start/stop
cycles of the real Caddy binary as a throw-away service; `CaddyReadyNudgeE2ETests` checks the "re-post the unchanged
config" nudge against real Caddy on every OS. `installer/test-msi.ps1` installs, restarts, repairs and uninstalls
the built MSI on the runner (artifact `cpm-msi-test.json`). What only a real server can show (domain, GPO, Microsoft
365, reboot) is in [vm-test-checklist.md](vm-test-checklist.md).

The HTTP/3 0-RTT test (`TlsE2ETests.Http3_resumed_requests_on_ip_restricted_hosts_never_get_425`) needs a QUIC client
that can send early data, which .NET's msquic client cannot do. It drives Caddy with Python
[aioquic](https://pypi.org/project/aioquic/) and skips itself unless `CPM_AIOQUIC_PYTHON` points at a Python that has it:

```bash
python3 -m venv .dev/h3venv && .dev/h3venv/bin/pip install aioquic==1.3.0
CPM_AIOQUIC_PYTHON=$PWD/.dev/h3venv/bin/python dotnet test tests/CaddyManager.Config.Tests --filter "FullyQualifiedName~Http3_"
```

CI installs Python (`actions/setup-python`) and `aioquic==1.3.0` and sets the variable. A skipped test is correct on a
developer machine, but on the runner it would pass without testing anything. After the tests,
`.github/scripts/assert-e2e-ran.ps1` reads the TRX files and fails the build when a required end-to-end test (0-RTT,
HTTP/3, the Windows suites, the START_PENDING tests) was skipped, is missing or failed, or when a required artifact was
not written. It writes `TestResults/e2e/e2e-required.json` and a table in the job summary. When you add or rename such a
test, update the list in `.github/workflows/build.yml`.

## Build the Windows release

On Windows: `.\build.ps1` → `artifacts\publish\CaddyManager.exe`, `artifacts\CaddyProxyManager-<ver>-x64.msi`
and the zip. Cross-publishing the exe also works from macOS/Linux:
`dotnet publish src/CaddyManager -c Release -r win-x64 -o artifacts/publish`. CI:
`.github/workflows/build.yml` (optional Authenticode signing via `SIGNING_CERT_BASE64` / `SIGNING_CERT_PASSWORD`).
