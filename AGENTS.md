# Agent instructions

## Look up the documentation for the version you are working with

Before writing or changing code that depends on how a third-party component behaves — Caddy, Windows / PowerShell
cmdlets, .NET, WiX, MailKit, Active Directory, etc. — look up that component's **current official documentation for
the exact version this project uses** (and its release notes for recent behaviour changes). Do not rely on memory or
older guidance: defaults change between versions.

Example: Caddy v2.11.0 started rewriting the `Host` header for HTTPS upstreams. Code written from the older behaviour
shipped a bug that the `reverse_proxy` documentation describes plainly.
