using CaddyManager.Core;
using CaddyManager.Core.Models;
using CaddyManager.Ops.Auth;
using CaddyManager.Ops.Backup;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Ops;

/// <summary>One-time startup work: DB indexes, first-run setup token, restore status reporting.</summary>
internal sealed class OpsStartupService(IStore store, AppPaths paths, SetupState setup, IAuditLog audit,
    ILogger<OpsStartupService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            store.Col<EventEntry>().EnsureIndex(e => e.Key);
            store.Col<User>().EnsureIndex(u => u.Role);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not create database indexes");
        }

        try
        {
            setup.EnsureToken(log: true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not prepare first-run setup");
        }

        if (RestoreStager.LastOutcome is { } outcome &&
            string.Equals(RestoreStager.LastOutcomeDataDir, paths.DataDir, StringComparison.OrdinalIgnoreCase))
        {
            if (RestoreStager.LastOutcomeFailed) logger.LogError("{Outcome}", outcome);
            else logger.LogWarning("{Outcome}", outcome);
            audit.Record(RestoreStager.LastOutcomeFailed ? "restoreFailed" : "restored", "system", null, "Backup restore", outcome);
        }
        if (RestoreStager.HasPendingRestore(paths))
        {
            logger.LogWarning(
                "A backup restore is staged in {Dir} but has not been applied. Stop the {Service} service and run " +
                "'CaddyManager.exe apply-restore', then start the service again.",
                RestoreStager.PendingDir(paths), AppPaths.ManagerServiceName);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
