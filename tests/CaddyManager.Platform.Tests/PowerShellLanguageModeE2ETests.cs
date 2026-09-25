using System.Text.Json.Nodes;
using CaddyManager.Platform.Infrastructure;
using CaddyManager.Platform.Readiness;

namespace CaddyManager.Platform.Tests;

/// <summary>
/// End to end: the readiness scripts are sent to a real PowerShell process exactly as the product sends them
/// (<see cref="PowerShellRunner.BuildStdin"/> on stdin of "-Command -"), once in FullLanguage and once after switching the
/// session to ConstrainedLanguage, which is what Windows Defender Application Control / AppLocker enforce for
/// "-Command" input on hardened servers
/// (https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_language_modes: in
/// ConstrainedLanguage "method invocation is supported only on core types"). Windows PowerShell 5.1 on the Windows CI
/// runner; PowerShell 7 (pwsh) elsewhere when installed.
///
/// Ways this can fail (written before the fix):
///  1. The stdin line calls [System.Text.Encoding]/[System.Convert]/[ScriptBlock]::Create, which CLM blocks, so the
///     product only gets "PowerShell returned no result" plus a stderr line nobody understands.
///  2. A language-mode check itself uses something CLM blocks (method calls, .NET types), so it never reports.
///  3. The check does not stop the line: the blocked payload still runs and its error replaces the clear message.
///  4. The message is not valid JSON for <see cref="PowerShellRunner.ParseOutput"/> (quotes, localisation) and turns
///     into "not valid JSON".
///  5. The check fires in FullLanguage (e.g. comparing an enum with a string fails in 5.1) and breaks every readiness
///     check on normal servers.
///  6. The stdin stops being a single line (the -Command - line-by-line parsing quirks the runner avoids).
/// </summary>
[Trait("Category", "PowerShell")]
public class PowerShellLanguageModeE2ETests
{
    private static string? Shell()
    {
        if (OperatingSystem.IsWindows()) return PowerShellRunner.Executable;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var p = Path.Combine(dir, "pwsh");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static Task<ProcessResult> RunAsync(string shell, string stdin, CancellationToken ct) =>
        ProcessRunner.RunAsync(shell, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", "-"],
            new ProcessOptions { StdIn = stdin, Timeout = TimeSpan.FromSeconds(60) }, ct);

    [Fact]
    public async Task ConstrainedLanguageModeIsReportedClearly()
    {
        var shell = Shell();
        Assert.SkipWhen(shell is null, "No PowerShell (Windows PowerShell or pwsh) available.");
        var ct = TestContext.Current.CancellationToken;
        const string script = "[ordered]@{ answer = 42; mode = [string]$ExecutionContext.SessionState.LanguageMode }";
        var stdin = PowerShellRunner.BuildStdin(script);
        Assert.Equal(1, stdin.Count(c => c == '\n')); // (6)

        var report = E2EArtifacts.Report(nameof(ConstrainedLanguageModeIsReportedClearly));
        report["shell"] = shell;

        // (5) FullLanguage: the product's line runs normally.
        var full = await RunAsync(shell!, stdin, ct);
        report["fullLanguage"] = new JsonObject { ["exitCode"] = full.ExitCode, ["stdout"] = full.StdOut.Trim(), ["stderr"] = full.StdErr.Trim() };
        var json = PowerShellRunner.ParseOutput(full.StdOut, full.StdErr, full.ExitCode);
        Assert.Equal(42, json.GetProperty("answer").GetInt32());
        Assert.Equal("FullLanguage", json.GetProperty("mode").GetString());

        // (1)-(4) ConstrainedLanguage: the same line after the session is locked down.
        var constrained = await RunAsync(shell!, "$ExecutionContext.SessionState.LanguageMode = 'ConstrainedLanguage'\n" + stdin, ct);
        report["constrainedLanguage"] = new JsonObject
        {
            ["exitCode"] = constrained.ExitCode, ["stdout"] = constrained.StdOut.Trim(), ["stderr"] = constrained.StdErr.Trim(),
        };
        var ex = Assert.Throws<PowerShellException>(() => PowerShellRunner.ParseOutput(constrained.StdOut, constrained.StdErr, constrained.ExitCode));
        report["productMessage"] = ex.Message;
        E2EArtifacts.Write("powershell-language-mode.json", report);

        Assert.Contains("ConstrainedLanguage", ex.Message);
        Assert.Contains("Application Control", ex.Message);
        Assert.DoesNotContain("not valid JSON", ex.Message);
        Assert.DoesNotContain("Method invocation", constrained.StdErr + constrained.StdOut); // (3) the payload never ran
    }
}
