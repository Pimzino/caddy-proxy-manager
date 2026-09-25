<#
.SYNOPSIS
    CI gate: fails when an end-to-end test that must run on this runner was skipped, is missing or failed, or when a
    required E2E artifact was not written. Writes a JSON report of what it checked (and a table to the job summary).
.DESCRIPTION
    The E2E tests skip themselves when a prerequisite is missing (no aioquic Python for the HTTP/3 0-RTT test, no msquic,
    not Windows, not elevated). A skip is correct on a developer machine, but on the windows-latest runner it would make
    the build green without the test ever running, so the workflow lists the tests that must have run.
    Reads every *.trx under -ResultsDirectory (one per test project). A TRX UnitTestResult has testName (fully qualified,
    theory rows with their arguments) and outcome: Passed, Failed, or NotExecuted for a skip (xunit.runner.visualstudio),
    with the skip reason in Output/ErrorInfo/Message.
.PARAMETER ResultsDirectory
    Folder with the TRX files (dotnet test --results-directory).
.PARAMETER RequiredTests
    Test names or -like wildcard patterns (e.g. 'CaddyManager.Platform.Tests.CaddyServiceStartPendingE2ETests.*'). Each must
    match at least one result and every matching result must be Passed. Several values may be given as an array or as
    one string separated by ';' (for pwsh -File).
.PARAMETER ArtifactsDirectory
    Folder of the E2E JSON artifacts (CPM_E2E_ARTIFACTS).
.PARAMETER RequiredArtifacts
    Artifact file names that must exist in -ArtifactsDirectory and be valid JSON (array or ';'-separated).
.PARAMETER ReportPath
    Where the gate's own JSON report is written (default: <ArtifactsDirectory>/e2e-required.json).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ResultsDirectory,
    [Parameter(Mandatory)] [string[]] $RequiredTests,
    [string] $ArtifactsDirectory = '',
    [string[]] $RequiredArtifacts = @(),
    [string] $ReportPath = ''
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

