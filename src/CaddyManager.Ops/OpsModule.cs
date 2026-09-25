using CaddyManager.Core;
using CaddyManager.Ops.Audit;
using CaddyManager.Ops.Auth;
using CaddyManager.Ops.Auth.Ldap;
using CaddyManager.Ops.Backup;
using CaddyManager.Ops.Dashboard;
using CaddyManager.Ops.Events;
using CaddyManager.Ops.Logs;
using CaddyManager.Ops.Monitoring;
using CaddyManager.Ops.Settings;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CaddyManager.Ops;

public static class OpsModule
{
    public static IServiceCollection AddOpsModule(this IServiceCollection services)
    {
        services.AddOptions<OpsOptions>();
        services.TryAddSingleton(TimeProvider.System);

        // Authentication, authorization policies, CSRF check, login rate limiting.
        services.AddOpsAuthentication();
        services.AddSingleton<ICurrentUser, CurrentUser>();
        services.AddSingleton<UserSnapshotCache>();
        services.AddSingleton<SetupState>();
        services.AddSingleton<ILdapConnector, LdapConnector>();
        services.AddSingleton<LdapAuthenticator>();
        services.AddSingleton<LdapSignIn>();

        // Audit, events and notifications.
        services.AddSingleton<IAuditLog, AuditLog>();
        services.AddSingleton<IEventLogWriter, WindowsEventLogWriter>();
        services.AddSingleton<NotificationHttp>();
        services.AddSingleton<OAuthTokenProvider>();
        services.AddSingleton<INotifier, Notifier>();
        services.AddSingleton<EventSink>();
        services.AddSingleton<IEventSink>(sp => sp.GetRequiredService<EventSink>());
        services.AddSingleton<IAlertState>(sp => sp.GetRequiredService<EventSink>());

        services.AddSingleton<DashboardBuilder>();
        services.AddSingleton<BackupService>();
        services.AddSingleton<ScheduledBackups>();
        services.AddSingleton<ScheduledBackupService>();

        services.AddHostedService<OpsStartupService>();
        services.AddSingleton<MonitorService>();
        services.AddSingleton<RetentionService>();
        services.AddHostedService(sp => new ConditionalHostedService(sp, typeof(MonitorService)));
        services.AddHostedService(sp => new ConditionalHostedService(sp, typeof(RetentionService)));
        services.AddHostedService(sp => new ConditionalHostedService(sp, typeof(ScheduledBackupService)));
        return services;
    }

    public static IEndpointRouteBuilder MapOpsEndpoints(this IEndpointRouteBuilder app)
    {
        AuthEndpoints.Map(app);
        LdapEndpoints.Map(app);
        AuditEventEndpoints.Map(app);
        DashboardEndpoints.Map(app);
        SettingsEndpoints.Map(app);
        LogEndpoints.Map(app);
        BackupEndpoints.Map(app);
#if DEBUG
        DevLogin.Map(app);
#endif
        return app;
    }

    /// <summary>Starts a background service only when OpsOptions.EnableBackgroundServices is set.</summary>
    private sealed class ConditionalHostedService(IServiceProvider sp, Type serviceType) : IHostedService
    {
        private IHostedService? _inner;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (!sp.GetRequiredService<IOptions<OpsOptions>>().Value.EnableBackgroundServices) return Task.CompletedTask;
            _inner = (IHostedService)sp.GetRequiredService(serviceType);
            return _inner.StartAsync(cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken) => _inner?.StopAsync(cancellationToken) ?? Task.CompletedTask;
    }
}
