using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CaddyManager.Config;
using CaddyManager.Config.Generation;
using CaddyManager.Config.Services;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Infrastructure;
using CaddyManager.Core.Models;
using CaddyManager.Telemetry;
using CaddyManager.Telemetry.Traffic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

// Live tests start real Caddy processes and measure machine-wide CPU/connections: run them one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace CaddyManager.Telemetry.Tests;

/// <summary>
/// Verifiable, repeatable output of the end-to-end tests: one JSON file per test in CPM_E2E_ARTIFACTS (CI uploads it)
/// or ./e2e-artifacts next to the test binaries. Every report records the exact Caddy binary tested (`caddy version`).
/// </summary>
public static class E2EArtifacts
{
    private static string? _caddyVersion;

    public static string Directory
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("CPM_E2E_ARTIFACTS") is { Length: > 0 } d
                ? d
                : Path.Combine(AppContext.BaseDirectory, "e2e-artifacts");
            System.IO.Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string CaddyVersion
    {
        get
        {
            if (_caddyVersion is not null) return _caddyVersion;
            var bin = CaddyBinary.Path;
            if (bin is null) return _caddyVersion = "(no caddy binary)";
            using var p = Process.Start(new ProcessStartInfo(bin, "version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(10_000);
            return _caddyVersion = output;
        }
    }

    public static JsonObject Report(string test) => new()
    {
        ["test"] = test,
        ["utc"] = DateTime.UtcNow.ToString("O"),
        ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        ["machine"] = Environment.MachineName,
        ["caddyVersion"] = CaddyVersion,
    };

    public static string Write(string fileName, JsonObject report)
    {
        var path = Path.Combine(Directory, fileName);
        File.WriteAllText(path, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }
}

public static class CaddyBinary
{
    /// <summary>The development Caddy binary (repo .dev/bin/caddy, or CPM_TEST_CADDY) or null when missing.</summary>
    public static string? Path
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("CPM_TEST_CADDY");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                var candidate = System.IO.Path.Combine(dir.FullName, ".dev", "bin", OperatingSystem.IsWindows() ? "caddy.exe" : "caddy");
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            return null;
        }
    }
}

/// <summary>Skips when the development Caddy binary is not present.</summary>
public sealed class CaddyFactAttribute : FactAttribute
{
    public CaddyFactAttribute()
    {
        if (CaddyBinary.Path is null) Skip = "Caddy binary not found (.dev/bin/caddy or CPM_TEST_CADDY).";
    }
}

public static class Net
{
    private static readonly HashSet<int> Handed = new();

    public static int FreeTcpPort()
    {
        lock (Handed)
        {
            while (true)
            {
                var l = new TcpListener(IPAddress.Loopback, 0);
                l.Start();
                var port = ((IPEndPoint)l.LocalEndpoint).Port;
                l.Stop();
                if (Handed.Add(port)) return port;
            }
        }
    }
}

/// <summary>Temp data directory + LiteDB store; deleted on dispose.</summary>
public sealed class TempEnv : IDisposable
{
    public string Dir { get; }
    public AppPaths Paths { get; }
    public LiteStore Store { get; }

    public TempEnv()
    {
        Dir = Path.Combine(Path.GetTempPath(), "cpm-telemetry-tests", Guid.NewGuid().ToString("N")[..10]);
        System.IO.Directory.CreateDirectory(Dir);
        Paths = new AppPaths(Dir);
        Paths.EnsureCreated();
        Store = new LiteStore(Paths);
    }

    private TrafficStore? _traffic;

    /// <summary>
    /// The telemetry database (db/telemetry.db). One instance per environment, shared by <see cref="NewIngestion"/> and
    /// <see cref="Services"/>: LiteDB opens the file exclusively, like the single instance of the product.
    /// </summary>
    public TrafficStore Traffic => _traffic ??= new TrafficStore(Paths, Store);

