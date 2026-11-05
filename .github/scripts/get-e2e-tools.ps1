<#
.SYNOPSIS
    Downloads the external tools of the end-to-end tests into .dev/bin (gitignored):
    - pebble(.exe): Let's Encrypt's test ACME CA, release $PebbleVersion, verified against the SHA-256 digest GitHub
      publishes for the release asset (the release has no checksums file);
    - caddy-rfc2136(.exe): Caddy CaddyVersion.Tested compiled with Go here, with github.com/caddy-dns/rfc2136 and
      github.com/caddy-dns/cloudflare (DNS-01 issuance test, secret-scrubbing test);
    - caddy-plugins(.exe): the same version with caddyserver/ntlm-transport, mholt/caddy-l4 and caddy-dns/cloudflare
      (cluster tests: a configuration only the primary's build accepts).
      Each binary is verified by running it: `caddy version` must report the tested version and `caddy list-modules`
      must list the requested modules. Building needs Go (Caddy's go.mod states the minimum version).
    The tests (tests/CaddyManager.Config.Tests/E2ETools.cs) download Pebble themselves when it is missing; the Caddy
    builds come only from this script. CI runs it first so a download or build problem fails loudly in its own step.
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
# Absolute: the Caddy builds run `go build -o` from another directory.
$OutDir = (New-Item -ItemType Directory -Force -Path $OutDir).FullName

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

# ---------------------------------------------------------------- Caddy builds with plugins
$source = Get-Content -LiteralPath (Join-Path $root 'src/CaddyManager.Platform/Binary/CaddyVersion.cs') -Raw
$m = [regex]::Match($source, 'public const string Tested = "(v\d+\.\d+\.\d+)";')
if (-not $m.Success) { throw 'CaddyVersion.Tested was not found in CaddyVersion.cs.' }
$tag = $m.Groups[1].Value

# caddyserver.com's build server cannot pin a custom build: with plugins it builds the newest Caddy tag whatever
# &version= says (and rejects p=github.com/caddyserver/caddy/v2@<tag>). So these builds are compiled here, at the tag,
# the way xcaddy does it: a main package importing the standard modules and the plugins, `go get` of the tag and the
# plugins' latest versions, `go build -trimpath` with xcaddy's default tags. Needs Go (>= the version in Caddy's go.mod).
function Get-CaddyBuild([string] $name, [string[]] $packages, [string[]] $requiredModules) {
    if (-not (Get-Command go -ErrorAction SilentlyContinue)) { throw "Go is required to build $name (https://go.dev/dl/)." }
    $src = Join-Path $tmp "build-$name"
    Remove-Item -Recurse -Force -LiteralPath $src -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $src | Out-Null
    $imports = (@('github.com/caddyserver/caddy/v2/modules/standard') + $packages | ForEach-Object { "`t_ `"$_`"" }) -join "`n"
    Set-Content -LiteralPath (Join-Path $src 'main.go') -Encoding utf8NoBOM -Value @"
package main

import (
`tcaddycmd "github.com/caddyserver/caddy/v2/cmd"
$imports
)

func main() { caddycmd.Main() }
"@
    $out = Join-Path $OutDir "$name$exe"
    # Staged under a name that still ends in .exe: Windows only runs executables by their extension.
    $staged = Join-Path $OutDir "$name.staged$exe"
    Push-Location -LiteralPath $src
    try {
        foreach ($goArgs in @(@('mod', 'init', $name),(@('get', "github.com/caddyserver/caddy/v2@$tag") + $packages), @('mod', 'tidy'),
                @('build', '-o', $staged, '-ldflags', '-w -s', '-trimpath', '-tags', 'nobadger,nomysql,nopgx', '.'))) {
            & go @goArgs
            if ($LASTEXITCODE -ne 0) { throw "go $($goArgs -join ' ') failed for $name (exit code $LASTEXITCODE)." }
        }
    } finally { Pop-Location }
    $reported = (& $staged version) -join ' '
    if (-not $reported.StartsWith("$tag ")) { throw "The Caddy build $name reports '$reported', expected $tag." }
    $modules = & $staged list-modules
    foreach ($mod in $requiredModules) {
        if (-not ($modules | Where-Object { $_.Trim() -eq $mod })) { throw "The Caddy build $name does not include $mod." }
    }
    Move-Item -LiteralPath $staged -Destination $out -Force
    Write-Host "Caddy $reported with $($packages -join ', ') -> $out"
}

# DNS-01 issuance and secret-scrubbing tests (Config).
Get-CaddyBuild 'caddy-rfc2136' @('github.com/caddy-dns/rfc2136', 'github.com/caddy-dns/cloudflare') @('dns.providers.rfc2136', 'dns.providers.cloudflare')
# Cluster tests: the primary runs a build the node's standard Caddy rejects (http_ntlm), like .dev/bin/caddy-plugins.
Get-CaddyBuild 'caddy-plugins' @('github.com/caddyserver/ntlm-transport', 'github.com/mholt/caddy-l4', 'github.com/caddy-dns/cloudflare') `
    @('http.reverse_proxy.transport.http_ntlm', 'layer4', 'dns.providers.cloudflare')
