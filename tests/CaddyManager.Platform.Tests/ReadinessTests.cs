using System.Text.Json;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using CaddyManager.Platform.Readiness;

namespace CaddyManager.Platform.Tests;

public class WindowsFactsTests
{
    private static SystemFacts System() => WindowsFactsParser.ParseSystem(JsonDocument.Parse(Fixture.Read("ps-system-facts.json")).RootElement);
    private static FirewallFacts Firewall() => WindowsFactsParser.ParseFirewall(JsonDocument.Parse(Fixture.Read("ps-firewall-facts.json")).RootElement);

    private static List<RequiredFirewallRule> Rules() =>
        ReadinessService.RequiredRules(new AppPaths(Path.GetTempPath()), new CaddySettings { EnableHttp3 = true }, new UiSettings(), 81);

    [Fact]
    public void ParsesSystemFacts()
    {
        var s = System();
        Assert.Equal(2, s.Profiles.Count);
        Assert.True(s.Domain.PartOfDomain);
        Assert.Equal("corp.example.com", s.Domain.Domain);
        Assert.Equal("CN=WEB01,OU=Web Servers,OU=Servers,DC=corp,DC=example,DC=com", s.Domain.ComputerDn);
        Assert.Null(s.Domain.DnError);
        Assert.Equal(3, s.Listeners.Count);
        Assert.True(s.Listeners[0].IsHttpSys);
        Assert.Null(s.Listeners[0].ProcessPath);
        Assert.Equal("Running", s.W3svc);
        Assert.Contains("Direct access", s.WinHttpProxy);
        Assert.Contains("HTTPS://+:443/ADFS/", s.HttpSysUrls);
    }

    [Fact]
    public void ParsesFirewallFactsIncludingScalarArrays()
    {
        var f = Firewall();
        Assert.Equal("Running", f.Service);
        Assert.Equal(3, f.Profiles.Count);
        Assert.Equal(0, f.GpoLocalMerge["Domain"]);
        Assert.Equal(-1, f.GpoLocalMerge["Public"]);
        var http = Assert.Single(f.Rules, r => r.Name == "CPM-HTTP");
        Assert.Equal(["80"], http.LocalPorts);           // PS 5.1 emitted a scalar string
        Assert.Equal(["Any"], http.RemoteAddresses);
        var iis = Assert.Single(f.Rules, r => r.Name == "IIS-WebServerRole-HTTP-In-TCP");
        Assert.Equal(["LocalSubnet", "10.0.0.0/8"], iis.RemoteAddresses); // PS 5.1 {"value":[...],"Count":n} form
    }

    [Fact]
    public void RequiredRulesUseStableIdsAndNames()
    {
        var rules = Rules();
        Assert.Equal(["firewall.tcp80", "firewall.tcp443", "firewall.udp443", "firewall.tcp81"], rules.Select(r => r.CheckId));
        Assert.Equal("Caddy Proxy Manager - HTTP (TCP-In)", rules[0].DisplayName);
        Assert.Equal("Caddy Proxy Manager - HTTP/3 (UDP-In)", rules[2].DisplayName);
        Assert.Equal("Caddy Proxy Manager - Management UI (TCP-In)", rules[3].DisplayName);

        // HTTP/3 is off by default, so no UDP rule is required unless it is turned on.
        Assert.Equal(["firewall.tcp80", "firewall.tcp443", "firewall.tcp81"],
            ReadinessService.RequiredRules(new AppPaths(Path.GetTempPath()), new CaddySettings(), new UiSettings(), 81).Select(r => r.CheckId));

        var loopbackUi = ReadinessService.RequiredRules(new AppPaths(Path.GetTempPath()),
            new CaddySettings { EnableHttp3 = false }, new UiSettings { BindAddress = "127.0.0.1" }, 81);
        Assert.Equal(["firewall.tcp80", "firewall.tcp443"], loopbackUi.Select(r => r.CheckId));
    }

    [Fact]
    public void ActiveProfilesFollowConnectionCategories()
    {
        Assert.Equal(["Domain", "Public"], FirewallEvaluator.ActiveProfiles(System().Profiles));
        Assert.Equal(["Domain", "Private", "Public"], FirewallEvaluator.ActiveProfiles([]));
    }

