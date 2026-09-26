using CaddyManager.Core;
using CaddyManager.Platform.Background;
using CaddyManager.Platform.Binary;
using CaddyManager.Platform.Hosting;
using CaddyManager.Platform.Infrastructure;
using CaddyManager.Platform.Readiness;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Platform;

public static class PlatformModule
{
    /// <summary>
    /// Registers the Caddy host (Windows service on Windows unless CM_CADDY_HOST=process; child process elsewhere),
    /// the binary manager, readiness service, bootstrapper and update checker.
    /// </summary>
    public static IServiceCollection AddPlatformModule(this IServiceCollection services)
    {
        services.AddSingleton<OutboundHttp>();
        services.AddSingleton<CaddyHostSupport>();
        services.AddSingleton<CaddyBinaryManager>();
        services.AddSingleton<ICaddyBinaryManager>(sp => sp.GetRequiredService<CaddyBinaryManager>());
        services.AddSingleton<ICaddyHost>(CreateHost);
        services.AddSingleton<CaddyEnvironmentSync>();
        services.AddSingleton<PowerShellRunner>();
        services.AddSingleton<ReadinessService>();
        services.AddSingleton<IReadinessService>(sp => sp.GetRequiredService<ReadinessService>());
        services.AddSingleton<CaddyBootstrapper>();
        services.AddHostedService(sp => sp.GetRequiredService<CaddyBootstrapper>());
        services.AddSingleton<UpdateChecker>();
        services.AddHostedService(sp => sp.GetRequiredService<UpdateChecker>());
        // Caddy start/stop/restart for local administrators (tray, CaddyManager.exe caddy ...); Windows only.
        if (OperatingSystem.IsWindows()) services.AddHostedService<LocalControlService>();
        return services;
    }

    /// <summary>True when Caddy should run as the Windows service (production); false for the child-process host.</summary>
    public static bool UseWindowsServiceHost() =>
        OperatingSystem.IsWindows() &&
        !string.Equals(Environment.GetEnvironmentVariable("CM_CADDY_HOST"), ProcessCaddyHost.Mode, StringComparison.OrdinalIgnoreCase);

    private static ICaddyHost CreateHost(IServiceProvider sp)
    {
        ICaddyHost host = UseWindowsServiceHost() && OperatingSystem.IsWindows()
            ? ActivatorUtilities.CreateInstance<WindowsServiceCaddyHost>(sp)
            : ActivatorUtilities.CreateInstance<ProcessCaddyHost>(sp);
        sp.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(PlatformModule))
            .LogInformation("Caddy host mode: {Mode}", host.HostMode);
        return host;
    }

    public static IEndpointRouteBuilder MapPlatformEndpoints(this IEndpointRouteBuilder app)
    {
        PlatformEndpoints.Map(app);
        return app;
    }
}