    public TrafficIngestion NewIngestion(TimeSpan? flushInterval = null, Action<TelemetryOptions>? configure = null)
    {
        var options = new TelemetryOptions { FlushInterval = flushInterval ?? TimeSpan.FromSeconds(1) };
        configure?.Invoke(options);
        return new TrafficIngestion(Paths, Traffic, Store, new SecretProtector(Paths), Options.Create(options), TimeProvider.System,
            NullLogger<TrafficIngestion>.Instance);
    }

    /// <summary>Core + Telemetry services (background services off unless enabled) with optional Platform fakes.</summary>
    public ServiceProvider Services(Action<TelemetryOptions>? configure = null, ICaddyHost? host = null, ICaddyBinaryManager? binaries = null,
        Action<IServiceCollection>? extra = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddCore(Paths, Store);
        services.AddTelemetryModule();
        services.Configure<TelemetryOptions>(o =>
        {
            o.EnableBackgroundServices = false;
            configure?.Invoke(o);
        });
        services.AddSingleton(Traffic); // replaces the module's own instance (same file)
        if (host is not null) services.AddSingleton(host);
        if (binaries is not null) services.AddSingleton(binaries);
        extra?.Invoke(services);
        return services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _traffic?.Dispose();
        Store.Dispose();
        try { System.IO.Directory.Delete(Dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>A running `caddy run --config file` process (killed on dispose).</summary>
public sealed class CaddyProcess : IDisposable
{
    private readonly Process _p;
    private readonly StringBuilder _output = new();

    public CaddyProcess(AppPaths paths, string configJson)
    {
        File.WriteAllText(paths.CaddyConfigFile, configJson);
        var psi = new ProcessStartInfo(CaddyBinary.Path!)
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
        StartedAt = DateTime.UtcNow;
    }

    public int Pid => _p.Id;
    public DateTime StartedAt { get; }
    public string Output { get { lock (_output) return _output.ToString(); } }
    public bool HasExited => _p.HasExited;

    /// <summary>Waits until the port accepts TCP connections.</summary>
    public async Task WaitForPortAsync(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_p.HasExited) throw new InvalidOperationException("Caddy exited:\n" + Output);
            try
            {
                using var c = new TcpClient();
                await c.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException) { await Task.Delay(100); }
        }
        throw new TimeoutException($"Caddy did not listen on {port}:\n{Output}");
    }

    public void Kill()
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
    }

    public void Dispose()
    {
        Kill();
        _p.Dispose();
    }
}

/// <summary>
/// The Caddy configuration exactly as the product generates it: Caddy settings and hosts are written to the store and the
/// Config module's CaddyConfigService.Generate() turns them into Caddy JSON, including the traffic statistics sink
/// `cpm_stats` (SPEC "Traffic statistics logging"). A test changes the generated JSON only where it must and says why.
/// </summary>
public static class GeneratedConfig
{
    /// <summary>
    /// Caddy settings for a test instance: HTTP on <paramref name="httpPort"/> bound to loopback only, the admin API on a
    /// free loopback port, an unused HTTPS port (the tests create plain-HTTP hosts only, so no HTTPS server is generated).
    /// </summary>
    public static CaddySettings Settings(TempEnv env, int httpPort, Action<CaddySettings>? configure = null)
    {
        var s = env.Store.GetSettings<CaddySettings>();
        s.HttpPort = httpPort;
        s.HttpsPort = Net.FreeTcpPort();
        s.AdminListen = $"127.0.0.1:{Net.FreeTcpPort()}";
        s.BindAddresses = ["127.0.0.1"];
        s.EnableHttp3 = false;
        configure?.Invoke(s);
        env.Store.SaveSettings(s);
        return s;
    }

    /// <summary>Plain-HTTP proxy host to 127.0.0.1:<paramref name="upstreamPort"/>, the model's defaults otherwise.</summary>
    public static SiteHost AddProxyHost(TempEnv env, int upstreamPort, params string[] domains)
    {
        var host = new SiteHost
        {
            Kind = HostKind.Proxy, Domains = domains.ToList(), Tls = TlsMode.None,
            Upstreams = [new Upstream { Scheme = UpstreamScheme.Http, Host = "127.0.0.1", Port = upstreamPort }],
        };
        env.Store.Col<SiteHost>().Insert(host);
        return host;
    }

