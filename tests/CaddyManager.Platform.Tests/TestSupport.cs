using System.Collections.Concurrent;
using System.Net.Sockets;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Infrastructure;
using CaddyManager.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Platform.Tests;

public static class Fixture
{
    public static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
    public static string Read(string name) => File.ReadAllText(Path(name));
}

/// <summary>Temporary data directory with its own AppPaths and LiteStore.</summary>
public sealed class TempEnvironment : IDisposable
{
    public string Root { get; }
    public AppPaths Paths { get; }
    public LiteStore Store { get; }

    public TempEnvironment()
    {
        Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cpm-platform-tests", Guid.NewGuid().ToString("N")[..10]);
        Paths = new AppPaths(Root);
        Paths.EnsureCreated();
        Store = new LiteStore(Paths);
    }

    public void Dispose()
    {
        Store.Dispose();
        for (var i = 0; i < 5; i++)
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(200);
            }
        }
    }
}

/// <summary>Locates the development Caddy binary (.dev/bin/caddy) by walking up from the test output directory.</summary>
public static class DevCaddy
{
    public static string? Find()
    {
        var env = Environment.GetEnvironmentVariable("CM_TEST_CADDY");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        var name = OperatingSystem.IsWindows() ? "caddy.exe" : "caddy";
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; dir is not null && i < 10; i++, dir = dir.Parent)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, ".dev", "bin", name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Copies the dev binary into AppPaths.CaddyExe, or skips the test when it is not available.</summary>
    public static void InstallInto(AppPaths paths)
    {
        var src = Find();
        Assert.SkipWhen(src is null, "Development Caddy binary (.dev/bin/caddy or CM_TEST_CADDY) not found.");
        Directory.CreateDirectory(paths.CaddyBinDir);
        File.Copy(src!, paths.CaddyExe, overwrite: true);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(paths.CaddyExe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public static int FreeTcpPort()
    {
        var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>Minimal Caddy JSON config: admin on the given loopback port, one HTTP server answering "hello".</summary>
    public static string MinimalConfig(int adminPort, int httpPort, string logFile) => $$"""
        {
          "admin": { "listen": "127.0.0.1:{{adminPort}}", "config": { "persist": false } },
          "logging": { "logs": { "default": { "level": "INFO", "writer": { "output": "file", "filename": {{System.Text.Json.JsonSerializer.Serialize(logFile)}} } } } },
          "apps": {
            "http": {
              "http_port": {{httpPort}},
              "servers": {
                "srv0": {
                  "listen": ["127.0.0.1:{{httpPort}}"],
                  "automatic_https": { "disable": true },
                  "routes": [ { "handle": [ { "handler": "static_response", "body": "hello" } ] } ]
                }
              }
            }
          }
        }
        """;
}

/// <summary>Admin client talking to a real Caddy admin endpoint (only the members the Platform module uses).</summary>
public sealed class FakeAdminClient(string baseUrl) : ICaddyAdminClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    public string BaseUrl { get; } = baseUrl.TrimEnd('/');
    /// <summary>When true, IsReachableAsync always reports false (simulates a broken new binary).</summary>
    public volatile bool ForceUnreachable;
    public int StopCalls;

    public async Task<bool> IsReachableAsync(CancellationToken ct = default)
    {
        if (ForceUnreachable) return false;
        try
        {
            using var resp = await Http.GetAsync(BaseUrl + "/config/", ct);
            return resp.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref StopCalls);
        try
        {
            using var resp = await Http.PostAsync(BaseUrl + "/stop", null, ct);
        }
        catch (HttpRequestException)
        {
            // Caddy may close the connection while exiting
        }
    }

    public async Task<string?> GetConfigAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await Http.GetAsync(BaseUrl + "/config/", ct);
            return resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync(ct) : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    public Task LoadAsync(string json, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<(string Json, List<string> Warnings)> AdaptCaddyfileAsync(string caddyfile, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<List<UpstreamHealth>> GetUpstreamsAsync(CancellationToken ct = default) => Task.FromResult(new List<UpstreamHealth>());
}

/// <summary>Config service that writes a fixed boot config and records apply calls.</summary>
public sealed class FakeConfigService(AppPaths paths, string bootConfig) : ICaddyConfigService
{
    public ConcurrentQueue<string> Applied { get; } = new();

    public string BuildConfigJson() => bootConfig;

    public Task<ApplyResult> ApplyAsync(string reason, CancellationToken ct = default)
    {
        Applied.Enqueue(reason);
        return Task.FromResult(new ApplyResult { Success = true, WrittenOnly = false });
    }

    public Task<ValidationResult> ValidateAsync(string json, CancellationToken ct = default) =>
        Task.FromResult(new ValidationResult { Valid = true });

    public void EnsureBootConfig()
    {
        if (!File.Exists(paths.CaddyConfigFile)) File.WriteAllText(paths.CaddyConfigFile, bootConfig);
    }
}

public sealed class FakeEventSink : IEventSink
{
    public ConcurrentQueue<(EventSeverity Severity, string Category, string Message, string? Details, string? Key, string? AlertRule)> Events { get; } = new();

    public void Raise(EventSeverity severity, string category, string message, string? details = null, string? key = null, string? alertRule = null) =>
        Events.Enqueue((severity, category, message, details, key, alertRule));
}

public sealed class FakeAuditLog : IAuditLog
{
    public ConcurrentQueue<string> Entries { get; } = new();
    public void Record(string action, string objectType, string? objectId = null, string? objectName = null, string? details = null) =>
        Entries.Enqueue($"{action} {objectType} {objectId} {details}");
}

/// <summary>Service provider wired like the real app (Core pieces + AddPlatformModule) with fakes for the other modules.</summary>
public sealed class PlatformServices : IDisposable
{
    public ServiceProvider Provider { get; }
    public FakeAdminClient Admin { get; }
    public FakeConfigService Config { get; }
    public FakeEventSink Events { get; } = new();
    public FakeAuditLog Audit { get; } = new();

    public PlatformServices(TempEnvironment env, int adminPort, string bootConfig, Action<IServiceCollection>? configure = null)
    {
        Admin = new FakeAdminClient($"http://127.0.0.1:{adminPort}");
        Config = new FakeConfigService(env.Paths, bootConfig);
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Debug));
        sc.AddSingleton(env.Paths);
        sc.AddSingleton<IStore>(env.Store);
        sc.AddSingleton<IJobRunner, JobRunner>();
        sc.AddHttpClient("default", c =>
        {
            c.DefaultRequestHeaders.UserAgent.ParseAdd("CaddyProxyManager-Tests/1.0");
            c.Timeout = TimeSpan.FromMinutes(10);
        });
        sc.AddSingleton<ICaddyAdminClient>(Admin);
        sc.AddSingleton<ICaddyConfigService>(Config);
        sc.AddSingleton<IEventSink>(Events);
        sc.AddSingleton<IAuditLog>(Audit);
        sc.AddPlatformModule();
        // Always the child-process host in tests (never touch real Windows services).
        sc.AddSingleton<ICaddyHost>(sp => ActivatorUtilities.CreateInstance<Hosting.ProcessCaddyHost>(sp));
        configure?.Invoke(sc);
        Provider = sc.BuildServiceProvider();
    }

    public T Get<T>() where T : notnull => Provider.GetRequiredService<T>();

    public void Dispose() => Provider.Dispose();
}
