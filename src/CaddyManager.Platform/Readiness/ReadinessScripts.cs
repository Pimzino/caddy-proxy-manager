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
    /// <summary>
    /// Set for rules that are nice to have (HTTP/3): when the rule is missing the check is a warning, not a failure,
    /// and this note explains the practical effect.
    /// </summary>
    public string? OptionalNote { get; init; }

    public string Description => $"Allows inbound {Protocol} {Port} for {Purpose}. Created by Caddy Proxy Manager.";
}

/// <summary>PowerShell scripts used by the readiness checks and fixes. Values are embedded as single-quoted literals.</summary>
public static class ReadinessScripts
{
    public const string FirewallGroup = "Caddy Proxy Manager";

    private static string Q(string? s) => PowerShellRunner.Quote(s);
    private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Network profiles, domain membership, listeners on the given ports with owning processes, http.sys URL
    /// registrations, IIS (W3SVC) state and the WinHTTP proxy.
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

        # --- Domain membership (the computer DN is looked up by ComputerDn(), in its own process with its own timeout)
        $cs = Get-CimInstance -ClassName Win32_ComputerSystem
        $out.domain = [ordered]@{
            partOfDomain = [bool]$cs.PartOfDomain
            domain       = [string]$cs.Domain
            domainRole   = [int]$cs.DomainRole
        }

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
    /// Distinguished name of the computer account (ADSI, as the computer account when run as LocalSystem). Kept out of
    /// <see cref="SystemFacts"/> because an LDAP bind to an unreachable domain controller can block for a long time.
    /// </summary>
    public static string ComputerDn() => """
        $r = [ordered]@{ computerDn = $null; dnError = $null }
        if (-not (Get-CimInstance -ClassName Win32_ComputerSystem).PartOfDomain) { return $r }
        try {
            $searcher = [adsisearcher]"(&(objectCategory=computer)(sAMAccountName=$($env:COMPUTERNAME)`$))"
            $searcher.ClientTimeout = [TimeSpan]::FromSeconds(15)
            $searcher.ServerTimeLimit = [TimeSpan]::FromSeconds(15)
            [void]$searcher.PropertiesToLoad.Add('distinguishedName')
            $found = $searcher.FindOne()
            if ($found) { $r.computerDn = [string]$found.Properties['distinguishedname'][0] }
            else { $r.dnError = 'Computer account not found in Active Directory.' }
        } catch {
            $r.dnError = [string]$_.Exception.Message
        }
        $r
        """;

    /// <summary>
    /// Firewall service + profiles (ActiveStore), GPO "AllowLocalPolicyMerge" registry values, and every enabled
    /// inbound rule of the ActiveStore (local + GPO rules) whose port filter could match the given ports.
    /// Servers can have thousands of rules, so the port filters are enumerated first (one CIM query) and only the
    /// matching rules are joined with their application/service/address filters.
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
                allowInboundRules       = [string]$_.AllowInboundRules
                allowLocalFirewallRules = [string]$_.AllowLocalFirewallRules
            }
        })

        # Group Policy "Apply local firewall rules: No" is stored as AllowLocalPolicyMerge = 0.
        $out.gpoLocalMerge = @('Domain', 'Private', 'Public' | ForEach-Object {
            $key = "HKLM:\SOFTWARE\Policies\Microsoft\WindowsFirewall\$($_)Profile"
            $v = (Get-ItemProperty -Path $key -Name AllowLocalPolicyMerge -ErrorAction SilentlyContinue).AllowLocalPolicyMerge
            [ordered]@{ profile = $_; value = if ($null -eq $v) { -1 } else { [int]$v } }
        })

        # 1. Port filters that can match one of the ports (keyed by the rule's InstanceID).
        $candidates = @{}
        foreach ($pf in @(Get-NetFirewallPortFilter -PolicyStore ActiveStore -ErrorAction SilentlyContinue)) {
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
            if ($match) { $candidates[[string]$pf.InstanceID] = @{ protocol = $proto; localPorts = $lp } }
        }

        # 2. Enabled inbound rules with such a port filter.
        $rules = @()
        if ($candidates.Count -gt 0) {
            $rules = @(foreach ($r in @(Get-NetFirewallRule -PolicyStore ActiveStore -Direction Inbound -Enabled True -ErrorAction SilentlyContinue)) {
                if ($candidates.ContainsKey([string]$r.InstanceID)) { $r }
            })
        }

        # 3. Their application / service / address filters: per rule when there are few, one bulk query each otherwise.
        $appF = @{}; $svcF = @{}; $addrF = @{}
        if ($rules.Count -gt 40) {
            foreach ($f in @(Get-NetFirewallApplicationFilter -PolicyStore ActiveStore -ErrorAction SilentlyContinue)) { $id = [string]$f.InstanceID; if ($candidates.ContainsKey($id)) { $appF[$id] = $f } }
            foreach ($f in @(Get-NetFirewallServiceFilter -PolicyStore ActiveStore -ErrorAction SilentlyContinue)) { $id = [string]$f.InstanceID; if ($candidates.ContainsKey($id)) { $svcF[$id] = $f } }
            foreach ($f in @(Get-NetFirewallAddressFilter -PolicyStore ActiveStore -ErrorAction SilentlyContinue)) { $id = [string]$f.InstanceID; if ($candidates.ContainsKey($id)) { $addrF[$id] = $f } }
        } else {
            foreach ($r in $rules) {
                $id = [string]$r.InstanceID
                $appF[$id] = $r | Get-NetFirewallApplicationFilter -ErrorAction SilentlyContinue | Select-Object -First 1
                $svcF[$id] = $r | Get-NetFirewallServiceFilter -ErrorAction SilentlyContinue | Select-Object -First 1
                $addrF[$id] = $r | Get-NetFirewallAddressFilter -ErrorAction SilentlyContinue | Select-Object -First 1
            }
        }

        $out.rules = @(foreach ($r in $rules) {
            $id = [string]$r.InstanceID
            $c = $candidates[$id]
            [ordered]@{
                name            = [string]$r.Name
                displayName     = [string]$r.DisplayName
                action          = [string]$r.Action
                profile         = [string]$r.Profile
                protocol        = $c.protocol
                localPorts      = $c.localPorts
                program         = if ($appF[$id]) { [string]$appF[$id].Program } else { 'Any' }
                service         = if ($svcF[$id]) { [string]$svcF[$id].Service } else { 'Any' }
                remoteAddresses = if ($addrF[$id]) { @($addrF[$id].RemoteAddress | ForEach-Object { [string]$_ }) } else { @('Any') }
                source          = [string]$r.PolicyStoreSourceType
                sourceName      = [string]$r.PolicyStoreSource
                group           = [string]$r.Group
            }
        })
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
