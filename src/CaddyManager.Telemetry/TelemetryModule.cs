using CaddyManager.Core;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CaddyManager.Telemetry;

/// <summary>
/// Server telemetry of THIS server: ServerInfo, live resource samples (CPU, memory, disks, network, Caddy/manager
/// process, connections) and traffic statistics aggregated from Caddy's stats access log (AppPaths.StatsLogFile).
/// Implements IServerTelemetry. Owner: TELEMETRY builder. See SPEC.md "Round 3".
/// </summary>
public static class TelemetryModule
{
    public static IServiceCollection AddTelemetryModule(this IServiceCollection services)
    {
        // TODO(telemetry): register sampler, stats ingestion, IServerTelemetry.
        return services;
    }

    public static IEndpointRouteBuilder MapTelemetryEndpoints(this IEndpointRouteBuilder app) => app;
}
