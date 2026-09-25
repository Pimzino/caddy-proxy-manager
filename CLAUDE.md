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
