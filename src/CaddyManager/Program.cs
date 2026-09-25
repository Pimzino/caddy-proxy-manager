using System.Reflection;
using CaddyManager;
using CaddyManager.Cluster;
using CaddyManager.Config;
using CaddyManager.Core;
using CaddyManager.Core.Infrastructure;
using CaddyManager.Core.Models;
using CaddyManager.Ops;
using CaddyManager.Ops.Events;
using CaddyManager.Ops.Settings;
using CaddyManager.Platform;
using CaddyManager.Telemetry;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.EventLog;

// CLI verbs (install / uninstall / reset-password ...) run and exit before the web host starts.
if (await PlatformCli.TryRunAsync(args) is int platformExit) return platformExit;
if (await OpsCli.TryRunAsync(args) is int opsExit) return opsExit;
if (await ClusterCli.TryRunAsync(args) is int clusterExit) return clusterExit;

var paths = new AppPaths();
paths.EnsureCreated();
// DataDir holds the database, key ring, private keys and the setup token. The installer restricts it to
// SYSTEM + Administrators; re-apply that when it still inherits ProgramData's "Users: read/create" ACL
// (e.g. the service was registered by hand), so local non-admin users cannot read or plant files there.
var startupWarnings = new List<string>();
if (DataDirSecurity.EnsureRestricted(paths.DataDir) is { } aclWarning) startupWarnings.Add(aclWarning);
// A backup restore uploaded through the UI is staged, then applied here before the database is opened.
CaddyManager.Ops.Backup.RestoreStager.ApplyPendingRestore(paths);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Runs as a Windows service (LocalSystem) when started by the SCM; console otherwise.
builder.Host.UseWindowsService(o => o.ServiceName = AppPaths.ManagerServiceName);
var fileLog = new FileLoggerProvider(paths.ManagerLogDir);
builder.Logging.AddProvider(fileLog);
// POST /api/system/restart ends the process with Environment.Exit (see PlatformEndpoints), which skips the host shutdown
// but raises ProcessExit: flush the queued log lines there (Dispose is idempotent and waits at most 3 s).
AppDomain.CurrentDomain.ProcessExit += (_, _) => fileLog.Dispose();
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
// Windows Event Log (Application): warnings and errors under the product's source, whose message file is repaired
// below. Operational events are written by the Ops event sink itself (with stable event IDs), so its own log lines
// are not duplicated there.
builder.Services.Configure<EventLogSettings>(o => o.SourceName = EventLogSource.Name);
builder.Logging.AddFilter<EventLogLoggerProvider>("CaddyManager.Ops.Events.EventSink", LogLevel.None);
var hostWarnings = new List<string>();
if (EventLogSource.Ensure() is { } eventLogWarning) hostWarnings.Add(eventLogWarning);

var store = new LiteStore(paths);
builder.Services
    .AddCore(paths, store)
    .AddConfigModule()
    .AddPlatformModule()
    .AddOpsModule()
    .AddTelemetryModule()
    .AddClusterModule();
builder.Services.ConfigureHttpJsonOptions(o => JsonDefaults.Configure(o.SerializerOptions));
builder.Services.AddProblemDetails();

// ---- Management UI listener (from UiSettings; CM_UI_PORT overrides for development).
// Never throws: invalid settings, an unavailable address/port or a broken PFX fall back to defaults with a
// warning instead of crash-looping the service (which would lock administrators out of the UI).
UiSettings ui;
try { ui = store.GetSettings<UiSettings>(); }
catch (Exception ex)
{
    startupWarnings.Add($"UI settings could not be read ({ex.Message}); using defaults.");
    ui = new UiSettings();
}
var listener = UiListener.Plan(paths, ui, Environment.GetEnvironmentVariable("CM_UI_PORT"), new SecretProtector(paths));
startupWarnings.AddRange(listener.Warnings);
// The UI certificate is loaded with MachineKeySet (SChannel cannot use ephemeral keys: dotnet/runtime#23749), which writes
// a key file under %ProgramData%\Microsoft\Crypto that .NET deletes only when the certificate is disposed. Kestrel keeps it
// for the process lifetime and finalizers do not run at exit, so without this every start (and every Restart from the UI,
// which ends with Environment.Exit) would leave one orphaned key file. ProcessExit is raised on a normal exit and on
// Environment.Exit (WindowsPlatformE2ETests.UiCertificateKeyFileIsRemovedOnExit).
if (listener.Certificate is { } uiCertificate)
    AppDomain.CurrentDomain.ProcessExit += (_, _) => uiCertificate.Dispose();
builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    k.Listen(listener.Http);
    if (listener.Https is not null && listener.Certificate is not null)
    {
        var cert = listener.Certificate;
        k.Listen(listener.Https, o => o.UseHttps(cert));
    }
});

var app = builder.Build();

app.UseExceptionHandler();
// X-Forwarded-For/Proto are honoured only from loopback peers (the UI published through Caddy on this server). Must run
// before authentication and the sign-in rate limiter so both see the real client address.
app.UseLoopbackForwardedHeaders();
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "same-origin";
    h["Cross-Origin-Opener-Policy"] = "same-origin";
    h["Cross-Origin-Resource-Policy"] = "same-origin";
    h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
    h["Content-Security-Policy"] =
        "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self'; font-src 'self' data:; " +
        "connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
    // API responses carry configuration, users and audit data: keep them out of browser/proxy caches.
    if (ctx.Request.Path.StartsWithSegments("/api")) h.CacheControl = "no-store";
    await next();
});

// Sessions must not travel in clear text once HTTPS is available: redirect plain-HTTP UI/API requests to the HTTPS
// listener (GET /api/health stays on HTTP). Only when the HTTPS listener is actually up, so a certificate problem
// can never lock administrators out.
var redirectToHttps = ui.HttpsEnabled && ui.RedirectHttpToHttps && listener.Https is not null;
if (redirectToHttps) app.UseUiHttpsRedirect(listener.Https!.Port);

// ---- Web UI: embedded SPA build (web/dist). CM_WEB_DIR serves from disk for development.
// Static assets are public and served before authentication: the authorization fallback policy
// (authenticated user required for anything not explicitly anonymous) must not apply to them.
IFileProvider web = Environment.GetEnvironmentVariable("CM_WEB_DIR") is { Length: > 0 } dir
    ? new PhysicalFileProvider(Path.GetFullPath(dir))
    : new ManifestEmbeddedFileProvider(typeof(Program).Assembly, "wwwroot");
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = web,
    OnPrepareResponse = c =>
    {
        // Vite emits content-hashed assets; index.html must never be cached.
        c.Context.Response.Headers.CacheControl = c.File.Name == "index.html"
            ? "no-cache" : "public, max-age=31536000, immutable";
    },
});

// A file-like path (e.g. /assets/x.js) that is not an embedded file matches no endpoint: answer 404 here
// rather than letting the authorization fallback policy turn it into a 401.
app.Use((ctx, next) =>
{
    if (ctx.GetEndpoint() is null)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }
    return next(ctx);
});

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3),
    product = AppPaths.ProductName,
})).AllowAnonymous();

app.MapCoreEndpoints();
app.MapConfigEndpoints();
app.MapPlatformEndpoints();
app.MapOpsEndpoints();
app.MapTelemetryEndpoints();
app.MapClusterEndpoints();

app.MapFallback(async ctx =>
{
    if (ctx.Request.Path.StartsWithSegments("/api"))
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    var index = web.GetFileInfo("index.html");
    if (!index.Exists)
    {
        ctx.Response.ContentType = "text/plain";
        await ctx.Response.WriteAsync("Web UI not built. Run: cd web && npm ci && npm run build");
        return;
    }
    ctx.Response.ContentType = "text/html; charset=utf-8";
    ctx.Response.Headers.CacheControl = "no-cache";
    await using var s = index.CreateReadStream();
    await s.CopyToAsync(ctx.Response.Body);
}).AllowAnonymous();

var httpsNote = listener.Https is not null ? $" and https://{listener.Https}" : "";
app.Logger.LogInformation("{Product} starting. Data: {Data}. UI: http://{Http}{Https}{Redirect}", AppPaths.ProductName, paths.DataDir,
    listener.Http, httpsNote, redirectToHttps ? " (HTTP redirects to HTTPS)" : "");
if (ui.HttpsEnabled && ui.RedirectHttpToHttps && !redirectToHttps)
    app.Logger.LogWarning("Redirect to HTTPS is enabled but the HTTPS listener is not running; plain HTTP stays available.");
foreach (var w in hostWarnings) app.Logger.LogWarning("Startup: {Warning}", w);
if (startupWarnings.Count > 0)
{
    foreach (var w in startupWarnings) app.Logger.LogError("Startup: {Warning}", w);
    try
    {
        app.Services.GetRequiredService<IEventSink>().Raise(EventSeverity.Warning, "system",
            "Management UI started with fallback settings", string.Join(Environment.NewLine, startupWarnings));
    }
    catch (Exception ex) { app.Logger.LogWarning(ex, "Could not record the startup warnings as an event"); }
}
await app.RunAsync();
return 0;
