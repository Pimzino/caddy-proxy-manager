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

    public static TheoryData<string, string> Scripts()
    {
        var paths = new AppPaths(Path.GetTempPath());
        var rules = ReadinessService.RequiredRules(paths, new CaddySettings(), new UiSettings(), 81);
        var data = new TheoryData<string, string>
        {
            { "SystemFacts", ReadinessScripts.SystemFacts([80, 443], [443]) },
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
