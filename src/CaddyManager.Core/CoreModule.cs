using CaddyManager.Core.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaddyManager.Core;

public static class CoreModule
{
    public static IServiceCollection AddCore(this IServiceCollection services, AppPaths paths, IStore? store = null)
    {
        paths.EnsureCreated();
        services.AddSingleton(paths);
        if (store is not null) services.AddSingleton(store);
        else services.AddSingleton<IStore, LiteStore>();
        services.AddSingleton<ISecretProtector, SecretProtector>();
        services.AddSingleton<IJobRunner, JobRunner>();
        // The host (Program.cs) registers its FileLoggerProvider. Without one, lines meant for the log file only are
        // dropped rather than sent to a provider that other users can read.
        services.TryAddSingleton<IManagerLogFile, NullManagerLogFile>();
        services.AddHttpClient("default", c =>
        {
            c.DefaultRequestHeaders.UserAgent.ParseAdd("CaddyProxyManager/1.0");
            c.Timeout = TimeSpan.FromMinutes(10);
        });
        return services;
    }

    public static IEndpointRouteBuilder MapCoreEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/jobs").RequireAuthorization(Policies.Viewer);
        g.MapGet("/", (IJobRunner jobs) => jobs.Recent());
        g.MapGet("/{id}", (string id, IJobRunner jobs) =>
            jobs.Get(id) is { } j ? Results.Ok(j) : ApiResults.NotFound("Job"));
        return app;
    }
}

internal sealed class NullManagerLogFile : IManagerLogFile
{
    public ILogger CreateLogger(string categoryName) => NullLogger.Instance;
}