function Split-List([string[]] $values) {
    @($values | ForEach-Object { $_ -split '[;\r\n]' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

$RequiredTests = Split-List $RequiredTests
$RequiredArtifacts = Split-List $RequiredArtifacts
if (-not $ReportPath) {
    $ReportPath = if ($ArtifactsDirectory) { Join-Path $ArtifactsDirectory 'e2e-required.json' } else { Join-Path $ResultsDirectory 'e2e-required.json' }
}

$problems = [System.Collections.Generic.List[string]]::new()
$results = [System.Collections.Generic.List[object]]::new()
$trxFiles = @()
if (Test-Path -LiteralPath $ResultsDirectory) {
    $trxFiles = @(Get-ChildItem -LiteralPath $ResultsDirectory -Filter '*.trx' -File -Recurse | Sort-Object FullName)
}
if ($trxFiles.Count -eq 0) { $problems.Add("No TRX files in '$ResultsDirectory': the test step wrote no results.") }

$ns = @{ t = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010' }
foreach ($trx in $trxFiles) {
    [xml] $doc = Get-Content -LiteralPath $trx.FullName -Raw
    foreach ($node in (Select-Xml -Xml $doc -XPath '//t:Results/t:UnitTestResult' -Namespace $ns)) {
        $r = $node.Node
        $msg = Select-Xml -Xml $r -XPath './t:Output/t:ErrorInfo/t:Message' -Namespace $ns | Select-Object -First 1
        $results.Add([pscustomobject]@{
            Name    = [string] $r.testName
            Outcome = [string] $r.outcome
            Message = if ($msg) { ([string] $msg.Node.InnerText).Trim() } else { '' }
            File    = $trx.Name
        })
    }
}

$testReport = @()
foreach ($pattern in $RequiredTests) {
    # -like: '*' and '?' are wildcards; escape '[' ']' so theory arguments in exact names match literally.
    $like = $pattern -replace '\[', '`[' -replace '\]', '`]'
    $matched = @($results | Where-Object { $_.Name -like $like })
    $outcomes = [ordered]@{}
    foreach ($g in ($matched | Group-Object Outcome | Sort-Object Name)) { $outcomes[$g.Name] = $g.Count }
    $bad = @($matched | Where-Object { $_.Outcome -ne 'Passed' })
    $reasons = @($bad | ForEach-Object { "$($_.Name): $($_.Outcome)$(if ($_.Message) { " ($($_.Message))" })" })
    if ($matched.Count -eq 0) {
        $problems.Add("$pattern`: no test result matches (renamed, filtered out, or its test project did not run).")
    }
    foreach ($b in $bad) {
        $what = if ($b.Outcome -eq 'NotExecuted') { 'did not run (skipped)' } else { "outcome $($b.Outcome)" }
        $problems.Add("$($b.Name) $what$(if ($b.Message) { ": $($b.Message)" })")
    }
    $testReport += [ordered]@{
        pattern  = $pattern
        matched  = $matched.Count
        outcomes = $outcomes
        ok       = ($matched.Count -gt 0 -and $bad.Count -eq 0)
        problems = $reasons
    }
}

$artifactReport = @()
foreach ($name in $RequiredArtifacts) {
    $path = if ($ArtifactsDirectory) { Join-Path $ArtifactsDirectory $name } else { $name }
    $entry = [ordered]@{ name = $name; exists = (Test-Path -LiteralPath $path -PathType Leaf); validJson = $false; caddyVersion = $null }
    if (-not $entry.exists) {
        $problems.Add("Required E2E artifact $name was not written to '$ArtifactsDirectory'.")
    } else {
        try {
            $json = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
            $entry.validJson = $true
            if ($json -is [pscustomobject] -and $json.PSObject.Properties['caddyVersion']) { $entry.caddyVersion = [string] $json.caddyVersion }
        } catch {
            $problems.Add("Required E2E artifact $name is not valid JSON: $($_.Exception.Message)")
        }
    }
    $artifactReport += $entry
}

$report = [ordered]@{
    utc       = (Get-Date).ToUniversalTime().ToString('o')
    ok        = ($problems.Count -eq 0)
    trxFiles  = @($trxFiles | ForEach-Object { $_.Name })
    results   = $results.Count
    tests     = $testReport
    artifacts = $artifactReport
    problems  = @($problems)
}
$dir = Split-Path -Parent $ReportPath
if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ReportPath -Encoding utf8

if ($env:GITHUB_STEP_SUMMARY) {
    $lines = @('## Required end-to-end tests', '', '| Test | Results | OK |', '|---|---|:-:|')
    foreach ($t in $testReport) {
        $o = ($t.outcomes.GetEnumerator() | ForEach-Object { "$($_.Key) $($_.Value)" }) -join ', '
        $lines += "| ``$($t.pattern)`` | $(if ($o) { $o } else { 'none' }) | $(if ($t.ok) { 'yes' } else { '**NO**' }) |"
    }
    ($lines -join "`n") + "`n" | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8
}

foreach ($t in $testReport) {
    Write-Host ("{0,-4} {1} ({2} result(s))" -f $(if ($t.ok) { 'ok' } else { 'FAIL' }), $t.pattern, $t.matched)
}
if ($problems.Count -gt 0) {
    $text = "Required end-to-end checks did not pass:`n - " + ($problems -join "`n - ")
    # One unwrapped line per problem (the error view below wraps long messages), as annotations on GitHub Actions.
    $prefix = if ($env:GITHUB_ACTIONS) { '::error title=Required E2E test::' } else { 'ERROR: ' }
    $problems | ForEach-Object { Write-Host "$prefix$_" }
    Write-Error $text
    exit 1
}
Write-Host "All $($RequiredTests.Count) required test pattern(s) ran and passed; report: $ReportPath"
