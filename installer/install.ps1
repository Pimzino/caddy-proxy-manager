<#
.SYNOPSIS
    Installs (or repairs/updates) Caddy Proxy Manager from the zip distribution.

.DESCRIPTION
    Wraps "CaddyManager.exe install": copies CaddyManager.exe to "C:\Program Files\Caddy Proxy Manager",
    registers the Windows service "CaddyProxyManager" (LocalSystem, automatic delayed start, restart on failure),
    opens the management UI port in Windows Defender Firewall and starts the service. The service then downloads
    and installs Caddy as the Windows service "Caddy".

    Run it from the extracted zip folder in an elevated PowerShell. Re-running it updates an existing installation;
    data in C:\ProgramData\CaddyProxyManager is kept.

.PARAMETER UiPort
    TCP port of the web UI (default: keep the configured port; 81 on a new install).

.PARAMETER Bind
    IP address the web UI listens on (0.0.0.0 = all interfaces, 127.0.0.1 = local only, or one of this server's
    addresses; other addresses are rejected).

.NOTES
    Locked out of the UI (wrong port, bind address or HTTPS certificate)? In an elevated PowerShell:
        Stop-Service CaddyProxyManager
        & 'C:\Program Files\Caddy Proxy Manager\CaddyManager.exe' configure --reset-ui
        Start-Service CaddyProxyManager
    Then open http://localhost:81/.

.PARAMETER NoStart
    Register the service but do not start it.

.EXAMPLE
    .\install.ps1
.EXAMPLE
    .\install.ps1 -UiPort 8081
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 65535)]
    [int] $UiPort,
    [ValidateScript({ [System.Net.IPAddress]::TryParse($_, [ref]$null) })]
    [string] $Bind,
    [switch] $NoStart
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$principal = New-Object System.Security.Principal.WindowsPrincipal([System.Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error 'Run this script from an elevated PowerShell (right-click PowerShell -> Run as administrator).'
    exit 1
}
if (-not [Environment]::Is64BitOperatingSystem) {
    Write-Error 'Caddy Proxy Manager requires 64-bit Windows.'
    exit 1
}

$exe = Join-Path $PSScriptRoot 'CaddyManager.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    Write-Error "CaddyManager.exe was not found next to this script ($PSScriptRoot). Extract the whole zip first."
    exit 1
}

# Files extracted from a downloaded zip carry the "Mark of the Web"; clear it so the service can start.
Get-ChildItem -LiteralPath $PSScriptRoot -File | Unblock-File -ErrorAction SilentlyContinue

$arguments = @('install')
if ($PSBoundParameters.ContainsKey('UiPort')) { $arguments += @('--ui-port', [string]$UiPort) }
if ($PSBoundParameters.ContainsKey('Bind')) { $arguments += @('--bind', $Bind) }
if ($NoStart) { $arguments += '--no-start' }

& $exe @arguments
$code = $LASTEXITCODE
if ($code -ne 0) {
    Write-Error "CaddyManager.exe install failed with exit code $code."
}
exit $code
