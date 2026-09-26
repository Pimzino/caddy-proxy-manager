using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using CaddyManager.Platform.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Platform.Background;

/// <summary>
/// Brings Caddy up when the manager starts, without blocking web host startup:
/// ensure binary (auto-install latest vanilla release when missing) → boot config → Windows service
/// registered/repaired (restarting a running Caddy when its environment changed) → Caddy started → current config applied.
/// After every later binary install (download, upload or rollback) <see cref="AfterInstallAsync"/> repeats the
/// service and apply steps, so an offline install through the UI completes what a failed bootstrap left undone.
/// Set CM_CADDY_AUTOSTART=0 to skip starting Caddy (the binary/service steps still run).
/// </summary>
public sealed class CaddyBootstrapper(
    AppPaths paths,
    CaddyBinaryManager binary,
    ICaddyHost host,
    IJobRunner jobs,
    IServiceProvider services,
    ILogger<CaddyBootstrapper> logger) : BackgroundService
{
    /// <summary>Completes when bootstrapping has finished (successfully or not). Used by tests and diagnostics.</summary>
    public Task Completion => _done.Task;
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _notInstalledRaised;

    public const string NotInstalledKey = "caddy-not-installed";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // never block host startup
        try
        {
            await RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Caddy bootstrap failed");
        }
        finally
        {
            _done.TrySetResult();
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var sink = services.GetService<IEventSink>();
        var config = services.GetService<ICaddyConfigService>();

        // 1. Binary
        if (!File.Exists(paths.CaddyExe))
        {
            if (!OperatingSystem.IsWindows() || !Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService())
            {
                try { binary.TryCopyDevBinary(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogWarning(ex, "Could not copy the development Caddy binary"); }
            }
        }
        if (!File.Exists(paths.CaddyExe))
        {
            logger.LogInformation("Caddy is not installed; installing the latest release automatically");
            if (!await InstallVanillaAsync(ct))
            {
                var platform = CaddyPlatform.Current;
                sink?.Raise(EventSeverity.Error, "caddy",
                    "Caddy is not installed and the automatic installation failed",
                    "Open Caddy → Service & Updates to retry the download. If this server has no Internet access, download " +
                    $"{platform.ReleaseAssetPattern} (or {CaddyPlatform.BinaryName}) " +
                    "from https://github.com/caddyserver/caddy/releases on another machine and install it with Upload on that page " +
                    "(optionally with the SHA-512 from caddy_<version>_checksums.txt); the manager then registers the service and applies " +
                    "the configuration automatically. Behind a proxy, set the outbound proxy in Settings → Updates.",
                    key: NotInstalledKey, alertRule: "caddyDown");
                _notInstalledRaised = true;
                return;
            }
        }

        // 2. Boot config
        if (config is not null)
        {
            try { config.EnsureBootConfig(); }
            catch (Exception ex) { logger.LogWarning(ex, "Could not ensure the Caddy boot config"); }
        }

        // 3. Windows service registered and correct (idempotent repair: binPath, start type, recovery, environment)
        var environmentChanged = false;
        if (OperatingSystem.IsWindows() && host is Hosting.WindowsServiceCaddyHost windowsHost)
        {
            try
            {
                var changes = await windowsHost.RepairServiceAsync(ct);
                environmentChanged = changes.Any(Hosting.WindowsServiceCaddyHost.IsEnvironmentChange);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Registering the Caddy Windows service failed");
                sink?.Raise(EventSeverity.Error, "caddy", "Registering the Caddy Windows service failed", ex.Message,
                    key: "caddy-service-install", alertRule: "caddyDown");
            }
        }

        // 4. Start
        var autostart = Environment.GetEnvironmentVariable("CM_CADDY_AUTOSTART");
        if (autostart is "0" or "false")
        {
            logger.LogInformation("CM_CADDY_AUTOSTART={Value}: not starting Caddy", autostart);
        }
        else
        {
            var status = await host.GetStatusAsync(ct);
            if (environmentChanged && status.State == CaddyRunState.Running)
            {
                try
                {
                    logger.LogInformation("The Caddy service environment changed (outbound proxy settings); restarting Caddy to apply it");
                    await host.RestartAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError("Restarting Caddy after its environment changed failed: {Error}", ex.Message);
                    sink?.Raise(EventSeverity.Error, "caddy", "Caddy failed to restart", ex.Message, key: "caddy-down", alertRule: "caddyDown");
                }
            }
            else if (status.State is not (CaddyRunState.Running or CaddyRunState.Starting))
            {
                try
                {
                    logger.LogInformation("Starting Caddy ({Mode})", host.HostMode);
                    await host.StartAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError("Caddy failed to start: {Error}", ex.Message);
                    sink?.Raise(EventSeverity.Error, "caddy", "Caddy failed to start", ex.Message, key: "caddy-down", alertRule: "caddyDown");
                }
            }
        }

        // 5. Apply the current configuration (writes the file only when Caddy is not running)
        if (config is not null)
        {
            try
            {
                var r = await config.ApplyAsync("startup", ct);
                if (r.Success) logger.LogInformation("Startup config applied{WrittenOnly}", r.WrittenOnly ? " (written only; Caddy not running)" : "");
                else logger.LogWarning("Applying the configuration at startup failed: {Error}", r.Error);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Applying the configuration at startup failed");
            }
        }
    }

    /// <summary>
    /// Runs inside every successful binary install job (download, upload, rollback) after the swap: boot config, Windows
    /// service registration/repair (restarting a running Caddy when its environment changed) and applying the current
    /// configuration. Clears the "Caddy is not installed" alert raised by a failed bootstrap.
    /// </summary>
    public async Task AfterInstallAsync(Action<string> log, CancellationToken ct)
    {
        var sink = services.GetService<IEventSink>();
        var config = services.GetService<ICaddyConfigService>();
        if (config is not null)
        {
            try { config.EnsureBootConfig(); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log("Warning: could not write the boot configuration: " + ex.Message); }
        }

        if (OperatingSystem.IsWindows() && host is Hosting.WindowsServiceCaddyHost windowsHost)
        {
            log($"Checking the Windows service '{AppPaths.CaddyServiceName}'");
            try
            {
                var changes = await windowsHost.RepairServiceAsync(ct);
                foreach (var c in changes) log(c);
                if (changes.Count == 0) log("The service registration is up to date");
                if (changes.Any(Hosting.WindowsServiceCaddyHost.IsEnvironmentChange)
                    && (await host.GetStatusAsync(ct)).State == CaddyRunState.Running)
                {
                    log("Restarting Caddy to apply its new service environment");
                    await host.RestartAsync(ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log("Warning: registering/repairing the Caddy service failed: " + ex.Message);
                sink?.Raise(EventSeverity.Error, "caddy", "Registering the Caddy Windows service failed", ex.Message,
                    key: "caddy-service-install", alertRule: "caddyDown");
            }
        }

        if (config is not null)
        {
            log("Applying the managed configuration");
            var r = await config.ApplyAsync("caddy installed", ct);
            log(r.Success
                ? r.WrittenOnly ? "Configuration written (Caddy is not running; it is loaded when Caddy starts)" : "Configuration applied"
                : $"Warning: applying the configuration failed: {r.Error}");
        }

        if (_notInstalledRaised)
        {
            _notInstalledRaised = false;
            sink?.Raise(EventSeverity.Recovered, "caddy", "Caddy is installed", null, key: NotInstalledKey, alertRule: "caddyDown");
        }
    }

    private async Task<bool> InstallVanillaAsync(CancellationToken ct)
    {
        JobInfo job;
        try
        {
            job = binary.StartInstall(null, pluginsOverride: [], "bootstrap");
        }
        catch (InvalidOperationException)
        {
            // Someone already started an install; wait for it.
            job = jobs.Recent().FirstOrDefault(j => j.Kind == CaddyBinaryManager.JobKind && j.State == JobState.Running)
                  ?? throw new InvalidOperationException("Install job vanished");
        }
        while (!ct.IsCancellationRequested)
        {
            var j = jobs.Get(job.Id);
            if (j is null || j.State != JobState.Running)
            {
                if (j?.State == JobState.Succeeded) return File.Exists(paths.CaddyExe);
                logger.LogError("Automatic Caddy installation failed: {Error}", j?.Error ?? "job not found");
                return false;
            }
            await Task.Delay(1000, ct);
        }
        return false;
    }
}
