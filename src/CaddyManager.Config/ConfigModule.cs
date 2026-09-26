using CaddyManager.Config.Admin;
using CaddyManager.Config.Certificates;
using CaddyManager.Config.Endpoints;
using CaddyManager.Config.Services;
using CaddyManager.Core;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Config;

public static class ConfigModule
{
    public static IServiceCollection AddConfigModule(this IServiceCollection services)
    {
        // Loopback admin API: never through an outbound proxy, fail fast when Caddy is down.
        services.AddHttpClient(CaddyAdminClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                UseProxy = false,
                ConnectTimeout = TimeSpan.FromSeconds(3),
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            });

        services.AddSingleton<CaddyAdminClient>();
        services.AddSingleton<ICaddyAdminClient>(sp => sp.GetRequiredService<CaddyAdminClient>());

        services.AddSingleton<CaddyConfigService>();
        services.AddSingleton<ICaddyConfigService>(sp => sp.GetRequiredService<CaddyConfigService>());
        services.AddSingleton<IConfigChangeFeed>(sp => sp.GetRequiredService<CaddyConfigService>());

        services.AddSingleton<CertificateFileStore>();
        services.AddSingleton<ICertificateMaterialStore>(sp => sp.GetRequiredService<CertificateFileStore>());
        services.AddSingleton<CertificateInventory>();
        services.AddSingleton<ICertificateInventory>(sp => sp.GetRequiredService<CertificateInventory>());

        // TryAdd: tests (and future platforms) can supply their own certificate store reader.
        services.TryAddSingleton<IWindowsCertificateSource, WindowsCertificateStoreSource>();
        services.AddSingleton<CertificateSyncService>();
        services.AddSingleton<CertificateWatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<CertificateWatcher>());
        services.AddHostedService<ConfigStartup>();

        services.AddSingleton<ConfigMutationGate>();
        services.AddSingleton<IConfigMutationLock>(sp => sp.GetRequiredService<ConfigMutationGate>());
        services.AddSingleton<SecretScrubber>();
        services.AddSingleton<ISecretScrubber>(sp => sp.GetRequiredService<SecretScrubber>());
        return services;
    }

    public static IEndpointRouteBuilder MapConfigEndpoints(this IEndpointRouteBuilder app)
    {
        HostEndpoints.Map(app);
        StreamEndpoints.Map(app);
        AccessListEndpoints.Map(app);
        CertificateEndpoints.Map(app);
        SettingsEndpoints.Map(app);
        ConfigEndpoints.Map(app);
        CaddyfileImportEndpoints.Map(app);
        return app;
    }
}

/// <summary>Makes sure Caddy always has a bootable config file before anything tries to start it.</summary>
internal sealed class ConfigStartup(ICaddyConfigService config, ILogger<ConfigStartup> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            config.EnsureBootConfig();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not write the Caddy boot configuration");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
