using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json.Nodes;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using CaddyManager.Platform.Infrastructure;
using CaddyManager.Platform.Readiness;
using CaddyManager.Platform.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace CaddyManager.Platform.Tests;

/// <summary>
/// Windows behaviour the documentation leaves open, proven against the real system on the elevated windows-latest CI
/// runner: Service Control Manager (throw-away services), sc.exe, the WinHTTP proxy, Windows Firewall (rules in a unique
/// group), the pending-reboot keys, the UI certificate's key file and PowerShell facts. Every test is idempotent, restores
/// what it changed in finally, and writes a JSON artifact (CPM_E2E_ARTIFACTS). Skipped elsewhere.
/// </summary>
[Trait("Category", "WindowsE2E")]
public class WindowsPlatformE2ETests
{
    private static void RequireElevatedWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        using var id = WindowsIdentity.GetCurrent();
        Assert.SkipUnless(new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator), "Needs an elevated process (the Windows CI runner is).");
    }

    private static string NewName() => "CpmE2E" + Guid.NewGuid().ToString("N")[..8];

    private static PowerShellRunner PowerShell() => new(NullLogger<PowerShellRunner>.Instance);

    private static async Task<string> Ps(string command, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync(PowerShellRunner.Executable, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command],
            new ProcessOptions { Timeout = TimeSpan.FromMinutes(2) }, ct);
        Assert.True(r.ExitCode == 0, $"PowerShell failed ({r.ExitCode}): {command}\n{r.Combined}");
        return r.StdOut.Trim();
    }

    private static async Task<List<int>> WaitForStartsAsync(string markers, int count, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var pids = Directory.Exists(markers)
                ? Directory.GetFiles(markers, "start-*.txt").Select(f => int.Parse(Path.GetFileNameWithoutExtension(f)["start-".Length..])).ToList()
                : [];
            if (pids.Count >= count || DateTime.UtcNow > deadline) return pids;
            await Task.Delay(500);
        }
    }

    /// <summary>
    /// sc.exe exit codes, structured SCM queries vs sc.exe output, the failure-actions flag and delayed start.
    ///
    /// Ways this can fail:
    ///  1. sc.exe does not exit with the Win32 error (1073 create-existing, 1060 delete-missing), so CreateOrRepairAsync's
    ///     "exists → config" fallback throws instead of repairing (not documented for sc.exe).
    ///  2. ServiceNative (QueryServiceStatusEx / QueryServiceConfig2) marshals the structs wrongly: wrong state, delays,
    ///     reset period or flag compared with what sc.exe prints on this English runner.
    ///  3. `sc failureflag 1` does not persist, so SERVICE_STOPPED-with-error never triggers recovery.
    ///  4. `sc config start= auto` leaves an existing "Automatic (Delayed Start)" flag, so every repair reports a change.
    ///  5. GetStatus throws for a missing service instead of returning null.
    /// </summary>
    [Fact]
    public async Task ScmQueriesExitCodesAndDelayedStart()
    {
        RequireElevatedWindows();
        var ct = TestContext.Current.CancellationToken;
        var name = NewName();
        var report = E2EArtifacts.Report(nameof(ScmQueriesExitCodesAndDelayedStart));
        var def = new ServiceDefinition
        {
            Name = name, DisplayName = "CPM E2E service (safe to delete)", Description = "WindowsPlatformE2ETests; deleted at the end.",
            BinaryPathName = "\"C:\\Program Files\\CPM E2E\\missing.exe\" run", StartType = "delayed-auto", RestartDelaysMs = [5000, 10000, 30000],
        };
        try
        {
            await WindowsServiceManager.CreateOrRepairAsync(def, ct);

            // (1) sc.exe exit codes.
            var create = await WindowsServiceManager.ScAsync(["create", name, "binPath=", "x"], ct);
            var deleteMissing = await WindowsServiceManager.ScAsync(["delete", "NoSuch" + Guid.NewGuid().ToString("N")], ct);
            report["scCreateExistingExit"] = create.ExitCode;
            report["scDeleteMissingExit"] = deleteMissing.ExitCode;
            Assert.Equal(1073, create.ExitCode);
            Assert.Equal(1060, deleteMissing.ExitCode);

            // (2)(3) Structured queries agree with sc.exe (English on the runner).
            var q = await WindowsServiceManager.QueryExAsync(name, ct);
            var scQ = ScOutputParser.ParseQueryEx((await WindowsServiceManager.ScAsync(["queryex", name], ct)).StdOut);
            var f = await WindowsServiceManager.QueryFailureAsync(name, ct);
            var scFOut = (await WindowsServiceManager.ScAsync(["qfailure", name], ct)).StdOut;
            var scF = ScOutputParser.ParseQFailure(scFOut);
            var flag = WindowsServiceManager.QueryFailureActionsFlag(name);
            var scFlag = (await WindowsServiceManager.ScAsync(["qfailureflag", name], ct)).StdOut;
            report["native"] = new JsonObject
            {
                ["state"] = q?.State, ["stateName"] = q?.StateName, ["win32ExitCode"] = q?.Win32ExitCode,
                ["resetSeconds"] = f?.ResetPeriodSeconds, ["actions"] = string.Join(",", f?.Actions.Select(a => $"{a.Item1}/{a.Item2}") ?? []),
                ["failureFlag"] = flag,
            };
            report["scExe"] = new JsonObject { ["queryex"] = scQ?.StateName, ["qfailure"] = scFOut.Trim(), ["qfailureflag"] = scFlag.Trim() };
            Assert.NotNull(q);
            Assert.NotNull(scQ);
            Assert.Equal(scQ.State, q.State);
            Assert.Equal(scQ.StateName, q.StateName);
            Assert.Equal(scQ.Win32ExitCode, q.Win32ExitCode);
            Assert.Equal(1077, q.Win32ExitCode); // ERROR_SERVICE_NEVER_STARTED
            Assert.NotNull(f);
            Assert.Equal(scF!.ResetPeriodSeconds, f.ResetPeriodSeconds);
            Assert.Equal(scF.Actions, f.Actions);
            Assert.Equal([("RESTART", 5000), ("RESTART", 10000), ("RESTART", 30000)], f.Actions);
            Assert.True(flag);
            Assert.Contains("TRUE", scFlag, StringComparison.OrdinalIgnoreCase);

            // (4) Delayed start → plain auto. Record what sc.exe alone does, then assert the repair converges.
            Assert.True(WindowsServiceManager.ReadRegistry(name)!.DelayedAutoStart);
            Assert.Equal(0, (await WindowsServiceManager.ScAsync(["config", name, "start=", "auto"], ct)).ExitCode);
            report["scConfigAutoClearsDelayed"] = !WindowsServiceManager.ReadRegistry(name)!.DelayedAutoStart;
            await WindowsServiceManager.ScAsync(["config", name, "start=", "delayed-auto"], ct);
            var auto = def with { StartType = "auto" };
            var repaired = await WindowsServiceManager.CreateOrRepairAsync(auto, ct);
            report["repairToAuto"] = string.Join(" | ", repaired);
            Assert.Contains(repaired, c => c.StartsWith("Repaired", StringComparison.Ordinal));
            Assert.False(WindowsServiceManager.ReadRegistry(name)!.DelayedAutoStart);
            Assert.Empty(await WindowsServiceManager.CreateOrRepairAsync(auto, ct));
        }
        finally
        {
            await WindowsServiceManager.DeleteAsync(name, CancellationToken.None);
            E2EArtifacts.Write("windows-scm-queries.json", report);
        }
        // (5)
        Assert.Null(WindowsServiceManager.GetStatus(name));
        Assert.Null(await WindowsServiceManager.QueryExAsync(name, ct));
        Assert.Null(WindowsServiceManager.QueryFailureActionsFlag(name));
    }

    /// <summary>
    /// The REG_MULTI_SZ "Environment" value of a service (how Caddy gets XDG_* and proxy variables).
    ///
    /// Ways this can fail (not documented on learn.microsoft.com):
    ///  1. The SCM ignores the value: Caddy writes to the SYSTEM profile's AppData instead of the data directory.
    ///  2. The entries replace the whole environment: SystemRoot / Path are missing and Caddy (or Go's runtime) fails.
    ///  3. Values with spaces or '=' inside the value are cut.
    /// </summary>
    [Fact]
    public async Task ServiceEnvironmentIsAddedToTheSystemEnvironment()
    {
        RequireElevatedWindows();
        var ct = TestContext.Current.CancellationToken;
        var name = NewName();
        var dir = Path.Combine(ServiceProbe.WorkRoot, name);
        Directory.CreateDirectory(dir);
        var outFile = Path.Combine(dir, "env.txt");
        var report = E2EArtifacts.Report(nameof(ServiceEnvironmentIsAddedToTheSystemEnvironment));
        try
        {
            var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            await WindowsServiceManager.CreateOrRepairAsync(new ServiceDefinition
            {
                Name = name, DisplayName = "CPM E2E env (safe to delete)", Description = "WindowsPlatformE2ETests",
                BinaryPathName = $"\"{cmd}\" /c set > \"{outFile}\"", StartType = "demand", RestartDelaysMs = [60000, 60000, 60000],
                Environment = ["CPM_MARK=1", @"XDG_DATA_HOME=C:\ProgramData\CPM E2E\caddy\data", "CPM_EQ=a=b c"],
            }, ct);
            // cmd.exe is not a service: the SCM reports a start failure (1053) after the command already ran.
            var start = await WindowsServiceManager.ScAsync(["start", name], ct);
            report["scStartExit"] = start.ExitCode;
            var deadline = DateTime.UtcNow.AddSeconds(40);
            while (!File.Exists(outFile) && DateTime.UtcNow < deadline) await Task.Delay(500, ct);
            await Task.Delay(500, ct);
            var env = File.Exists(outFile) ? await File.ReadAllTextAsync(outFile, ct) : "";
            var vars = env.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Contains('='))
                .GroupBy(l => l[..l.IndexOf('=')], StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First()[(g.Key.Length + 1)..], StringComparer.OrdinalIgnoreCase);
            report["variables"] = new JsonObject(vars.Where(v => v.Key.StartsWith("CPM_", StringComparison.Ordinal) || v.Key is "XDG_DATA_HOME" or "SystemRoot" or "Path" or "USERPROFILE")
                .Select(v => KeyValuePair.Create(v.Key, (JsonNode?)v.Value)));
            Assert.Equal("1", vars.GetValueOrDefault("CPM_MARK"));
            Assert.Equal(@"C:\ProgramData\CPM E2E\caddy\data", vars.GetValueOrDefault("XDG_DATA_HOME"));
            Assert.Equal("a=b c", vars.GetValueOrDefault("CPM_EQ"));
            Assert.False(string.IsNullOrEmpty(vars.GetValueOrDefault("SystemRoot")), "SystemRoot missing: the Environment value replaced the block.");
            Assert.False(string.IsNullOrEmpty(vars.GetValueOrDefault("Path")), "Path missing: the Environment value replaced the block.");
        }
        finally
        {
            await WindowsServiceManager.DeleteAsync(name, CancellationToken.None);
            E2EArtifacts.Write("windows-service-environment.json", report);
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// How the SCM recovers a .NET service host (the model PlatformEndpoints' restart relies on).
    ///
    /// Ways this can fail:
    ///  1. Environment.Exit(1) inside UseWindowsService makes the lifetime report SERVICE_STOPPED (exit code 0), so the SCM
    ///     treats it as a clean stop and never restarts the manager ("Restart" in the UI leaves it stopped).
    ///  2. The restart only happens when the failure-actions flag is set (then a machine that never had the flag, or where
    ///     the flag only applies after a reboot, would not restart).
    ///  3. A graceful stop with ServiceBase.ExitCode = 1 is restarted even without the flag (the documented model is wrong).
    ///  4. With the flag set, a SERVICE_STOPPED-with-error is only recovered after a reboot ("takes effect the next time the
    ///     system is started"), which matters for services that exit cleanly with an error (recorded, not asserted).
    ///  5. The framework-dependent host cannot start as LocalSystem (no DOTNET_ROOT), so nothing is proven.
    /// </summary>
    [Fact]
    public async Task ServiceRecoveryAfterExitAndAfterStopWithError()
    {
        RequireElevatedWindows();
        var ct = TestContext.Current.CancellationToken;
        var probe = await ServiceProbe.BuildAsync(ct);
        var report = E2EArtifacts.Report(nameof(ServiceRecoveryAfterExitAndAfterStopWithError));
        report["probe"] = probe;

        async Task<List<int>> RunScenario(string mode, bool flag, int wantStarts, TimeSpan window)
        {
            var name = NewName();
            var markers = Path.Combine(ServiceProbe.WorkRoot, name);
            try
            {
                await WindowsServiceManager.CreateOrRepairAsync(new ServiceDefinition
                {
                    Name = name, DisplayName = $"CPM E2E probe {mode} (safe to delete)", Description = "WindowsPlatformE2ETests",
                    BinaryPathName = $"\"{probe}\" service {mode} \"{markers}\" {name}", StartType = "demand",
                    RestartDelaysMs = [1000, 1000, 1000], Environment = [$"DOTNET_ROOT={ServiceProbe.DotnetRoot}"],
                }, ct);
                if (!flag) Assert.Equal(0, (await WindowsServiceManager.ScAsync(["failureflag", name, "0"], ct)).ExitCode);
                Assert.Equal(flag, WindowsServiceManager.QueryFailureActionsFlag(name));
                await WindowsServiceManager.StartAsync(name, TimeSpan.FromSeconds(30), ct);
                var starts = await WaitForStartsAsync(markers, wantStarts, window);
                var last = await WindowsServiceManager.QueryExAsync(name, ct);
                report[$"{mode}-flag{(flag ? 1 : 0)}"] = new JsonObject
                {
                    ["starts"] = starts.Count, ["pids"] = string.Join(",", starts),
                    ["lastState"] = last?.StateName, ["lastWin32ExitCode"] = last?.Win32ExitCode,
                };
                return starts;
            }
            finally
            {
                await WindowsServiceManager.DeleteAsync(name, CancellationToken.None);
                try { Directory.Delete(markers, true); } catch { /* best effort */ }
            }
        }

        try
        {
            // (1)(2)(5) The manager's restart path: Environment.Exit(1), flag cleared → still restarted.
            var exit = await RunScenario("exit", flag: false, wantStarts: 2, TimeSpan.FromSeconds(30));
            Assert.True(exit.Count >= 2, $"Environment.Exit(1) was not recovered by the SCM (starts: {exit.Count}).");
            Assert.Equal(exit.Count, exit.Distinct().Count());

            // (3) Graceful SERVICE_STOPPED with exit code 1 and no flag → no recovery.
            var noFlag = await RunScenario("stop-error", flag: false, wantStarts: 2, TimeSpan.FromSeconds(15));
            Assert.Single(noFlag);

            // (4) Same with the flag, right after setting it (no reboot): recorded.
            var withFlag = await RunScenario("stop-error", flag: true, wantStarts: 2, TimeSpan.FromSeconds(20));
            report["failureFlagEffectiveWithoutReboot"] = withFlag.Count >= 2;
        }
        finally
        {
            E2EArtifacts.Write("windows-service-recovery.json", report);
        }
    }

    /// <summary>
    /// The UI certificate is loaded with MachineKeySet (SChannel cannot use ephemeral keys), which writes a key file that
    /// .NET deletes only when the certificate is disposed; Kestrel keeps it for the process lifetime and the restart path
    /// ends with Environment.Exit.
    ///
    /// Ways this can fail:
    ///  1. Every start (and every Restart from the UI) leaves one orphaned file under %ProgramData%\Microsoft\Crypto.
    ///  2. The ProcessExit handler in Program.cs does not run on Environment.Exit, so the file still stays.
    ///  3. Disposing on ProcessExit deletes a key that another certificate object still uses (not the case: one load per run).
    /// </summary>
    [Fact]
    public async Task UiCertificateKeyFileIsRemovedOnExit()
    {
        RequireElevatedWindows();
        var ct = TestContext.Current.CancellationToken;
        var probe = await ServiceProbe.BuildAsync(ct);
        var dir = Path.Combine(ServiceProbe.WorkRoot, NewName());
        Directory.CreateDirectory(dir);
        var pfx = Path.Combine(dir, "ui.pfx");
        using (var key = RSA.Create(2048))
        {
            var req = new CertificateRequest("CN=cpm-e2e", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
            await File.WriteAllBytesAsync(pfx, cert.Export(X509ContentType.Pfx), ct);
        }
        var report = E2EArtifacts.Report(nameof(UiCertificateKeyFileIsRemovedOnExit));
        var crypto = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Crypto");

        async Task<(string Unique, bool Left)> Run(bool disposeOnExit)
        {
            var outFile = Path.Combine(dir, $"key-{disposeOnExit}.txt");
            var r = await ProcessRunner.RunAsync(probe, ["pfx", pfx, disposeOnExit ? "1" : "0", outFile],
                new ProcessOptions { Timeout = TimeSpan.FromSeconds(60), Environment = new Dictionary<string, string> { ["DOTNET_ROOT"] = ServiceProbe.DotnetRoot } }, ct);
            Assert.Equal(1, r.ExitCode);
            var unique = (await File.ReadAllTextAsync(outFile, ct)).Trim();
            var left = Directory.EnumerateFiles(crypto, unique, SearchOption.AllDirectories).ToList();
            foreach (var f in left) File.Delete(f); // clean up the orphan the old behaviour leaves
            return (unique, left.Count > 0);
        }

        try
        {
            var without = await Run(disposeOnExit: false);
            var with = await Run(disposeOnExit: true);
            report["withoutDispose"] = new JsonObject { ["keyFile"] = without.Unique, ["leftBehind"] = without.Left };
            report["disposeOnProcessExit"] = new JsonObject { ["keyFile"] = with.Unique, ["leftBehind"] = with.Left };
            Assert.False(with.Left, $"Key file {with.Unique} stayed although the certificate was disposed on ProcessExit.");
        }
        finally
        {
            E2EArtifacts.Write("windows-ui-certificate-keyfile.json", report);
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// The machine-wide WinHTTP proxy (used by Windows for CRL/OCSP downloads while validating SMTP/LDAPS certificates).
    ///
    /// Ways this can fail:
    ///  1. The P/Invoke struct is wrong (crash, garbage, or the proxy string is not freed/decoded).
    ///  2. "Direct access" is reported as a proxy (or the reverse) — the old netsh text parsing failed on non-English Windows.
    ///  3. The readiness check still claims nothing uses the WinHTTP proxy.
    ///  4. The test leaves the runner with a proxy set (restored in finally, also when it was set before).
    /// </summary>
    [Fact]
    public async Task WinHttpProxyIsReadStructurally()
    {
        RequireElevatedWindows();
        var ct = TestContext.Current.CancellationToken;
        var before = WinHttpProxy.Read();
        var report = E2EArtifacts.Report(nameof(WinHttpProxyIsReadStructurally));
        report["before"] = before.IsDirect ? "direct" : $"{before.Proxy} bypass {before.Bypass}";
        try
        {
            Assert.Equal(0, (await ProcessRunner.RunAsync("netsh.exe", ["winhttp", "set", "proxy", "proxy-server=127.0.0.1:9", "bypass-list=<local>;*.cpm-e2e.invalid"], ct: ct)).ExitCode);
            var set = WinHttpProxy.Read();
            var netsh = (await ProcessRunner.RunAsync("netsh.exe", ["winhttp", "show", "proxy"], ct: ct)).StdOut.Trim();
            var check = ReadinessService.WinHttpProxyCheck((set, null), netsh, new BinarySettings());
            report["set"] = new JsonObject { ["proxy"] = set.Proxy, ["bypass"] = set.Bypass, ["check"] = check.Status.ToString(), ["summary"] = check.Summary };
            Assert.False(set.IsDirect);
            Assert.Equal("127.0.0.1:9", set.Proxy);
            Assert.Contains("*.cpm-e2e.invalid", set.Bypass);
            Assert.Equal(CheckStatus.Info, check.Status);
            Assert.Contains("revocation", check.Summary);

            Assert.Equal(0, (await ProcessRunner.RunAsync("netsh.exe", ["winhttp", "reset", "proxy"], ct: ct)).ExitCode);
            var reset = WinHttpProxy.Read();
            var direct = ReadinessService.WinHttpProxyCheck((reset, null), null, new BinarySettings());
            report["reset"] = new JsonObject { ["direct"] = reset.IsDirect, ["check"] = direct.Status.ToString() };
            Assert.True(reset.IsDirect);
            Assert.Equal(CheckStatus.Pass, direct.Status);
            var withOutbound = ReadinessService.WinHttpProxyCheck((reset, null), null, new BinarySettings { OutboundProxy = "http://proxy.corp.invalid:8080" });
            Assert.Equal(CheckStatus.Info, withOutbound.Status);
        }
        finally
        {
            if (before.IsDirect) await ProcessRunner.RunAsync("netsh.exe", ["winhttp", "reset", "proxy"], ct: CancellationToken.None);
            else
                await ProcessRunner.RunAsync("netsh.exe", ["winhttp", "set", "proxy", $"proxy-server={before.Proxy}",
                    .. before.Bypass is null ? Array.Empty<string>() : [$"bypass-list={before.Bypass}"]], ct: CancellationToken.None);
            E2EArtifacts.Write("windows-winhttp-proxy.json", report);
        }
    }

    /// <summary>
    /// FirewallFacts with the real NetSecurity cmdlets (no mocks) and the AllowInboundRules profile setting.
    ///
    /// Ways this can fail:
    ///  1. Port filters and rules do not share InstanceID, so the join drops real rules (the unit tests mock the cmdlets).
    ///  2. LocalPort ranges / program / remote addresses come back in another shape (5.1 ConvertTo-Json quirks).
    ///  3. The bulk path (> 40 candidate rules) joins differently from the per-rule path.
    ///  4. With AllowInboundRules = False and DefaultInboundAction = Allow, a Block rule is still counted (the documentation
    ///     says all rules are ignored), so readiness reports a false failure.
    ///  5. The profile or the rules are not restored and the runner's firewall is left changed.
    /// </summary>
    [Fact]
    public async Task FirewallFactsMatchTheRealRulesAndProfiles()
    {
        RequireElevatedWindows();
        var ct = TestContext.Current.CancellationToken;
        var group = "CPM-E2E-" + Guid.NewGuid().ToString("N")[..8];
        var report = E2EArtifacts.Report(nameof(FirewallFactsMatchTheRealRulesAndProfiles));
        var ps = PowerShell();
        var profileName = "Public";
        string? savedProfile = null;
        try
        {
            await Ps($"New-NetFirewallRule -DisplayName '{group} allow' -Group '{group}' -Direction Inbound -Protocol TCP -LocalPort 45678 " +
                     "-Program 'C:\\cpm-e2e\\caddy.exe' -RemoteAddress 10.0.0.0/8 -Action Allow -Profile Any | Out-Null; " +
                     $"New-NetFirewallRule -DisplayName '{group} block' -Group '{group}' -Direction Inbound -Protocol TCP -LocalPort 45000-46000 " +
                     "-Action Block -Profile Any | Out-Null", ct);

            // (1)(2) per-rule path
            var facts = WindowsFactsParser.ParseFirewall(await ps.RunJsonAsync(ReadinessScripts.FirewallFacts([45678]), TimeSpan.FromMinutes(3), ct));
            var mine = facts.Rules.Where(r => r.Group == group).ToList();
            report["perRule"] = new JsonArray(mine.Select(r => (JsonNode)new JsonObject
            {
                ["displayName"] = r.DisplayName, ["action"] = r.Action, ["localPorts"] = string.Join(",", r.LocalPorts),
                ["program"] = r.Program, ["remote"] = string.Join(",", r.RemoteAddresses), ["source"] = r.Source,
            }).ToArray());
            var allow = Assert.Single(mine, r => r.Action == "Allow");
            Assert.Equal(["45678"], allow.LocalPorts);
            Assert.Equal(@"C:\cpm-e2e\caddy.exe", allow.Program, ignoreCase: true);
            Assert.Contains(allow.RemoteAddresses, a => a.StartsWith("10.0.0.0", StringComparison.Ordinal));
            Assert.Equal("Local", allow.Source);
            var block = Assert.Single(mine, r => r.Action == "Block");
            Assert.Equal(["45000-46000"], block.LocalPorts);

            // (3) bulk path: > 40 candidate rules in the group.
            await Ps($"1..41 | ForEach-Object {{ New-NetFirewallRule -DisplayName \"{group} bulk $_\" -Group '{group}' -Direction Inbound -Protocol TCP -LocalPort 45678 -Action Allow -Profile Any | Out-Null }}", ct);
            var bulk = WindowsFactsParser.ParseFirewall(await ps.RunJsonAsync(ReadinessScripts.FirewallFacts([45678]), TimeSpan.FromMinutes(3), ct));
            var bulkMine = bulk.Rules.Where(r => r.Group == group).ToList();
            report["bulkRuleCount"] = bulkMine.Count;
            Assert.Equal(43, bulkMine.Count);
            var bulkAllow = Assert.Single(bulkMine, r => r.DisplayName == $"{group} allow");
            Assert.Equal(@"C:\cpm-e2e\caddy.exe", bulkAllow.Program, ignoreCase: true);
            Assert.Contains(bulkAllow.RemoteAddresses, a => a.StartsWith("10.0.0.0", StringComparison.Ordinal));

            var req = new RequiredFirewallRule { CheckId = "firewall.e2e", DisplayName = "e2e", Protocol = "TCP", Port = 45678, Purpose = "E2E" };
            var normal = FirewallEvaluator.Evaluate(req, bulk, [profileName]);
            Assert.Equal(CheckStatus.Fail, normal.Status); // the block range wins
            Assert.Contains("BLOCKED", normal.Details);

            // (4) AllowInboundRules = False + DefaultInboundAction = Allow on the Public profile: rules are ignored.
            savedProfile = await Ps($"$p = Get-NetFirewallProfile -Name {profileName} -PolicyStore PersistentStore; \"$($p.AllowInboundRules)|$($p.DefaultInboundAction)\"", ct);
            report["savedProfile"] = savedProfile;
            await Ps($"Set-NetFirewallProfile -Name {profileName} -PolicyStore PersistentStore -AllowInboundRules False -DefaultInboundAction Allow", ct);
            var shieldsOff = WindowsFactsParser.ParseFirewall(await ps.RunJsonAsync(ReadinessScripts.FirewallFacts([45678]), TimeSpan.FromMinutes(3), ct));
            var pub = shieldsOff.Profiles.Single(p => p.Name == profileName);
            var allowAll = FirewallEvaluator.Evaluate(req, shieldsOff, [profileName]);
            report["allowInboundRulesFalse_Allow"] = new JsonObject { ["profile"] = $"{pub.AllowInboundRules}|{pub.DefaultInboundAction}", ["status"] = allowAll.Status.ToString(), ["details"] = allowAll.Details };
            Assert.Equal("False", pub.AllowInboundRules);
            Assert.Equal(CheckStatus.Pass, allowAll.Status);

            await Ps($"Set-NetFirewallProfile -Name {profileName} -PolicyStore PersistentStore -DefaultInboundAction Block", ct);
            var shieldsUp = WindowsFactsParser.ParseFirewall(await ps.RunJsonAsync(ReadinessScripts.FirewallFacts([45678]), TimeSpan.FromMinutes(3), ct));
            var denied = FirewallEvaluator.Evaluate(req, shieldsUp, [profileName]);
            report["allowInboundRulesFalse_Block"] = new JsonObject { ["status"] = denied.Status.ToString(), ["remediation"] = denied.Remediation };
            Assert.Equal(CheckStatus.Fail, denied.Status);
            Assert.Contains("AllowInboundRules True", denied.Remediation);
        }
        finally
        {
            if (savedProfile?.Split('|') is [var air, var dia])
                await Ps($"Set-NetFirewallProfile -Name {profileName} -PolicyStore PersistentStore -AllowInboundRules {air} -DefaultInboundAction {dia}", CancellationToken.None);
            await Ps($"Get-NetFirewallRule -Group '{group}' -ErrorAction SilentlyContinue | Remove-NetFirewallRule", CancellationToken.None);
            E2EArtifacts.Write("windows-firewall-facts.json", report);
        }
    }

    /// <summary>
    /// SystemFacts with real cmdlets: a TCP listener owned by this process is reported with its PID and executable.
    ///
    /// Ways this can fail: OwningProcess is not the PID; Get-Process path is empty; the port filter drops the listener.
    /// </summary>
    [Fact]
    public async Task SystemFactsReportListenersWithTheirProcess()
    {
        RequireElevatedWindows();
        var ct = TestContext.Current.CancellationToken;
        var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var sys = WindowsFactsParser.ParseSystem(await PowerShell().RunJsonAsync(ReadinessScripts.SystemFacts([port], []), TimeSpan.FromMinutes(2), ct));
            var mine = sys.Listeners.Where(l => l.Port == port).ToList();
            var report = E2EArtifacts.Report(nameof(SystemFactsReportListenersWithTheirProcess));
            report["port"] = port;
            report["listeners"] = string.Join("; ", mine.Select(l => $"{l.Protocol} {l.Address}:{l.Port} pid {l.ProcessId} {l.ProcessPath}"));
            report["winHttpProxyText"] = sys.WinHttpProxy;
            E2EArtifacts.Write("windows-system-facts.json", report);
            var l = Assert.Single(mine);
            Assert.Equal(Environment.ProcessId, l.ProcessId);
            Assert.Equal(Environment.ProcessPath, l.ProcessPath, ignoreCase: true);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// Pending reboot keys (only PendingFileRenameOperations is documented; the Windows Update key is a convention).
    ///
    /// Ways this can fail: the key path is wrong so the check never fires; the test leaves the key behind (only created
    /// when absent and removed in finally).
    /// </summary>
    [Fact]
    public void PendingRebootIsDetectedFromTheWindowsUpdateKey()
    {
        RequireElevatedWindows();
        const string key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired";
        var report = E2EArtifacts.Report(nameof(PendingRebootIsDetectedFromTheWindowsUpdateKey));
        var existed = Registry.LocalMachine.OpenSubKey(key) is { } k && Close(k);
        report["keyExistedBefore"] = existed;
        try
        {
            if (!existed) Registry.LocalMachine.CreateSubKey(key).Dispose();
            var check = ReadinessService.PendingRebootCheck();
            report["summary"] = check.Summary;
            Assert.Equal(CheckStatus.Info, check.Status);
            Assert.Contains("Windows Update", check.Summary);
        }
        finally
        {
            if (!existed) Registry.LocalMachine.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
            E2EArtifacts.Write("windows-pending-reboot.json", report);
        }

        static bool Close(RegistryKey k) { k.Dispose(); return true; }
    }
}
