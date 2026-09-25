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
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _lastError;

    public string HostMode => Mode;

    /// <summary>Desired service configuration; the environment includes the proxy variables when Caddy should use the proxy.</summary>
    public static ServiceDefinition Definition(AppPaths paths, BinarySettings? binary = null) => new()
    {
        Name = AppPaths.CaddyServiceName,
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
        var reg = WindowsServiceManager.ReadRegistry(AppPaths.CaddyServiceName);
        var status = reg is null ? null : WindowsServiceManager.GetStatus(AppPaths.CaddyServiceName);
        var installed = status is not null;

        int? pid = null;
        DateTime? startedAt = null;
        var lastError = _lastError;
        if (installed && status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending
                or ServiceControllerStatus.StopPending)
        {
            try
            {
                var q = await WindowsServiceManager.QueryExAsync(AppPaths.CaddyServiceName, ct);
                pid = q?.ProcessId;
                if (pid is int id)
                {
                    using var p = Process.GetProcessById(id);
                    startedAt = p.StartTime.ToUniversalTime();
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                logger.LogDebug(ex, "Could not query the Caddy service process");
            }
        }
        else if (installed && status == ServiceControllerStatus.Stopped && lastError is null)
        {
            try
            {
                var q = await WindowsServiceManager.QueryExAsync(AppPaths.CaddyServiceName, ct);
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
            ServiceControllerStatus.StartPending or ServiceControllerStatus.ContinuePending => CaddyRunState.Starting,
            ServiceControllerStatus.StopPending or ServiceControllerStatus.PausePending => CaddyRunState.Stopping,
            _ => CaddyRunState.Unknown,
        };

        return new CaddyStatus
        {
            BinaryInstalled = binary,
            ServiceInstalled = installed,
            State = state,
            ProcessId = pid,
            Version = binary ? await support.GetVersionAsync(ct) : null,
            AdminReachable = await support.IsAdminReachableAsync(ct),
            StartedAt = startedAt,
            BinaryPath = paths.CaddyExe,
            ConfigPath = paths.CaddyConfigFile,
            HostMode = Mode,
            ServiceStartType = reg?.StartTypeDisplay,
            LastError = lastError,
        };
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
        var changes = await WindowsServiceManager.CreateOrRepairAsync(Definition(paths, support.BinarySettings), ct);
        foreach (var c in changes) logger.LogInformation("{Change}", c);
        return changes;
    }

    public async Task UninstallServiceAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await WindowsServiceManager.DeleteAsync(AppPaths.CaddyServiceName, ct);
            logger.LogInformation("Removed the Windows service '{Name}'", AppPaths.CaddyServiceName);
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
            if (WindowsServiceManager.GetStatus(AppPaths.CaddyServiceName) is null)
            {
                logger.LogInformation("The Caddy service is not registered; registering it now");
                await InstallCoreAsync(ct);
            }
            else
            {
                support.EnsureBootConfig();
            }
            _lastError = null;
            await WindowsServiceManager.StartAsync(AppPaths.CaddyServiceName, StartTimeout, ct);
            logger.LogInformation("Caddy service started");
            // The service is "Running" as soon as caddy.exe starts; give the admin API a moment to come up.
            await support.WaitForAdminAsync(TimeSpan.FromSeconds(10),
                () => WindowsServiceManager.GetStatus(AppPaths.CaddyServiceName) == ServiceControllerStatus.Running, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var tail = support.TailLog(8);
            _lastError = ex.Message + (tail.Length > 0 ? "\nLast log lines:\n" + tail : "");
            throw new InvalidOperationException(_lastError, ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (WindowsServiceManager.GetStatus(AppPaths.CaddyServiceName) is null) return;
            await WindowsServiceManager.StopAsync(AppPaths.CaddyServiceName, StopTimeout, ct);
            _lastError = null;
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