    /// <summary>Plain-HTTP fixed-response host.</summary>
    public static SiteHost AddResponseHost(TempEnv env, int status, string body, params string[] domains)
    {
        var host = new SiteHost
        {
            Kind = HostKind.Response, Domains = domains.ToList(), Tls = TlsMode.None, ResponseStatus = status, ResponseBody = body,
        };
        env.Store.Col<SiteHost>().Insert(host);
        return host;
    }

    /// <summary>CaddyConfigService.Generate() over the store (no Caddy binary manager: installed modules unknown).</summary>
    public static ConfigGeneratorResult Generate(TempEnv env)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddCore(env.Paths, env.Store);
        services.AddConfigModule();
        // The store is registered as an instance: disposing the provider leaves it open.
        using var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<CaddyConfigService>().Generate();
    }

    /// <summary>The generated traffic statistics sink (logging.logs.cpm_stats).</summary>
    public static JsonObject StatsSink(JsonObject config) =>
        (JsonObject)config["logging"]!["logs"]![CaddyConfigGenerator.StatsLogName]!;
}

/// <summary>IConfigChangeFeed of the Config module, raised by the test (a configuration apply happened).</summary>
public sealed class FakeConfigChangeFeed : IConfigChangeFeed
{
    public event Action<ApplyResult, string>? Applied;
    public void RaiseApplied(string reason = "test") => Applied?.Invoke(new ApplyResult(), reason);
}

public sealed class FakeCaddyHost : ICaddyHost
{
    public CaddyStatus Status { get; set; } = new() { State = CaddyRunState.Stopped };
    public string HostMode => "process";
    public Task<CaddyStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(Status);
    public Task InstallServiceAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task UninstallServiceAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task RestartAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class FakeBinaryManager(InstalledBinary? installed) : ICaddyBinaryManager
{
    public Task<InstalledBinary?> GetInstalledAsync(CancellationToken ct = default) => Task.FromResult(installed);
    public Task<ReleaseInfo?> GetLatestAsync(bool force = false, CancellationToken ct = default) => Task.FromResult<ReleaseInfo?>(null);
    public Task<BinaryOverview> GetOverviewAsync(CancellationToken ct = default) => Task.FromResult(new BinaryOverview { Installed = installed });
    public JobInfo StartInstallOrUpdate(string? version = null) => throw new NotSupportedException();
    public Task<(int ExitCode, string Output)> RunCaddyAsync(IEnumerable<string> args, string? stdin = null, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task<List<PluginPackage>> GetPluginCatalogAsync(string? query = null, CancellationToken ct = default) => Task.FromResult(new List<PluginPackage>());
}

public static class TestKeys
{
    /// <summary>A fixed 32-byte client-hash key (bytes 1..32) for repeatable estimates.</summary>
    public static byte[] Fixed => Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
}

/// <summary>Stats log lines in the exact shape the generated cpm_stats sink writes (Caddy v2.11.4, filter encoder).</summary>
public static class StatsLines
{
    public static string Line(DateTime at, string host, string client, int status = 200, long size = 2, long bytesRead = 0)
    {
        var ts = (at - DateTime.UnixEpoch).TotalSeconds.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
        return "{\"level\":\"info\",\"ts\":" + ts + ",\"logger\":\"http.log.access\",\"msg\":\"handled request\"," +
            "\"request\":{\"remote_ip\":\"127.0.0.1\",\"remote_port\":\"50000\",\"client_ip\":\"" + client + "\"," +
            "\"proto\":\"HTTP/1.1\",\"method\":\"GET\",\"host\":\"" + host + "\",\"uri\":\"/\"},\"bytes_read\":" + bytesRead +
            ",\"user_id\":\"\",\"duration\":0.0001,\"size\":" + size + ",\"status\":" + status + "}\n";
    }

    public static void Append(AppPaths paths, IEnumerable<string> lines)
    {
        using var fs = new FileStream(paths.StatsLogFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        using var w = new StreamWriter(fs, new UTF8Encoding(false));
        foreach (var l in lines) w.Write(l);
    }
}
