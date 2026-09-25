using System.Text.Json;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using CaddyManager.Platform.Infrastructure;
using CaddyManager.Platform.Readiness;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaddyManager.Platform.Tests;

public class PowerShellOutputTests
{
    [Fact]
    public void ParsesJsonAfterNoise()
    {
        var e = PowerShellRunner.ParseOutput("WARNING: something\r\n{\"a\":1,\"b\":[\"x\"]}\r\n");
        Assert.Equal(1, e.GetProperty("a").GetInt32());
        var ansi = PowerShellRunner.ParseOutput("\u001b[?1h\u001b[?1l{\"a\":2}\n\u001b[?1h");
        Assert.Equal(2, ansi.GetProperty("a").GetInt32());
    }

    [Fact]
    public void SurfacesScriptErrors()
    {
        var ex = Assert.Throws<PowerShellException>(() =>
            PowerShellRunner.ParseOutput("{\"__error\":\"Access is denied.\",\"__at\":\"At line:3 char:1\"}"));
        Assert.StartsWith("Access is denied.", ex.Message);
        Assert.Throws<PowerShellException>(() => PowerShellRunner.ParseOutput("", "powershell.exe: not found", 1));
        Assert.Throws<PowerShellException>(() => PowerShellRunner.ParseOutput("{not json"));
    }

    [Fact]
    public void KeepsTheConsoleCodePage()
    {
        // Setting [Console]::OutputEncoding to UTF-8 would make Windows PowerShell mis-decode netsh.exe output
        // (OEM code page); the JSON result is ASCII-escaped instead.
        Assert.DoesNotContain("[Console]::OutputEncoding =", PowerShellRunner.Wrap("1"));
        Assert.Contains("__Ascii", PowerShellRunner.Wrap("1"));
    }

    [Fact]
    public void StdinIsASingleLine()
    {
        var stdin = PowerShellRunner.BuildStdin("Get-Date\n'multi'\n@{ a = 1 }");
        Assert.Equal(1, stdin.Count(c => c == '\n'));
        Assert.EndsWith("\n", stdin);
        Assert.Equal("'it''s'", PowerShellRunner.Quote("it's"));
    }
}