    [Fact]
    public void GpoRuleSatisfiesAllProfilesEvenWhenLocalRulesAreIgnored()
    {
        var check = FirewallEvaluator.Evaluate(Rules().Single(r => r.CheckId == "firewall.tcp443"), Firewall(), ["Domain", "Public"]);
        Assert.Equal(CheckStatus.Pass, check.Status);
        Assert.Contains("GPO", check.Details);
        Assert.False(check.Fixable);
    }

    [Fact]
    public void LocalRuleIgnoredOnDomainProfileGivesWarningWithGpoAdvice()
    {
        var check = FirewallEvaluator.Evaluate(Rules().Single(r => r.CheckId == "firewall.tcp80"), Firewall(), ["Domain", "Public"]);
        Assert.Equal(CheckStatus.Warn, check.Status);
        Assert.Contains("local rules are ignored", check.Details);
        Assert.False(check.Fixable); // a local rule would not help
        Assert.Contains("GPO", check.Remediation);
        // The IIS rule is restricted to program "System" (http.sys) and must not count for caddy.exe.
        Assert.DoesNotContain("World Wide Web", check.Details);
    }

    [Fact]
    public void BlockRuleWins()
    {
        var check = FirewallEvaluator.Evaluate(Rules().Single(r => r.CheckId == "firewall.udp443"), Firewall(), ["Domain", "Public"]);
        // HTTP/3 is optional: a blocked UDP 443 is a warning that explains the effect, not a failure.
        Assert.Equal(CheckStatus.Warn, check.Status);
        Assert.Contains("HTTP/3 is optional", check.Summary);
        Assert.Contains("BLOCKED", check.Details);
        Assert.False(check.Fixable);
    }

    [Fact]
    public void MissingRuleIsFixableWhenLocalRulesApply()
    {
        var check = FirewallEvaluator.Evaluate(Rules().Single(r => r.CheckId == "firewall.tcp81"), Firewall(), ["Private"]);
        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.True(check.Fixable);
        Assert.Contains("New-NetFirewallRule -DisplayName 'Caddy Proxy Manager - Management UI (TCP-In)'", check.Script);
        Assert.Contains("-LocalPort 81", check.Script);
    }

    [Fact]
    public void NotConfiguredProfileCountsAsEnabled()
    {
        var f = Firewall() with
        {
            Profiles = [new FirewallProfileFact("Private", "NotConfigured", "NotConfigured", "NotConfigured", "NotConfigured")],
            GpoLocalMerge = new(),
        };
        Assert.True(f.Profiles[0].IsEnabled);
        var check = FirewallEvaluator.Evaluate(Rules().Single(r => r.CheckId == "firewall.tcp81"), f, ["Private"]);
        Assert.Equal(CheckStatus.Fail, check.Status); // enabled, default inbound Block, no rule for 81
        Assert.DoesNotContain("disabled", check.Details);
        Assert.True(check.Fixable);
    }

    [Fact]
    public void BlockAllInboundConnectionsIgnoresAllowRules()
    {
        var f = Firewall() with
        {
            Profiles = [new FirewallProfileFact("Private", "True", "Block", "True", "False")],
            GpoLocalMerge = new(),
        };
        Assert.False(f.Profiles[0].AllowsInboundRules);
        var check = FirewallEvaluator.Evaluate(Rules().Single(r => r.CheckId == "firewall.tcp443"), f, ["Private"]);
        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Contains("blocks all incoming connections", check.Details);
        Assert.False(check.Fixable); // another allow rule would not help
        Assert.Contains("-AllowInboundRules True", check.Remediation);
        Assert.Null(check.Script);
    }

    [Fact]
    public void ParsesComputerDnResult()
    {
        var (dn, err) = WindowsFactsParser.ParseComputerDn(JsonDocument.Parse("{\"computerDn\":\"CN=WEB01,CN=Computers,DC=corp,DC=local\",\"dnError\":null}").RootElement);
        Assert.Equal("CN=WEB01,CN=Computers,DC=corp,DC=local", dn);
        Assert.Null(err);
        (dn, err) = WindowsFactsParser.ParseComputerDn(JsonDocument.Parse("{\"computerDn\":null,\"dnError\":\"The server is not operational.\"}").RootElement);
        Assert.Null(dn);
        Assert.Equal("The server is not operational.", err);
    }

