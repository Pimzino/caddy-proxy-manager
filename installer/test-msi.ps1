<#
.SYNOPSIS
    Installs the MSI on this machine, verifies what Windows actually configured, and uninstalls it again.
.DESCRIPTION
    Proves the installer behaviour that WiX / Windows Installer documentation leaves to authoring details:

      * MSI tables: the OS launch condition uses the real build number (VersionNT is 603 on Windows 8.1 and on everything
        after it), CPM_ConfigureService runs after Wix4SchedServiceConfig (so the 5 s / 10 s / 30 s recovery actions win),
        and the quiet-exec custom actions use the native x64 WixQuietExec entry point.
      * Install (/qn, UI_PORT): service registered as LocalSystem, Automatic (Delayed Start), recovery actions and the
        failure-actions flag, firewall exception on UI_PORT, Start-menu .url, /api/health answers.
      * Recovery: the service process is killed; the SCM must start a new process within 30 s.
      * Reinstall without UI_PORT keeps the port (RegistrySearch).
      * Uninstall (/qn): service, firewall exception, .url and the Caddy service are removed.

    Needs an elevated PowerShell on a machine where Caddy Proxy Manager is NOT installed (CI runner or a throw-away VM).
    The first start downloads Caddy from GitHub (the manager's bootstrap); no certificates are requested.
.EXAMPLE
    .\installer\test-msi.ps1 -Msi .\artifacts\CaddyProxyManager-1.0.0-x64.msi
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Msi,
    [int] $UiPort = 8181,
    [string] $LogDir = $env:RUNNER_TEMP
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
if (-not $LogDir) { $LogDir = [IO.Path]::GetTempPath() }
$Msi = (Resolve-Path -LiteralPath $Msi).Path
$service = 'CaddyProxyManager'
$ruleName = 'Caddy Proxy Manager - Management UI (TCP-In)'
$shortcut = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Caddy Proxy Manager.url'
$failures = [System.Collections.Generic.List[string]]::new()
$results = [System.Collections.Generic.List[object]]::new()

function Check([bool] $ok, [string] $what) {
    $results.Add([ordered]@{ ok = $ok; check = $what })
    if ($ok) { Write-Host "  ok   $what" -ForegroundColor Green }
    else { Write-Host "  FAIL $what" -ForegroundColor Red; $failures.Add($what) }
}

function Invoke-Msiexec([string[]] $arguments, [string] $log) {
    $p = Start-Process -FilePath msiexec.exe -ArgumentList ($arguments + @('/qn', '/norestart', '/l*v', "`"$log`"")) -Wait -PassThru
    return $p.ExitCode
}

function Query-Msi([string] $sql) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $db = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($Msi, 0))
    $view = $db.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $db, @($sql))
    $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null) | Out-Null
    $rows = @()
    while ($true) {
        $rec = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
        if ($null -eq $rec) { break }
        $count = $rec.GetType().InvokeMember('FieldCount', 'GetProperty', $null, $rec, $null)
        # A plain loop with an [int]: inside ForEach-Object $_ is a PSObject wrapper, which the COM IDispatch call
        # rejects with DISP_E_TYPEMISMATCH.
        $fields = for ([int] $i = 1; $i -le $count; $i++) {
            $rec.GetType().InvokeMember('StringData', 'GetProperty', $null, $rec, [object[]] @([int] $i))
        }
        $rows += , @($fields)
    }
    $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null) | Out-Null
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($db) | Out-Null
    return , $rows
}

function Service-Pid {
    $s = Get-CimInstance Win32_Service -Filter "Name='$service'"
    if ($s -and $s.State -eq 'Running') { return [int]$s.ProcessId } else { return 0 }
}

function Wait-Health([int] $seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $r = Invoke-WebRequest -Uri "http://localhost:$UiPort/api/health" -UseBasicParsing -TimeoutSec 5
            if ($r.StatusCode -eq 200) { return $true }
        } catch { }
        Start-Sleep -Seconds 2
    }
    return $false
}

if (Get-Service -Name $service -ErrorAction SilentlyContinue) { throw "$service is already installed; run this on a clean machine." }

Write-Host "==> MSI tables ($Msi)"
$launch = Query-Msi 'SELECT `Condition`, `Description` FROM `LaunchCondition`'
Check (@($launch | Where-Object { $_[0] -like '*CPM_OSBUILD >= 17763*' }).Count -eq 1) 'LaunchCondition uses the OS build number (CPM_OSBUILD >= 17763)'
Check (@($launch | Where-Object { $_[0] -like '*VersionNT >= 603*' }).Count -eq 0) 'LaunchCondition no longer relies on VersionNT >= 603'
$seq = @{}
foreach ($r in (Query-Msi 'SELECT `Action`, `Sequence` FROM `InstallExecuteSequence`')) { $seq[$r[0]] = [int]$r[1] }
Check ($seq.ContainsKey('Wix4SchedServiceConfig_X64') -and $seq['CPM_ConfigureService'] -gt $seq['Wix4SchedServiceConfig_X64']) `
    "CPM_ConfigureService ($($seq['CPM_ConfigureService'])) is sequenced after Wix4SchedServiceConfig_X64 ($($seq['Wix4SchedServiceConfig_X64']))"
Check ($seq['CPM_ConfigureService'] -gt $seq['InstallServices'] -and $seq['CPM_ConfigureService'] -lt $seq['StartServices']) 'CPM_ConfigureService runs between InstallServices and StartServices'
$cas = Query-Msi 'SELECT `Action`, `Source`, `Target` FROM `CustomAction`'
foreach ($name in 'CPM_ConfigureUi', 'CPM_ConfigureService', 'CPM_RemoveCaddyService') {
    $ca = @($cas | Where-Object { $_[0] -eq $name })
    Check ($ca.Count -eq 1 -and $ca[0][1] -eq 'Wix4UtilCA_X64' -and $ca[0][2] -eq 'WixQuietExec') "$name uses Wix4UtilCA_X64!WixQuietExec"
}

Write-Host "==> Install (UI_PORT=$UiPort)"
$installLog = Join-Path $LogDir 'cpm-install.log'
$code = Invoke-Msiexec @('/i', "`"$Msi`"", "UI_PORT=$UiPort") $installLog
Check ($code -eq 0) "msiexec /i exit code 0 (was $code; log: $installLog)"
try {
    $log = Get-Content -LiteralPath $installLog -Raw   # BOM detection handles the UTF-16 verbose log
    # WixQuietExec logs the command's output; configure-service prints the start type it applied.
    Check ($log -match 'WixQuietExec:.*Automatic \(Delayed Start\)') 'install log shows WixQuietExec running configure-service'

    $key = "HKLM:\SYSTEM\CurrentControlSet\Services\$service"
    function RegValue([string] $name) { Get-ItemPropertyValue -LiteralPath $key -Name $name -ErrorAction SilentlyContinue }
    Check ((RegValue 'Start') -eq 2 -and (RegValue 'DelayedAutostart') -eq 1) 'service start type is Automatic (Delayed Start)'
    Check ((RegValue 'ObjectName') -eq 'LocalSystem') "service account is LocalSystem (was $(RegValue 'ObjectName'))"
    # sc.exe output is English on the CI runner (it is localised elsewhere; the product uses QueryServiceConfig2).
    $qf = (& sc.exe qfailure $service) -join "`n"
    $delays = @([regex]::Matches($qf, 'RESTART -- Delay = (\d+)') | ForEach-Object { [int]$_.Groups[1].Value })
    Check (($delays -join ',') -eq '5000,10000,30000') "recovery actions are restart 5 s / 10 s / 30 s (sc qfailure: $($delays -join ','))"
    Check ((RegValue 'FailureActionsOnNonCrashFailures') -eq 1) 'failure-actions flag is set (FailureActionsOnNonCrashFailures = 1)'

    $rule = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
    $port = if ($rule) { ($rule | Get-NetFirewallPortFilter).LocalPort } else { $null }
    Check ("$port" -eq "$UiPort") "firewall exception '$ruleName' opens TCP $UiPort (found: $port)"
    Check ((Test-Path -LiteralPath $shortcut) -and ((Get-Content -LiteralPath $shortcut -Raw) -match [regex]::Escape("URL=http://localhost:$UiPort/"))) "Start-menu shortcut points to http://localhost:$UiPort/"
    Check (Wait-Health 120) "GET http://localhost:$UiPort/api/health answers 200"

    Write-Host '==> Recovery after a crash'
    $pid1 = Service-Pid
    Check ($pid1 -gt 0) "service is running (PID $pid1)"
    if ($pid1 -gt 0) {
        Stop-Process -Id $pid1 -Force
        $deadline = (Get-Date).AddSeconds(45); $pid2 = 0
        while ((Get-Date) -lt $deadline -and ($pid2 -eq 0 -or $pid2 -eq $pid1)) { Start-Sleep -Seconds 1; $pid2 = Service-Pid }
        Check ($pid2 -gt 0 -and $pid2 -ne $pid1) "the SCM restarted the killed service (new PID $pid2)"
        Check (Wait-Health 60) 'the UI is back after the restart'
    }

    # The UI's "Restart" (POST /api/system/restart) ends the process with Environment.Exit(1) without reporting
    # SERVICE_STOPPED; the SCM must treat that as a failure and restart the manager (PlatformEndpoints.cs).
    Write-Host '==> Restart from the API (first-run setup, sign-in, POST /api/system/restart)'
    $base = "http://localhost:$UiPort"
    $tokenFile = Join-Path $env:ProgramData 'CaddyProxyManager\setup-token.txt'
    $session = $null
    if (Test-Path -LiteralPath $tokenFile) {
        $setup = @{ token = (Get-Content -LiteralPath $tokenFile -Raw).Trim(); email = 'msi-test@example.com'; name = 'MSI test'
                    password = 'msi-test-' + [guid]::NewGuid().ToString('N') } | ConvertTo-Json
        try {
            Invoke-RestMethod -Uri "$base/api/setup" -Method Post -ContentType 'application/json' -Body $setup `
                -Headers @{ 'X-CPM-Request' = '1' } -SessionVariable session | Out-Null
        } catch { Write-Host "  setup failed: $($_.Exception.Message)" }
    }
    Check ($null -ne $session) 'first-run setup signed in an administrator'
    if ($null -ne $session) {
        $before = Service-Pid
        $r = Invoke-WebRequest -Uri "$base/api/system/restart" -Method Post -WebSession $session -Headers @{ 'X-CPM-Request' = '1' } -UseBasicParsing
        Check ($r.StatusCode -eq 202) "POST /api/system/restart answered 202 (was $($r.StatusCode))"
        $deadline = (Get-Date).AddSeconds(45); $after = 0
        while ((Get-Date) -lt $deadline -and ($after -eq 0 -or $after -eq $before)) { Start-Sleep -Seconds 1; $after = Service-Pid }
        Check ($after -gt 0 -and $after -ne $before) "the SCM restarted the manager after the API restart (PID $before -> $after)"
        Check (Wait-Health 60) 'the UI is back after the API restart'
        $scm = @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Service Control Manager'; Id = 7031, 7034; StartTime = (Get-Date).AddMinutes(-5) } -ErrorAction SilentlyContinue |
            Where-Object { $_.Message -match 'Caddy Proxy Manager' })
        Write-Host "  SCM events 7031/7034 for the manager in the last 5 minutes: $($scm.Count)"
    }

    Write-Host '==> Reinstall without UI_PORT keeps the port'
    $repairLog = Join-Path $LogDir 'cpm-repair.log'
    $code = Invoke-Msiexec @('/fvomus', "`"$Msi`"") $repairLog
    Check ($code -eq 0) "repair exit code 0 (was $code)"
    $port = (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Get-NetFirewallPortFilter).LocalPort
    Check ("$port" -eq "$UiPort") "firewall exception still on TCP $UiPort after the repair (found: $port)"
}
finally {
    Write-Host '==> Uninstall'
    $uninstallLog = Join-Path $LogDir 'cpm-uninstall.log'
    $code = Invoke-Msiexec @('/x', "`"$Msi`"") $uninstallLog
    Check ($code -eq 0) "msiexec /x exit code 0 (was $code; log: $uninstallLog)"
    Check (-not (Get-Service -Name $service -ErrorAction SilentlyContinue)) 'manager service removed'
    Check (-not (Get-Service -Name 'Caddy' -ErrorAction SilentlyContinue)) 'Caddy service removed'
    Check (-not (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)) 'firewall exception removed'
    Check (-not (Test-Path -LiteralPath $shortcut)) 'Start-menu shortcut removed'
}

# Verifiable artifact of this run (uploaded by the workflow next to the msiexec logs).
[ordered]@{
    msi = $Msi; utc = (Get-Date).ToUniversalTime().ToString('o'); os = [Environment]::OSVersion.VersionString
    build = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').CurrentBuildNumber
    checks = $results; failed = $failures.Count
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $LogDir 'cpm-msi-test.json') -Encoding utf8

if ($failures.Count -gt 0) { throw "$($failures.Count) MSI check(s) failed:`n  " + ($failures -join "`n  ") }
Write-Host 'All MSI checks passed.' -ForegroundColor Green
