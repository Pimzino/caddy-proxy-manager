using System.Text.Json;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using CaddyManager.Platform.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Platform.Background;

/// <summary>
/// Checks GitHub for a newer Caddy release every BinarySettings.CheckIntervalHours. Raises one
/// "update-available:&lt;ver&gt;" event per new version (alert rule "updateAvailable") and optionally
/// installs it (BinarySettings.AutoInstallUpdates).
/// </summary>
public sealed class UpdateChecker(
    AppPaths paths,
    IStore store,
    CaddyBinaryManager binary,
    IServiceProvider services,
    ILogger<UpdateChecker> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(15);

    private sealed record State(string? NotifiedVersion, string? AutoInstalledVersion);

    private string StateFile => Path.Combine(paths.DataDir, "caddy", "update-state.json");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckOnceAsync(force: false, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogWarning("Caddy update check failed: {Error}", ex.Message);
                }
                await Task.Delay(Tick, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Runs one check if due (or always when <paramref name="force"/>). Returns the newer version found, if any.</summary>
    public async Task<string?> CheckOnceAsync(bool force, CancellationToken ct)
    {
        var settings = store.GetSettings<BinarySettings>();
        if (!settings.AutoCheckUpdates && !force) return null;
        var interval = TimeSpan.FromHours(Math.Clamp(settings.CheckIntervalHours, 1, 168));
        if (!force && settings.LastCheckedAt is { } last && DateTime.UtcNow - last < interval) return null;

        var latest = await binary.GetLatestAsync(force: true, ct);
        var installed = await binary.GetInstalledAsync(ct);
        if (latest is null || installed is null || !CaddyVersion.IsNewer(latest.Version, installed.Version)) return null;

        var state = ReadState();
        if (!string.Equals(state.NotifiedVersion, latest.Version, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Caddy {Latest} is available (installed: {Installed})", latest.Version, installed.Version);
            services.GetService<IEventSink>()?.Raise(EventSeverity.Info, "update",
                $"Caddy {latest.Version} is available (installed: {installed.Version})",
                $"Release notes: {latest.Url}" + (settings.AutoInstallUpdates ? "\nAutomatic installation is enabled." : "\nInstall it from the Caddy page."),
                key: $"update-available:{latest.Version}", alertRule: "updateAvailable");
            state = state with { NotifiedVersion = latest.Version };
            WriteState(state);
        }

        if (settings.AutoInstallUpdates && !string.Equals(state.AutoInstalledVersion, latest.Version, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var job = binary.StartInstall(latest.Version, pluginsOverride: null, "auto-update");
                WriteState(state with { AutoInstalledVersion = latest.Version });
                services.GetService<IAuditLog>()?.Record("auto-update", "caddy", details: $"Automatic update to {latest.Version} started (job {job.Id})");
                logger.LogInformation("Automatic update to Caddy {Version} started (job {Job})", latest.Version, job.Id);
            }
            catch (InvalidOperationException ex)
            {
                logger.LogInformation("Automatic update postponed: {Reason}", ex.Message);
            }
        }
        return latest.Version;
    }

    private State ReadState()
    {
        try
        {
            return File.Exists(StateFile)
                ? JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile), JsonDefaults.Storage) ?? new State(null, null)
                : new State(null, null);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read {File}", StateFile);
            return new State(null, null);
        }
    }

    private void WriteState(State state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(state, JsonDefaults.Storage));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not write {File}", StateFile);
        }
    }
}