    [Theory]
    [InlineData("Any", 443, true)]
    [InlineData("443", 443, true)]
    [InlineData("400-500", 443, true)]
    [InlineData("80", 443, false)]
    [InlineData("RPC", 443, false)]
    public void PortSpecs(string spec, int port, bool expected) => Assert.Equal(expected, FirewallEvaluator.PortMatches(spec, port));

    [Theory]
    [InlineData("Any", "Public", true)]
    [InlineData("Domain, Private", "Private", true)]
    [InlineData("Domain, Private", "Public", false)]
    [InlineData("", "Domain", true)]
    public void ProfileSpecs(string spec, string profile, bool expected) => Assert.Equal(expected, FirewallEvaluator.ProfileMatches(spec, profile));

    [Theory]
    [InlineData("127.0.0.1:2019", true)]
    [InlineData("localhost:2019", true)]
    [InlineData("[::1]:2019", true)]
    [InlineData("unix//run/caddy-admin.sock", true)]
    [InlineData("", true)]
    [InlineData(":2019", false)]
    [InlineData("0.0.0.0:2019", false)]
    [InlineData("10.0.0.5:2019", false)]
    public void AdminListenLoopback(string listen, bool loopback) =>
        Assert.Equal(loopback, ReadinessService.AdminListenIsLoopback(listen).Loopback);
}

public class GpoScriptTests
{
    private static GpoScriptInput Input(bool internalCa = false, string? dn = "CN=WEB01,OU=Web Servers,OU=Servers,DC=corp,DC=example,DC=com") => new()
    {
        Domain = "corp.example.com",
        ComputerName = "WEB01",
        ComputerDn = dn,
        Rules = ReadinessService.RequiredRules(new AppPaths(Path.GetTempPath()), new CaddySettings { EnableHttp3 = true }, new UiSettings(), 81),
        InternalCaUsed = internalCa,
        InternalRootPath = @"C:\ProgramData\CaddyProxyManager\caddy\data\pki\authorities\local\root.crt",
        ProductVersion = "1.0.0",
    };

    [Theory]
    [InlineData("CN=WEB01,OU=Servers,DC=corp,DC=local", "OU=Servers,DC=corp,DC=local")]
    [InlineData(@"CN=Web\, 01,OU=Servers,DC=corp,DC=local", "OU=Servers,DC=corp,DC=local")]
    [InlineData("DC=local", null)]
    [InlineData(null, null)]
    public void ParentDn(string? dn, string? expected) => Assert.Equal(expected, GpoScriptBuilder.ParentDn(dn));

    [Fact]
    public void ScriptIsIdempotentAndComplete()
    {
        var s = GpoScriptBuilder.Build(Input());
        Assert.Contains("[string] $GpoName = 'Caddy Proxy Manager - Firewall'", s);
        Assert.Contains("[string] $Domain = 'corp.example.com'", s);
        Assert.Contains("[string] $TargetOU = 'OU=Web Servers,OU=Servers,DC=corp,DC=example,DC=com'", s);
        Assert.Contains("try { $gpo = Get-GPO -Name $GpoName -Domain $Domain -ErrorAction Stop } catch { $gpo = $null }", s);
        Assert.Contains("New-GPO -Name $GpoName", s);
        Assert.Contains("$policyStore = \"$Domain\\$GpoName\"", s);
        Assert.Contains("Remove-NetFirewallRule -PolicyStore $policyStore -DisplayName $rule.DisplayName", s);
        Assert.Contains("New-NetFirewallRule -PolicyStore $policyStore", s);
        Assert.Contains("Get-GPInheritance -Target $linkTarget", s);
        Assert.Contains("New-GPLink -Name $GpoName -Domain $Domain -Target $linkTarget", s);
        Assert.Contains("S-1-5-11", s);
        // Security filtering is applied before the GPO is linked.
        Assert.True(s.IndexOf("Set-GPPermission", StringComparison.Ordinal) < s.IndexOf("New-GPLink -Name $GpoName", StringComparison.Ordinal));
        Assert.Contains("#Requires -PSEdition Desktop", s);
        Assert.Contains("Invoke-GPUpdate -Computer $ComputerName", s);
        Assert.Contains("gpupdate /target:computer /force", s);
        foreach (var name in new[] { "HTTP (TCP-In)", "HTTPS (TCP-In)", "HTTP/3 (UDP-In)", "Management UI (TCP-In)" })
            Assert.Contains($"DisplayName = 'Caddy Proxy Manager - {name}'", s);
        Assert.Contains("LocalPort = '443'", s);
        Assert.DoesNotContain("certutil", s);
    }

