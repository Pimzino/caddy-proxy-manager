using System.Runtime.Versioning;
using System.ServiceProcess;
using TimeoutException = System.TimeoutException;
using CaddyManager.Platform.Infrastructure;
using Microsoft.Win32;

namespace CaddyManager.Platform.Windows;

/// <summary>Desired configuration of a Windows service registered by this product.</summary>
public sealed record ServiceDefinition
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    /// <summary>Full command line including quotes around the executable, e.g. "\"C:\x\caddy.exe\" run --config \"C:\y\caddy.json\"".</summary>
    public required string BinaryPathName { get; init; }
    /// <summary>sc.exe start type: "auto", "delayed-auto", "demand" or "disabled".</summary>
    public string StartType { get; init; } = "auto";
    /// <summary>Recovery actions: restart delays in milliseconds (first, second, subsequent failures).</summary>
    public IReadOnlyList<int> RestartDelaysMs { get; init; } = [5000, 5000, 30000];
    public int FailureResetSeconds { get; init; } = 86400;
    /// <summary>REG_MULTI_SZ "Environment" value (NAME=value entries); null leaves it untouched.</summary>
    public IReadOnlyList<string>? Environment { get; init; }
}

/// <summary>Registry view of a service's configuration (HKLM\SYSTEM\CurrentControlSet\Services\&lt;name&gt;).</summary>
public sealed record ServiceRegistryInfo
{
    public string? ImagePath { get; init; }
    public int Start { get; init; } = -1;
    public bool DelayedAutoStart { get; init; }
    public string? ObjectName { get; init; }
    public string? DisplayName { get; init; }
    public string[] Environment { get; init; } = [];

    /// <summary>Human readable start type ("Automatic (Delayed Start)", "Manual" ...).</summary>
    public string StartTypeDisplay => Start switch
    {
        0 => "Boot",
        1 => "System",
        2 => DelayedAutoStart ? "Automatic (Delayed Start)" : "Automatic",
        3 => "Manual",
        4 => "Disabled",
        _ => "Unknown",
    };

