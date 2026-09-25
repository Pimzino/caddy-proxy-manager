using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using CaddyManager.Config;
using CaddyManager.Config.Certificates;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Infrastructure;
using CaddyManager.Core.Models;
using CaddyManager.Ops;
using CaddyManager.Platform;
using CaddyManager.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Cluster.Tests;

/// <summary>The development Caddy binary (.dev/bin/caddy, v2.11.4) found above the test binaries, or CM_DEV_CADDY.</summary>
public static class DevCaddy
{
    public static readonly string? Path = Find("caddy", "CM_DEV_CADDY");
    /// <summary>.dev/bin/caddy-plugins: v2.11.4 with caddy-dns/cloudflare, caddyserver/ntlm-transport and mholt/caddy-l4.</summary>
    public static readonly string? PluginsPath = Find("caddy-plugins", "CM_DEV_CADDY_PLUGINS");

    private static string? Find(string baseName, string variable)
    {
        if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } env && File.Exists(env)) return env;
        var name = OperatingSystem.IsWindows() ? baseName + ".exe" : baseName;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, ".dev", "bin", name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}

public static class Net
{
    private static readonly HashSet<int> Handed = new();

    /// <summary>A loopback port free for TCP (and UDP, where Caddy may bind HTTPS) not handed out before in this run.</summary>
    public static int FreePort()
    {
        lock (Handed)
        {
            while (true)
            {
                var l = new TcpListener(IPAddress.Loopback, 0);
                l.Start();
                var port = ((IPEndPoint)l.LocalEndpoint).Port;
                l.Stop();
                if (!Handed.Add(port)) continue;
                try { using var u = new UdpClient(new IPEndPoint(IPAddress.Any, port)); }
                catch (SocketException) { continue; }
                return port;
            }
        }
    }
}

/// <summary>Polling helper: no fixed sleeps, every wait has a deadline and a description of what did not happen.</summary>
public static class Wait
{
    public static async Task<T> ForAsync<T>(Func<Task<T?>> probe, TimeSpan timeout, Func<string> what) where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (true)
        {
            try
            {
                if (await probe() is { } value) return value;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or TaskCanceledException or JsonException or InvalidOperationException)
            {
                last = ex;
            }
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0} s waiting for: {what()}" + (last is null ? "" : $" (last error: {last.Message})"));
            await Task.Delay(100);
        }
    }

    public static Task UntilAsync(Func<Task<bool>> probe, TimeSpan timeout, Func<string> what) =>
        ForAsync(async () => await probe() ? "ok" : null, timeout, what);

    /// <summary>Polls until the probe reports Ok and returns its value.</summary>
    public static async Task<T> ForValueAsync<T>(Func<Task<(bool Ok, T Value)>> probe, TimeSpan timeout, Func<string> what)
    {
        var box = await ForAsync(async () =>
        {
            var (ok, value) = await probe();
            return ok ? new Box<T>(value) : null;
        }, timeout, what);
        return box.Value;
    }

    private sealed record Box<T>(T Value);
}

/// <summary>Stand-in for the Config module's IConfigChangeFeed while this build's Config module does not provide one.</summary>
public sealed class FakeConfigChangeFeed : IConfigChangeFeed
{
    public event Action<ApplyResult, string>? Applied;
    public void Raise(string reason) => Applied?.Invoke(new ApplyResult { Success = true }, reason);
}

/// <summary>
/// ICertificateMaterialStore over the Config module's CertificateFileStore — registered only when the Config module in
/// this build does not provide one yet (it is implemented by the Config builder in parallel).
/// </summary>
public sealed class TestCertificateMaterialStore(CertificateFileStore files) : ICertificateMaterialStore
{
    public (string CertPath, string KeyPath) WritePem(string certificateId, string certificatePem, string privateKeyPem) =>
        files.Write(certificateId, CertificateParser.FromPem(certificatePem, privateKeyPem));

    public void Delete(string certificateId)
    {
        var dir = System.IO.Path.Combine(files.StoreRoot, certificateId);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}

/// <summary>Collects log lines of one manager (printed when a test fails).</summary>
public sealed class CaptureLoggerProvider(string prefix) : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();
    public ILogger CreateLogger(string categoryName) => new L(this, categoryName);
    public void Dispose() { }

    private sealed class L(CaptureLoggerProvider p, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= (category.StartsWith("CaddyManager", StringComparison.Ordinal) ? LogLevel.Information : LogLevel.Warning);
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            p.Lines.Enqueue($"{DateTime.UtcNow:HH:mm:ss.fff} [{p.Prefix}] {level} {category}: {formatter(state, ex)}{(ex is null ? "" : " | " + ex.Message)}");
            while (p.Lines.Count > 3000) p.Lines.TryDequeue(out _);
        }
    }

    public string Prefix => prefix;
}

