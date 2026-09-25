<#
.SYNOPSIS
    Builds Caddy Proxy Manager: web UI, tests, the self-contained single-file CaddyManager.exe (win-x64),
    the MSI and the zip distribution.

.DESCRIPTION
    Steps (each can be skipped):
      1. web/        npm ci + npm run build            -> web/dist (embedded into the exe)
      2. tests       dotnet test CaddyManager.sln      (network tests excluded by default)
      3. publish     dotnet publish src/CaddyManager   -> artifacts/publish/CaddyManager.exe
      4. MSI         dotnet build installer/*.wixproj  -> artifacts/CaddyProxyManager-<ver>-x64.msi  (Windows only)
      5. zip         exe + install.ps1/uninstall.ps1   -> artifacts/CaddyProxyManager-<ver>-win-x64.zip
    plus artifacts/SHA256SUMS.txt.

    Requirements: .NET 10 SDK, Node.js 24 + npm. The MSI step needs Windows (WiX restores from NuGet).

.EXAMPLE
    .\build.ps1
.EXAMPLE
    .\build.ps1 -Version 1.2.0 -SkipTests
#>
[CmdletBinding()]
param(
    # Product version (SemVer). Default: <Version> from Directory.Build.props.
    [string] $Version,
    [string] $Configuration = 'Release',
    [string] $Runtime = 'win-x64',
    [string] $TestFilter = 'Category!=Network',
    [switch] $SkipWeb,
    [switch] $SkipTests,
    [switch] $SkipMsi,
    [switch] $SkipZip
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$publishDir = Join-Path $artifacts 'publish'
$onWindows = [System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT

function Invoke-Step {
    param([string] $Name, [scriptblock] $Action)
    Write-Host ''
    Write-Host "==> $Name" -ForegroundColor Cyan
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $global:LASTEXITCODE = 0
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Name failed (exit code $LASTEXITCODE)." }
    Write-Host ("    {0} done in {1:n1}s" -f $Name, $sw.Elapsed.TotalSeconds) -ForegroundColor DarkGray
}

# ------------------------------------------------------------------ version
if (-not $Version) {
    $node = Select-Xml -LiteralPath (Join-Path $root 'Directory.Build.props') -XPath '/Project/PropertyGroup/Version' | Select-Object -First 1
    if (-not $node) { throw 'No <Version> in Directory.Build.props; pass -Version.' }
    $Version = $node.Node.InnerText.Trim()
}
$Version = $Version.TrimStart('v')
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.\-]+)?$') { throw "Version '$Version' is not a valid SemVer version (e.g. 1.2.3 or 1.2.3-rc.1)." }
$numericVersion = ($Version -split '-')[0]
Write-Host "Caddy Proxy Manager $Version ($Configuration, $Runtime)" -ForegroundColor Green

foreach ($tool in 'dotnet') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "'$tool' was not found on PATH." }
}
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

# ------------------------------------------------------------------ 1. web UI
if (-not $SkipWeb) {
    if (-not (Get-Command npm -ErrorAction SilentlyContinue)) { throw "'npm' was not found on PATH (install Node.js 24) or use -SkipWeb." }
    Invoke-Step 'Web UI (npm ci + build)' {
        Push-Location (Join-Path $root 'web')
        try {
            npm ci --no-audit --no-fund
            if ($LASTEXITCODE -ne 0) { return }
            npm run build
        } finally { Pop-Location }
    }
}
if (-not (Test-Path (Join-Path $root 'web/dist/index.html'))) {
    Write-Warning 'web/dist/index.html does not exist: the exe will be built without the web UI.'
}

# ------------------------------------------------------------------ 2. tests
if (-not $SkipTests) {
    Invoke-Step "Tests ($TestFilter)" {
        dotnet test (Join-Path $root 'CaddyManager.sln') -c $Configuration --filter $TestFilter --nologo
    }
}

# ------------------------------------------------------------------ 3. publish
Invoke-Step "Publish CaddyManager.exe ($Runtime, self-contained single file)" {
    if (Test-Path $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
    dotnet publish (Join-Path $root 'src/CaddyManager/CaddyManager.csproj') -c $Configuration -r $Runtime -o $publishDir `
        "-p:Version=$Version" "-p:FileVersion=$numericVersion.0" "-p:InformationalVersion=$Version" --nologo
}
Get-ChildItem -LiteralPath $publishDir -Filter '*.pdb' | Remove-Item -Force
$exe = Join-Path $publishDir 'CaddyManager.exe'
if ($Runtime -like 'win-*' -and -not (Test-Path -LiteralPath $exe)) { throw "Publish did not produce $exe." }

# ------------------------------------------------------------------ 4. MSI
$msiTarget = Join-Path $artifacts "CaddyProxyManager-$Version-x64.msi"
if (-not $SkipMsi) {
    if (-not $onWindows) {
        Write-Warning 'Skipping the MSI: WiX Toolset only runs on Windows.'
    } elseif ($Runtime -ne 'win-x64') {
        Write-Warning "Skipping the MSI: it is built for win-x64 only (runtime is $Runtime)."
    } else {
        $msiOut = Join-Path $artifacts 'msi-build'
        Invoke-Step 'MSI (WiX v6)' {
            if (Test-Path $msiOut) { Remove-Item -LiteralPath $msiOut -Recurse -Force }
            dotnet build (Join-Path $root 'installer/CaddyProxyManager.wixproj') -c $Configuration -o $msiOut `
                "-p:ProductVersion=$numericVersion" "-p:CpmPublishDir=$publishDir" --nologo
        }
        $built = Get-ChildItem -LiteralPath $msiOut -Filter '*.msi' -Recurse | Select-Object -First 1
        if (-not $built) { throw "The WiX build did not produce an .msi in $msiOut." }
        Copy-Item -LiteralPath $built.FullName -Destination $msiTarget -Force
        Remove-Item -LiteralPath $msiOut -Recurse -Force
        Write-Host "    $msiTarget"
    }
}

# ------------------------------------------------------------------ 5. zip
$zipTarget = Join-Path $artifacts "CaddyProxyManager-$Version-$Runtime.zip"
if (-not $SkipZip) {
    Invoke-Step 'Zip distribution' {
        $staging = Join-Path $artifacts 'zip-staging'
        if (Test-Path $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
        New-Item -ItemType Directory -Path $staging | Out-Null
        Copy-Item -Path (Join-Path $publishDir '*') -Destination $staging -Recurse
        Copy-Item -LiteralPath (Join-Path $root 'installer/install.ps1') -Destination $staging
        Copy-Item -LiteralPath (Join-Path $root 'installer/uninstall.ps1') -Destination $staging
        @"
Caddy Proxy Manager $Version

Install (elevated PowerShell, in this folder):
    .\install.ps1                  # web UI on port 81
    .\install.ps1 -UiPort 8081     # custom port

Then open http://<server>:<port>/ and complete the setup with the token from
C:\ProgramData\CaddyProxyManager\setup-token.txt

Uninstall:
    .\uninstall.ps1                # keeps data in C:\ProgramData\CaddyProxyManager
    .\uninstall.ps1 -Purge         # deletes all data

Other commands: CaddyManager.exe help
"@ | Set-Content -LiteralPath (Join-Path $staging 'README.txt') -Encoding UTF8
        if (Test-Path $zipTarget) { Remove-Item -LiteralPath $zipTarget -Force }
        Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zipTarget -CompressionLevel Optimal
        Remove-Item -LiteralPath $staging -Recurse -Force
        Write-Host "    $zipTarget"
    }
}

# ------------------------------------------------------------------ checksums
$files = @(Get-ChildItem -LiteralPath $artifacts -File | Where-Object { $_.Extension -in '.msi', '.zip' })
if ($files.Count -gt 0) {
    $sums = foreach ($f in $files) { '{0}  {1}' -f (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $f.Name }
    $sums | Set-Content -LiteralPath (Join-Path $artifacts 'SHA256SUMS.txt') -Encoding ASCII
}

Write-Host ''
Write-Host 'Build complete. Artifacts:' -ForegroundColor Green
Get-ChildItem -LiteralPath $artifacts -File | ForEach-Object { Write-Host ("  {0,-50} {1,8:n1} MB" -f $_.Name, ($_.Length / 1MB)) }
