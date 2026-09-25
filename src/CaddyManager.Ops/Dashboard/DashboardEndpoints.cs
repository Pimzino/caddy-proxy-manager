using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaddyManager.Ops.Dashboard;

public sealed record DashboardCounts(int Proxy, int Redirect, int Static, int Response, int Streams, int AccessLists,
    int Certificates, int CertificatesExpiring, int HostsDisabled);
public sealed record DashboardReadiness(DateTime? RanAt, int Pass, int Warn, int Fail);
public sealed record DashboardUpstreams(int Total, int Unhealthy);
public sealed record DashboardSystem(string Hostname, string Os, string ManagerVersion, long UptimeSeconds, string DataDir);
public sealed record DashboardDto(
    CaddyStatus Caddy,
    BinaryOverview Binary,
    DashboardCounts Counts,
    DashboardReadiness? Readiness,
    DashboardUpstreams Upstreams,
    List<EventEntry> RecentEvents,
    DashboardSystem System,
    // Human-readable notes about parts that could not be loaded (the rest still renders).
    List<string> Warnings);

/// <summary>
/// GET /api/dashboard. Every part is loaded independently with a timeout; failures degrade to
/// defaults plus a warning so the dashboard always renders.
/// </summary>
internal sealed class DashboardBuilder(IServiceProvider services, IStore store, AppPaths paths,
    IOptions<OpsOptions> options, ILogger<DashboardBuilder> logger)
{
    private static readonly DateTime ProcessStart = SafeProcessStart();

    public async Task<DashboardDto> BuildAsync(CancellationToken ct)
    {
        var warnings = new List<string>();
        var timeout = options.Value.DashboardPartTimeout;

        async Task<T> Part<T>(string name, Func<CancellationToken, Task<T>> load, T fallback)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);
                return await Task.Run(() => load(cts.Token), cts.Token).WaitAsync(timeout, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                var msg = ex is TimeoutException or OperationCanceledException ? "timed out" : ex.Message;
                logger.LogWarning("Dashboard: {Part} unavailable: {Message}", name, msg);
                lock (warnings) warnings.Add($"{name}: {msg}");
                return fallback;
            }
        }

        var caddyFallback = new CaddyStatus
        {
            State = CaddyRunState.Unknown,
            BinaryInstalled = File.Exists(paths.CaddyExe),
            BinaryPath = paths.CaddyExe,
            ConfigPath = paths.CaddyConfigFile,
            LastError = "Status unavailable",
        };

        var caddyTask = Part("Caddy status", async t =>
            services.GetService<ICaddyHost>() is { } h ? await h.GetStatusAsync(t) : throw new InvalidOperationException("Caddy host service not available"),
            caddyFallback);
        var binaryTask = Part("Caddy binary", async t =>
            services.GetService<ICaddyBinaryManager>() is { } b ? await b.GetOverviewAsync(t) : throw new InvalidOperationException("Binary manager not available"),
            new BinaryOverview());
        var certsTask = Part<List<CertificateInfo>?>("Certificates", async t =>
            services.GetService<ICertificateInventory>() is { } inv ? await inv.ListAsync(t) : null, null);
        var upstreamTask = Part("Upstreams", async t =>
        {
            if (services.GetService<ICaddyAdminClient>() is not { } admin) return new DashboardUpstreams(0, 0);
            if (!await admin.IsReachableAsync(t)) return new DashboardUpstreams(0, 0);
            var list = await admin.GetUpstreamsAsync(t);
            return new DashboardUpstreams(list.Count, list.Count(u => !u.Healthy));
        }, new DashboardUpstreams(0, 0));
        var readinessTask = Part<DashboardReadiness?>("Readiness", _ =>
        {
            var r = services.GetService<IReadinessService>()?.LastReport;
            return Task.FromResult(r is null ? null : new DashboardReadiness(r.RanAt, r.Pass, r.Warn, r.Fail));
        }, null);
        var eventsTask = Part("Recent events", _ =>
            Task.FromResult(store.Col<EventEntry>().Query().OrderByDescending(e => e.CreatedAt).Limit(10).ToList()), new List<EventEntry>());

        await Task.WhenAll(caddyTask, binaryTask, certsTask, upstreamTask, readinessTask, eventsTask);
        var certs = certsTask.Result;

        var counts = await Part("Counts", _ => Task.FromResult(Counts(certs)), new DashboardCounts(0, 0, 0, 0, 0, 0, 0, 0, 0));

        var system = new DashboardSystem(
            Environment.MachineName,
            RuntimeInformation.OSDescription,
            ManagerVersion(),
            (long)Math.Max(0, (DateTime.UtcNow - ProcessStart).TotalSeconds),
            paths.DataDir);

        return new DashboardDto(caddyTask.Result, binaryTask.Result, counts, readinessTask.Result, upstreamTask.Result,
            eventsTask.Result, system, warnings);
    }

    private DashboardCounts Counts(List<CertificateInfo>? certs)
    {
        var hosts = store.Col<SiteHost>().FindAll().ToList();
        var threshold = 14;
        try { threshold = Math.Max(1, store.GetSettings<NotificationSettings>().CertificateExpiryDays); } catch { /* default */ }
        int certCount, expiring;
        if (certs is not null)
        {
            var relevant = certs.Where(c => c.Kind is CertificateKind.Custom or CertificateKind.Acme).ToList();
            certCount = certs.Count(c => c.Kind != CertificateKind.InternalRoot);
            expiring = relevant.Count(c => c.DaysRemaining <= threshold);
        }
        else
        {
            var stored = store.Col<Certificate>().FindAll().ToList();
            certCount = stored.Count;
            var cutoff = DateTime.UtcNow.AddDays(threshold);
            expiring = stored.Count(c => c.NotAfter <= cutoff);
        }
        return new DashboardCounts(
            hosts.Count(h => h.Kind == HostKind.Proxy),
            hosts.Count(h => h.Kind == HostKind.Redirect),
            hosts.Count(h => h.Kind == HostKind.Static),
            hosts.Count(h => h.Kind == HostKind.Response),
            store.Col<StreamHost>().Count(),
            store.Col<AccessList>().Count(),
            certCount,
            expiring,
            hosts.Count(h => !h.Enabled));
    }

    internal static string ManagerVersion() =>
        (Assembly.GetEntryAssembly() ?? typeof(DashboardBuilder).Assembly).GetName().Version?.ToString(3) ?? "0.0.0";

    private static DateTime SafeProcessStart()
    {
        try { return Process.GetCurrentProcess().StartTime.ToUniversalTime(); }
        catch { return DateTime.UtcNow; }
    }
}

internal static class DashboardEndpoints
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/dashboard", (DashboardBuilder builder, CancellationToken ct) => builder.BuildAsync(ct))
            .RequireAuthorization(Policies.Viewer);
}