/// <summary>
/// A complete Caddy Proxy Manager composed like src/CaddyManager/Program.cs (Core + Config + Platform + Ops + Telemetry +
/// Cluster) on a real Kestrel loopback port, with its own data directory and a real Caddy child process ("process" host
/// mode, .dev/bin/caddy) on its own HTTP/HTTPS/admin ports. Stopping and starting again with the same data directory and
/// ports is a manager restart.
/// </summary>
public sealed class Manager : IAsyncDisposable
{
    public const string AdminEmail = "admin@cluster.test";
    public const string AdminPassword = "cluster e2e password";

    public string Name { get; }
    public string DataDir { get; }
    public AppPaths Paths { get; }
    public int UiPort { get; }
    public int HttpPort { get; }
    public int HttpsPort { get; }
    public int AdminPort { get; }
    public string Url => UiCertificate is null ? $"http://127.0.0.1:{UiPort}" : $"https://127.0.0.1:{UiPort}";
    /// <summary>When set, the management UI listens with HTTPS using this certificate (takes effect at the next start).</summary>
    public System.Security.Cryptography.X509Certificates.X509Certificate2? UiCertificate { get; set; }
    public WebApplication App { get; private set; } = null!;
    public HttpClient Api { get; private set; } = null!;
    public CaptureLoggerProvider Logs { get; }
    /// <summary>Type of the registered IServerTelemetry (the Telemetry module's ServerTelemetry).</summary>
    public string? TelemetryImplementation { get; private set; }
    public bool UsesTestMaterialStore { get; private set; }
    /// <summary>Set when the Config module in this build has no IConfigChangeFeed: the test raises Applied itself.</summary>
    public FakeConfigChangeFeed? FakeFeed { get; private set; }
    private Action<ClusterOptions>? _clusterOptions;
    private LiteStore? _store;

    private Manager(string name, string dataDir, int ui, int http, int https, int admin, CaptureLoggerProvider logs)
    {
        Name = name;
        DataDir = dataDir;
        Paths = new AppPaths(dataDir);
        UiPort = ui;
        HttpPort = http;
        HttpsPort = https;
        AdminPort = admin;
        Logs = logs;
    }

    public IServiceProvider Services => App.Services;
    public IStore Store => Services.GetRequiredService<IStore>();

    /// <summary>New data directory: seeds settings (ports, admin listen, display name) and an admin user, installs the dev Caddy binary.</summary>
    public static async Task<Manager> CreateAsync(string name, System.Security.Cryptography.X509Certificates.X509Certificate2? uiCertificate = null,
        string? caddyBinary = null, Action<ClusterOptions>? clusterOptions = null)
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cpm-cluster-e2e", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        var m = new Manager(name, dir, Net.FreePort(), Net.FreePort(), Net.FreePort(), Net.FreePort(), new CaptureLoggerProvider(name))
        {
            UiCertificate = uiCertificate,
            _clusterOptions = clusterOptions,
        };
        m.Paths.EnsureCreated();
        using (var seed = new LiteStore(m.Paths))
        {
            seed.SaveSettings(new CaddySettings
            {
                HttpPort = m.HttpPort, HttpsPort = m.HttpsPort, AdminListen = $"127.0.0.1:{m.AdminPort}", LogLevel = "info", EnableHttp3 = false,
            });
            seed.SaveSettings(new UiSettings { DisplayName = name, Port = m.UiPort, BindAddress = "127.0.0.1" });
            seed.SaveSettings(new BinarySettings { AutoCheckUpdates = false });
            seed.Col<User>().Insert(new User { Email = AdminEmail, Name = "Admin", Role = UserRole.Admin, PasswordHash = Passwords.Hash(AdminPassword) });
        }
        var bin = caddyBinary ?? DevCaddy.Path ?? throw new InvalidOperationException("The development Caddy binary .dev/bin/caddy was not found (copy it from the main checkout).");
        File.Copy(bin, m.Paths.CaddyExe, overwrite: true);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(m.Paths.CaddyExe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await m.StartAsync();
        return m;
    }

