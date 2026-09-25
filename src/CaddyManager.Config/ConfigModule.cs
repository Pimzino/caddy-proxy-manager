using CaddyManager.Core;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CaddyManager.Config;

public static class ConfigModule
{
    public static IServiceCollection AddConfigModule(this IServiceCollection services)
    {
        // TODO(Config builder): register services, hosted services, options.
        return services;
    }

    public static IEndpointRouteBuilder MapConfigEndpoints(this IEndpointRouteBuilder app)
    {
        // TODO(Config builder): map /api endpoints per SPEC.md.
        return app;
    }
}
