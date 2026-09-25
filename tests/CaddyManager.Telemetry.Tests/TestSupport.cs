using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Infrastructure;
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

    public TrafficIngestion NewIngestion(TimeSpan? flushInterval = null) => new(Paths, new TrafficStore(Store),
        Options.Create(new TelemetryOptions { FlushInterval = flushInterval ?? TimeSpan.FromSeconds(1) }), TimeProvider.System,
        NullLogger<TrafficIngestion>.Instance);

    /// <summary>Core + Telemetry services (background services off unless enabled) with optional Platform fakes.</summary>
    public ServiceProvider Services(Action<TelemetryOptions>? configure = null, ICaddyHost? host = null, ICaddyBinaryManager? binaries = null)
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
        if (host is not null) services.AddSingleton(host);
        if (binaries is not null) services.AddSingleton(binaries);
        return services.BuildServiceProvider();
    }

    public void Dispose()
    {
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
/// Caddy JSON with the traffic statistics sink exactly as SPEC "Traffic statistics logging" defines `cpm_stats`
/// (include http.log.access, file writer, roll_keep 5, roll_compression none, mode 0600, filter encoder deleting
/// request>headers, resp_headers and request>tls; logs.default excludes http.log.access; servers have `logs`).
/// Hand-written because the Config builder implements the generator in parallel — after the merge these tests should take
/// the sink from CaddyConfigGenerator instead (only roll_size_mb differs: the tests use 1 MB to force rotations).
/// </summary>
public static class StatsConfig
{
    public static JsonObject StatsSink(AppPaths paths, int rollSizeMb) => new()
    {
        ["writer"] = new JsonObject
        {
            ["output"] = "file",
            ["filename"] = paths.StatsLogFile,
            ["mode"] = "0600",
            ["roll_size_mb"] = rollSizeMb,
            ["roll_keep"] = 5,
            ["roll_compression"] = "none",
        },
        ["encoder"] = new JsonObject
        {
            ["format"] = "filter",
            ["wrap"] = new JsonObject { ["format"] = "json" },
            ["fields"] = new JsonObject
            {
                ["request>headers"] = new JsonObject { ["filter"] = "delete" },
                ["resp_headers"] = new JsonObject { ["filter"] = "delete" },
                ["request>tls"] = new JsonObject { ["filter"] = "delete" },
            },
        },
        ["include"] = new JsonArray("http.log.access"),
    };

    /// <summary>Full config: admin on a free loopback port, the stats sink, and one HTTP server with the given routes.</summary>
    public static string Build(AppPaths paths, int httpPort, JsonArray routes, int rollSizeMb = 10, bool trustLoopbackProxy = false)
    {
        var server = new JsonObject
        {
            ["listen"] = new JsonArray($"127.0.0.1:{httpPort}"),
            ["automatic_https"] = new JsonObject { ["disable"] = true },
            ["logs"] = new JsonObject(),
            ["routes"] = routes,
        };
        if (trustLoopbackProxy)
        {
            // Mirrors the generator's trusted proxies (static ranges + strict) so X-Forwarded-For sets request.client_ip.
            server["trusted_proxies"] = new JsonObject { ["source"] = "static", ["ranges"] = new JsonArray("127.0.0.1/32") };
            server["trusted_proxies_strict"] = 1;
            server["client_ip_headers"] = new JsonArray("X-Forwarded-For");
        }
        var config = new JsonObject
        {
            ["admin"] = new JsonObject { ["listen"] = $"127.0.0.1:{Net.FreeTcpPort()}" },
            ["logging"] = new JsonObject
            {
                ["logs"] = new JsonObject
                {
                    ["default"] = new JsonObject
                    {
                        ["writer"] = new JsonObject { ["output"] = "file", ["filename"] = paths.CaddyProcessLog },
                        ["exclude"] = new JsonArray("http.log.access"),
                    },
                    ["cpm_stats"] = StatsSink(paths, rollSizeMb),
                },
            },
            ["apps"] = new JsonObject
            {
                ["http"] = new JsonObject { ["servers"] = new JsonObject { ["srv0"] = server } },
            },
        };
        return config.ToJsonString();
    }

    public static JsonObject StaticRoute(int status, string body, string? host = null, string? path = null)
    {
        var route = new JsonObject
        {
            ["handle"] = new JsonArray(new JsonObject { ["handler"] = "static_response", ["status_code"] = status, ["body"] = body }),
            ["terminal"] = true,
        };
        var match = new JsonObject();
        if (host is not null) match["host"] = new JsonArray(host);
        if (path is not null) match["path"] = new JsonArray(path);
        if (match.Count > 0) route["match"] = new JsonArray(match);
        return route;
    }

    public static JsonObject ProxyRoute(string path, int upstreamPort) => new()
    {
        ["match"] = new JsonArray(new JsonObject { ["path"] = new JsonArray(path) }),
        ["handle"] = new JsonArray(new JsonObject
        {
            ["handler"] = "reverse_proxy",
            ["upstreams"] = new JsonArray(new JsonObject { ["dial"] = $"127.0.0.1:{upstreamPort}" }),
        }),
        ["terminal"] = true,
    };
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