    public async Task StartAsync()
    {
        _store = new LiteStore(Paths);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production", ContentRootPath = AppContext.BaseDirectory });
        builder.WebHost.UseKestrel(k =>
        {
            k.AddServerHeader = false;
            if (UiCertificate is { } cert) k.Listen(IPAddress.Loopback, UiPort, o => o.UseHttps(cert));
            else k.Listen(IPAddress.Loopback, UiPort);
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(Logs);
        builder.Services
            .AddCore(Paths, _store)
            .AddConfigModule()
            .AddPlatformModule()
            .AddOpsModule()
            .AddTelemetryModule()
            .AddClusterModule();
        builder.Services.Configure<OpsOptions>(o => o.EnableBackgroundServices = false);
        builder.Services.Configure<ClusterOptions>(o =>
        {
            o.HeartbeatInterval = TimeSpan.FromMilliseconds(500);
            o.SyncDebounce = TimeSpan.FromMilliseconds(200);
            o.QueryTimeout = TimeSpan.FromSeconds(5);
            o.OfflineThreshold = 3;
            _clusterOptions?.Invoke(o);
        });
        // The real Telemetry module (sampler + stats-log ingester running) with short timers: a sample every 500 ms, the
        // stats log tailed every 200 ms and flushed every 500 ms, server facts re-read after 1 s.
        builder.Services.Configure<TelemetryOptions>(o =>
        {
            o.SampleInterval = TimeSpan.FromMilliseconds(500);
            o.CaddyStatusCacheDuration = TimeSpan.FromMilliseconds(500);
            o.InfoCacheDuration = TimeSpan.FromSeconds(1);
            o.IngestInterval = TimeSpan.FromMilliseconds(200);
            o.FlushInterval = TimeSpan.FromMilliseconds(500);
        });
        var fakeFeed = new FakeConfigChangeFeed();
        builder.Services.TryAddSingleton<IConfigChangeFeed>(fakeFeed);
        // ICertificateMaterialStore is provided by the Config module; the stand-in is used only when it is absent.
        builder.Services.TryAddSingleton<ICertificateMaterialStore>(sp => new TestCertificateMaterialStore(sp.GetRequiredService<CertificateFileStore>()));
        builder.Services.ConfigureHttpJsonOptions(o => JsonDefaults.Configure(o.SerializerOptions));
        builder.Services.AddProblemDetails();

        var app = builder.Build();
        app.UseExceptionHandler();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/api/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
        app.MapCoreEndpoints();
        app.MapConfigEndpoints();
        app.MapPlatformEndpoints();
        app.MapOpsEndpoints();
        app.MapTelemetryEndpoints();
        app.MapClusterEndpoints();
        App = app;
        TelemetryImplementation = app.Services.GetService<IServerTelemetry>()?.GetType().FullName;
        UsesTestMaterialStore = app.Services.GetService<ICertificateMaterialStore>() is TestCertificateMaterialStore;
        FakeFeed = ReferenceEquals(app.Services.GetService<IConfigChangeFeed>(), fakeFeed) ? fakeFeed : null;
        await app.StartAsync();

        Api = NewClient();
        var login = await Api.PostAsJsonAsync("api/auth/login", new { email = AdminEmail, password = AdminPassword });
        Assert.True(login.IsSuccessStatusCode, $"{Name}: login failed {(int)login.StatusCode} {await login.Content.ReadAsStringAsync()}");
        // Caddy is started by the Platform bootstrapper in the background: wait until it runs with the manager's config.
        await Wait.UntilAsync(async () =>
        {
            var s = await Api.GetFromJsonAsync<JsonElement>("api/caddy/status");
            return s.GetProperty("state").GetString() == "running" && s.GetProperty("adminReachable").GetBoolean();
        }, TimeSpan.FromSeconds(60), () => $"{Name}: Caddy running\n{LogTail()}");
    }

    public HttpClient NewClient(bool csrf = true)
    {
        var c = new HttpClient(new HttpClientHandler
        {
            CookieContainer = new CookieContainer(), UseProxy = false,
            // Test client only: the node's self-signed UI certificate (the primary's own validation is what is tested).
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        })
        {
            BaseAddress = new Uri(Url + "/"),
            Timeout = TimeSpan.FromSeconds(150),
        };
        if (csrf) c.DefaultRequestHeaders.Add("X-CPM-Request", "1");
        return c;
    }

    /// <summary>Stops the manager (its Caddy child stops with it) and releases the database; the data directory stays.</summary>
    public async Task StopAsync()
    {
        Api.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
        _store?.Dispose();
        _store = null;
    }

    public string LogTail(int lines = 80) => string.Join("\n", Logs.Lines.TakeLast(lines));

    public async ValueTask DisposeAsync()
    {
        if (_store is not null)
        {
            try { await StopAsync(); } catch { /* best effort */ }
        }
        try { Directory.Delete(DataDir, recursive: true); } catch { /* temp */ }
    }
}

public static class JsonHttp
{
    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync();
        return JsonDocument.Parse(string.IsNullOrEmpty(text) ? "null" : text).RootElement.Clone();
    }

    public static async Task<JsonElement> OkJsonAsync(this Task<HttpResponseMessage> call, string what)
    {
        using var resp = await call;
        var text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"{what}: HTTP {(int)resp.StatusCode} {text}");
        return JsonDocument.Parse(string.IsNullOrEmpty(text) ? "null" : text).RootElement.Clone();
    }
}
