using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CaddyManager.Core;
using CaddyManager.Ops.Events;

namespace CaddyManager.Ops.Tests;

/// <summary>
/// The first-run setup token must never reach the Windows Application log: every local user can read that log, while
/// setup-token.txt and the manager log are restricted to SYSTEM and Administrators (docs/security.md). Runs the real
/// manager (src/CaddyManager, composed by Program.cs with the host's default logging providers) as a console process with
/// a throw-away data directory on the elevated windows-latest CI runner, then reads the Application log back. Skipped
/// elsewhere. Writes windows-setup-token-eventlog.json (the token itself only as a SHA-256 prefix).
/// </summary>
[Trait("Category", "WindowsE2E")]
[Collection(nameof(WindowsOpsE2ECollection))]
public class SetupTokenEventLogE2ETests
{
    /// <summary>
    /// Ways this can fail:
    ///  1. The start-up warning (or any other line) carries the token through ILogger, and the host's EventLog provider
    ///     (added by WebApplication.CreateBuilder on Windows, warnings and above) writes it to the Application log.
    ///  2. The token is written under another source (the EventLog provider falls back to the application name when
    ///     EventLogSettings.SourceName is not applied), so a search limited to our source would miss it: every
    ///     Application log entry since the manager started is searched.
    ///  3. The test passes vacuously because the EventLog provider is not active in this process: the pointer warning
    ///     (without the token) must appear under the product's source.
    ///  4. The token no longer reaches the manager log at all (IManagerLogFile not registered by Program.cs, so the
    ///     no-op default drops it), which breaks the documented fallback when setup-token.txt cannot be written.
    ///  5. The console (stdout) prints the token.
    ///  6. The test leaves the event source registered on a machine where it did not exist, or a manager process behind.
    /// </summary>
    [ElevatedWindowsFact]
    public async Task SetupTokenIsInTheManagerLogButNotInTheEventLog()
    {
        if (!OperatingSystem.IsWindows()) return;
        var report = E2EArtifacts.Report(nameof(SetupTokenIsInTheManagerLogButNotInTheEventLog));
        var dataDir = Path.Combine(Path.GetTempPath(), "cpm-setup-token-e2e", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(dataDir);
        var sourceExisted = EventLog.SourceExists(EventLogSource.Name);
        report["sourceExistedBefore"] = sourceExisted;
        Process? manager = null;
        var stdout = new StringBuilder();
        try
        {
            var dll = await BuildManagerAsync(report);
            // A placeholder binary: the bootstrapper neither downloads Caddy nor copies the development binary, and with
            // CM_CADDY_HOST=process / CM_CADDY_AUTOSTART=0 it never touches the real Caddy service.
            paths.EnsureCreated();
            await File.WriteAllTextAsync(paths.CaddyExe, "placeholder");

            var started = DateTime.UtcNow.AddSeconds(-2);
            var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add(dll);
            psi.Environment["CM_DATA_DIR"] = dataDir;
            psi.Environment["CM_UI_PORT"] = FreePort().ToString();
            psi.Environment["CM_CADDY_HOST"] = "process";
            psi.Environment["CM_CADDY_AUTOSTART"] = "0";
            manager = Process.Start(psi)!;
            manager.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.AppendLine(e.Data); };
            manager.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.AppendLine(e.Data); };
            manager.BeginOutputReadLine();
            manager.BeginErrorReadLine();

            // (4) The token line in the manager log (written after the pointer warning).
            var tokenLine = await PollAsync(() => ManagerLog(paths).Split('\n').FirstOrDefault(l => l.Contains("Setup token: ")),
                TimeSpan.FromSeconds(60), () => $"'Setup token:' in {paths.ManagerLogDir}. Output:\n{Output(stdout)}\nLog:\n{ManagerLog(paths)}");
            var token = File.ReadAllText(paths.SetupTokenFile).Trim();
            report["tokenSha256Prefix"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..12];
            report["tokenLength"] = token.Length;
            Assert.True(token.Length >= 32, "setup-token.txt holds no token");
            Assert.EndsWith("Setup token: " + token, tokenLine.TrimEnd('\r'));
            report["managerLogHasToken"] = true;

            // (3) The pointer warning reached the Application log under the product's source: the provider is live.
            var pointer = await PollAsync(() => ApplicationLogSince(started).FirstOrDefault(e =>
                    e.Provider == EventLogSource.Name && e.Text.Contains("No administrator account exists yet") && e.Text.Contains(paths.SetupTokenFile)),
                TimeSpan.FromSeconds(30), () => "the setup pointer warning in the Application log");
            report["pointerEvent"] = new JsonObject
            {
                ["provider"] = pointer.Provider, ["level"] = pointer.Level, ["recordId"] = pointer.RecordId,
                ["mentionsManagerLog"] = pointer.Text.Contains(paths.ManagerLogDir),
            };

            // (1)(2) No Application log entry since the start carries the token, whatever its source.
            var all = ApplicationLogSince(started);
            var leaked = all.Where(e => e.Text.Contains(token, StringComparison.OrdinalIgnoreCase)).ToList();
            report["applicationLogEntriesScanned"] = all.Count;
            report["productSourceEntries"] = all.Count(e => e.Provider == EventLogSource.Name);
            report["entriesWithToken"] = new JsonArray(leaked.Select(e => (JsonNode)$"{e.Provider} #{e.RecordId}").ToArray());
            Assert.True(leaked.Count == 0, "The setup token is in the Application log: " + string.Join(", ", leaked.Select(e => $"{e.Provider} #{e.RecordId}")));

            // (5) Nor on the console.
            var consoleHasToken = Output(stdout).Contains(token, StringComparison.OrdinalIgnoreCase);
            report["consoleHasToken"] = consoleHasToken;
            Assert.False(consoleHasToken, "The setup token was printed on the console.");
        }
        finally
        {
            // (6)
            if (manager is { HasExited: false }) { manager.Kill(entireProcessTree: true); manager.WaitForExit(10_000); }
            manager?.Dispose();
            if (!sourceExisted && EventLog.SourceExists(EventLogSource.Name)) EventLog.DeleteEventSource(EventLogSource.Name);
            report["sourceLeftRegistered"] = EventLog.SourceExists(EventLogSource.Name);
            E2EArtifacts.Write("windows-setup-token-eventlog.json", report);
            try { Directory.Delete(dataDir, true); } catch { /* temp */ }
        }
    }

    private sealed record LogEvent(string Provider, string Level, long? RecordId, string Text);

    /// <summary>Every Application log record created at or after <paramref name="sinceUtc"/>: rendered message and raw insertion strings.</summary>
    private static List<LogEvent> ApplicationLogSince(DateTime sinceUtc)
    {
        if (!OperatingSystem.IsWindows()) return [];
        var xpath = $"*[System[TimeCreated[@SystemTime>='{sinceUtc:yyyy-MM-ddTHH:mm:ss.fff}Z']]]";
        using var reader = new EventLogReader(new EventLogQuery(EventLogSource.LogName, PathType.LogName, xpath));
        var list = new List<LogEvent>();
        for (var record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
        {
            using (record)
            {
                string? description;
                try { description = record.FormatDescription(); } catch (EventLogException) { description = null; }
                var raw = string.Join('\n', record.Properties.Select(p => p.Value?.ToString()));
                list.Add(new LogEvent(record.ProviderName, record.LevelDisplayName ?? record.Level?.ToString() ?? "", record.RecordId,
                    description + "\n" + raw));
            }
        }
        return list;
    }

    /// <summary>Builds src/CaddyManager in this test's configuration (incremental) and returns CaddyManager.dll.</summary>
    private static async Task<string> BuildManagerAsync(JsonObject report)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CaddyManager.sln"))) root = root.Parent;
        Assert.True(root is not null, "Repository root (CaddyManager.sln) not found above " + AppContext.BaseDirectory);
        // bin/<Configuration>/net10.0/
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
        var project = Path.Combine(root.FullName, "src", "CaddyManager", "CaddyManager.csproj");
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "build", project, "-c", configuration, "--nologo", "-v", "q" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromMinutes(5)).Token);
        Assert.True(p.ExitCode == 0, $"dotnet build {project} failed ({p.ExitCode}):\n{await output}\n{await error}");
        var dll = Path.Combine(root.FullName, "src", "CaddyManager", "bin", configuration, "net10.0", "CaddyManager.dll");
        Assert.True(File.Exists(dll), dll + " was not built");
        report["manager"] = dll;
        return dll;
    }

    private static string ManagerLog(AppPaths paths)
    {
        if (!Directory.Exists(paths.ManagerLogDir)) return "";
        var sb = new StringBuilder();
        foreach (var f in Directory.GetFiles(paths.ManagerLogDir, "manager-*.log").Order())
        {
            // The manager keeps the file open (FileShare.ReadWrite | Delete).
            using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            sb.Append(new StreamReader(fs).ReadToEnd());
        }
        return sb.ToString();
    }

    private static string Output(StringBuilder stdout)
    {
        lock (stdout) return stdout.ToString();
    }

    private static async Task<T> PollAsync<T>(Func<T?> probe, TimeSpan timeout, Func<string> what) where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                if (probe() is { } value) return value;
            }
            catch (Exception ex) when (ex is IOException or EventLogException) { /* file being created, log busy: retry */ }
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0} s waiting for {what()}");
            await Task.Delay(250);
        }
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
