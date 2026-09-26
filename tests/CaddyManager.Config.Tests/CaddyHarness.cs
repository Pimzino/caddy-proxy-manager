using System.Diagnostics;
using System.Text;
using CaddyManager.Config.Services;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Config.Tests;

/// <summary>Skips when the development Caddy binary is not present.</summary>
public sealed class CaddyFactAttribute : FactAttribute
{
    public CaddyFactAttribute()
    {
        if (CaddyBinary.Path is null) Skip = "Caddy binary not found (.dev/bin/caddy or CPM_TEST_CADDY).";
    }
}

public sealed class RecordingEventSink : IEventSink
{
    public List<(EventSeverity Severity, string Category, string Message, string? Key, string? AlertRule)> Events { get; } = new();

    /// <summary>The details text of every event, in order.</summary>
    public List<string?> Details { get; } = new();

    public void Raise(EventSeverity severity, string category, string message, string? details = null, string? key = null, string? alertRule = null)
    {
        lock (Events)
        {
            Events.Add((severity, category, message, key, alertRule));
            Details.Add(details);
        }
    }
}

public sealed class RecordingAuditLog : IAuditLog
{
    public List<(string Action, string ObjectType, string? ObjectId)> Entries { get; } = new();

    public void Record(string action, string objectType, string? objectId = null, string? objectName = null, string? details = null)
    {
        lock (Entries) Entries.Add((action, objectType, objectId));
    }

    public void RecordAs(string userName, string action, string objectType, string? objectId = null, string? objectName = null, string? details = null) =>
        Record(action, objectType, objectId, objectName, details);
}

/// <summary>Service provider with Core + Config wired against a temp data dir.</summary>
public sealed class ConfigServices : IDisposable
{
    public TempEnv Env { get; } = new();
    public ServiceProvider Provider { get; }
    public RecordingEventSink Events { get; } = new();

    public ConfigServices(bool installBinary, string? caddyBinary = null)
    {
        if (installBinary) InstallBinary(Env.Paths, caddyBinary);
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddCore(Env.Paths, Env.Store);
        services.AddConfigModule();
        services.AddSingleton<IEventSink>(Events);
        Provider = services.BuildServiceProvider();
    }

    public IStore Store => Env.Store;
    public AppPaths Paths => Env.Paths;
    public CaddyConfigService Config => Provider.GetRequiredService<CaddyConfigService>();

    public static void InstallBinary(AppPaths paths, string? source = null)
    {
        var src = source ?? CaddyBinary.Path ?? throw new InvalidOperationException("no caddy binary");
        Directory.CreateDirectory(paths.CaddyBinDir);
        if (File.Exists(paths.CaddyExe)) return;
        if (OperatingSystem.IsWindows()) File.Copy(src, paths.CaddyExe);
        else File.CreateSymbolicLink(paths.CaddyExe, src);
    }

    public void Dispose()
    {
        Provider.Dispose();
        Env.Dispose();
    }
}

/// <summary>A running `caddy run` process (killed on dispose).</summary>
public sealed class CaddyProcess : IDisposable
{
    private readonly Process _p;
    private readonly StringBuilder _output = new();

    public CaddyProcess(AppPaths paths)
    {
        var psi = new ProcessStartInfo(paths.CaddyExe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = paths.DataDir,
        };
        foreach (var a in new[] { "run", "--config", paths.CaddyConfigFile }) psi.ArgumentList.Add(a);
        psi.Environment["XDG_DATA_HOME"] = paths.CaddyStorageDir;
        psi.Environment["XDG_CONFIG_HOME"] = Path.Combine(paths.DataDir, "caddy", "config");
        _p = new Process { StartInfo = psi };
        _p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (_output) _output.AppendLine(e.Data); };
        _p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (_output) _output.AppendLine(e.Data); };
        _p.Start();
        _p.BeginOutputReadLine();
        _p.BeginErrorReadLine();
    }

    public string Output { get { lock (_output) return _output.ToString(); } }
    public bool HasExited => _p.HasExited;

    public async Task WaitForExitAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try { await _p.WaitForExitAsync(cts.Token); } catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        try
        {
            if (!_p.HasExited)
            {
                _p.Kill(entireProcessTree: true);
                _p.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException) { }
        _p.Dispose();
    }
}

/// <summary>Minimal Kestrel upstream that echoes what it received.</summary>
public sealed class EchoUpstream : IAsyncDisposable
{
    private readonly WebApplication _app;
    public int Port { get; }

    private EchoUpstream(WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    public static async Task<EchoUpstream> StartAsync(bool https = false)
    {
        var port = Net.FreeTcpPort();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        if (https)
        {
            // Self-signed backend, like an appliance's management interface.
            using var key = System.Security.Cryptography.RSA.Create(2048);
            var req = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=backend", key,
                System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            var cert = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
            var pfx = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx), null);
            builder.WebHost.ConfigureKestrel(k => k.Listen(System.Net.IPAddress.Loopback, port, o => o.UseHttps(pfx)));
        }
        else builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        app.Run(async ctx =>
        {
            if (ctx.Request.Path == "/big")
            {
                ctx.Response.ContentType = "text/plain";
                await ctx.Response.WriteAsync(new string('x', 4096));
                return;
            }
            ctx.Response.ContentType = "text/plain";
            ctx.Response.Headers["Server"] = "echo";
            var sb = new StringBuilder();
            sb.Append("path=").Append(ctx.Request.Path).Append(ctx.Request.QueryString).Append('\n');
            sb.Append("host=").Append(ctx.Request.Host).Append('\n');
            sb.Append("auth=").Append(ctx.Request.Headers.Authorization.Count > 0 ? "yes" : "no").Append('\n');
            sb.Append("x-test=").Append(ctx.Request.Headers["X-Test"].ToString()).Append('\n');
            await ctx.Response.WriteAsync(sb.ToString());
        });
        await app.StartAsync();
        return new EchoUpstream(app, port);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
