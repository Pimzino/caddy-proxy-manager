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

/// <summary>How <see cref="WindowsServiceManager.StartAsync"/> finished.</summary>
public enum ServiceStartOutcome
{
    /// <summary>The service was already RUNNING.</summary>
    AlreadyRunning,
    /// <summary>The service reported RUNNING.</summary>
    Running,
    /// <summary>The service still reports START_PENDING, but the caller's probe says it serves (Caddy issue #8012).</summary>
    ServingWhileStartPending,
}

/// <summary>How <see cref="WindowsServiceManager.StopAsync"/> stopped the service.</summary>
public enum ServiceStopPath
{
    AlreadyStopped,
    /// <summary>The SCM stop worked (the service reported SERVICE_STOPPED).</summary>
    Stopped,
    /// <summary>The SCM refused or timed out; the caller's fallback (e.g. Caddy's admin API /stop) made the process exit.</summary>
    Fallback,
    /// <summary>The SCM refused or timed out; the service process was terminated.</summary>
    Killed,
}

/// <summary>Options for stopping a service that may refuse the stop control.</summary>
public sealed record ServiceStopOptions
{
    /// <summary>
    /// How long a refused stop (ERROR_SERVICE_CANNOT_ACCEPT_CTRL / ERROR_INVALID_SERVICE_CONTROL: the service is
    /// START_PENDING or accepts no controls, e.g. Caddy while it reloads) is retried before the fallback.
    /// </summary>
    public TimeSpan RefusedGrace { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Asks the service to exit by other means before its process is killed (e.g. POST /stop to Caddy's admin API).</summary>
    public Func<CancellationToken, Task>? Fallback { get; init; }
    /// <summary>How long to wait for the process to exit after the fallback.</summary>
    public TimeSpan FallbackWait { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>
    /// Disable the recovery actions for the whole stop (restored afterwards), not only around the fallback/kill: a process
    /// that exits before it reports SERVICE_STOPPED counts as failed and would be restarted by the SCM.
    /// </summary>
    public bool SuppressRecovery { get; init; }
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
    // sc.exe exits with the Win32 error of the failing SCM call (not documented for sc.exe itself; asserted against the
    // real SCM by WindowsServiceManagerTests on the Windows CI runner). Codes:
    // https://learn.microsoft.com/en-us/windows/win32/debug/system-error-codes--1000-1299-
    private const int ErrorServiceExists = 1073;
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceMarkedForDelete = 1072;
    private const int ErrorServiceNotActive = 1062;
    // ControlService refuses a stop with 1061 ERROR_SERVICE_CANNOT_ACCEPT_CTRL (state START_PENDING / STOP_PENDING) or
    // 1052 ERROR_INVALID_SERVICE_CONTROL (the service accepts no stop right now, e.g. Caddy's StartPending with Accepts 0).
    // https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-controlservice
    private const int ErrorServiceCannotAcceptCtrl = 1061;
    private const int ErrorInvalidServiceControl = 1052;
    /// <summary>Time for the SCM to finish handling a terminated service process before recovery actions are restored.</summary>
    private static readonly TimeSpan RecoverySettle = TimeSpan.FromSeconds(1);

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

    /// <summary>
    /// Runs sc.exe with the given arguments (each element is one argv entry; .NET applies the quoting, which matches the
    /// CRT parsing sc.exe uses: an embedded quote becomes \" so binPath= "\"C:\a b\x.exe\" run" reaches sc.exe intact).
    /// sc.exe writes its messages in the OEM code page.
    /// </summary>
    public static Task<ProcessResult> ScAsync(IEnumerable<string> args, CancellationToken ct = default) =>
        ProcessRunner.RunAsync(ScExe, args, new ProcessOptions { Timeout = TimeSpan.FromSeconds(60), OutputEncoding = ProcessRunner.OemEncoding }, ct);

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

        // `sc config start= auto` is not documented to clear an existing "Automatic (Delayed Start)" flag
        // (https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/sc-config). Clear it explicitly so a
        // repair converges instead of reporting "Repaired ... Automatic (Delayed Start)" on every call. Asserted against the
        // real SCM by WindowsPlatformE2ETests (the artifact records whether sc.exe alone cleared it).
        if (def.StartType == "auto" && ReadRegistry(def.Name) is { DelayedAutoStart: true })
            ServiceNative.SetDelayedAutoStart(def.Name, false);

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

    /// <summary>
    /// State, PID and exit codes of the service (QueryServiceStatusEx; sc.exe output is localised). Null when the service
    /// does not exist or cannot be queried.
    /// </summary>
    public static Task<ScQueryResult?> QueryExAsync(string name, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try { return Task.FromResult(ServiceNative.QueryStatus(name)); }
        catch (System.ComponentModel.Win32Exception) { return Task.FromResult<ScQueryResult?>(null); }
    }

    /// <summary>Recovery actions (QueryServiceConfig2 SERVICE_CONFIG_FAILURE_ACTIONS). Null when missing or not queryable.</summary>
    public static Task<ScFailureConfig?> QueryFailureAsync(string name, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try { return Task.FromResult(ServiceNative.QueryFailureActions(name)); }
        catch (System.ComponentModel.Win32Exception) { return Task.FromResult<ScFailureConfig?>(null); }
    }

    /// <summary>The failure-actions flag (`sc failureflag`); null when missing or not queryable.</summary>
    public static bool? QueryFailureActionsFlag(string name)
    {
        try { return ServiceNative.QueryFailureActionsFlag(name); }
        catch (System.ComponentModel.Win32Exception) { return null; }
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

    /// <summary>
    /// Starts the service and waits until it is Running. Throws with a descriptive message when it stops again.
    /// <paramref name="isServing"/> (optional) is asked while the service is START_PENDING: when it returns true the start
    /// counts as done although the SCM has not seen RUNNING yet (Caddy v2.11.4 can stay START_PENDING while it serves,
    /// https://github.com/caddyserver/caddy/pull/8012).
    /// </summary>
    public static async Task<ServiceStartOutcome> StartAsync(string name, TimeSpan timeout, CancellationToken ct = default,
        Func<CancellationToken, Task<bool>>? isServing = null)
    {
        using var sc = new ServiceController(name);
        sc.Refresh();
        if (sc.Status == ServiceControllerStatus.Running) return ServiceStartOutcome.AlreadyRunning;
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
                    return ServiceStartOutcome.Running;
                case ServiceControllerStatus.StartPending:
                    sawPending = true;
                    if (isServing is not null && await isServing(ct))
                    {
                        sc.Refresh();
                        return sc.Status == ServiceControllerStatus.Running ? ServiceStartOutcome.Running : ServiceStartOutcome.ServingWhileStartPending;
                    }
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

    /// <summary>
    /// Stops the service and returns only after the service process has exited: a service may report SERVICE_STOPPED while
    /// its process is still shutting down (Caddy does), and until it is gone its executable, log files and ports are in use.
    /// Bounded in every case: a stop the SCM refuses (1061/1052, e.g. a service stuck in START_PENDING) is retried for
    /// <see cref="ServiceStopOptions.RefusedGrace"/>; a refused or timed-out stop then runs the caller's fallback and finally
    /// terminates the service process. The recovery actions are disabled while the process is made to exit that way (a
    /// process that ends without SERVICE_STOPPED counts as failed and would be restarted by the SCM) and restored afterwards.
    /// </summary>
    public static async Task<ServiceStopPath> StopAsync(string name, TimeSpan timeout, CancellationToken ct = default,
        ServiceStopOptions? options = null)
    {
        options ??= new ServiceStopOptions();
        using var sc = new ServiceController(name);
        sc.Refresh();
        if (sc.Status == ServiceControllerStatus.Stopped) return ServiceStopPath.AlreadyStopped;
        using var recovery = options.SuppressRecovery ? SuppressRecovery(name) : null;
        // Hold a handle to the service process before stopping it (also protects against PID reuse).
        using var process = await OpenServiceProcessAsync(name, ct);
        var path = await StopCoreAsync(name, sc, process, timeout, options, ct);
        if (recovery is not null) await Task.Delay(RecoverySettle, ct);
        return path;
    }

    private static async Task<ServiceStopPath> StopCoreAsync(string name, ServiceController sc, System.Diagnostics.Process? process,
        TimeSpan timeout, ServiceStopOptions options, CancellationToken ct)
    {
        DateTime? refusedSince = null;
        while (sc.Status is not (ServiceControllerStatus.StopPending or ServiceControllerStatus.Stopped))
        {
            try
            {
                sc.Stop(stopDependentServices: true);
                break;
            }
            catch (InvalidOperationException ex) when (Win32Error(ex) == ErrorServiceNotActive)
            {
                await WaitForProcessExitAsync(process, TimeSpan.FromSeconds(15), ct);
                return ServiceStopPath.Stopped;
            }
            catch (InvalidOperationException ex) when (Win32Error(ex) is ErrorServiceCannotAcceptCtrl or ErrorInvalidServiceControl)
            {
                refusedSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - refusedSince >= options.RefusedGrace)
                    return await ForceStopAsync(name, sc, process, options, ct);
                await Task.Delay(500, ct);
                sc.Refresh();
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
            return await ForceStopAsync(name, sc, process, options, ct);
        }
        await WaitForProcessExitAsync(process, TimeSpan.FromSeconds(15), ct);
        return ServiceStopPath.Stopped;
    }

    private static int? Win32Error(InvalidOperationException ex) => (ex.InnerException as System.ComponentModel.Win32Exception)?.NativeErrorCode;

    /// <summary>Fallback, then kill; the recovery actions are disabled meanwhile. Bounded: at most FallbackWait + ~40 s.</summary>
    private static async Task<ServiceStopPath> ForceStopAsync(string name, ServiceController sc, System.Diagnostics.Process? process,
        ServiceStopOptions options, CancellationToken ct)
    {
        using var recovery = SuppressRecovery(name);
        var victim = process ?? await OpenServiceProcessAsync(name, ct);
        try
        {
            if (options.Fallback is { } fallback && victim is not { HasExited: true })
            {
                try
                {
                    await fallback(ct).WaitAsync(options.FallbackWait, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    // The fallback is best effort; the kill below still ends the process.
                }
                if (await ExitedWithinAsync(victim, sc, options.FallbackWait, ct))
                    return await FinishForcedStopAsync(sc, recovery, ServiceStopPath.Fallback, ct);
            }
            if (victim is null)
            {
                sc.Refresh();
                if (sc.Status == ServiceControllerStatus.Stopped) return ServiceStopPath.Stopped;
                throw new InvalidOperationException(
                    $"Service '{name}' does not accept a stop (state: {sc.Status}) and its process could not be opened to end it. " +
                    "End the process in Task Manager (Details, column PID from `sc queryex`), then try again.");
            }
            try
            {
                if (!victim.HasExited) victim.Kill(entireProcessTree: true);
                await victim.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
            }
            // Already gone, or still exiting (then the STOPPED wait below decides).
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException) { }
            return await FinishForcedStopAsync(sc, recovery, ServiceStopPath.Killed, ct);
        }
        finally
        {
            if (!ReferenceEquals(victim, process)) victim?.Dispose();
        }
    }

    private static async Task<ServiceStopPath> FinishForcedStopAsync(ServiceController sc, IDisposable? recovery, ServiceStopPath path, CancellationToken ct)
    {
        await WaitForAsync(sc, s => s == ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15), ct);
        // The SCM handles the process exit (and would schedule the recovery action) right after it; restore only then.
        if (recovery is not null) await Task.Delay(RecoverySettle, ct);
        return path;
    }

    /// <summary>True when the process exited (or, without a process handle, the service reached STOPPED) within the time.</summary>
    private static async Task<bool> ExitedWithinAsync(System.Diagnostics.Process? p, ServiceController sc, TimeSpan wait, CancellationToken ct)
    {
        if (p is not null)
        {
            try
            {
                await p.WaitForExitAsync(ct).WaitAsync(wait, ct);
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
        try
        {
            await WaitForAsync(sc, s => s == ServiceControllerStatus.Stopped, wait, ct);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Replaces the recovery actions with NONE until disposed (then restores them exactly). Null when there is nothing to
    /// suppress or the actions cannot be read/changed; the next service repair (every manager start) re-applies them anyway.
    /// </summary>
    private static IDisposable? SuppressRecovery(string name)
    {
        try
        {
            var original = ServiceNative.QueryFailureActions(name);
            if (original is null || original.Actions.All(a => a.Item1 == "NONE")) return null;
            ServiceNative.SetFailureActions(name, original.ResetPeriodSeconds, [("NONE", 0)]);
            return new RecoveryRestore(name, original);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private sealed class RecoveryRestore(string name, ScFailureConfig original) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 1) return;
            try
            {
                ServiceNative.SetFailureActions(name, original.ResetPeriodSeconds, original.Actions);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Best effort: CreateOrRepairAsync (run at every manager start) sets the recovery actions again.
            }
        }
    }

    /// <summary>
    /// Opens the process of an own-process service (null when it has none, shares a svchost process, is not the service's
    /// executable or is not accessible; callers then skip the exit wait). .NET enables SeDebugPrivilege for the process
    /// the first time it opens another one (ProcessManager's static constructor), so an elevated admin can open a
    /// LocalSystem service process.
    /// </summary>
    private static async Task<System.Diagnostics.Process?> OpenServiceProcessAsync(string name, CancellationToken ct)
    {
        System.Diagnostics.Process? p = null;
        try
        {
            var q = await QueryExAsync(name, ct);
            // Never wait for / kill a shared svchost process: it hosts other services.
            if (q is not { IsOwnProcess: true, ProcessId: int pid }) return null;
            p = System.Diagnostics.Process.GetProcessById(pid);
            _ = p.Handle; // open the handle now, while the PID still belongs to the service
            // In START_PENDING / STOP_PENDING "the process identifier may not be valid" (QueryServiceStatusEx, Remarks:
            // https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-queryservicestatusex). This is exactly
            // when a refused stop ends up killing the process, so only accept a process running the service's executable.
            // ProcessName is the image file name without ".exe" (runtime ProcessManager.Win32.cs GetProcessShortName).
            var image = ImageFileName(ReadRegistry(name)?.ImagePath);
            if (image is not null)
            {
                var expected = image.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? image[..^4] : image;
                if (!string.Equals(p.ProcessName, expected, StringComparison.OrdinalIgnoreCase))
                {
                    p.Dispose();
                    return null;
                }
            }
            return p;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            p?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// File name of the executable in a service ImagePath ("\"C:\a b\caddy.exe\" run ..." → "caddy.exe"); null when empty.
    /// An unquoted path is taken up to ".exe", else up to the first space (the services this product registers are quoted).
    /// </summary>
    public static string? ImageFileName(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return null;
        var s = imagePath.Trim();
        string exe;
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            if (end <= 1) return null;
            exe = s[1..end];
        }
        else
        {
            var dot = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            var space = s.IndexOf(' ');
            exe = dot > 0 ? s[..(dot + 4)] : space > 0 ? s[..space] : s;
        }
        var slash = exe.LastIndexOfAny(['\\', '/']);
        var file = slash >= 0 ? exe[(slash + 1)..] : exe;
        return file.Length == 0 ? null : file;
    }

    /// <summary>Waits for a (stopped) service process to exit; terminates it when it lingers past the grace period.</summary>
    private static async Task WaitForProcessExitAsync(System.Diagnostics.Process? p, TimeSpan grace, CancellationToken ct)
    {
        if (p is null) return;
        try
        {
            if (p.HasExited) return;
            try
            {
                await p.WaitForExitAsync(ct).WaitAsync(grace, ct);
                return;
            }
            catch (TimeoutException)
            {
                p.Kill(entireProcessTree: true);
            }
            await p.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            // gone already, or not ours to wait for
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
