using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using CaddyManager.Platform.Binary;
using CaddyManager.Platform.Infrastructure;
using CaddyManager.Platform.Readiness;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Platform;

public sealed record CaddyInstallRequest(string? Version);
public sealed record PluginsRequest(List<string>? Plugins);

/// <summary>/api/caddy/*, /api/settings/binary, /api/readiness/*, /api/system/* (SPEC "Caddy service &amp; binary", "Readiness", "System").</summary>
internal static partial class PlatformEndpoints
{
    private const int MaxPlugins = 50;

    [GeneratedRegex(@"^[a-z0-9][a-z0-9.\-]*\.[a-z]{2,}(/[A-Za-z0-9_.\-~]+)+(@[A-Za-z0-9_.\-+]+)?$")]
    private static partial Regex GoPackage();

    public static void Map(IEndpointRouteBuilder app)
    {
        MapCaddy(app);
        MapBinarySettings(app);
        MapReadiness(app);
        MapSystem(app);
    }

    // ------------------------------------------------------------------ /api/caddy

    private static void MapCaddy(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/caddy").RequireAuthorization(Policies.Viewer);

        g.MapGet("/status", (ICaddyHost host, CancellationToken ct) => host.GetStatusAsync(ct));

        g.MapPost("/start", (ICaddyHost host, IAuditLog audit) =>
                Control(host, audit, "started", "start", h => h.StartAsync()))
            .RequireAuthorization(Policies.Operator);
        g.MapPost("/stop", (ICaddyHost host, IAuditLog audit) =>
                Control(host, audit, "stopped", "stop", h => h.StopAsync()))
            .RequireAuthorization(Policies.Operator);
        g.MapPost("/restart", (ICaddyHost host, IAuditLog audit) =>
                Control(host, audit, "restarted", "restart", h => h.RestartAsync()))
            .RequireAuthorization(Policies.Operator);
        g.MapPost("/service/install", (ICaddyHost host, IAuditLog audit) =>
                Control(host, audit, "service-installed", "register the service for", h => h.InstallServiceAsync()))
            .RequireAuthorization(Policies.Admin);
        g.MapPost("/service/uninstall", (ICaddyHost host, IAuditLog audit) =>
                Control(host, audit, "service-uninstalled", "remove the service of", h => h.UninstallServiceAsync()))
            .RequireAuthorization(Policies.Admin);

        g.MapGet("/binary", (ICaddyBinaryManager bin, CancellationToken ct) => bin.GetOverviewAsync(ct));

        g.MapPost("/binary/check", async (ICaddyBinaryManager bin, IAuditLog audit, CancellationToken ct) =>
        {
            ReleaseInfo? latest;
            try
            {
                latest = await bin.GetLatestAsync(force: true, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return Results.Problem(title: "Update check failed", detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
            }
            audit.Record("checked-updates", "caddyBinary", details: $"Latest release: {latest?.Version ?? "unknown"}");
            return Results.Ok(await bin.GetOverviewAsync(ct));
        }).RequireAuthorization(Policies.Operator);

        g.MapPost("/binary/install", (CaddyInstallRequest? body, ICaddyBinaryManager bin, IAuditLog audit) =>
        {
            try
            {
                var job = bin.StartInstallOrUpdate(string.IsNullOrWhiteSpace(body?.Version) ? null : body.Version.Trim());
                audit.Record("install-started", "caddyBinary", job.Id, body?.Version ?? "latest", job.Title);
                return Results.Ok(job);
            }
            catch (ArgumentException ex)
            {
                return ApiResults.BadRequest(ex.Message, new Dictionary<string, string[]> { ["version"] = [ex.Message.Split(" (Parameter")[0]] });
            }
            catch (InvalidOperationException ex)
            {
                return ApiResults.Conflict(ex.Message);
            }
        }).RequireAuthorization(Policies.Admin);

        g.MapGet("/plugins/catalog", async (string? q, ICaddyBinaryManager bin, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await bin.GetPluginCatalogAsync(q, ct));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(title: "Plugin catalog unavailable", detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        g.MapPut("/plugins", async (PluginsRequest body, CaddyBinaryManager bin, IStore store, IAuditLog audit, CancellationToken ct) =>
        {
            var (plugins, error) = await ValidatePluginsAsync(body.Plugins ?? [], bin, ct);
            if (error is not null) return error;
            var s = store.GetSettings<BinarySettings>();
            var before = string.Join(", ", s.Plugins);
            s.Plugins = plugins;
            store.SaveSettings(s);
            audit.Record("updated", "caddyPlugins", details: $"Desired plugins: [{before}] → [{string.Join(", ", plugins)}]");
            return Results.Ok(await bin.GetOverviewAsync(ct));
        }).RequireAuthorization(Policies.Admin);
    }

    private static async Task<IResult> Control(ICaddyHost host, IAuditLog audit, string action, string verb, Func<ICaddyHost, Task> op)
    {
        // Not bound to the request: a start/stop must not be abandoned half-way when the browser disconnects.
        try
        {
            await op(host);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException
                                       or UnauthorizedAccessException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            audit.Record(action, "caddy", AppPaths.CaddyServiceName, "Caddy", "Failed: " + ex.Message);
            return ApiResults.Failed($"Could not {verb} Caddy", ex.Message);
        }
        audit.Record(action, "caddy", AppPaths.CaddyServiceName, "Caddy");
        return Results.Ok(await host.GetStatusAsync());
    }

    /// <summary>Validates Go package paths; checks them against the caddyserver.com registry when it is reachable.</summary>
    internal static async Task<(List<string> Plugins, IResult? Error)> ValidatePluginsAsync(IEnumerable<string> input, CaddyBinaryManager bin, CancellationToken ct)
    {
        var v = new Validator();
        var plugins = input.Select(p => (p ?? "").Trim()).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (plugins.Count > MaxPlugins) v.Add("plugins", $"At most {MaxPlugins} plugins can be selected.");
        foreach (var p in plugins)
        {
            if (!GoPackage().IsMatch(p))
                v.Add("plugins", $"'{p}' is not a valid Go package path (e.g. github.com/mholt/caddy-l4).");
            else if (CaddyOutputParser.PackageWithoutVersion(p).Equals(CaddyOutputParser.StandardPackage, StringComparison.OrdinalIgnoreCase))
                v.Add("plugins", $"'{p}' is Caddy itself, not a plugin.");
        }
        if (v.IsValid && plugins.Count > 0)
        {
            List<PluginPackage>? catalog = null;
            try { catalog = await bin.GetCatalogAsync(ct); }
            catch (InvalidOperationException) { /* registry unreachable: accept syntactically valid paths */ }
            if (catalog is not null)
            {
                var known = new HashSet<string>(catalog.Select(c => c.Path), StringComparer.OrdinalIgnoreCase);
                foreach (var p in plugins.Where(p => !known.Contains(CaddyOutputParser.PackageWithoutVersion(p))))
                    v.Add("plugins", $"'{p}' is not in the Caddy package registry (caddyserver.com/download), so it cannot be built.");
            }
        }
        return v.IsValid ? (plugins, null) : (plugins, v.ToResult("The plugin list is invalid."));
    }

    // ------------------------------------------------------------------ /api/settings/binary

    private static void MapBinarySettings(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/settings/binary").RequireAuthorization(Policies.Viewer);

        g.MapGet("", (IStore store) => Redact(store.GetSettings<BinarySettings>()));

        g.MapPut("", async (BinarySettings body, IStore store, CaddyBinaryManager bin, IAuditLog audit, CancellationToken ct) =>
        {
            var current = store.GetSettings<BinarySettings>();
            var v = new Validator();
            v.Require(body.CheckIntervalHours is >= 1 and <= 168, "checkIntervalHours", "Check interval must be between 1 and 168 hours.");
            var proxy = string.IsNullOrWhiteSpace(body.OutboundProxy) ? null : body.OutboundProxy.Trim();
            if (proxy is not null)
            {
                proxy = RestoreProxyPassword(proxy, current.OutboundProxy);
                try { OutboundHttp.CreateProxy(proxy); }
                catch (ArgumentException ex) { v.Add("outboundProxy", ex.Message); }
            }
            if (!v.IsValid) return v.ToResult();
            var (plugins, error) = await ValidatePluginsAsync(body.Plugins ?? [], bin, ct);
            if (error is not null) return error;

            var updated = new BinarySettings
            {
                Plugins = plugins,
                AutoCheckUpdates = body.AutoCheckUpdates,
                CheckIntervalHours = body.CheckIntervalHours,
                AutoInstallUpdates = body.AutoInstallUpdates,
                OutboundProxy = proxy,
                LastCheckedAt = current.LastCheckedAt,           // server-managed
                LatestKnownVersion = current.LatestKnownVersion, // server-managed
            };
            store.SaveSettings(updated);
            audit.Record("updated", "settings", "binary", "Caddy binary & updates",
                $"autoCheck={updated.AutoCheckUpdates}, interval={updated.CheckIntervalHours}h, autoInstall={updated.AutoInstallUpdates}, " +
                $"proxy={(updated.OutboundProxy is null ? "none" : OutboundHttp.RedactProxy(updated.OutboundProxy))}, plugins=[{string.Join(", ", plugins)}]");
            return Results.Ok(Redact(updated));
        }).RequireAuthorization(Policies.Admin);
    }

    /// <summary>The proxy password is never returned; "********" in a PUT keeps the stored password.</summary>
    private static BinarySettings Redact(BinarySettings s) => new()
    {
        Plugins = s.Plugins,
        AutoCheckUpdates = s.AutoCheckUpdates,
        CheckIntervalHours = s.CheckIntervalHours,
        AutoInstallUpdates = s.AutoInstallUpdates,
        LastCheckedAt = s.LastCheckedAt,
        LatestKnownVersion = s.LatestKnownVersion,
        OutboundProxy = string.IsNullOrEmpty(s.OutboundProxy) ? s.OutboundProxy : OutboundHttp.RedactProxy(s.OutboundProxy),
    };

    internal static string RestoreProxyPassword(string incoming, string? stored)
    {
        if (string.IsNullOrEmpty(stored) || !Uri.TryCreate(incoming, UriKind.Absolute, out var inUri)) return incoming;
        var parts = inUri.UserInfo.Split(':', 2);
        if (parts.Length != 2 || Uri.UnescapeDataString(parts[1]) != OutboundHttp.RedactedPassword) return incoming;
        if (!Uri.TryCreate(stored, UriKind.Absolute, out var old)) return incoming;
        var oldParts = old.UserInfo.Split(':', 2);
        if (oldParts.Length != 2) return incoming;
        return new UriBuilder(inUri) { Password = Uri.UnescapeDataString(oldParts[1]) }.Uri.ToString().TrimEnd('/');
    }

    // ------------------------------------------------------------------ /api/readiness

    private static void MapReadiness(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/readiness").RequireAuthorization(Policies.Viewer);

        g.MapGet("", (IReadinessService readiness) =>
            readiness.LastReport is { } r ? Results.Ok(r) : Results.NoContent());

        g.MapPost("/run", async (IReadinessService readiness, CancellationToken ct) =>
            Results.Ok(await readiness.RunAsync(ct))).RequireAuthorization(Policies.Operator);

        g.MapPost("/fix/{checkId}", async (string checkId, IReadinessService readiness, IAuditLog audit, ILoggerFactory lf) =>
        {
            string message;
            try
            {
                message = await readiness.FixAsync(checkId);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.Problem(title: "Not found", detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
            }
            catch (Exception ex) when (ex is InvalidOperationException or PowerShellException or TimeoutException
                                           or PlatformNotSupportedException or UnauthorizedAccessException or IOException)
            {
                lf.CreateLogger("Readiness").LogWarning("Readiness fix {CheckId} failed: {Error}", checkId, ex.Message);
                audit.Record("fix-failed", "readiness", checkId, checkId, ex.Message);
                return ApiResults.Failed("Fix failed", ex.Message);
            }
            audit.Record("fixed", "readiness", checkId, checkId, message);
            var report = await readiness.RunAsync();
            return Results.Ok(new { message, report });
        }).RequireAuthorization(Policies.Admin);

        g.MapGet("/gpo-script", (IReadinessService readiness) =>
            Results.Text(readiness.BuildGpoScript(), "text/plain; charset=utf-8"));
    }

    // ------------------------------------------------------------------ /api/system

    private static void MapSystem(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/system").RequireAuthorization(Policies.Viewer);

        g.MapGet("/info", (ICaddyHost host, AppPaths paths) =>
        {
            using var self = Process.GetCurrentProcess();
            return Results.Ok(new
            {
                version = ProductVersion(),
                product = AppPaths.ProductName,
                hostMode = host.HostMode,
                dataDir = paths.DataDir,
                installDir = paths.InstallDir,
                os = RuntimeInformation.OSDescription,
                machineName = Environment.MachineName,
                uptimeSeconds = (long)(DateTime.Now - self.StartTime).TotalSeconds,
                isService = WindowsServiceHelpers.IsWindowsService(),
            });
        });

        g.MapPost("/restart", (IAuditLog audit, ILoggerFactory lf) =>
        {
            var logger = lf.CreateLogger("System");
            audit.Record("restart", "system", AppPaths.ManagerServiceName, AppPaths.ProductName);
            if (!WindowsServiceHelpers.IsWindowsService())
            {
                logger.LogWarning("Restart requested, but the manager runs in console mode; restart it manually.");
                return Results.Accepted(value: new { message = "The manager runs in console mode and cannot restart itself; restart it manually." });
            }
            logger.LogWarning("Restart requested; exiting with code 1 so the service recovery actions restart the manager");
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(1.5)); // let the response and the audit entry flush
                Environment.Exit(1);
            });
            return Results.Accepted(value: new { message = "The manager service is restarting; the UI will be back in a few seconds." });
        }).RequireAuthorization(Policies.Admin);
    }

    internal static string ProductVersion() =>
        (Assembly.GetEntryAssembly() ?? typeof(PlatformEndpoints).Assembly).GetName().Version?.ToString(3) ?? "0.0.0";
}
