using CaddyManager.Core;
using CaddyManager.Telemetry.Resources;
using CaddyManager.Telemetry.Traffic;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaddyManager.Telemetry;

/// <summary>
/// Server telemetry of THIS server: ServerInfo, live resource samples (CPU, memory, disks, network, Caddy/manager
/// process, connections) and traffic statistics aggregated from Caddy's stats access log (AppPaths.StatsLogFile).
/// Implements IServerTelemetry. Owner: TELEMETRY builder. See SPEC.md "Round 3" and docs/traffic-statistics.md.
/// </summary>
public static class TelemetryModule
{
    public static IServiceCollection AddTelemetryModule(this IServiceCollection services)
    {
        services.AddOptions<TelemetryOptions>();
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<TrafficStore>();
        services.AddSingleton(sp => new TrafficIngestion(sp.GetRequiredService<AppPaths>(), sp.GetRequiredService<TrafficStore>(),
            sp.GetRequiredService<IOptions<TelemetryOptions>>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<TrafficIngestion>>(), sp));
        services.AddSingleton<StatsIngesterService>();
        services.AddSingleton<ResourceSampler>();
        services.AddSingleton<ServerTelemetry>();
        services.AddSingleton<IServerTelemetry>(sp => sp.GetRequiredService<ServerTelemetry>());

        services.AddHostedService(sp => new ConditionalHostedService(sp, typeof(ResourceSampler)));
        services.AddHostedService(sp => new ConditionalHostedService(sp, typeof(StatsIngesterService)));
        return services;
    }

    /// <summary>
    /// No endpoints of its own: /api/servers/{id}/samples and /traffic (Cluster module) serve IServerTelemetry for the
    /// local server and proxy to nodes.
    /// </summary>
    public static IEndpointRouteBuilder MapTelemetryEndpoints(this IEndpointRouteBuilder app) => app;

    /// <summary>Starts a background service only when TelemetryOptions.EnableBackgroundServices is set.</summary>
    private sealed class ConditionalHostedService(IServiceProvider sp, Type serviceType) : IHostedService
    {
        private IHostedService? _inner;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (!sp.GetRequiredService<IOptions<TelemetryOptions>>().Value.EnableBackgroundServices) return Task.CompletedTask;
            _inner = (IHostedService)sp.GetRequiredService(serviceType);
            return _inner.StartAsync(cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken) => _inner?.StopAsync(cancellationToken) ?? Task.CompletedTask;
    }
}
