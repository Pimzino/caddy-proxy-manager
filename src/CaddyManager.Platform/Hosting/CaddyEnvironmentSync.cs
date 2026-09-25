using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Platform.Hosting;

/// <summary>
/// Brings the environment Caddy runs with (XDG paths + HTTPS_PROXY/HTTP_PROXY/NO_PROXY) in line with the settings:
/// rewrites the Windows service's Environment value (or detects that the dev child process is outdated) and restarts
/// Caddy when it is running with an outdated environment.
/// </summary>
public sealed class CaddyEnvironmentSync(
    ICaddyHost host,
    CaddyHostSupport support,
    IServiceProvider services,
    ILogger<CaddyEnvironmentSync> logger)
{
    /// <summary>Human readable description of how Caddy reaches the Internet with the given settings.</summary>
    public static string Describe(BinarySettings settings) =>
        CaddyHostSupport.CaddyProxy(settings) is { } proxy
            ? $"Caddy uses the outbound proxy {Infrastructure.OutboundHttp.RedactProxy(proxy)} (NO_PROXY: {CaddyHostSupport.NormalizeNoProxy(settings.NoProxy)})"
            : "Caddy connects directly (no proxy)";

    /// <summary>Applies the current environment. Never throws for operational problems; returns a message for the admin.</summary>
    public async Task<string> ApplyAsync(CancellationToken ct = default)
    {
        var settings = support.BinarySettings ?? new BinarySettings();
        var what = Describe(settings);
        CaddyStatus status;
        try
        {
            status = await host.GetStatusAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"{what}. The Caddy status could not be read ({ex.Message}); restart Caddy to apply the change.";
        }
        if (!status.BinaryInstalled)
            return $"{what}. Caddy is not installed yet; the setting is used when it is installed.";

        bool outdated;
        try
        {
            if (OperatingSystem.IsWindows() && host is WindowsServiceCaddyHost windows)
            {
                var changes = await windows.RepairServiceAsync(ct);
                outdated = changes.Any(WindowsServiceCaddyHost.IsEnvironmentChange);
            }
            else if (host is ProcessCaddyHost process)
            {
                outdated = process.EnvironmentOutdated;
            }
            else
            {
                return $"{what}. Restart Caddy to apply the change.";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Updating the Caddy service environment failed");
            Sink?.Raise(EventSeverity.Warning, "caddy", "Updating the Caddy service environment (outbound proxy) failed", ex.Message,
                key: "caddy-environment", alertRule: "caddyDown");
            return $"{what}, but updating the Caddy service environment failed: {ex.Message}";
        }

        var running = status.State is CaddyRunState.Running or CaddyRunState.Starting;
        if (!outdated)
            return running ? $"{what}. Caddy already runs with this environment." : $"{what}. Caddy is stopped and uses this when it starts.";
        if (!running)
            return $"{what}. The Caddy service environment was updated; Caddy is stopped and uses it when it starts.";
        if (support.IsBinarySwapInProgress)
            return $"{what}. A Caddy binary update is running; it restarts Caddy with the new environment.";

        try
        {
            logger.LogInformation("Restarting Caddy to apply its new environment: {What}", what);
            await host.RestartAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Restarting Caddy after an environment change failed");
            Sink?.Raise(EventSeverity.Error, "caddy", "Caddy did not restart after its outbound proxy settings changed", ex.Message,
                key: "caddy-down", alertRule: "caddyDown");
            return $"{what}. The environment was updated, but restarting Caddy failed: {ex.Message}";
        }
        Sink?.Raise(EventSeverity.Info, "caddy", "Caddy was restarted to apply its outbound proxy settings", what);
        services.GetService<IAuditLog>()?.Record("restarted", "caddy", AppPaths.CaddyServiceName, "Caddy", $"Environment changed: {what}");
        return $"{what}. Caddy was restarted to use it.";
    }

    private IEventSink? Sink => services.GetService<IEventSink>();
}