/// <summary>
/// Uses PowerShell 7 (pwsh) when installed to (a) exercise the stdin/Base64/ConvertTo-Json round trip and
/// (b) syntax-check every generated Windows script with the PowerShell parser.
/// </summary>
[Trait("Category", "PowerShell")]
public class PowerShellScriptTests
{
    private static string? Pwsh()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            foreach (var name in new[] { "pwsh", "pwsh.exe" })
            {
                var p = Path.Combine(dir, name);
                if (File.Exists(p)) return p;
            }
        }
        return null;
    }

    private static PowerShellRunner Runner()
    {
        var pwsh = Pwsh();
        Assert.SkipWhen(pwsh is null, "pwsh (PowerShell 7) is not installed.");
        return new PowerShellRunner(NullLogger<PowerShellRunner>.Instance) { ExecutableOverride = pwsh };
    }

    [Fact]
    public async Task RoundTripsObjectsThroughStdin()
    {
        var ps = Runner();
        var result = await ps.RunJsonAsync("""
            function Get-Thing([string]$n) { "thing-$n" }
            $list = @(1)
            [ordered]@{
                text  = 'Réseau – "quoted" \ back'
                one   = @($list)
                thing = Get-Thing 'x'
                nested = @{ a = @(@{ b = 2 }) }
            }
            """, ct: TestContext.Current.CancellationToken);
        Assert.Equal("Réseau – \"quoted\" \\ back", result.GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Array, result.GetProperty("one").ValueKind);
        Assert.Equal("thing-x", result.GetProperty("thing").GetString());
        Assert.Equal(2, result.GetProperty("nested").GetProperty("a")[0].GetProperty("b").GetInt32());
    }

    [Fact]
    public async Task ReportsTerminatingErrors()
    {
        var ps = Runner();
        // Non-terminating errors become terminating ($ErrorActionPreference = 'Stop') ...
        var ex = await Assert.ThrowsAsync<PowerShellException>(() =>
            ps.RunJsonAsync("Get-Item -Path 'cpm-missing-item-5f3a'", ct: TestContext.Current.CancellationToken));
        Assert.Contains("cpm-missing-item-5f3a", ex.Message);
        // ... and thrown errors are reported with their message.
        var thrown = await Assert.ThrowsAsync<PowerShellException>(() =>
            ps.RunJsonAsync("throw 'cpm custom failure'", ct: TestContext.Current.CancellationToken));
        Assert.StartsWith("cpm custom failure", thrown.Message);
    }

    [Fact]
    public async Task TimesOut()
    {
        var ps = Runner();
        await Assert.ThrowsAsync<TimeoutException>(() =>
            ps.RunJsonAsync("Start-Sleep -Seconds 30", TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Mocks of the NetSecurity cmdlets (functions take precedence over cmdlets) so the firewall script's own logic -
    /// port matching, the InstanceID join and both filter lookup strategies - runs on any OS.
    /// </summary>
    private static string FirewallMocks(int extraAnyRules) => $$"""
        $__extra = {{extraAnyRules}}
        function Get-Service { param($Name) [pscustomobject]@{ Status = 'Running' } }
        function Get-NetFirewallProfile { [CmdletBinding()] param($PolicyStore)
            [pscustomobject]@{ Name = 'Domain'; Enabled = 'True'; DefaultInboundAction = 'NotConfigured'; AllowInboundRules = 'NotConfigured'; AllowLocalFirewallRules = 'False' }
            [pscustomobject]@{ Name = 'Public'; Enabled = 'NotConfigured'; DefaultInboundAction = 'Block'; AllowInboundRules = 'False'; AllowLocalFirewallRules = 'NotConfigured' }
        }
        $__pf = @(
            [pscustomobject]@{ InstanceID = 'R80'; Protocol = 'TCP'; LocalPort = '80' }
            [pscustomobject]@{ InstanceID = 'R443'; Protocol = 'TCP'; LocalPort = @('8443', '443') }
            [pscustomobject]@{ InstanceID = 'RRange'; Protocol = 'UDP'; LocalPort = '400-500' }
            [pscustomobject]@{ InstanceID = 'RAny'; Protocol = 'Any'; LocalPort = 'Any' }
            [pscustomobject]@{ InstanceID = 'RIcmp'; Protocol = 'ICMPv4'; LocalPort = 'RPC' }
            [pscustomobject]@{ InstanceID = 'R22'; Protocol = 'TCP'; LocalPort = '22' }
            [pscustomobject]@{ InstanceID = 'ROut'; Protocol = 'TCP'; LocalPort = '443' }
        ) + @(if ($__extra -gt 0) { 1..$__extra | ForEach-Object { [pscustomobject]@{ InstanceID = "X$_"; Protocol = 'TCP'; LocalPort = 'Any' } } })
        function Get-NetFirewallPortFilter { [CmdletBinding()] param($PolicyStore) $__pf }
        function Get-NetFirewallRule { [CmdletBinding()] param($PolicyStore, $Direction, $Enabled)
            # ROut is outbound/disabled: the real cmdlet filters it out through -Direction/-Enabled.
            foreach ($id in @('R80', 'R443', 'RRange', 'RAny', 'RIcmp', 'R22') + @($__pf | Where-Object { $_.InstanceID -like 'X*' } | ForEach-Object { $_.InstanceID })) {
                [pscustomobject]@{ InstanceID = $id; Name = $id; DisplayName = "Rule $id"; Action = $(if ($id -eq 'RRange') { 'Block' } else { 'Allow' });
                    Profile = 'Domain, Private'; PolicyStoreSourceType = $(if ($id -eq 'R443') { 'GroupPolicy' } else { 'Local' });
                    PolicyStoreSource = 'corp.example.com\CPM'; Group = 'Caddy Proxy Manager' }
            }
        }
        $__app = @([pscustomobject]@{ InstanceID = 'R80'; Program = 'C:\ProgramData\CaddyProxyManager\caddy\bin\caddy.exe' })
        $__svc = @([pscustomobject]@{ InstanceID = 'RAny'; Service = 'Caddy' })
        $__addr = @([pscustomobject]@{ InstanceID = 'R443'; RemoteAddress = @('LocalSubnet', '10.0.0.0/8') })
        $global:__calls = @{ bulk = 0; perRule = 0 }
        function __Filter($all, $rule) {
            if ($null -eq $rule) { $global:__calls.bulk++; $all } else { $global:__calls.perRule++; $all | Where-Object { $_.InstanceID -eq $rule.InstanceID } }
        }
        function Get-NetFirewallApplicationFilter { [CmdletBinding()] param([Parameter(ValueFromPipeline = $true)] $AssociatedNetFirewallRule, $PolicyStore) process { __Filter $__app $AssociatedNetFirewallRule } }
        function Get-NetFirewallServiceFilter { [CmdletBinding()] param([Parameter(ValueFromPipeline = $true)] $AssociatedNetFirewallRule, $PolicyStore) process { __Filter $__svc $AssociatedNetFirewallRule } }
        function Get-NetFirewallAddressFilter { [CmdletBinding()] param([Parameter(ValueFromPipeline = $true)] $AssociatedNetFirewallRule, $PolicyStore) process { __Filter $__addr $AssociatedNetFirewallRule } }

        """;

    [Theory]
    [InlineData(0)]   // few candidate rules: filters looked up per rule
    [InlineData(60)]  // many candidate rules: one bulk query per filter type
    public async Task FirewallFactsScriptJoinsPortFiltersWithRules(int extraAnyRules)
    {
        var ps = Runner();
        var script = FirewallMocks(extraAnyRules) + ReadinessScripts.FirewallFacts([80, 443, 81]) +
                     "\n$out.calls = $global:__calls"; // $out was already emitted; serialisation happens later
        var json = await ps.RunJsonAsync(script, TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
        var f = WindowsFactsParser.ParseFirewall(json);

        Assert.Equal(JsonValueKind.Object, json.ValueKind);
        Assert.Equal("Running", f.Service);
        Assert.Equal(["Domain", "Public"], f.Profiles.Select(p => p.Name));
        Assert.False(f.Profiles[1].AllowsInboundRules);
        Assert.True(f.Profiles[1].IsEnabled);
        Assert.Equal(["R80", "R443", "RRange", "RAny"], f.Rules.Select(r => r.Name).Where(n => !n.StartsWith('X')));
        Assert.Equal(extraAnyRules, f.Rules.Count(r => r.Name.StartsWith('X')));
        var r80 = f.Rules.Single(r => r.Name == "R80");
        Assert.Equal(@"C:\ProgramData\CaddyProxyManager\caddy\bin\caddy.exe", r80.Program);
        Assert.Equal(["80"], r80.LocalPorts);
        Assert.Equal("Any", r80.Service);
        Assert.Equal(["Any"], r80.RemoteAddresses);
        var r443 = f.Rules.Single(r => r.Name == "R443");
        Assert.Equal(["8443", "443"], r443.LocalPorts);
        Assert.Equal(["LocalSubnet", "10.0.0.0/8"], r443.RemoteAddresses);
        Assert.Equal("GroupPolicy", r443.Source);
        Assert.Equal("Block", f.Rules.Single(r => r.Name == "RRange").Action);
        Assert.Equal("Caddy", f.Rules.Single(r => r.Name == "RAny").Service);

        var calls = json.GetProperty("calls");
        if (extraAnyRules > 40)
        {
            Assert.Equal(3, calls.GetProperty("bulk").GetInt32());
            Assert.Equal(0, calls.GetProperty("perRule").GetInt32());
        }
        else
        {
            Assert.Equal(0, calls.GetProperty("bulk").GetInt32());
            Assert.Equal(12, calls.GetProperty("perRule").GetInt32()); // 4 matching rules x 3 filter types
        }
    }

    private const string SystemMocks = """
        function Get-NetConnectionProfile { [CmdletBinding()] param($InterfaceIndex)
            [pscustomobject]@{ InterfaceAlias = 'Ethernet0'; InterfaceIndex = 6; Name = 'corp.example.com'; NetworkCategory = 'DomainAuthenticated' }
        }
        function Get-CimInstance { [CmdletBinding()] param($ClassName)
            [pscustomobject]@{ PartOfDomain = $true; Domain = 'corp.example.com'; DomainRole = 3 }
        }
        function Get-NetTCPConnection { [CmdletBinding()] param($State)
            [pscustomobject]@{ LocalPort = 80; LocalAddress = '::'; OwningProcess = 4 }
            [pscustomobject]@{ LocalPort = 443; LocalAddress = '0.0.0.0'; OwningProcess = 4712 }
            [pscustomobject]@{ LocalPort = 443; LocalAddress = '::'; OwningProcess = 4712 }
            [pscustomobject]@{ LocalPort = 3389; LocalAddress = '0.0.0.0'; OwningProcess = 1000 }
        }
        function Get-NetUDPEndpoint { [CmdletBinding()] param()
            [pscustomobject]@{ LocalPort = 443; LocalAddress = '0.0.0.0'; OwningProcess = 4712 }
            [pscustomobject]@{ LocalPort = 53; LocalAddress = '0.0.0.0'; OwningProcess = 2000 }
        }
        function Get-Process { [CmdletBinding()] param($Id)
            if ($Id -eq 4) { [pscustomobject]@{ ProcessName = 'System'; Path = $null } }
            elseif ($Id -eq 4712) { [pscustomobject]@{ ProcessName = 'caddy'; Path = 'C:\ProgramData\CaddyProxyManager\caddy\bin\caddy.exe' } }
        }
        function netsh.exe { 'Registered URLs:'; '    HTTP://+:80/WSMAN/'; '    HTTPS://+:443/ADFS/'; '    HTTPS://+:443/ADFS/' }
        function Get-Service { [CmdletBinding()] param($Name) if ($Name -eq 'W3SVC') { [pscustomobject]@{ Status = 'Stopped' } } }

        """;

    [Fact]
    public async Task SystemFactsScriptCollectsListenersAndDomain()
    {
        var ps = Runner();
        var json = await ps.RunJsonAsync(SystemMocks + ReadinessScripts.SystemFacts([80, 443], [443]), TimeSpan.FromSeconds(60),
            TestContext.Current.CancellationToken);
        Assert.Equal(JsonValueKind.Object, json.ValueKind);
        var s = WindowsFactsParser.ParseSystem(json);
        var profile = Assert.Single(s.Profiles);
        Assert.Equal(("Ethernet0", 6, "DomainAuthenticated"), (profile.InterfaceAlias, profile.InterfaceIndex, profile.Category));
        Assert.True(s.Domain.PartOfDomain);
        Assert.Equal("corp.example.com", s.Domain.Domain);
        Assert.Equal(3, s.Domain.DomainRole);
        Assert.Equal(4, s.Listeners.Count); // 3389 and UDP 53 are filtered out
        Assert.True(s.Listeners.Single(l => l.Port == 80).IsHttpSys);
        Assert.Equal("System", s.Listeners.Single(l => l.Port == 80).ProcessName);
        Assert.All(s.Listeners.Where(l => l.Port == 443), l => Assert.Equal(@"C:\ProgramData\CaddyProxyManager\caddy\bin\caddy.exe", l.ProcessPath));
        Assert.Contains(s.Listeners, l => l.Protocol == "UDP" && l.Port == 443);
        Assert.Equal(["HTTP://+:80/WSMAN/", "HTTPS://+:443/ADFS/"], s.HttpSysUrls.Where(u => u.Contains("://")));
        Assert.Equal("Stopped", s.W3svc);
        Assert.Contains("Registered URLs:", s.WinHttpProxy); // the netsh mock answers every call
    }

    [Fact]
    public async Task ComputerDnScriptHandlesWorkgroupAndLookupFailures()
    {
        var ps = Runner();
        var ct = TestContext.Current.CancellationToken;
        const string workgroup = "function Get-CimInstance { [CmdletBinding()] param($ClassName) [pscustomobject]@{ PartOfDomain = $false } }\n";
        var (dn, err) = WindowsFactsParser.ParseComputerDn(await ps.RunJsonAsync(workgroup + ReadinessScripts.ComputerDn(), ct: ct));
        Assert.Null(dn);
        Assert.Null(err);
        // A failing directory lookup is reported, not thrown ([adsisearcher] is replaced by a failing stub).
        const string joined = "function Get-CimInstance { [CmdletBinding()] param($ClassName) [pscustomobject]@{ PartOfDomain = $true } }\n";
        var script = joined + ReadinessScripts.ComputerDn().Replace("[adsisearcher]", "[System.Int32]", StringComparison.Ordinal);
        (dn, err) = WindowsFactsParser.ParseComputerDn(await ps.RunJsonAsync(script, ct: ct));
        Assert.Null(dn);
        Assert.NotNull(err);
    }

    private const string GpoMocks = """
        $global:__log = New-Object System.Collections.Generic.List[string]
        function Get-Module { [CmdletBinding()] param([switch] $ListAvailable, $Name) [pscustomobject]@{ Name = $Name } }
        function Import-Module { [CmdletBinding()] param($Name) }
        function Get-GPO { [CmdletBinding()] param($Name, $Domain) $global:__log.Add("Get-GPO $Name"); throw "A GPO with the display name '$Name' was not found." }
        function New-GPO { [CmdletBinding()] param($Name, $Domain, $Comment) $global:__log.Add("New-GPO $Name@$Domain"); [pscustomobject]@{ Id = [guid]::Empty; GpoStatus = 'AllSettingsEnabled' } }
        function Remove-NetFirewallRule { [CmdletBinding()] param($PolicyStore, $DisplayName) $global:__log.Add("Remove-NetFirewallRule $PolicyStore|$DisplayName") }
        function New-NetFirewallRule { [CmdletBinding()] param($PolicyStore, $DisplayName, $Group, $Description, $Direction, $Action, $Protocol, $LocalPort, $Profile, $Enabled)
            $global:__log.Add("New-NetFirewallRule $PolicyStore|$DisplayName|$Direction|$Action|$Protocol|$LocalPort|$Profile|$Enabled"); 'rule' }
        function Set-GPPermission { [CmdletBinding()] param($Name, $Domain, $TargetName, $TargetType, $PermissionLevel, [switch] $Replace)
            $global:__log.Add("Set-GPPermission $TargetName|$TargetType|$PermissionLevel|$Replace"); 'perm' }
        function Get-GPInheritance { [CmdletBinding()] param($Target, $Domain) $global:__log.Add("Get-GPInheritance $Target"); [pscustomobject]@{ GpoLinks = @() } }
        function New-GPLink { [CmdletBinding()] param($Name, $Domain, $Target, $LinkEnabled) $global:__log.Add("New-GPLink $Target|$LinkEnabled"); 'link' }
        function Set-GPLink { [CmdletBinding()] param($Name, $Domain, $Target, $LinkEnabled) $global:__log.Add("Set-GPLink $Target") }

        """;

    [Theory]
    [InlineData("CN=WEB01,OU=Web Servers,OU=Servers,DC=corp,DC=example,DC=com", "OU=Web Servers,OU=Servers,DC=corp,DC=example,DC=com")]
    [InlineData("CN=WEB01,CN=Computers,DC=corp,DC=example,DC=com", "DC=corp,DC=example,DC=com")]
    public async Task GpoScriptRunsAgainstMockedGroupPolicyCmdlets(string computerDn, string expectedLinkTarget)
    {
        var ps = Runner();
        var rules = ReadinessService.RequiredRules(new AppPaths(Path.GetTempPath()), new CaddySettings(), new UiSettings(), 81);
        var gpo = GpoScriptBuilder.Build(new GpoScriptInput
            {
                Domain = "corp.example.com", ComputerName = "WEB01", ComputerDn = computerDn, Rules = rules, ProductVersion = "1.0.0",
            })
            // PowerShell 7 test host: drop the Windows PowerShell requirement and the Windows-only SID translation.
            .Replace("#Requires -PSEdition Desktop", "", StringComparison.Ordinal)
            .Replace("(New-Object System.Security.Principal.SecurityIdentifier('S-1-5-11')).Translate([System.Security.Principal.NTAccount]).Value",
                "'NT AUTHORITY\\Authenticated Users'", StringComparison.Ordinal);
        var file = Path.Combine(Path.GetTempPath(), $"cpm-gpo-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(file, gpo, TestContext.Current.CancellationToken);
        try
        {
            var json = await ps.RunJsonAsync(GpoMocks + $"& {PowerShellRunner.Quote(file)} 6>$null | Out-Null\n[ordered]@{{ log = @($global:__log) }}",
                TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            var log = json.GetProperty("log").EnumerateArray().Select(e => e.GetString()!).ToList();
            var store = "corp.example.com\\Caddy Proxy Manager - Firewall";
            Assert.Equal("Get-GPO Caddy Proxy Manager - Firewall", log[0]);
            Assert.Equal("New-GPO Caddy Proxy Manager - Firewall@corp.example.com", log[1]);
            foreach (var r in rules)
            {
                var remove = log.IndexOf($"Remove-NetFirewallRule {store}|{r.DisplayName}");
                var create = log.IndexOf($"New-NetFirewallRule {store}|{r.DisplayName}|Inbound|Allow|{r.Protocol}|{r.Port}|Any|True");
                Assert.True(remove >= 0 && create > remove, string.Join("\n", log));
            }
            var permission = log.IndexOf("Set-GPPermission WEB01|Computer|GpoApply|False");
            var authUsers = log.IndexOf("Set-GPPermission Authenticated Users|Group|GpoRead|True");
            var link = log.IndexOf($"New-GPLink {expectedLinkTarget}|Yes");
            Assert.True(permission >= 0 && authUsers > permission && link > authUsers, string.Join("\n", log));
            Assert.Contains($"Get-GPInheritance {expectedLinkTarget}", log);
        }
        finally
        {
            File.Delete(file);
        }
    }

    public static TheoryData<string, string> Scripts()
    {
        var paths = new AppPaths(Path.GetTempPath());
        var rules = ReadinessService.RequiredRules(paths, new CaddySettings(), new UiSettings(), 81);
        var data = new TheoryData<string, string>
        {
            { "SystemFacts", ReadinessScripts.SystemFacts([80, 443], [443]) },
            { "ComputerDn", ReadinessScripts.ComputerDn() },
            { "FirewallFacts", ReadinessScripts.FirewallFacts([80, 443, 81]) },
            { "CreateFirewallRule", ReadinessScripts.CreateFirewallRule(rules[0]) },
            { "CreateFirewallRuleCommand", ReadinessScripts.CreateFirewallRuleCommand(rules[2]) },
            { "SetNetworkPrivate", ReadinessScripts.SetNetworkPrivate(6) },
            { "RemoveFirewallGroup", ReadinessScripts.RemoveFirewallGroup() },
            { "Wrap", PowerShellRunner.Wrap("[ordered]@{ a = 1 }") },
            { "GpoScript", GpoScriptBuilder.Build(new GpoScriptInput
                {
                    Domain = "corp.example.com", ComputerName = "WEB01", ComputerDn = "CN=WEB01,OU=Servers,DC=corp,DC=example,DC=com",
                    Rules = rules, InternalCaUsed = true, ProductVersion = "1.0.0",
                }) },
            { "GpoScriptNoDomain", GpoScriptBuilder.Build(new GpoScriptInput { ComputerName = "WEB01", Rules = rules }) },
        };
        return data;
    }

    [Theory]
    [MemberData(nameof(Scripts))]
    public async Task GeneratedScriptsParse(string name, string script)
    {
        var ps = Runner();
        var file = Path.Combine(Path.GetTempPath(), $"cpm-{name}-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(file, script, TestContext.Current.CancellationToken);
        try
        {
            var result = await ps.RunJsonAsync($$"""
                $tokens = $null; $errors = $null
                [void][System.Management.Automation.Language.Parser]::ParseFile({{PowerShellRunner.Quote(file)}}, [ref]$tokens, [ref]$errors)
                [ordered]@{ errors = @($errors | ForEach-Object { "$($_.Extent.StartLineNumber): $($_.Message)" }) }
                """, ct: TestContext.Current.CancellationToken);
            var errors = result.GetProperty("errors").EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.True(errors.Count == 0, $"{name} has syntax errors:\n{string.Join("\n", errors)}");
        }
        finally
        {
            File.Delete(file);
        }
    }
}

public class OutboundProxyTests
{
    [Fact]
    public void RedactsAndRestoresProxyPasswords()
    {
        Assert.Equal("http://proxy:8080", OutboundHttp.RedactProxy("http://proxy:8080"));
        var redacted = OutboundHttp.RedactProxy("http://svc-user:S3cr%21t@proxy.corp:8080");
        Assert.Equal($"http://svc-user:{OutboundHttp.RedactedPassword}@proxy.corp:8080", redacted);
        Assert.Equal("http://svc-user:S3cr!t@proxy.corp:8080",
            PlatformEndpoints.RestoreProxyPassword(redacted, "http://svc-user:S3cr%21t@proxy.corp:8080").Replace("%21", "!"));
        Assert.Equal("http://other:pw@proxy:1", PlatformEndpoints.RestoreProxyPassword("http://other:pw@proxy:1", "http://a:b@proxy:1"));
    }

    [Fact]
    public void BuildsProxyWithCredentials()
    {
        var proxy = OutboundHttp.CreateProxy("http://user:p%40ss@proxy.corp:3128");
        Assert.Equal(new Uri("http://proxy.corp:3128/"), proxy.Address);
        var cred = proxy.Credentials!.GetCredential(new Uri("http://proxy.corp:3128"), "Basic");
        Assert.Equal("user", cred!.UserName);
        Assert.Equal("p@ss", cred.Password);
        Assert.Throws<ArgumentException>(() => OutboundHttp.CreateProxy("proxy.corp:3128"));
        Assert.Throws<ArgumentException>(() => OutboundHttp.CreateProxy("socks9://x"));
    }
}
