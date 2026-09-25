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
/// registered/repaired → Caddy started → current config applied.
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
                sink?.Raise(EventSeverity.Error, "caddy",
                    "Caddy is not installed and the automatic installation failed",
                    "Open the Caddy page to retry the installation. If the server has no Internet access, configure an outbound proxy in Settings → Updates or copy caddy.exe to " + paths.CaddyExe + ".",
                    key: "caddy-not-installed", alertRule: "caddyDown");
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
        if (host.HostMode == Hosting.WindowsServiceCaddyHost.Mode)
        {
            try
            {
                await host.InstallServiceAsync(ct);
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
            if (status.State is not (CaddyRunState.Running or CaddyRunState.Starting))
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
