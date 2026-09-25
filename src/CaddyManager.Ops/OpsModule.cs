using CaddyManager.Core;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CaddyManager.Ops;

public static class OpsModule
{
    public static IServiceCollection AddOpsModule(this IServiceCollection services)
    {
        // TODO(Ops builder): register services, hosted services, options.
        return services;
    }

    public static IEndpointRouteBuilder MapOpsEndpoints(this IEndpointRouteBuilder app)
    {
        // TODO(Ops builder): map /api endpoints per SPEC.md.
        return app;
    }
}
