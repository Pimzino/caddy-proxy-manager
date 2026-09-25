using CaddyManager.Core;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CaddyManager.Cluster;

/// <summary>
/// Multi-server management: this server as standalone / cluster primary / managed node, node enrollment (join tokens),
/// encrypted primary→node RPC, configuration replication, the Servers API (/api/servers, /api/cluster) and remote
/// server details/traffic proxying. Owner: CLUSTER builder. See SPEC.md "Round 3".
/// </summary>
public static class ClusterModule
{
    public static IServiceCollection AddClusterModule(this IServiceCollection services)
    {
        // TODO(cluster): register ClusterService (IClusterRole), node client, heartbeat/sync background service.
        return services;
    }

    public static IEndpointRouteBuilder MapClusterEndpoints(this IEndpointRouteBuilder app)
    {
        // TODO(cluster): /api/servers*, /api/cluster*, /api/cluster/rpc.
        return app;
    }
}
