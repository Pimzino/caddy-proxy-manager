<#
.SYNOPSIS
    Downloads the external tools of the Config end-to-end tests into .dev/bin (gitignored):
    - pebble(.exe): Let's Encrypt's test ACME CA, release $PebbleVersion, verified against the SHA-256 digest GitHub
      publishes for the release asset (the release has no checksums file);
    - caddy-rfc2136(.exe): Caddy CaddyVersion.Tested built by caddyserver.com with github.com/caddy-dns/rfc2136 and
      github.com/caddy-dns/cloudflare (DNS-01 issuance test, secret-scrubbing test). The build server publishes no
      checksum for custom builds, so the binary is verified by running it: `caddy version` must report the tested version
      and `caddy list-modules` must list dns.providers.rfc2136 and dns.providers.cloudflare.
    The tests (tests/CaddyManager.Config.Tests/E2ETools.cs) download the same files themselves when they are missing
    (developer machines, macOS/Linux); CI runs this script first so a download problem fails loudly in its own step.
.PARAMETER OutDir
    Destination folder (default: .dev/bin in the repository).
.PARAMETER PebbleVersion
    Pebble release tag (default v2.10.1, the version E2ETools.PebbleVersion expects).
#>
[CmdletBinding()]
param(
    [string] $OutDir = '',
    [string] $PebbleVersion = 'v2.10.1'
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '../..')
if (-not $OutDir) { $OutDir = Join-Path $root '.dev/bin' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$download = @{ 'User-Agent' = 'caddy-proxy-manager-ci' }
$api = $download.Clone()
if ($env:GH_TOKEN) { $api.Authorization = "Bearer $env:GH_TOKEN" }

$isWin = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)
$isMac = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::OSX)
$goos = if ($isWin) { 'windows' } elseif ($isMac) { 'darwin' } else { 'linux' }
$goarch = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'amd64' }
$exe = if ($isWin) { '.exe' } else { '' }
$tmp = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }

function Invoke-Retry([scriptblock] $action, [string] $what) {
    for ($i = 1; ; $i++) {
        try { return & $action }
        catch {
            if ($i -ge 3) { throw "$what failed after $i attempts: $($_.Exception.Message)" }
            Write-Host "$what failed ($($_.Exception.Message)); retrying in $(5 * $i) s"
            Start-Sleep -Seconds (5 * $i)
        }
    }
}

# ---------------------------------------------------------------- Pebble
$assetName = if ($isWin) { "pebble-$goos-$goarch.zip" } else { "pebble-$goos-$goarch.tar.gz" }
$rel = Invoke-Retry { Invoke-RestMethod -Uri "https://api.github.com/repos/letsencrypt/pebble/releases/tags/$PebbleVersion" -Headers $api } 'Pebble release lookup'
$asset = $rel.assets | Where-Object name -eq $assetName
if (-not $asset) { throw "Pebble $PebbleVersion has no asset $assetName." }
# GitHub publishes "sha256:<hex>" per release asset (REST API field `digest`).
$digest = [string] $asset.digest
if ($digest -notmatch '^sha256:([0-9a-fA-F]{64})$') { throw "GitHub publishes no SHA-256 digest for $assetName; refusing to use it unverified." }
$expected = $Matches[1].ToUpperInvariant()
$archive = Join-Path $tmp $assetName
Invoke-Retry { Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $archive -Headers $download } "Download of $assetName" | Out-Null
$actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
if ($actual -ne $expected) { throw "SHA-256 of $assetName does not match GitHub's digest (expected $expected, got $actual)." }
Write-Host "SHA-256 of $assetName verified against the release asset digest"
$extract = Join-Path $tmp "pebble-extract"
Remove-Item -Recurse -Force -LiteralPath $extract -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $extract | Out-Null
if ($isWin) { Expand-Archive -LiteralPath $archive -DestinationPath $extract -Force } else { tar -xzf $archive -C $extract; if ($LASTEXITCODE -ne 0) { throw 'tar failed' } }
$pebbleBin = Get-ChildItem -LiteralPath $extract -Recurse -File -Filter "pebble$exe" | Select-Object -First 1
if (-not $pebbleBin) { throw "$assetName does not contain pebble$exe." }
$pebbleOut = Join-Path $OutDir "pebble$exe"
Copy-Item -LiteralPath $pebbleBin.FullName -Destination $pebbleOut -Force
if (-not $isWin) { chmod +x $pebbleOut }
Write-Host "Pebble $PebbleVersion -> $pebbleOut"

# ---------------------------------------------------------------- Caddy with caddy-dns/rfc2136 + caddy-dns/cloudflare
$source = Get-Content -LiteralPath (Join-Path $root 'src/CaddyManager.Platform/Binary/CaddyVersion.cs') -Raw
$m = [regex]::Match($source, 'public const string Tested = "(v\d+\.\d+\.\d+)";')
if (-not $m.Success) { throw 'CaddyVersion.Tested was not found in CaddyVersion.cs.' }
$tag = $m.Groups[1].Value
$packages = @('github.com/caddy-dns/rfc2136', 'github.com/caddy-dns/cloudflare')
$url = "https://caddyserver.com/api/download?os=$goos&arch=$goarch&version=$tag" + (($packages | ForEach-Object { "&p=$_" }) -join '')
$caddyOut = Join-Path $OutDir "caddy-rfc2136$exe"
# Staged under a name that still ends in .exe: Windows only runs executables by their extension.
$staged = Join-Path $OutDir "caddy-rfc2136.staged$exe"
# The build server compiles on demand: allow several minutes.
Invoke-Retry { Invoke-WebRequest -Uri $url -OutFile $staged -Headers $download -TimeoutSec 600 } "Caddy build $url" | Out-Null
if (-not $isWin) { chmod +x $staged }
$reported = (& $staged version) -join ' '
if (-not $reported.StartsWith("$tag ")) { throw "The custom Caddy build reports '$reported', expected $tag." }
$modules = & $staged list-modules
foreach ($mod in 'dns.providers.rfc2136', 'dns.providers.cloudflare') {
    if (-not ($modules | Where-Object { $_.Trim() -eq $mod })) { throw "The custom Caddy build does not include $mod." }
}
Move-Item -LiteralPath $staged -Destination $caddyOut -Force
Write-Host "Caddy $reported with $($packages -join ', ') -> $caddyOut"
