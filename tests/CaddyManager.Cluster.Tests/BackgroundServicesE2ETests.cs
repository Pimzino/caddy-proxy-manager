using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;

namespace CaddyManager.Cluster.Tests;

/// <summary>
/// A manager composed like Program.cs (every module) must actually start every background service. Regression: several
/// services were registered with AddHostedService(sp => new ConditionalHostedService(...)); AddHostedService uses
/// TryAddEnumerable, which treats factory registrations returning the same type as duplicates, so only the first of them
/// ever ran (Ops: retention and scheduled backups; Telemetry: the stats-log ingester).
/// https://github.com/dotnet/runtime/blob/main/src/libraries/Microsoft.Extensions.Hosting.Abstractions/src/ServiceCollectionHostedServiceExtensions.cs
/// </summary>
public sealed class BackgroundServicesE2ETests
{
    private static readonly string[] Expected =
    [
        "CaddyManager.Config.Certificates.CertificateWatcher, CaddyManager.Config",
        "CaddyManager.Platform.Background.CaddyBootstrapper, CaddyManager.Platform",
        "CaddyManager.Platform.Background.UpdateChecker, CaddyManager.Platform",
        "CaddyManager.Ops.Monitoring.MonitorService, CaddyManager.Ops",
        "CaddyManager.Ops.Monitoring.RetentionService, CaddyManager.Ops",
        "CaddyManager.Ops.Backup.ScheduledBackupService, CaddyManager.Ops",
        "CaddyManager.Telemetry.Resources.ResourceSampler, CaddyManager.Telemetry",
        "CaddyManager.Telemetry.Traffic.StatsIngesterService, CaddyManager.Telemetry",
        "CaddyManager.Cluster.ClusterWorker, CaddyManager.Cluster",
    ];

    [Fact]
    public async Task EveryBackgroundServiceStartsInAComposedManager()
    {
        await using var m = await Manager.CreateAsync("bg", opsOptions: _ => { });
        var report = new JsonObject();
        var notStarted = new List<string>();
        foreach (var name in Expected)
        {
            var type = Type.GetType(name, throwOnError: true)!;
            var service = m.Services.GetService(type) as BackgroundService;
            // ExecuteTask is set by BackgroundService.StartAsync, i.e. only when the host started this instance.
            var started = service?.ExecuteTask is not null;
            report[type.Name] = started;
            if (!started) notStarted.Add(type.Name);
        }
        E2EArtifacts.Write("background-services.json", report);
        Assert.True(notStarted.Count == 0, "Not started by the host: " + string.Join(", ", notStarted));
    }
}
