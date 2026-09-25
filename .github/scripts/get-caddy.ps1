<#
.SYNOPSIS
    Downloads the official Caddy release for Windows amd64 into .dev/bin/caddy.exe for the integration tests.
.PARAMETER Which
    "tested" (default): the release the product is verified against, read from CaddyVersion.Tested in
    src/CaddyManager.Platform/Binary/CaddyVersion.cs, so CI and the product can never disagree. Also exports
    CPM_EXPECT_CADDY_VERSION for the test that asserts the binary's version.
    "latest": the newest release (informational job).
    Both fail when the release has no caddy_<ver>_checksums.txt or the zip does not match its SHA-512 entry.
.PARAMETER ApiBase
    GitHub API base URL (tests point it at a local mock; GH_TOKEN is only sent to https://api.github.com).
.PARAMETER OutDir
    Where caddy.exe is written (default: .dev/bin in the repository).
#>
[CmdletBinding()]
param(
    [ValidateSet('tested', 'latest')] [string] $Which = 'tested',
    [string] $ApiBase = 'https://api.github.com',
    [string] $OutDir = ''
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '../..')
$download = @{ 'User-Agent' = 'caddy-proxy-manager-ci' }   # asset downloads redirect to another host: no token there
$headers = $download.Clone()
if ($env:GH_TOKEN -and $ApiBase -eq 'https://api.github.com') { $headers.Authorization = "Bearer $env:GH_TOKEN" }
$ApiBase = $ApiBase.TrimEnd('/')
if (-not $OutDir) { $OutDir = Join-Path $root '.dev/bin' }

if ($Which -eq 'tested') {
    $source = Get-Content -LiteralPath (Join-Path $root 'src/CaddyManager.Platform/Binary/CaddyVersion.cs') -Raw
    $m = [regex]::Match($source, 'public const string Tested = "(v\d+\.\d+\.\d+)";')
    if (-not $m.Success) { throw 'CaddyVersion.Tested was not found in CaddyVersion.cs.' }
    $tag = $m.Groups[1].Value
    $rel = Invoke-RestMethod -Uri "$ApiBase/repos/caddyserver/caddy/releases/tags/$tag" -Headers $headers
} else {
    $rel = Invoke-RestMethod -Uri "$ApiBase/repos/caddyserver/caddy/releases/latest" -Headers $headers
    $tag = $rel.tag_name
}

$ver = $tag.TrimStart('v')
$asset = $rel.assets | Where-Object name -eq "caddy_${ver}_windows_amd64.zip"
$sums = $rel.assets | Where-Object name -eq "caddy_${ver}_checksums.txt"
if (-not $asset) { throw "Release $tag has no caddy_${ver}_windows_amd64.zip." }
# Never run an unverified binary: Caddy releases publish caddy_<ver>_checksums.txt (goreleaser, checksum
# algorithm sha512: https://github.com/caddyserver/caddy/blob/v2.11.4/.goreleaser.yml). A release without it is
# incomplete or not what we expect, so stop instead of silently skipping the SHA-512 check.
if (-not $sums) {
    $msg = "Release $tag has no caddy_${ver}_checksums.txt asset; refusing to use caddy.exe without its SHA-512 check."
    if ($env:GITHUB_ACTIONS) { Write-Host "::error title=Caddy checksums missing::$msg" }
    throw $msg
}

$tmp = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$zip = Join-Path $tmp 'caddy.zip'
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip -Headers $download
# caddy_<ver>_checksums.txt lists "<sha512 hex>  <file name>" lines for the release assets.
$sumsFile = Join-Path $tmp 'caddy_checksums.txt'
Invoke-WebRequest -Uri $sums.browser_download_url -OutFile $sumsFile -Headers $download
$expected = (Get-Content -LiteralPath $sumsFile | Where-Object { $_ -match "\s$([regex]::Escape($asset.name))\s*$" } |
    Select-Object -First 1) -replace '\s.*$', ''
if (-not $expected) { throw "caddy_${ver}_checksums.txt has no entry for $($asset.name)." }
if ($expected -notmatch '^[0-9a-fA-F]{128}$') { throw "The checksums entry for $($asset.name) is not a SHA-512 digest: '$expected'." }
$actual = (Get-FileHash -LiteralPath $zip -Algorithm SHA512).Hash
if ($actual -ne $expected.ToUpperInvariant()) { throw "SHA-512 of $($asset.name) does not match the release checksums (expected $expected, got $actual)." }
Write-Host "SHA-512 of $($asset.name) verified against caddy_${ver}_checksums.txt"
$dest = Join-Path $tmp 'caddy'
Expand-Archive -LiteralPath $zip -DestinationPath $dest -Force
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$exe = Join-Path $OutDir 'caddy.exe'
Copy-Item (Join-Path $dest 'caddy.exe') $exe -Force

$reported = (& $exe version) -join ' '
Write-Host "Caddy for the tests: $reported"
if (-not $reported.StartsWith("$tag ")) { throw "caddy.exe reports '$reported', expected $tag." }
if ($Which -eq 'tested' -and $env:GITHUB_ENV) {
    "CPM_EXPECT_CADDY_VERSION=$tag" | Out-File -FilePath $env:GITHUB_ENV -Append -Encoding utf8
}
