using System.Globalization;

namespace CaddyManager.Platform.Readiness;

/// <summary>An inbound firewall rule the product needs.</summary>
public sealed record RequiredFirewallRule
{
    /// <summary>Readiness check id, e.g. "firewall.tcp443".</summary>
    public required string CheckId { get; init; }
    public required string DisplayName { get; init; }
    /// <summary>"TCP" or "UDP".</summary>
    public required string Protocol { get; init; }
    public required int Port { get; init; }
    public required string Purpose { get; init; }
    /// <summary>Executable that will listen on the port (rules restricted to other programs do not count).</summary>
    public string? Program { get; init; }
    /// <summary>Windows service that will listen on the port (rules restricted to other services do not count).</summary>
    public string? Service { get; init; }

    public string Description => $"Allows inbound {Protocol} {Port} for {Purpose}. Created by Caddy Proxy Manager.";
}

/// <summary>PowerShell scripts used by the readiness checks and fixes. Values are embedded as single-quoted literals.</summary>
public static class ReadinessScripts
{
    public const string FirewallGroup = "Caddy Proxy Manager";

    private static string Q(string? s) => PowerShellRunner.Quote(s);
    private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Network profiles, domain membership + computer DN (ADSI), listeners on the given ports with owning
    /// processes, http.sys URL registrations, IIS (W3SVC) state and the WinHTTP proxy.
    /// </summary>
    public static string SystemFacts(IEnumerable<int> tcpPorts, IEnumerable<int> udpPorts) => $$"""
        $tcpPorts = @({{string.Join(",", tcpPorts.Distinct().Select(N))}})
        $udpPorts = @({{string.Join(",", udpPorts.Distinct().Select(N))}})
        $out = [ordered]@{}

        # --- Network connection profiles
        $out.profiles = @(Get-NetConnectionProfile -ErrorAction SilentlyContinue | ForEach-Object {
            [ordered]@{
                interfaceAlias = [string]$_.InterfaceAlias
                interfaceIndex = [int]$_.InterfaceIndex
                name           = [string]$_.Name
                category       = [string]$_.NetworkCategory
            }
        })

        # --- Domain membership
        $cs = Get-CimInstance -ClassName Win32_ComputerSystem
        $domain = [ordered]@{
            partOfDomain = [bool]$cs.PartOfDomain
            domain       = [string]$cs.Domain
            domainRole   = [int]$cs.DomainRole
            computerDn   = $null
            dnError      = $null
        }
        if ($cs.PartOfDomain) {
            try {
                $searcher = [adsisearcher]"(&(objectCategory=computer)(sAMAccountName=$($env:COMPUTERNAME)`$))"
                $searcher.ClientTimeout = [TimeSpan]::FromSeconds(15)
                [void]$searcher.PropertiesToLoad.Add('distinguishedName')
                $found = $searcher.FindOne()
                if ($found) { $domain.computerDn = [string]$found.Properties['distinguishedname'][0] }
                else { $domain.dnError = 'Computer account not found in Active Directory.' }
            } catch {
                $domain.dnError = [string]$_.Exception.Message
            }
        }
        $out.domain = $domain

        # --- Listeners on the Caddy ports and their processes
        $listeners = @()
        $listeners += @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $tcpPorts -contains [int]$_.LocalPort } | ForEach-Object {
            [ordered]@{ protocol = 'TCP'; port = [int]$_.LocalPort; address = [string]$_.LocalAddress; processId = [int]$_.OwningProcess }
        })
        $listeners += @(Get-NetUDPEndpoint -ErrorAction SilentlyContinue | Where-Object { $udpPorts -contains [int]$_.LocalPort } | ForEach-Object {
            [ordered]@{ protocol = 'UDP'; port = [int]$_.LocalPort; address = [string]$_.LocalAddress; processId = [int]$_.OwningProcess }
        })
        $procCache = @{}
        foreach ($l in $listeners) {
            $procId = [int]$l.processId
            if (-not $procCache.ContainsKey($procId)) {
                $p = Get-Process -Id $procId -ErrorAction SilentlyContinue
                $procCache[$procId] = @{ name = [string]$p.ProcessName; path = [string]$p.Path }
            }
            $l.processName = $procCache[$procId].name
            $l.processPath = $procCache[$procId].path
        }
        $out.listeners = @($listeners)

        # --- http.sys URL registrations (PID 4 "System" listeners belong to http.sys)
        $out.httpSysUrls = @()
        try {
            $out.httpSysUrls = @(& netsh.exe http show servicestate view=requestq verbose=no |
                Where-Object { $_ -match '://' } | ForEach-Object { ([string]$_).Trim() } | Select-Object -Unique -First 50)
        } catch { }

        # --- IIS
        $w3svc = Get-Service -Name W3SVC -ErrorAction SilentlyContinue
        $out.w3svc = if ($w3svc) { [string]$w3svc.Status } else { $null }

        # --- WinHTTP proxy
        try { $out.winHttpProxy = ((& netsh.exe winhttp show proxy) -join "`n").Trim() } catch { $out.winHttpProxy = $null }

        $out
        """;

