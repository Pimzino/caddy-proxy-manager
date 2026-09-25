<#
.SYNOPSIS
    Removes a zip-based installation of Caddy Proxy Manager.

.DESCRIPTION
    Wraps "CaddyManager.exe uninstall": stops and deletes the Windows services "CaddyProxyManager" and "Caddy",
    removes the firewall rules of the group "Caddy Proxy Manager" and the "Caddy Proxy Manager" event log source
    (past entries stay in the Application log). Then deletes
    "C:\Program Files\Caddy Proxy Manager". With -Purge all data in C:\ProgramData\CaddyProxyManager
    (database, certificates, Caddy storage incl. ACME accounts and the internal CA, logs) is deleted as well.

    If Caddy Proxy Manager was installed with the MSI, uninstall it from "Apps & features" instead.

.PARAMETER Purge
    Also delete all data. This cannot be undone; take a backup first (Administration -> Backup).

.EXAMPLE
    .\uninstall.ps1
.EXAMPLE
    .\uninstall.ps1 -Purge
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [switch] $Purge
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$principal = New-Object System.Security.Principal.WindowsPrincipal([System.Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error 'Run this script from an elevated PowerShell (right-click PowerShell -> Run as administrator).'
    exit 1
}

$installDir = Join-Path $env:ProgramFiles 'Caddy Proxy Manager'
$candidates = @((Join-Path $installDir 'CaddyManager.exe'), (Join-Path $PSScriptRoot 'CaddyManager.exe'))
$exe = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $exe) {
    Write-Error "CaddyManager.exe was not found in '$installDir' or next to this script."
    exit 1
}

$msi = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
    Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -eq 'Caddy Proxy Manager' -and $_.PSObject.Properties['WindowsInstaller'] -and $_.WindowsInstaller -eq 1 }
if ($msi) {
    Write-Warning 'Caddy Proxy Manager was installed with the MSI. Uninstall it from Apps & features (or: msiexec /x <msi>) instead.'
    exit 1
}

$what = if ($Purge) { 'services, firewall rules, program files AND ALL DATA' } else { 'services, firewall rules and program files (data is kept)' }
if (-not $PSCmdlet.ShouldProcess('Caddy Proxy Manager', "Remove $what")) { exit 0 }

# Run a copy from TEMP so the program folder can be deleted afterwards.
$tempExe = Join-Path ([System.IO.Path]::GetTempPath()) ("CaddyManager-uninstall-{0}.exe" -f [guid]::NewGuid().ToString('N'))
Copy-Item -LiteralPath $exe -Destination $tempExe -Force
try {
    $arguments = @('uninstall')
    if ($Purge) { $arguments += '--purge' }
    & $tempExe @arguments
    $code = $LASTEXITCODE
} finally {
    Remove-Item -LiteralPath $tempExe -Force -ErrorAction SilentlyContinue
}

if (Test-Path -LiteralPath $installDir) {
    try {
        Remove-Item -LiteralPath $installDir -Recurse -Force
        Write-Host "Deleted $installDir."
    } catch {
        Write-Warning "Could not delete $installDir : $($_.Exception.Message)"
        $code = 1
    }
}

if ($code -ne 0) { Write-Warning "Uninstall finished with errors (exit code $code)." }
exit $code