    public bool IsLocalSystem =>
        string.IsNullOrEmpty(ObjectName) || ObjectName.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase)
        || ObjectName.Equals(@".\LocalSystem", StringComparison.OrdinalIgnoreCase)
        || ObjectName.Equals(@"NT AUTHORITY\SYSTEM", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Creates, repairs, starts, stops and deletes Windows services via sc.exe, ServiceController and the registry.
/// All members require Windows; callers guard with OperatingSystem.IsWindows().
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsServiceManager
{
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services\";
    private const int ErrorServiceExists = 1073;
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceMarkedForDelete = 1072;
    private const int ErrorServiceNotActive = 1062;

    private static string ScExe => Path.Combine(System.Environment.SystemDirectory, "sc.exe");

    public static bool Exists(string name)
    {
        var services = ServiceController.GetServices();
        try
        {
            return services.Any(s => s.ServiceName.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            foreach (var s in services) s.Dispose();
        }
    }

    public static ServiceRegistryInfo? ReadRegistry(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(ServicesKey + name);
        if (key is null) return null;
        return new ServiceRegistryInfo
        {
            ImagePath = key.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
            Start = key.GetValue("Start") is int s ? s : -1,
            DelayedAutoStart = key.GetValue("DelayedAutostart") is int d && d != 0,
            ObjectName = key.GetValue("ObjectName") as string,
            DisplayName = key.GetValue("DisplayName") as string,
            Environment = key.GetValue("Environment") as string[] ?? [],
        };
    }

    /// <summary>Runs sc.exe with the given arguments (each element is one argv entry; .NET applies the quoting).</summary>
    public static Task<ProcessResult> ScAsync(IEnumerable<string> args, CancellationToken ct = default) =>
        ProcessRunner.RunAsync(ScExe, args, new ProcessOptions { Timeout = TimeSpan.FromSeconds(60) }, ct);

    /// <summary>
    /// Creates the service, or repairs it when its binary path / start type / account / display name differ,
    /// then (re)applies description, recovery actions, failure flag and environment. Idempotent.
    /// Returns a list of human readable changes made.
    /// </summary>
    public static async Task<List<string>> CreateOrRepairAsync(ServiceDefinition def, CancellationToken ct = default)
    {
        var changes = new List<string>();
        string[] common =
        [
            "binPath=", def.BinaryPathName,
            "start=", def.StartType,
            "obj=", "LocalSystem",
            "DisplayName=", def.DisplayName,
        ];

        var existing = ReadRegistry(def.Name);
        if (existing is null || !Exists(def.Name))
        {
            var r = await ScAsync(["create", def.Name, .. common], ct);
            if (r.ExitCode == ErrorServiceExists)
                Check(await ScAsync(["config", def.Name, .. common], ct), $"config {def.Name}");
            else
                Check(r, $"create {def.Name}");
            changes.Add($"Registered service '{def.Name}'.");
        }
        else
        {
            var wantStart = def.StartType switch { "auto" or "delayed-auto" => 2, "demand" => 3, "disabled" => 4, _ => -1 };
            var wantDelayed = def.StartType == "delayed-auto";
            var diffs = new List<string>();
            if (!string.Equals(existing.ImagePath?.Trim(), def.BinaryPathName, StringComparison.OrdinalIgnoreCase))
                diffs.Add($"binary path was '{existing.ImagePath}'");
            if (existing.Start != wantStart || existing.DelayedAutoStart != wantDelayed)
                diffs.Add($"start type was {existing.StartTypeDisplay}");
            if (!existing.IsLocalSystem) diffs.Add($"account was '{existing.ObjectName}'");
            if (!string.Equals(existing.DisplayName, def.DisplayName, StringComparison.Ordinal))
                diffs.Add("display name differed");
            if (diffs.Count > 0)
            {
                Check(await ScAsync(["config", def.Name, .. common], ct), $"config {def.Name}");
                changes.Add($"Repaired service '{def.Name}' ({string.Join("; ", diffs)}).");
            }
        }

        Check(await ScAsync(["description", def.Name, def.Description], ct), $"description {def.Name}");

        var actions = string.Join('/', def.RestartDelaysMs.Select(ms => $"restart/{ms}"));
        Check(await ScAsync(["failure", def.Name, "reset=", def.FailureResetSeconds.ToString(), "actions=", actions], ct),
            $"failure {def.Name}");
        // Also run the recovery actions when the service stops with a non-zero exit code (not only on crashes).
        Check(await ScAsync(["failureflag", def.Name, "1"], ct), $"failureflag {def.Name}");

        if (def.Environment is not null && SetEnvironment(def.Name, def.Environment))
            changes.Add($"Updated environment of service '{def.Name}'.");
        return changes;
    }

    /// <summary>Writes the REG_MULTI_SZ Environment value. Returns true when it changed.</summary>
    public static bool SetEnvironment(string name, IReadOnlyList<string> entries)
    {
        using var key = Registry.LocalMachine.OpenSubKey(ServicesKey + name, writable: true)
                        ?? throw new InvalidOperationException($"Registry key HKLM\\{ServicesKey}{name} does not exist.");
        var current = key.GetValue("Environment") as string[] ?? [];
        if (current.SequenceEqual(entries, StringComparer.Ordinal)) return false;
        key.SetValue("Environment", entries.ToArray(), RegistryValueKind.MultiString);
        return true;
    }

    public static async Task<ScQueryResult?> QueryExAsync(string name, CancellationToken ct = default)
    {
        var r = await ScAsync(["queryex", name], ct);
        return r.ExitCode == 0 ? ScOutputParser.ParseQueryEx(r.StdOut) : null;
    }

    public static async Task<ScFailureConfig?> QueryFailureAsync(string name, CancellationToken ct = default)
    {
        var r = await ScAsync(["qfailure", name], ct);
        return r.ExitCode == 0 ? ScOutputParser.ParseQFailure(r.StdOut) : null;
    }

    public static ServiceControllerStatus? GetStatus(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            return sc.Status;
        }
        catch (InvalidOperationException)
        {
            return null; // not installed (or not accessible)
        }
    }

    /// <summary>Starts the service and waits until it is Running. Throws with a descriptive message when it stops again.</summary>
    public static async Task StartAsync(string name, TimeSpan timeout, CancellationToken ct = default)
    {
        using var sc = new ServiceController(name);
        sc.Refresh();
        if (sc.Status == ServiceControllerStatus.Running) return;
        if (sc.Status == ServiceControllerStatus.StopPending)
            await WaitForAsync(sc, s => s == ServiceControllerStatus.Stopped, timeout, ct);
        if (sc.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.Paused)
        {
            try
            {
                if (sc.Status == ServiceControllerStatus.Paused) sc.Continue();
                else sc.Start();
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException($"Windows could not start service '{name}': {ex.InnerException?.Message ?? ex.Message}", ex);
            }
        }

        var started = DateTime.UtcNow;
        var deadline = started + timeout;
        var sawPending = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            sc.Refresh();
            switch (sc.Status)
            {
                case ServiceControllerStatus.Running:
                    return;
                case ServiceControllerStatus.StartPending:
                    sawPending = true;
                    break;
                case ServiceControllerStatus.Stopped when sawPending || DateTime.UtcNow - started > TimeSpan.FromSeconds(3):
                    var q = await QueryExAsync(name, ct);
                    throw new InvalidOperationException(
                        $"Service '{name}' stopped right after starting (Win32 exit code {q?.Win32ExitCode ?? 0}, service exit code {q?.ServiceExitCode ?? 0}). " +
                        "Check the Caddy log and the Windows System event log for the reason.");
            }
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Service '{name}' did not reach the Running state within {timeout.TotalSeconds:0}s (current state: {sc.Status}).");
            await Task.Delay(300, ct);
        }
    }

    /// <summary>Stops the service; if it does not stop within the timeout its process is terminated.</summary>
    public static async Task StopAsync(string name, TimeSpan timeout, CancellationToken ct = default)
    {
        using var sc = new ServiceController(name);
        sc.Refresh();
        if (sc.Status == ServiceControllerStatus.Stopped) return;
        if (sc.Status != ServiceControllerStatus.StopPending)
        {
            try
            {
                sc.Stop();
            }
            catch (InvalidOperationException ex) when (ex.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: ErrorServiceNotActive })
            {
                return;
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException($"Windows could not stop service '{name}': {ex.InnerException?.Message ?? ex.Message}", ex);
            }
        }
        try
        {
            await WaitForAsync(sc, s => s == ServiceControllerStatus.Stopped, timeout, ct);
        }
        catch (TimeoutException)
        {
            var q = await QueryExAsync(name, ct);
            if (q?.ProcessId is not int pid) throw;
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(pid);
                p.Kill(entireProcessTree: true);
                await p.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(15), ct);
            }
            catch (ArgumentException) { /* already gone */ }
            await WaitForAsync(sc, s => s == ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15), ct);
        }
    }

    /// <summary>Stops (if needed) and deletes the service. Missing services are ignored.</summary>
    public static async Task DeleteAsync(string name, CancellationToken ct = default)
    {
        if (!Exists(name)) return;
        await StopAsync(name, TimeSpan.FromSeconds(30), ct);
        var r = await ScAsync(["delete", name], ct);
        if (r.ExitCode is not (0 or ErrorServiceDoesNotExist or ErrorServiceMarkedForDelete))
            Check(r, $"delete {name}");
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (Exists(name) && DateTime.UtcNow < deadline) await Task.Delay(300, ct);
    }

    private static async Task WaitForAsync(ServiceController sc, Func<ServiceControllerStatus, bool> done, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            sc.Refresh();
            if (done(sc.Status)) return;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Service '{sc.ServiceName}' did not change state within {timeout.TotalSeconds:0}s (current state: {sc.Status}).");
            await Task.Delay(300, ct);
        }
    }

    private static void Check(ProcessResult r, string what)
    {
        if (r.ExitCode != 0)
            throw new InvalidOperationException($"sc.exe {what} failed with exit code {r.ExitCode}: {r.Combined.Trim()}");
    }
}