    [Fact]
    public void InternalCaGuidanceAndMissingOu()
    {
        var s = GpoScriptBuilder.Build(Input(internalCa: true, dn: null));
        Assert.Contains("certutil -dspublish -f root.crt RootCA", s);
        Assert.Contains("[string] $TargetOU = ''", s);
        Assert.Contains("was not linked", s);
    }

    [Fact]
    public void ComputersInTheDefaultContainerAreLinkedAtTheDomainRoot()
    {
        var s = GpoScriptBuilder.Build(Input(dn: "CN=WEB01,CN=Computers,DC=corp,DC=example,DC=com"));
        Assert.Contains("[string] $TargetOU = 'CN=Computers,DC=corp,DC=example,DC=com'", s);
        Assert.Contains("if ($TargetOU -match '^CN=')", s);
        Assert.Contains("$linkTarget = $domainDn", s);
    }

    [Fact]
    public void QuotesValues()
    {
        var s = GpoScriptBuilder.Build(Input() with { GpoName = "O'Brien's GPO" });
        Assert.Contains("[string] $GpoName = 'O''Brien''s GPO'", s);
    }
}

/// <summary>Readiness on a development machine (macOS/Linux): Windows categories are Skipped, portable checks run.</summary>
public class ReadinessNonWindowsTests
{
    [Fact]
    public async Task ProducesReportPersistsItAndBuildsGpoScript()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Non-Windows behaviour.");
        using var env = new TempEnvironment();
        env.Store.Col<SiteHost>().Insert(new SiteHost { Domains = ["cpm-readiness-test.invalid"], Tls = TlsMode.Acme, Upstreams = [new Upstream { Host = "127.0.0.1", Port = 8080 }] });
        env.Store.Col<SiteHost>().Insert(new SiteHost { Domains = ["*.wildcard.invalid"], Tls = TlsMode.Acme });
        using var svc = new PlatformServices(env, DevCaddy.FreeTcpPort(), "{}");
        var readiness = svc.Get<IReadinessService>();

        Assert.Null(readiness.LastReport);
        var report = await readiness.RunAsync(TestContext.Current.CancellationToken);

        Assert.False(report.Machine.IsWindows);
        Assert.Equal(Environment.MachineName, report.Machine.Hostname);
        Assert.Equal(CheckStatus.Skipped, report.Checks.Single(c => c.Id == "firewall.windows").Status);
        Assert.Equal(CheckStatus.Skipped, report.Checks.Single(c => c.Id == "network.profiles").Status);
        Assert.Equal(CheckStatus.Skipped, report.Checks.Single(c => c.Id == "domain.membership").Status);
        Assert.Equal(CheckStatus.Info, report.Checks.Single(c => c.Id == "system.os").Status);
        Assert.Contains(report.Checks, c => c.Id == "system.disk" && c.Status is CheckStatus.Pass or CheckStatus.Warn or CheckStatus.Fail);
        Assert.Contains(report.Checks, c => c.Id == "system.clock");
        Assert.Equal(CheckStatus.Skipped, report.Checks.Single(c => c.Id == "ports").Status);
        Assert.DoesNotContain(report.Checks, c => c.Id.StartsWith("ports.", StringComparison.Ordinal));
        Assert.Contains(report.Checks, c => c.Id == "connectivity.letsencrypt");
        var dns = report.Checks.Single(c => c.Id == "dns.cpm-readiness-test.invalid");
        Assert.Equal(CheckStatus.Fail, dns.Status);
        Assert.DoesNotContain(report.Checks, c => c.Id.StartsWith("dns.*", StringComparison.Ordinal));
        var binary = report.Checks.Single(c => c.Id == "caddy.binary");
        Assert.Equal(CheckStatus.Fail, binary.Status);
        Assert.True(binary.Fixable);
        Assert.Equal(CheckStatus.Info, report.Checks.Single(c => c.Id == "caddy.service").Status);
        Assert.Equal(CheckStatus.Fail, report.Checks.Single(c => c.Id == "caddy.running").Status);
        Assert.Equal(CheckStatus.Skipped, report.Checks.Single(c => c.Id == "caddy.admin").Status);
        Assert.All(report.Checks, c => Assert.Contains(c.Category, new[] { "System", "Firewall", "Network", "Domain", "Ports", "Connectivity", "DNS", "Caddy" }));
        Assert.Equal(report.Checks.Count, report.Checks.Select(c => c.Id).Distinct().Count());

