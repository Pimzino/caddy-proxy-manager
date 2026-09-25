using System.Text.Json;
using System.Text.Json.Nodes;

namespace CaddyManager.Platform.Tests;

/// <summary>
/// Verifiable, repeatable output of the end-to-end tests: one JSON file per test in CPM_E2E_ARTIFACTS (CI uploads it)
/// or ./e2e-artifacts next to the test binaries. Each file records what was observed on the real system (OS build,
/// commands, exit codes, values read back), so a run can be compared with a later one.
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

    /// <summary>New report with the common header (test name, UTC time, OS, runtime, machine).</summary>
    public static JsonObject Report(string test) => new()
    {
        ["test"] = test,
        ["utc"] = DateTime.UtcNow.ToString("O"),
        ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        ["osVersion"] = Environment.OSVersion.Version.ToString(),
        ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        ["machine"] = Environment.MachineName,
    };

    public static string Write(string fileName, JsonObject report)
    {
        var path = Path.Combine(Directory, fileName);
        File.WriteAllText(path, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }
}
