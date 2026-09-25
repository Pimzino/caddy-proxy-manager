<#
.SYNOPSIS
    Builds Caddy Proxy Manager: web UI, tests, the self-contained single-file CaddyManager.exe (win-x64),
    the MSI and the zip distribution.

.DESCRIPTION
    Steps (each can be skipped):
      1. web/        npm ci + npm run build            -> web/dist (embedded into the exe)
      2. tests       dotnet test CaddyManager.sln      (network tests excluded by default)
      3. publish     dotnet publish src/CaddyManager   -> artifacts/publish/CaddyManager.exe
                     (Authenticode-signed when a signing certificate is given)
      4. MSI         dotnet build installer/*.wixproj  -> artifacts/CaddyProxyManager-<ver>-x64.msi  (Windows only; signed too)
      5. zip         CaddyManager.exe, install.ps1, uninstall.ps1, README.md, docs/*.md
                                                       -> artifacts/CaddyProxyManager-<ver>-win-x64.zip
    plus artifacts/SHA256SUMS.txt.

    Code signing (optional): pass -SigningCertificate <pfx> [-SigningCertificatePassword <pw>], or set the environment
    variables CPM_SIGNING_CERT (path of a .pfx) and CPM_SIGNING_CERT_PASSWORD. signtool.exe from the Windows SDK signs
    with SHA-256 and an RFC 3161 timestamp (-TimestampUrl). Without a certificate the signing steps are skipped.

    Requirements: .NET 10 SDK, Node.js 24 + npm. The MSI and signing steps need Windows (WiX restores from NuGet).

.EXAMPLE
    .\build.ps1
.EXAMPLE
    .\build.ps1 -Version 1.2.0 -SkipTests
.EXAMPLE
    .\build.ps1 -Version 1.2.0 -SigningCertificate C:\keys\codesign.pfx -SigningCertificatePassword (Read-Host -AsSecureString)
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
    [switch] $SkipZip,
    # Authenticode certificate (.pfx) for CaddyManager.exe and the MSI. Default: $env:CPM_SIGNING_CERT.
    [string] $SigningCertificate = $env:CPM_SIGNING_CERT,
    # Password of the .pfx (string or SecureString). Default: $env:CPM_SIGNING_CERT_PASSWORD.
    [object] $SigningCertificatePassword = $env:CPM_SIGNING_CERT_PASSWORD,
    [string] $TimestampUrl = 'http://timestamp.digicert.com'
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

# ------------------------------------------------------------------ code signing
$signing = -not [string]::IsNullOrWhiteSpace($SigningCertificate)
$signtool = $null
if ($signing) {
    if (-not $onWindows) { throw 'Code signing needs signtool.exe from the Windows SDK; run the build on Windows or omit -SigningCertificate.' }
    if (-not (Test-Path -LiteralPath $SigningCertificate)) { throw "Signing certificate '$SigningCertificate' was not found." }
    $signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1
    if (-not $signtool) {
        $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
        $signtool = Get-ChildItem -LiteralPath $kits -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.Directory.Name -eq 'x64' } | Sort-Object { $v = $null; if ([version]::TryParse($_.Directory.Parent.Name, [ref]$v)) { $v } else { [version]'0.0' } } -Descending |
            Select-Object -ExpandProperty FullName -First 1
    }
    if (-not $signtool) { throw 'signtool.exe was not found (install the Windows SDK "Signing Tools for Desktop Apps").' }
    if ($SigningCertificatePassword -is [System.Security.SecureString]) {
        $SigningCertificatePassword = [System.Net.NetworkCredential]::new('', $SigningCertificatePassword).Password
    }
}

function Invoke-Sign {
    param([string] $File, [string] $Description)
    if (-not $signing) { return }
    Invoke-Step "Sign $([System.IO.Path]::GetFileName($File))" {
        $signArgs = @('sign', '/fd', 'sha256', '/f', $SigningCertificate, '/tr', $TimestampUrl, '/td', 'sha256', '/d', $Description)
        if ($SigningCertificatePassword) { $signArgs += @('/p', [string]$SigningCertificatePassword) }
        # The timestamp server is occasionally unavailable: retry before failing the build.
        for ($attempt = 1; $attempt -le 3; $attempt++) {
            & $signtool @signArgs $File | Out-Host
            if ($LASTEXITCODE -eq 0) { break }
            if ($attempt -lt 3) { Write-Warning "signtool failed (exit code $LASTEXITCODE); retrying in 10 s"; Start-Sleep -Seconds 10 }
        }
        if ($LASTEXITCODE -ne 0) { return }
        & $signtool verify /pa /q $File | Out-Host
    }
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
Write-Host "Caddy Proxy Manager $Version ($Configuration, $Runtime)$(if ($signing) { ', signed' })" -ForegroundColor Green

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
# Sign before packaging so the MSI and the zip carry the signed exe.
if (Test-Path -LiteralPath $exe) { Invoke-Sign -File $exe -Description 'Caddy Proxy Manager' }

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
        Invoke-Sign -File $msiTarget -Description "Caddy Proxy Manager $Version"
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
        $zipExe = Join-Path $publishDir 'CaddyManager.exe'
        if (-not (Test-Path -LiteralPath $zipExe)) { throw "The zip needs $zipExe (publish for a win-* runtime)." }
        Copy-Item -LiteralPath $zipExe -Destination $staging
        Copy-Item -LiteralPath (Join-Path $root 'installer/install.ps1') -Destination $staging
        Copy-Item -LiteralPath (Join-Path $root 'installer/uninstall.ps1') -Destination $staging
        $readme = Join-Path $root 'README.md'
        if (Test-Path -LiteralPath $readme) { Copy-Item -LiteralPath $readme -Destination $staging }
        else { Write-Warning 'README.md was not found at the repository root; the zip is built without it.' }
        $docs = @(Get-ChildItem -LiteralPath (Join-Path $root 'docs') -Filter '*.md' -File -ErrorAction SilentlyContinue)
        if ($docs.Count -gt 0) {
            $docsTarget = New-Item -ItemType Directory -Path (Join-Path $staging 'docs')
            $docs | Copy-Item -Destination $docsTarget.FullName
        } else {
            Write-Warning 'No docs/*.md found; the zip is built without documentation.'
        }
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
