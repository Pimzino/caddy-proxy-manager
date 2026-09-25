using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CaddyManager.Cluster.Tests;

/// <summary>
/// Verifiable, repeatable output of the end-to-end tests: one JSON file per test in CPM_E2E_ARTIFACTS (CI uploads it)
/// or ./e2e-artifacts next to the test binaries. Each report records the Caddy binary used, timings, revisions and what
/// was verified, so a run can be compared with a later one.
/// </summary>
public static class E2EArtifacts
{
    public static string Directory
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("CPM_E2E_ARTIFACTS") is { Length: > 0 } d
                ? d
                : Path.Combine(AppContext.BaseDirectory, "e2e-artifacts");
            System.IO.Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>New report with the common header (test name, UTC time, OS, runtime, machine, Caddy version).</summary>
    public static JsonObject Report(string test) => new()
    {
        ["test"] = test,
        ["utc"] = DateTime.UtcNow.ToString("O"),
        ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        ["machine"] = Environment.MachineName,
        ["caddyVersion"] = CaddyVersion(),
    };

    public static string Write(string fileName, JsonObject report)
    {
        var path = Path.Combine(Directory, fileName);
        File.WriteAllText(path, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    private static string CaddyVersion()
    {
        if (DevCaddy.Path is not { } bin) return "(no caddy binary)";
        using var p = Process.Start(new ProcessStartInfo(bin, "version") { RedirectStandardOutput = true, UseShellExecute = false })!;
        var output = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit(10_000);
        return output;
    }
}