        // Persisted and reloaded by a fresh instance.
        Assert.True(File.Exists(Path.Combine(env.Paths.DataDir, ReadinessService.ReportFileName)));
        using var svc2 = new PlatformServices(env, DevCaddy.FreeTcpPort(), "{}");
        var reloaded = svc2.Get<IReadinessService>().LastReport;
        Assert.NotNull(reloaded);
        Assert.Equal(report.Checks.Count, reloaded.Checks.Count);
        Assert.Equal(report.Fail, reloaded.Fail);

        // Wire format: camelCase enums, computed counters.
        var json = JsonSerializer.Serialize(report, JsonDefaults.Api);
        Assert.Contains("\"status\":\"skipped\"", json);
        Assert.Contains("\"fail\":", json);

        var script = readiness.BuildGpoScript();
        Assert.Contains("New-NetFirewallRule -PolicyStore $policyStore", script);

        await Assert.ThrowsAsync<InvalidOperationException>(() => readiness.FixAsync("firewall.tcp80", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => readiness.FixAsync("system.disk", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => readiness.FixAsync("nonsense", TestContext.Current.CancellationToken));
    }
}

/// <summary>Runs the real Windows checks (PowerShell 5.1, firewall, ports ...) — executed on the Windows CI runner.</summary>
public class ReadinessWindowsTests
{
    [Fact]
    public async Task CollectsWindowsFactsWithWindowsPowerShell()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        using var env = new TempEnvironment();
        var streamPort = DevCaddy.FreeTcpPort();
        env.Store.Col<StreamHost>().Insert(new StreamHost { Protocol = StreamProtocol.Tcp, ListenPort = streamPort, UpstreamHost = "10.0.0.5", UpstreamPort = 3389 });
        using var svc = new PlatformServices(env, DevCaddy.FreeTcpPort(), "{}");
        var report = await svc.Get<IReadinessService>().RunAsync(TestContext.Current.CancellationToken);

        string Dump() => string.Join("\n", report.Checks.Select(c => $"{c.Id} [{c.Status}] {c.Summary} {c.Details}"));
        Assert.True(report.Machine.IsWindows);
        Assert.DoesNotContain(report.Checks, c => c.Id is "firewall.collect" or "network.collect" or "ports.collect");
        Assert.DoesNotContain(report.Checks, c => c.Id == "domain.membership" && c.Status == CheckStatus.Warn);
        Assert.Contains(report.Checks, c => c.Id == "firewall.service");
        Assert.Contains(report.Checks, c => c.Id == "firewall.localrules");
        Assert.Contains(report.Checks, c => c.Id == "firewall.tcp443");
        Assert.Contains(report.Checks, c => c.Id == "ports.tcp80");
        Assert.Contains(report.Checks, c => c.Id == $"ports.stream.tcp{streamPort}" && c.Status == CheckStatus.Pass && c.Title.Contains("stream"));
        Assert.Contains(report.Checks, c => c.Id == "ports.iis");
        Assert.Contains(report.Checks, c => c.Id == "connectivity.proxy");
        Assert.Contains(report.Checks, c => c.Id == "system.reboot");
        Assert.True(report.Checks.Count >= 20, Dump());
        Assert.Contains("New-NetFirewallRule -PolicyStore", svc.Get<IReadinessService>().BuildGpoScript());
    }
}
