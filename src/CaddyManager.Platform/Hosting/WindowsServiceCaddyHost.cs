using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;
using TimeoutException = System.TimeoutException;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using CaddyManager.Platform.Windows;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Platform.Hosting;

/// <summary>
/// Production host: Caddy runs as its own Windows service "Caddy" (LocalSystem, automatic start,
/// restart on failure) so it keeps serving even while the manager is stopped or updated.
///
/// Caddy v2.11.4 can stay in START_PENDING although it serves: runner.Execute reports StartPending, and the
/// notify.Ready() that caddy.Run sent before Execute registered its status channel is lost (open upstream PR
/// https://github.com/caddyserver/caddy/pull/8012; https://github.com/caddyserver/caddy/blob/v2.11.4/service_windows.go,
/// notify/notify_windows.go). The SCM then refuses every stop (1061/1052). This host therefore treats "the admin API
/// answers" as started, nudges the SCM to RUNNING by re-posting the unchanged config to /load (caddy.Load always ends with
/// notify.Ready(), https://github.com/caddyserver/caddy/blob/v2.11.4/caddy.go), and stops a service that still refuses
/// through the admin API's /stop and, as the last resort, by ending its process (with the recovery actions suspended).
/// Proven by CaddyServiceStartPendingE2ETests on the Windows CI runner.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsServiceCaddyHost(AppPaths paths, CaddyHostSupport support, ILogger<WindowsServiceCaddyHost> logger) : ICaddyHost
{
    public const string Mode = "windows-service";
    public const string DisplayName = "Caddy (managed by Caddy Proxy Manager)";
    public const string Description =
        "Caddy web server / reverse proxy. Configuration is generated and applied by Caddy Proxy Manager; do not edit caddy.json by hand.";

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);
    /// <summary>How long the nudge waits for the SCM to report RUNNING once the admin API answers.</summary>
    private static readonly TimeSpan NudgeTimeout = TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _lastError;
    /// <summary>Set after this host stopped the service (possibly by ending its process): the exit code is then not an error.</summary>
    private volatile bool _stoppedByManager;
    private DateTime _lastPendingWarning = DateTime.MinValue;

    public string HostMode => Mode;

    /// <summary>The Windows service name (tests use a throw-away service).</summary>
    internal string ServiceName { get; init; } = AppPaths.CaddyServiceName;

    /// <summary>
    /// A START_PENDING service whose admin API has not answered for this long after its process started counts as hung:
    /// the status becomes Unknown (so the monitor alerts) and StartAsync replaces the process.
    /// </summary>
    internal TimeSpan StuckStartGrace { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>What the last StartAsync did (for logs and the E2E artifacts).</summary>
    internal StartInfo? LastStart { get; private set; }

    /// <summary>How the last StopAsync stopped the service.</summary>
    internal ServiceStopPath? LastStop { get; private set; }

    /// <param name="Outcome">How the SCM start ended.</param>
    /// <param name="Reposted">The config had to be re-posted because RUNNING did not follow by itself (the #8012 race).</param>
    /// <param name="ReachedRunning">The SCM reports RUNNING at the end.</param>
    /// <param name="ReplacedHungInstance">A START_PENDING instance without admin API was ended first.</param>
    internal sealed record StartInfo(ServiceStartOutcome Outcome, bool Reposted, bool ReachedRunning, bool ReplacedHungInstance);

    /// <summary>Desired service configuration; the environment includes the proxy variables when Caddy should use the proxy.</summary>
    public static ServiceDefinition Definition(AppPaths paths, BinarySettings? binary = null, string name = AppPaths.CaddyServiceName) => new()
    {
        Name = name,
        DisplayName = DisplayName,
        Description = Description,
        BinaryPathName = $"\"{paths.CaddyExe}\" run --config \"{paths.CaddyConfigFile}\"",
        StartType = "auto",
        RestartDelaysMs = [5000, 5000, 30000],
        FailureResetSeconds = 86400,
        Environment = CaddyHostSupport.CaddyEnvironment(paths, binary).Select(kv => $"{kv.Key}={kv.Value}").ToList(),
    };

    public async Task<CaddyStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var binary = File.Exists(paths.CaddyExe);
        var reg = WindowsServiceManager.ReadRegistry(ServiceName);
        var status = reg is null ? null : WindowsServiceManager.GetStatus(ServiceName);
        var installed = status is not null;
        var adminReachable = await support.IsAdminReachableAsync(ct);
        // Started again since this host stopped it (by the SCM at boot, by hand): its next exit code matters again.
        if (status is not (null or ServiceControllerStatus.Stopped)) _stoppedByManager = false;

        int? pid = null;
        DateTime? startedAt = null;
        var lastError = _lastError;
        if (installed && status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending
                or ServiceControllerStatus.StopPending)
        {
            (pid, startedAt) = await ServiceProcessAsync(ct);
        }
        else if (installed && status == ServiceControllerStatus.Stopped && lastError is null && !_stoppedByManager)
        {
            try
            {
                var q = await WindowsServiceManager.QueryExAsync(ServiceName, ct);
                if (q is { Win32ExitCode: not 0 and not 1077 })
                    lastError = $"The Caddy service last stopped with Win32 exit code {q.Win32ExitCode} (service exit code {q.ServiceExitCode}).";
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
            {
                logger.LogDebug(ex, "sc queryex failed");
            }
        }

        var state = !binary || !installed ? CaddyRunState.NotInstalled : status switch
        {
            ServiceControllerStatus.Running => CaddyRunState.Running,
            ServiceControllerStatus.Stopped => CaddyRunState.Stopped,
            // Serving although the SCM never saw RUNNING (caddy PR #8012): running for the UI and the monitor.
            ServiceControllerStatus.StartPending when adminReachable => CaddyRunState.Running,
            ServiceControllerStatus.StartPending when startedAt is { } t && DateTime.UtcNow - t > StuckStartGrace => CaddyRunState.Unknown,
            ServiceControllerStatus.StartPending or ServiceControllerStatus.ContinuePending => CaddyRunState.Starting,
            ServiceControllerStatus.StopPending or ServiceControllerStatus.PausePending => CaddyRunState.Stopping,
            _ => CaddyRunState.Unknown,
        };
        if (state == CaddyRunState.Running && status == ServiceControllerStatus.StartPending && DateTime.UtcNow - _lastPendingWarning > TimeSpan.FromMinutes(30))
        {
            _lastPendingWarning = DateTime.UtcNow;
            logger.LogWarning("Caddy serves, but its Windows service still reports START_PENDING (Caddy issue #8012). " +
                              "The manager handles it on the next start/stop; `sc stop {Name}` is refused until then", ServiceName);
        }
        if (state == CaddyRunState.Unknown && status == ServiceControllerStatus.StartPending)
            lastError = $"The Caddy service has been in START_PENDING for more than {StuckStartGrace.TotalMinutes:0.#} minute(s) and its " +
                        "admin API does not answer. Start (or restart) Caddy: the manager ends the hung process and starts it again." +
                        (lastError is null ? "" : "\n" + lastError);

        return new CaddyStatus
        {
            BinaryInstalled = binary,
            ServiceInstalled = installed,
            State = state,
            ProcessId = pid,
            Version = binary ? await support.GetVersionAsync(ct) : null,
            AdminReachable = adminReachable,
            StartedAt = startedAt,
            BinaryPath = paths.CaddyExe,
            ConfigPath = paths.CaddyConfigFile,
            HostMode = Mode,
            ServiceStartType = reg?.StartTypeDisplay,
            LastError = lastError,
        };
    }

    /// <summary>PID and start time (UTC) of the service process; nulls when it has none or cannot be opened.</summary>
    private async Task<(int? Pid, DateTime? StartedAt)> ServiceProcessAsync(CancellationToken ct)
    {
        int? pid = null;
        try
        {
            var q = await WindowsServiceManager.QueryExAsync(ServiceName, ct);
            pid = q?.ProcessId;
            if (pid is int id)
            {
                using var p = Process.GetProcessById(id);
                return (pid, p.StartTime.ToUniversalTime());
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogDebug(ex, "Could not query the Caddy service process");
        }
        return (pid, null);
    }

    public async Task InstallServiceAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await InstallCoreAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Registers or repairs the service like <see cref="InstallServiceAsync"/> and returns the changes made. A change of the
    /// environment only takes effect when the service restarts; callers restart Caddy when it is running.
    /// </summary>
    public async Task<IReadOnlyList<string>> RepairServiceAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await InstallCoreAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Prefix of the change message WindowsServiceManager reports when the Environment value was rewritten.</summary>
    public static bool IsEnvironmentChange(string change) => change.StartsWith("Updated environment", StringComparison.Ordinal);

    private async Task<List<string>> InstallCoreAsync(CancellationToken ct)
    {
        if (!File.Exists(paths.CaddyExe))
            throw new InvalidOperationException($"The Caddy binary is not installed yet ({paths.CaddyExe}). Install Caddy first, then register the service.");
        support.EnsureDirectories();
        support.EnsureBootConfig();
        var changes = await WindowsServiceManager.CreateOrRepairAsync(Definition(paths, support.BinarySettings, ServiceName), ct);
        foreach (var c in changes) logger.LogInformation("{Change}", c);
        return changes;
    }

    public async Task UninstallServiceAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await WindowsServiceManager.DeleteAsync(ServiceName, ct);
            logger.LogInformation("Removed the Windows service '{Name}'", ServiceName);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        support.ThrowIfBinarySwapInProgress();
        await _gate.WaitAsync(ct);
        try
        {
            if (WindowsServiceManager.GetStatus(ServiceName) is null)
            {
                logger.LogInformation("The Caddy service is not registered; registering it now");
                await InstallCoreAsync(ct);
            }
            else
            {
                support.EnsureBootConfig();
            }
            _lastError = null;

            var replaced = false;
            if (WindowsServiceManager.GetStatus(ServiceName) == ServiceControllerStatus.StartPending)
            {
                if (await support.IsAdminReachableAsync(ct))
                {
                    // Left over from an earlier start (or started by the SCM at boot): it serves, only the SCM state is stuck.
                    var (reached, reposted) = await NudgeToRunningAsync(ct);
                    LastStart = new StartInfo(ServiceStartOutcome.ServingWhileStartPending, reposted, reached, false);
                    _stoppedByManager = false;
                    return;
                }
                var (_, startedAt) = await ServiceProcessAsync(ct);
                if (startedAt is { } t && DateTime.UtcNow - t > StuckStartGrace)
                {
                    logger.LogWarning("The Caddy service has been in START_PENDING since {Started:u} and its admin API does not answer; ending the hung process", t);
                    LastStop = await WindowsServiceManager.StopAsync(ServiceName, StopTimeout, ct, StopOptions(TimeSpan.FromSeconds(3)));
                    replaced = true;
                }
            }

            // "The admin API answers" only proves that this service serves when nothing answered there before the start
            // (another Caddy, e.g. a leftover console instance, would otherwise be taken for it).
            Func<CancellationToken, Task<bool>>? serving = support.IsAdminReachableAsync;
            if (WindowsServiceManager.GetStatus(ServiceName) == ServiceControllerStatus.Stopped && await support.IsAdminReachableAsync(ct))
            {
                logger.LogWarning("Something already answers on Caddy's admin address before the service starts; waiting for the SCM to report RUNNING instead");
                serving = null;
            }

            var outcome = await WindowsServiceManager.StartAsync(ServiceName, StartTimeout, ct, serving);
            if (outcome == ServiceStartOutcome.ServingWhileStartPending)
            {
                var (reached, reposted) = await NudgeToRunningAsync(ct);
                LastStart = new StartInfo(outcome, reposted, reached, replaced);
            }
            else
            {
                LastStart = new StartInfo(outcome, false, true, replaced);
                // The service is "Running" as soon as Caddy has loaded its config; give the admin API a moment anyway.
                await support.WaitForAdminAsync(TimeSpan.FromSeconds(10),
                    () => WindowsServiceManager.GetStatus(ServiceName) == ServiceControllerStatus.Running, ct);
            }
            _stoppedByManager = false;
            logger.LogInformation("Caddy service started ({Outcome})", LastStart);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var tail = support.TailLog(8);
            _lastError = support.Scrub(ex.Message + (tail.Length > 0 ? "\nLast log lines:\n" + tail : ""));
            throw new InvalidOperationException(_lastError, ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Moves a serving service that is stuck in START_PENDING to RUNNING: waits a moment for Caddy's own notify.Ready(),
    /// then re-posts the running config unchanged to /load (see <see cref="CaddyHostSupport.ReloadUnchangedConfigAsync"/>).
    /// Returns whether the SCM reports RUNNING within <see cref="NudgeTimeout"/> and whether a re-post was needed.
    /// </summary>
    private async Task<(bool Reached, bool Reposted)> NudgeToRunningAsync(CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        var lastPost = DateTime.MinValue;
        var reposted = false;
        while (DateTime.UtcNow - start < NudgeTimeout)
        {
            var s = WindowsServiceManager.GetStatus(ServiceName);
            if (s == ServiceControllerStatus.Running) return (true, reposted);
            if (s != ServiceControllerStatus.StartPending) return (false, reposted);
            // Normally Ready() follows the admin API within milliseconds; nudge only when it does not come by itself.
            if (DateTime.UtcNow - start > TimeSpan.FromSeconds(1) && DateTime.UtcNow - lastPost > TimeSpan.FromSeconds(3))
            {
                lastPost = DateTime.UtcNow;
                reposted = true;
                logger.LogInformation("Caddy serves but its Windows service reports START_PENDING (Caddy issue #8012); re-posting the unchanged config so Caddy reports RUNNING");
                try
                {
                    await support.ReloadUnchangedConfigAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    logger.LogDebug(ex, "Posting the unchanged config to Caddy failed");
                }
            }
            await Task.Delay(250, ct);
        }
        logger.LogWarning("The Caddy service still reports START_PENDING although Caddy serves; it keeps serving, and the manager stops it through the admin API when needed");
        return (WindowsServiceManager.GetStatus(ServiceName) == ServiceControllerStatus.Running, reposted);
    }

    /// <summary>
    /// Stop options for the Caddy service: a refused stop falls back to POST /stop on the admin API (Caddy stops its apps and
    /// exits), then to ending the process; the recovery actions are suspended for the whole stop because Caddy's process can
    /// exit before the SCM has recorded SERVICE_STOPPED, which the SCM would treat as a crash and restart it after 5 s.
    /// </summary>
    private ServiceStopOptions StopOptions(TimeSpan refusedGrace) => new()
    {
        RefusedGrace = refusedGrace,
        FallbackWait = TimeSpan.FromSeconds(10),
        SuppressRecovery = true,
        Fallback = async c =>
        {
            if (support.Admin is not { } admin) return;
            logger.LogWarning("The Caddy service refuses the stop request; asking Caddy to exit through its admin API");
            await admin.StopAsync(c);
        },
    };

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (WindowsServiceManager.GetStatus(ServiceName) is null) return;
            var refusedGrace = TimeSpan.FromSeconds(10);
            if (WindowsServiceManager.GetStatus(ServiceName) == ServiceControllerStatus.StartPending && await support.IsAdminReachableAsync(ct))
            {
                // A service that reached RUNNING stops cleanly through the SCM (SERVICE_STOPPED, exit code 0).
                if (!(await NudgeToRunningAsync(ct)).Reached) refusedGrace = TimeSpan.FromSeconds(3); // no need to wait much longer
            }
            LastStop = await WindowsServiceManager.StopAsync(ServiceName, StopTimeout, ct, StopOptions(refusedGrace));
            _lastError = null;
            _stoppedByManager = true;
            if (LastStop is ServiceStopPath.Fallback or ServiceStopPath.Killed)
                logger.LogWarning("Caddy service stopped ({Path}): the service did not accept the stop request", LastStop);
            else
                logger.LogInformation("Caddy service stopped");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RestartAsync(CancellationToken ct = default)
    {
        await StopAsync(ct);
        await StartAsync(ct);
    }
}
