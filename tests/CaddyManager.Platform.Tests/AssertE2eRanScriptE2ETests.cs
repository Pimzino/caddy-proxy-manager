using System.Security;
using System.Text;
using System.Text.Json.Nodes;
using CaddyManager.Platform.Binary;
using CaddyManager.Platform.Infrastructure;

namespace CaddyManager.Platform.Tests;

/// <summary>
/// .github/scripts/assert-e2e-ran.ps1, the CI gate that makes the "test" job fail when an end-to-end test that must run on
/// the windows-latest runner was skipped (review round 2: the HTTP/3 0-RTT test was skipped on CI because
/// CPM_AIOQUIC_PYTHON was not set, and every Windows-only suite could skip without anyone noticing). Run for real with
/// pwsh against TRX files in the exact format xunit.runner.visualstudio 3.1.5 writes (captured from a real
/// `dotnet test --logger trx` run: outcome="NotExecuted" plus the skip reason in Output/ErrorInfo/Message, theory rows
/// with XML-escaped quotes in testName).
///
/// Ways the gate can fail:
///  1. A required test that was skipped (outcome NotExecuted, e.g. "Set CPM_AIOQUIC_PYTHON ...") passes the gate, so CI is
///     green although the test never ran (the finding this gate exists for).
///  2. A required test that is missing from the TRX files (renamed, excluded by --filter, project not run) passes.
///  3. A required test that ran and passed is rejected, so CI can never go green: theory rows (several results for one
///     pattern), names with parentheses and XML-escaped quotes, exact names without wildcards.
///  4. Only one TRX file is read (there is one per test project), so tests of the other projects count as missing.
///  5. A required artifact that was not written (or is not valid JSON) passes, so the uploaded evidence is incomplete.
///  6. The skip reason is not reported, so the CI log does not say why a test did not run.
///  7. No TRX file at all (the test step crashed before writing one) passes.
///  8. The gate writes no report of its own (no verifiable record of what it checked).
///  9. A failed required test is accepted by the gate (dotnet test fails the step anyway, but the report must say so).
/// Control: the "skipped" case below is the review's situation (0-RTT test NotExecuted); the gate must reject it.
/// Skipped when pwsh is not installed. Artifact: e2e-gate-script.json.
/// </summary>
[Trait("Category", "Script")]
public class AssertE2eRanScriptE2ETests
{
    private static string Script()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; dir is not null && i < 10; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, ".github", "scripts", "assert-e2e-ran.ps1");
            if (File.Exists(candidate)) return candidate;
        }
        Assert.Skip(".github/scripts/assert-e2e-ran.ps1 not found above the test output directory.");
        return "";
    }

    private static async Task<string?> PwshAsync(CancellationToken ct)
    {
        try
        {
            var r = await ProcessRunner.RunAsync("pwsh", ["-NoProfile", "-Command", "$PSVersionTable.PSVersion.ToString()"],
                new ProcessOptions { Timeout = TimeSpan.FromSeconds(60) }, ct);
            return r.ExitCode == 0 ? r.StdOut.Trim() : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private const string ZeroRtt = "CaddyManager.Config.Tests.TlsE2ETests.Http3_resumed_requests_on_ip_restricted_hosts_never_get_425";
    private const string Theory = "CaddyManager.Platform.Tests.CaddyServiceStartPendingE2ETests.StopOfAServiceThatNeverLeavesStartPendingIsBoundedAndDoesNotTriggerRecovery";
    private const string SkipReason = "Set CPM_AIOQUIC_PYTHON to a Python with aioquic (pip install aioquic) to run the HTTP/3 0-RTT test; .NET's QUIC client cannot send 0-RTT.";

    /// <summary>A TRX file as xunit.runner.visualstudio writes it (only the parts the gate reads, same element names).</summary>
    private static string Trx(params (string Name, string Outcome, string? Message)[] results)
    {
        var sb = new StringBuilder();
        sb.Append("﻿<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        sb.Append("<TestRun id=\"").Append(Guid.NewGuid()).Append("\" name=\"ci\" xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\">\n  <Results>\n");
        foreach (var (name, outcome, message) in results)
        {
            sb.Append("    <UnitTestResult executionId=\"").Append(Guid.NewGuid()).Append("\" testId=\"").Append(Guid.NewGuid())
              .Append("\" testName=\"").Append(SecurityElement.Escape(name)).Append("\" computerName=\"ci\" duration=\"00:00:00.0010000\" outcome=\"")
              .Append(outcome).Append('"');
            if (message is null) sb.Append(" />\n");
            else sb.Append(">\n      <Output>\n        <ErrorInfo>\n          <Message>").Append(SecurityElement.Escape(message))
                   .Append("</Message>\n        </ErrorInfo>\n      </Output>\n    </UnitTestResult>\n");
        }
        sb.Append("  </Results>\n  <ResultSummary outcome=\"Completed\"><Counters total=\"1\" executed=\"1\" passed=\"1\" failed=\"0\" /></ResultSummary>\n</TestRun>\n");
        return sb.ToString();
    }

    [Fact]
    public async Task SkippedOrMissingRequiredE2ETestsFailTheBuild()
    {
        var ct = TestContext.Current.CancellationToken;
        var script = Script();
        var pwsh = await PwshAsync(ct);
        Assert.SkipWhen(pwsh is null, "pwsh is not installed.");

        var work = Path.Combine(Path.GetTempPath(), "cpm-e2e-gate-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var report = E2EArtifacts.Report(nameof(SkippedOrMissingRequiredE2ETestsFailTheBuild));
        report["pwsh"] = pwsh;
        report["caddyVersion"] = $"{CaddyVersion.Tested} (pinned for CI; no Caddy binary is executed, only the gate script is tested)";
        report["script"] = script;
        var cases = new JsonObject();
        report["cases"] = cases;

        async Task<(int Exit, string Output, JsonNode? Report)> Run(string label, Dictionary<string, string> trxFiles,
            Dictionary<string, string>? artifacts = null, string[]? requiredArtifacts = null)
        {
            var results = Path.Combine(work, label, "TestResults");
            var art = Path.Combine(results, "e2e");
            Directory.CreateDirectory(art);
            foreach (var (file, content) in trxFiles) await File.WriteAllTextAsync(Path.Combine(results, file), content, ct);
            foreach (var (file, content) in artifacts ?? []) await File.WriteAllTextAsync(Path.Combine(art, file), content, ct);
            var gateReport = Path.Combine(work, label, "gate.json");
            List<string> args =
            [
                "-NoProfile", "-NonInteractive", "-File", script, "-ResultsDirectory", results,
                "-RequiredTests", $"{ZeroRtt};{Theory}*;CaddyManager.Ops.Tests.WindowsOpsE2ETests.*",
                "-ArtifactsDirectory", art, "-ReportPath", gateReport,
            ];
            if (requiredArtifacts is not null) args.AddRange(["-RequiredArtifacts", string.Join(';', requiredArtifacts)]);
            var r = await ProcessRunner.RunAsync("pwsh", args, new ProcessOptions
            {
                Timeout = TimeSpan.FromMinutes(2),
                Environment = new Dictionary<string, string> { ["GITHUB_STEP_SUMMARY"] = "", ["NO_COLOR"] = "1" },
            }, ct);
            var parsed = File.Exists(gateReport) ? JsonNode.Parse(await File.ReadAllTextAsync(gateReport, ct)) : null;
            cases[label] = new JsonObject { ["exitCode"] = r.ExitCode, ["output"] = r.Combined.Trim(), ["gateReport"] = parsed?.DeepClone() };
            return (r.ExitCode, r.Combined, parsed);
        }

        // Every required test passed, spread over two TRX files (one per test project, 4), theory rows with quotes (3).
        var configTrx = Trx((ZeroRtt, "Passed", null), ("CaddyManager.Config.Tests.GeneratorTests.Something", "Passed", null));
        var platformTrx = Trx((Theory + "(mode: \"deaf\")", "Passed", null), (Theory + "(mode: \"hung\")", "Passed", null),
            (Theory + "(mode: \"noadmin\")", "Passed", null));
        var opsTrx = Trx(("CaddyManager.Ops.Tests.WindowsOpsE2ETests.DpapiRoundTrip", "Passed", null));
        var allPassed = new Dictionary<string, string> { ["config.trx"] = configTrx, ["platform.trx"] = platformTrx, ["ops.trx"] = opsTrx };
        var goodArtifacts = new Dictionary<string, string> { ["http3-0rtt-ip-access-list.json"] = "{\"caddyVersion\":\"v2.11.4 h1:x\"}" };

        try
        {
            // (3)(4)(8) All ran: the gate passes and records what it checked.
            var ok = await Run("all-passed", allPassed, goodArtifacts, ["http3-0rtt-ip-access-list.json"]);
            Assert.Equal(0, ok.Exit);
            Assert.NotNull(ok.Report);
            Assert.True(ok.Report!["ok"]!.GetValue<bool>());
            Assert.Equal(3, ok.Report["trxFiles"]!.AsArray().Count);
            var theoryRow = ok.Report["tests"]!.AsArray().Single(t => t!["pattern"]!.GetValue<string>() == Theory + "*")!;
            Assert.Equal(3, theoryRow["matched"]!.GetValue<int>());
            Assert.Equal("v2.11.4 h1:x", ok.Report["artifacts"]!.AsArray().Single()!["caddyVersion"]!.GetValue<string>());

            // CONTROL / (1)(6): the review's situation, the 0-RTT test skipped for lack of CPM_AIOQUIC_PYTHON.
            var skipped = await Run("skipped", new Dictionary<string, string>
            {
                ["config.trx"] = Trx((ZeroRtt, "NotExecuted", SkipReason)), ["platform.trx"] = platformTrx, ["ops.trx"] = opsTrx,
            });
            Assert.NotEqual(0, skipped.Exit);
            Assert.Contains("did not run", skipped.Output);
            Assert.Contains("Set CPM_AIOQUIC_PYTHON", skipped.Output);
            Assert.False(skipped.Report!["ok"]!.GetValue<bool>());

            // (1) One theory row skipped is enough to fail.
            var oneRow = await Run("one-theory-row-skipped", new Dictionary<string, string>
            {
                ["config.trx"] = configTrx, ["ops.trx"] = opsTrx,
                ["platform.trx"] = Trx((Theory + "(mode: \"deaf\")", "Passed", null), (Theory + "(mode: \"hung\")", "NotExecuted", "Windows only.")),
            });
            Assert.NotEqual(0, oneRow.Exit);
            Assert.Contains("Windows only.", oneRow.Output);

            // (2)(4) The Ops project's TRX is missing: its required tests count as missing.
            var missing = await Run("missing", new Dictionary<string, string> { ["config.trx"] = configTrx, ["platform.trx"] = platformTrx });
            Assert.NotEqual(0, missing.Exit);
            Assert.Contains("CaddyManager.Ops.Tests.WindowsOpsE2ETests.*", missing.Output);
            Assert.Contains("no test result", missing.Output);

            // (9) A failed required test.
            var failed = await Run("failed", new Dictionary<string, string>
            {
                ["config.trx"] = Trx((ZeroRtt, "Failed", "Assert.Equal() Failure: expected 200, actual 425")), ["platform.trx"] = platformTrx, ["ops.trx"] = opsTrx,
            });
            Assert.NotEqual(0, failed.Exit);
            Assert.Contains("Failed", failed.Output);

            // (5) Required artifact missing, and one that is not JSON.
            var noArtifact = await Run("artifact-missing", allPassed, [], ["http3-0rtt-ip-access-list.json"]);
            Assert.NotEqual(0, noArtifact.Exit);
            Assert.Contains("http3-0rtt-ip-access-list.json", noArtifact.Output);
            var badArtifact = await Run("artifact-not-json", allPassed, new Dictionary<string, string> { ["http3-0rtt-ip-access-list.json"] = "{truncated" },
                ["http3-0rtt-ip-access-list.json"]);
            Assert.NotEqual(0, badArtifact.Exit);
            Assert.Contains("not valid JSON", badArtifact.Output);

            // (7) No TRX at all.
            var none = await Run("no-trx", []);
            Assert.NotEqual(0, none.Exit);
            Assert.Contains("No TRX", none.Output);
        }
        finally
        {
            E2EArtifacts.Write("e2e-gate-script.json", report);
            try { Directory.Delete(work, true); } catch { /* best effort */ }
        }
    }
}
