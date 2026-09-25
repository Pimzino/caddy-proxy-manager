using System.Diagnostics;
using System.Text.Json.Nodes;
using CaddyManager.Core;
using CaddyManager.Core.Infrastructure;
using CaddyManager.Ops.Backup;
using CaddyManager.Ops.Events;
using Microsoft.Win32;

namespace CaddyManager.Ops.Tests;

/// <summary>
/// Windows behaviour of the Ops module against the real system (elevated windows-latest CI runner): the Event Log source,
/// the computer account named in UNC backup errors, and DPAPI LocalMachine secrets. Idempotent and self-cleaning; each
/// test writes a JSON artifact to CPM_E2E_ARTIFACTS.
/// </summary>
[Trait("Category", "WindowsE2E")]
[Collection(nameof(WindowsOpsE2ECollection))]
public class WindowsOpsE2ETests
{
    private static string RunPowerShell(string command)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        psi.Environment.Remove("PSModulePath"); // inherited from the PowerShell 7 CI step; 5.1 builds its own (about_PSModulePath)
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(120_000), "PowerShell timed out");
        Assert.True(p.ExitCode == 0, $"PowerShell exit {p.ExitCode}: {stderr.Result}");
        return stdout.Result.Trim();
    }

    /// <summary>
    /// Ways this can fail:
    ///  1. EventMessageFile points at a file that does not exist (System.Diagnostics.EventLog.Messages.dll beside a
    ///     single-file exe), so Event Viewer shows "The description for Event ID ... cannot be found".
    ///  2. .NET 10 registers another file than documented in the code comment (runtime release/10.0 EventLog.GetDllPath).
    ///  3. Long details (up to the 30,000 characters the writer keeps) are rejected (EventLog limit 32766) or cut.
    ///  4. A registration left by an older build with a missing file is not repaired.
    ///  5. The test leaves the source registered on a machine where it did not exist.
    /// </summary>
    [ElevatedWindowsFact]
    public void EventLogSourceIsRegisteredWithAWorkingMessageFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        var report = E2EArtifacts.Report(nameof(EventLogSourceIsRegisteredWithAWorkingMessageFile));
        var key = $@"SYSTEM\CurrentControlSet\Services\EventLog\{EventLogSource.LogName}\{EventLogSource.Name}";
        var existed = EventLog.SourceExists(EventLogSource.Name);
        report["sourceExistedBefore"] = existed;
        try
        {
            if (existed) EventLog.DeleteEventSource(EventLogSource.Name);
            // (2) What .NET 10 itself registers.
            EventLog.CreateEventSource(new EventSourceCreationData(EventLogSource.Name, EventLogSource.LogName));
            string? Registered()
            {
                using var k = Registry.LocalMachine.OpenSubKey(key);
                return k?.GetValue("EventMessageFile", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            }
            report["dotnetRegisters"] = Registered();
            report["dotnetFileExists"] = Registered() is { } r0 && EventLogSource.MessageFilesExist(r0);

            // (4) A broken registration is repaired.
            using (var k = Registry.LocalMachine.OpenSubKey(key, writable: true)!)
                k.SetValue("EventMessageFile", @"C:\cpm-e2e-missing\System.Diagnostics.EventLog.Messages.dll", RegistryValueKind.ExpandString);
            Assert.Null(EventLogSource.Ensure());
            var repaired = Registered();
            report["afterEnsure"] = repaired;
            Assert.NotNull(repaired);
            Assert.True(EventLogSource.MessageFilesExist(repaired), repaired);

            // (1)(3) A 30,000-character entry is written and renders with its text.
            var marker = "cpm-e2e-" + Guid.NewGuid().ToString("N");
            var text = marker + " " + new string('x', 30000 - marker.Length - 1);
            EventLog.WriteEntry(EventLogSource.Name, text, EventLogEntryType.Information, 1900);
            var rendered = RunPowerShell(
                $"$e = Get-WinEvent -FilterHashtable @{{ LogName = '{EventLogSource.LogName}'; ProviderName = '{EventLogSource.Name}' }} -MaxEvents 20 | " +
                $"Where-Object {{ $_.Message -like '{marker}*' }} | Select-Object -First 1; if ($e) {{ $e.Message.Length }} else {{ 'missing' }}");
            report["renderedLength"] = rendered;
            Assert.Equal(text.Length.ToString(), rendered);
        }
        finally
        {
            if (!existed && EventLog.SourceExists(EventLogSource.Name)) EventLog.DeleteEventSource(EventLogSource.Name);
            else if (existed) EventLogSource.Ensure();
            E2EArtifacts.Write("windows-eventlog-source.json", report);
        }
    }

    /// <summary>
    /// Ways this can fail: the hint names "NT AUTHORITY\HOST$" (Environment.UserDomainName of SYSTEM) instead of the
    /// computer account; a domain member is reported as workgroup or the reverse; NetGetJoinInformation marshalling is wrong.
    /// </summary>
    [ElevatedWindowsFact]
    public void UncBackupHintNamesTheComputerAccount()
    {
        if (!OperatingSystem.IsWindows()) return;
        var cs = RunPowerShell("$c = Get-CimInstance Win32_ComputerSystem; \"$($c.PartOfDomain)|$($c.Domain)\"").Split('|');
        var partOfDomain = bool.Parse(cs[0]);
        var joined = ComputerAccount.JoinedDomain();
        var name = ComputerAccount.Name();
        var report = E2EArtifacts.Report(nameof(UncBackupHintNamesTheComputerAccount));
        report["win32ComputerSystem"] = string.Join("|", cs);
        report["joinedDomain"] = joined;
        report["hintAccount"] = name;
        report["environmentUserDomainName"] = Environment.UserDomainName;
        E2EArtifacts.Write("windows-computer-account.json", report);
        Assert.Equal(partOfDomain, joined is not null);
        Assert.StartsWith(joined is null ? $"{Environment.MachineName}$" : $"{joined}\\{Environment.MachineName}$", name);
        Assert.DoesNotContain("NT AUTHORITY", name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// DPAPI LocalMachine: "Any process running on the computer can unprotect data"
    /// (https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.dataprotectionscope). The fixed entropy is
    /// in the source code, so the file ACLs of the data directory and of backups are the only barrier (docs/security.md).
    ///
    /// Ways this can fail (i.e. the documentation would be wrong): a blob written by the service cannot be read back after
    /// a restart (different process); another process can NOT read it (then restores on the same machine would break);
    /// the blob is readable without the entropy.
    /// </summary>
    [ElevatedWindowsFact]
    public void DpapiLocalMachineSecretsAreReadableByAnyProcessWithTheEntropy()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(Path.GetTempPath(), "cpm-dpapi-e2e", Guid.NewGuid().ToString("N"));
        try
        {
            var protector = new SecretProtector(new AppPaths(dir));
            var blob = protector.Protect("smtp-password-e2e");
            Assert.StartsWith("enc:v1:", blob);
            var b64 = blob["enc:v1:".Length..];
            string Other(string entropy) => RunPowerShell(
                "Add-Type -AssemblyName System.Security; " +
                $"try {{ $p = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String('{b64}'), {entropy}, 'LocalMachine'); " +
                "[Text.Encoding]::UTF8.GetString($p) } catch { 'FAILED' }");
            var withEntropy = Other("[Text.Encoding]::UTF8.GetBytes('CaddyProxyManager/secrets')");
            var withoutEntropy = Other("$null");
            var report = E2EArtifacts.Report(nameof(DpapiLocalMachineSecretsAreReadableByAnyProcessWithTheEntropy));
            report["otherProcessWithEntropy"] = withEntropy == "smtp-password-e2e" ? "decrypted" : withEntropy;
            report["otherProcessWithoutEntropy"] = withoutEntropy;
            E2EArtifacts.Write("windows-dpapi.json", report);
            Assert.Equal("smtp-password-e2e", withEntropy);
            Assert.Equal("FAILED", withoutEntropy);
            Assert.Equal("smtp-password-e2e", new SecretProtector(new AppPaths(dir)).Unprotect(blob));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* temp */ }
        }
    }
}

/// <summary>Runs alone: the Event Log test (re)registers the product's source, which other suites may write to.</summary>
[CollectionDefinition(nameof(WindowsOpsE2ECollection), DisableParallelization = true)]
public sealed class WindowsOpsE2ECollection;