    /// <summary>
    /// Firewall service + profiles (ActiveStore), GPO "AllowLocalPolicyMerge" registry values, and every enabled
    /// inbound rule of the ActiveStore (local + GPO rules) whose port filter could match the given ports.
    /// </summary>
    public static string FirewallFacts(IEnumerable<int> ports) => $$"""
        $ports = @({{string.Join(",", ports.Distinct().Select(N))}})
        $out = [ordered]@{}
        $svc = Get-Service -Name mpssvc -ErrorAction SilentlyContinue
        $out.service = if ($svc) { [string]$svc.Status } else { 'Missing' }

        $out.profiles = @(Get-NetFirewallProfile -PolicyStore ActiveStore -ErrorAction SilentlyContinue | ForEach-Object {
            [ordered]@{
                name                    = [string]$_.Name
                enabled                 = [string]$_.Enabled
                defaultInboundAction    = [string]$_.DefaultInboundAction
                allowLocalFirewallRules = [string]$_.AllowLocalFirewallRules
            }
        })

        # Group Policy "Apply local firewall rules: No" is stored as AllowLocalPolicyMerge = 0.
        $out.gpoLocalMerge = @('Domain', 'Private', 'Public' | ForEach-Object {
            $key = "HKLM:\SOFTWARE\Policies\Microsoft\WindowsFirewall\$($_)Profile"
            $v = (Get-ItemProperty -Path $key -Name AllowLocalPolicyMerge -ErrorAction SilentlyContinue).AllowLocalPolicyMerge
            [ordered]@{ profile = $_; value = if ($null -eq $v) { -1 } else { [int]$v } }
        })

        $rules = @(Get-NetFirewallRule -PolicyStore ActiveStore -Direction Inbound -Enabled True -ErrorAction SilentlyContinue)
        $portF = @{}; Get-NetFirewallPortFilter -PolicyStore ActiveStore -ErrorAction SilentlyContinue | ForEach-Object { $portF[[string]$_.InstanceID] = $_ }
        $appF = @{};  Get-NetFirewallApplicationFilter -PolicyStore ActiveStore -ErrorAction SilentlyContinue | ForEach-Object { $appF[[string]$_.InstanceID] = $_ }
        $svcF = @{};  Get-NetFirewallServiceFilter -PolicyStore ActiveStore -ErrorAction SilentlyContinue | ForEach-Object { $svcF[[string]$_.InstanceID] = $_ }
        $addrF = @{}; Get-NetFirewallAddressFilter -PolicyStore ActiveStore -ErrorAction SilentlyContinue | ForEach-Object { $addrF[[string]$_.InstanceID] = $_ }

        $relevant = foreach ($r in $rules) {
            $id = [string]$r.InstanceID
            $pf = $portF[$id]
            if (-not $pf) { continue }
            $proto = [string]$pf.Protocol
            if (@('TCP', 'UDP', 'Any', '6', '17') -notcontains $proto) { continue }
            $lp = @($pf.LocalPort | ForEach-Object { [string]$_ })
            $match = $false
            foreach ($p in $lp) {
                if ($p -eq 'Any') { $match = $true; break }
                if ($p -match '^(\d+)-(\d+)$') {
                    foreach ($want in $ports) { if ($want -ge [int]$Matches[1] -and $want -le [int]$Matches[2]) { $match = $true } }
                } elseif ($p -match '^\d+$' -and $ports -contains [int]$p) { $match = $true }
            }
            if (-not $match) { continue }
            [ordered]@{
                name            = [string]$r.Name
                displayName     = [string]$r.DisplayName
                action          = [string]$r.Action
                profile         = [string]$r.Profile
                protocol        = $proto
                localPorts      = $lp
                program         = if ($appF[$id]) { [string]$appF[$id].Program } else { 'Any' }
                service         = if ($svcF[$id]) { [string]$svcF[$id].Service } else { 'Any' }
                remoteAddresses = if ($addrF[$id]) { @($addrF[$id].RemoteAddress | ForEach-Object { [string]$_ }) } else { @('Any') }
                source          = [string]$r.PolicyStoreSourceType
                sourceName      = [string]$r.PolicyStoreSource
                group           = [string]$r.Group
            }
        }
        $out.rules = @($relevant)
        $out
        """;

    /// <summary>Creates (or re-creates) one local inbound allow rule in the product's rule group.</summary>
    public static string CreateFirewallRule(RequiredFirewallRule rule) => $$"""
        $name = {{Q(rule.DisplayName)}}
        Get-NetFirewallRule -PolicyStore PersistentStore -DisplayName $name -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
        New-NetFirewallRule -DisplayName $name -Group {{Q(FirewallGroup)}} -Description {{Q(rule.Description)}} `
            -Direction Inbound -Action Allow -Protocol {{rule.Protocol}} -LocalPort {{N(rule.Port)}} -Profile Any -Enabled True | Out-Null
        [ordered]@{ created = $name }
        """;

    /// <summary>The same command as the fix, for copy/paste in the UI.</summary>
    public static string CreateFirewallRuleCommand(RequiredFirewallRule rule) =>
        $"New-NetFirewallRule -DisplayName {Q(rule.DisplayName)} -Group {Q(FirewallGroup)} -Direction Inbound -Action Allow " +
        $"-Protocol {rule.Protocol} -LocalPort {N(rule.Port)} -Profile Any -Enabled True";

    public static string SetNetworkPrivate(int interfaceIndex) => $$"""
        Set-NetConnectionProfile -InterfaceIndex {{N(interfaceIndex)}} -NetworkCategory Private
        $p = Get-NetConnectionProfile -InterfaceIndex {{N(interfaceIndex)}}
        [ordered]@{ interfaceAlias = [string]$p.InterfaceAlias; category = [string]$p.NetworkCategory }
        """;

    public static string RemoveFirewallGroup() => $$"""
        $removed = @(Get-NetFirewallRule -PolicyStore PersistentStore -Group {{Q(FirewallGroup)}} -ErrorAction SilentlyContinue)
        $removed | Remove-NetFirewallRule -ErrorAction SilentlyContinue
        [ordered]@{ removed = @($removed | ForEach-Object { [string]$_.DisplayName }) }
        """;
}
