using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using CaddyManager.Core.Infrastructure;
using CaddyManager.Platform.Binary;
using CaddyManager.Platform.Hosting;
using CaddyManager.Platform.Infrastructure;
using CaddyManager.Platform.Readiness;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
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

        g.MapPost("/binary/upload", async (HttpRequest request, CaddyBinaryManager bin, AppPaths paths, IAuditLog audit,
            ILoggerFactory lf, CancellationToken ct) =>
        {
            var logger = lf.CreateLogger("CaddyBinary");
            if (!bin.CanStartJob)
                return ApiResults.Conflict("A Caddy install/update job is already running. Wait for it to finish, then upload again.");
            Directory.CreateDirectory(paths.CaddyStagingDir);
            BinaryUploadReceiver.CleanStale(paths.CaddyStagingDir, TimeSpan.FromHours(24));
            ReceivedUpload upload;
            try
            {
                upload = await BinaryUploadReceiver.ReceiveAsync(request, paths.CaddyStagingDir, BinaryUploadReceiver.MaxFileBytes, ct);
            }
            catch (BinaryUploadException ex)
            {
                audit.Record("upload-rejected", "caddyBinary", details: ex.Message);
                return ex.StatusCode == StatusCodes.Status413PayloadTooLarge
                    ? Results.Problem(title: "Upload too large", detail: ex.Message, statusCode: StatusCodes.Status413PayloadTooLarge)
                    : ApiResults.BadRequest(ex.Message, new Dictionary<string, string[]> { [ex.Field] = [ex.Message] });
            }
            try
            {
                var job = bin.StartInstallFromFile(upload.FilePath, upload.ExpectedSha512, upload.FileName);
                audit.Record("upload-install-started", "caddyBinary", job.Id, upload.FileName,
                    $"{upload.Length} bytes, SHA-512 {upload.Sha512}{(upload.ExpectedSha512 is null ? " (no expected checksum given)" : " (matches the expected checksum)")}");
                logger.LogInformation("Offline Caddy install started from uploaded file {File} ({Bytes} bytes, SHA-512 {Sha}) as job {Job}",
                    upload.FileName, upload.Length, upload.Sha512, job.Id);
                return Results.Ok(job);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FileNotFoundException)
            {
                bin.DeleteUpload(upload.FilePath);
                audit.Record("upload-rejected", "caddyBinary", objectName: upload.FileName, details: ex.Message);
                return ex is InvalidOperationException ? ApiResults.Conflict(ex.Message) : ApiResults.BadRequest(ex.Message);
            }
        }).RequireAuthorization(Policies.Admin)
          .DisableAntiforgery()
          .WithMetadata(new UploadSizeLimit(BinaryUploadReceiver.MaxRequestBytes));

        g.MapPost("/binary/rollback", async (CaddyBinaryManager bin, IAuditLog audit, CancellationToken ct) =>
        {
            var from = (await bin.GetInstalledAsync(ct))?.Version;
            var to = await bin.GetPreviousVersionAsync(ct);
            try
            {
                var job = bin.StartRollback();
                audit.Record("rollback-started", "caddyBinary", job.Id, to, $"Rolling back from {from ?? "unknown"} to {to ?? "unknown"}");
                return Results.Ok(job);
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

        g.MapPut("/plugins", async (PluginsRequest body, CaddyBinaryManager bin, IStore store, IAuditLog audit, HttpContext http, CancellationToken ct) =>
        {
            // Desired plugins are replicated from the cluster primary to managed nodes (SPEC "Cluster module").
            if (ApiResults.RejectIfManagedNode(http.RequestServices) is { } managed) return managed;
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

        g.MapPut("", async (BinarySettings body, IStore store, CaddyBinaryManager bin, AppPaths paths, CaddyEnvironmentSync envSync,
            IAuditLog audit, CancellationToken ct) =>
        {
            var current = store.GetSettings<BinarySettings>();
            var (updated, error) = await ValidateBinarySettingsAsync(body, current, bin, ct);
            if (error is not null) return error;

            store.SaveSettings(updated!);
            audit.Record("updated", "settings", "binary", "Caddy binary & updates",
                $"autoCheck={updated!.AutoCheckUpdates}, interval={updated.CheckIntervalHours}h, autoInstall={updated.AutoInstallUpdates}, " +
                $"proxy={(updated.OutboundProxy is null ? "none" : OutboundHttp.RedactProxy(updated.OutboundProxy))}, " +
                $"proxyCaddyTraffic={updated.ProxyCaddyTraffic}, noProxy={updated.NoProxy}, " +
                $"managerReleaseRepo={updated.ManagerReleaseRepo ?? "none"}, plugins=[{string.Join(", ", updated.Plugins)}]");

            // Caddy's own environment (HTTPS_PROXY/HTTP_PROXY/NO_PROXY) changed: repair the service and restart Caddy.
            string? notice = null;
            if (!SameEnvironment(CaddyHostSupport.CaddyEnvironment(paths, current), CaddyHostSupport.CaddyEnvironment(paths, updated)))
                notice = await envSync.ApplyAsync(CancellationToken.None); // not bound to the request: never stop half-way
            var node = JsonSerializer.SerializeToNode(Redact(updated), JsonDefaults.Api)!.AsObject();
            if (notice is not null) node["notice"] = notice;
            return Results.Json(node, JsonDefaults.Api);
        }).RequireAuthorization(Policies.Admin);
    }

    /// <summary>Validates and normalises a PUT /api/settings/binary body. Server-managed fields are kept from <paramref name="current"/>.</summary>
    internal static async Task<(BinarySettings? Settings, IResult? Error)> ValidateBinarySettingsAsync(BinarySettings body, BinarySettings current,
        CaddyBinaryManager bin, CancellationToken ct)
    {
        var v = new Validator();
        v.Require(body.CheckIntervalHours is >= 1 and <= 168, "checkIntervalHours", "Check interval must be between 1 and 168 hours.");
        var proxy = string.IsNullOrWhiteSpace(body.OutboundProxy) ? null : body.OutboundProxy.Trim();
        if (proxy is not null)
        {
            proxy = RestoreProxyPassword(proxy, current.OutboundProxy);
            try { OutboundHttp.CreateProxy(proxy); }
            catch (ArgumentException ex) { v.Add("outboundProxy", ex.Message); }
        }
        if (body.ProxyCaddyTraffic && proxy is null)
            v.Add("proxyCaddyTraffic", "Set the outbound proxy first: Caddy can only use a proxy when one is configured.");
        var noProxy = CaddyHostSupport.NormalizeNoProxy(body.NoProxy);
        foreach (var e in CaddyHostSupport.ValidateNoProxy(noProxy)) v.Add("noProxy", e);
        if (noProxy.Length > 2000) v.Add("noProxy", "NO_PROXY is limited to 2000 characters.");
        string? repo = null;
        try { repo = CaddyBinaryManager.NormalizeReleaseRepo(body.ManagerReleaseRepo); }
        catch (ArgumentException ex) { v.Add("managerReleaseRepo", ex.Message); }
        if (!v.IsValid) return (null, v.ToResult());
        var (plugins, error) = await ValidatePluginsAsync(body.Plugins ?? [], bin, ct);
        if (error is not null) return (null, error);

        return (new BinarySettings
        {
            Plugins = plugins,
            AutoCheckUpdates = body.AutoCheckUpdates,
            CheckIntervalHours = body.CheckIntervalHours,
            AutoInstallUpdates = body.AutoInstallUpdates,
            OutboundProxy = proxy,
            ProxyCaddyTraffic = body.ProxyCaddyTraffic,
            NoProxy = noProxy,
            ManagerReleaseRepo = repo,
            LastCheckedAt = current.LastCheckedAt,           // server-managed
            LatestKnownVersion = current.LatestKnownVersion, // server-managed
        }, null);
    }

    private static bool SameEnvironment(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

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
        ProxyCaddyTraffic = s.ProxyCaddyTraffic,
        NoProxy = s.NoProxy,
        ManagerReleaseRepo = s.ManagerReleaseRepo,
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
            // Environment.Exit ends the process without the Windows service lifetime reporting SERVICE_STOPPED
            // (WindowsServiceLifetime, runtime release/10.0, has no ProcessExit handler). The SCM therefore sees a service
            // that "terminated unexpectedly" (System event 7031/7034) and runs the recovery actions (restart after 5 s), which
            // does not depend on the failure-actions flag: "failure actions are queued only if the service terminates without
            // reporting a status of SERVICE_STOPPED" when the flag is FALSE.
            // https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-service_failure_actions_flag
            // A graceful StopApplication() would report SERVICE_STOPPED and only be recovered with the flag, whose change
            // Microsoft documents as taking effect "the next time the system is started". Hosted services are not stopped:
            // the database (LiteDB) is crash-safe and the host flushes the file log on ProcessExit (Program.cs).
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(1.5)); // let the response and the audit entry flush
                Environment.Exit(1);
            });
            return Results.Accepted(value: new { message = "The manager service is restarting; the UI will be back in a few seconds." });
        }).RequireAuthorization(Policies.Admin);
    }

    /// <summary>Request body limit for one endpoint (honoured by Kestrel through endpoint routing).</summary>
    private sealed class UploadSizeLimit(long bytes) : IRequestSizeLimitMetadata
    {
        public long? MaxRequestBodySize => bytes;
    }

    internal static string ProductVersion() =>
        (Assembly.GetEntryAssembly() ?? typeof(PlatformEndpoints).Assembly).GetName().Version?.ToString(3) ?? "0.0.0";
}
