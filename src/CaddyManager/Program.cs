using System.Net;
using System.Reflection;
using CaddyManager;
using CaddyManager.Config;
using CaddyManager.Core;
using CaddyManager.Core.Infrastructure;
using CaddyManager.Core.Models;
using CaddyManager.Ops;
using CaddyManager.Platform;
using Microsoft.Extensions.FileProviders;

// CLI verbs (install / uninstall / reset-password ...) run and exit before the web host starts.
if (await PlatformCli.TryRunAsync(args) is int platformExit) return platformExit;
if (await OpsCli.TryRunAsync(args) is int opsExit) return opsExit;

var paths = new AppPaths();
paths.EnsureCreated();

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Runs as a Windows service (LocalSystem) when started by the SCM; console otherwise.
builder.Host.UseWindowsService(o => o.ServiceName = AppPaths.ManagerServiceName);
builder.Logging.AddProvider(new FileLoggerProvider(paths.ManagerLogDir));
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

var store = new LiteStore(paths);
builder.Services
    .AddCore(paths, store)
    .AddConfigModule()
    .AddPlatformModule()
    .AddOpsModule();
builder.Services.ConfigureHttpJsonOptions(o => JsonDefaults.Configure(o.SerializerOptions));
builder.Services.AddProblemDetails();

// ---- Management UI listener (from UiSettings; CM_UI_PORT overrides for development)
var ui = store.GetSettings<UiSettings>();
var uiPort = int.TryParse(Environment.GetEnvironmentVariable("CM_UI_PORT"), out var p) ? p : ui.Port;
var bind = IPAddress.TryParse(ui.BindAddress, out var ip) ? ip : IPAddress.Any;
builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    k.Listen(bind, uiPort);
    if (ui.HttpsEnabled)
    {
        var cert = UiCertificate.Load(paths, ui, new SecretProtector(paths));
        k.Listen(bind, ui.HttpsPort, o => o.UseHttps(cert));
    }
});

var app = builder.Build();

app.UseExceptionHandler();
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "same-origin";
    h["Content-Security-Policy"] =
        "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self'; font-src 'self' data:; connect-src 'self'; frame-ancestors 'none'";
    await next();
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

// ---- Web UI: embedded SPA build (web/dist). CM_WEB_DIR serves from disk for development.
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
});

app.Logger.LogInformation("{Product} starting. Data: {Data}. UI: http://{Bind}:{Port}", AppPaths.ProductName, paths.DataDir, bind, uiPort);
await app.RunAsync();
return 0;
