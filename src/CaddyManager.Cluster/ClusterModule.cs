using CaddyManager.Core;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CaddyManager.Cluster;

/// <summary>
/// Multi-server management: this server as standalone / cluster primary / managed node, node enrollment (join tokens),
/// encrypted primary→node RPC, configuration replication, the Servers API (/api/servers, /api/cluster) and remote
/// server details/traffic proxying. Owner: CLUSTER builder. See SPEC.md "Round 3" and docs/cluster.md.
/// </summary>
public static class ClusterModule
{
    public static IServiceCollection AddClusterModule(this IServiceCollection services)
    {
        services.AddOptions<ClusterOptions>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ClusterService>();
        services.AddSingleton<IClusterRole>(sp => sp.GetRequiredService<ClusterService>());
        services.AddSingleton<NonceCache>();
        services.AddSingleton<NodeClient>();
        services.AddSingleton<NodeSync>();
        services.AddSingleton<NodeRpcHandler>();
        services.AddSingleton<ClusterWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<ClusterWorker>());
        return services;
    }

    public static IEndpointRouteBuilder MapClusterEndpoints(this IEndpointRouteBuilder app)
    {
        ClusterEndpoints.Map(app);
        return app;
    }
}
