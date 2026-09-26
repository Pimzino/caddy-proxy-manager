using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using CaddyManager.Platform.Binary;
using CaddyManager.Platform.Hosting;

namespace CaddyManager.Platform.Tests;

/// <summary>End-to-end tests of the child-process host with the real Caddy binary.</summary>
[Trait("Category", "Caddy")]
public class ProcessCaddyHostTests
{
    private const int AdminPort = 12119;

    [Fact]
    public async Task StartsReportsStatusRestartsAndStops()
    {
        var ct = TestContext.Current.CancellationToken;
        using var env = new TempEnvironment();
        DevCaddy.InstallInto(env.Paths);
        var httpPort = DevCaddy.FreeTcpPort();
        using var svc = new PlatformServices(env, AdminPort, DevCaddy.MinimalConfig(AdminPort, httpPort, Path.Combine(env.Root, "caddy-runtime.log")));
        var host = svc.Get<ICaddyHost>();
        Assert.IsType<ProcessCaddyHost>(host);
        Assert.Equal("process", host.HostMode);

        var before = await host.GetStatusAsync(ct);
        Assert.True(before.BinaryInstalled);
        Assert.Equal(CaddyRunState.Stopped, before.State);
        Assert.False(before.AdminReachable);

        try
        {
            await host.StartAsync(ct);   // boot config is written by the (fake) config service
            Assert.True(File.Exists(env.Paths.CaddyConfigFile));

            var running = await host.GetStatusAsync(ct);
            Assert.Equal(CaddyRunState.Running, running.State);
            Assert.True(running.AdminReachable);
            Assert.True(running.ServiceInstalled);
            Assert.NotNull(running.ProcessId);
            Assert.NotNull(running.StartedAt);
            Assert.Equal("v2.11.4", running.Version);
            Assert.Equal("process", running.HostMode);
            Assert.Equal(env.Paths.CaddyExe, running.BinaryPath);
            Assert.Equal(env.Paths.CaddyConfigFile, running.ConfigPath);
            using (var p = Process.GetProcessById(running.ProcessId!.Value)) Assert.False(p.HasExited);

            using var http = new HttpClient();
            Assert.Equal("hello", await http.GetStringAsync($"http://127.0.0.1:{httpPort}/", ct));

            // Starting again is a no-op.
            await host.StartAsync(ct);
            Assert.Equal(running.ProcessId, (await host.GetStatusAsync(ct)).ProcessId);

            await host.RestartAsync(ct);
            var restarted = await host.GetStatusAsync(ct);
            Assert.Equal(CaddyRunState.Running, restarted.State);
            Assert.NotEqual(running.ProcessId, restarted.ProcessId);

            await host.StopAsync(ct);
            var stopped = await host.GetStatusAsync(ct);
            Assert.Equal(CaddyRunState.Stopped, stopped.State);
            Assert.False(stopped.AdminReachable);
            Assert.Null(stopped.ProcessId);
            Assert.True(svc.Admin.StopCalls >= 2); // graceful stop through the admin API
            Assert.Throws<ArgumentException>(() => Process.GetProcessById(restarted.ProcessId!.Value));

            // stdout/stderr of the child were appended to the Caddy process log.
            Assert.Contains("Caddy Proxy Manager starting", await File.ReadAllTextAsync(env.Paths.CaddyProcessLog, ct));
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ReportsStartupFailureWithCaddyOutput()
    {
        var ct = TestContext.Current.CancellationToken;
        using var env = new TempEnvironment();
        DevCaddy.InstallInto(env.Paths);
        const int port = 12129;
        using var svc = new PlatformServices(env, port, """{ "admin": { "listen": "127.0.0.1:12129" }, "apps": { "http": { "servers": { "x": { "listen": ["127.0.0.1:1"], "routes": [ { "handle": [ { "handler": "no_such_handler" } ] } ] } } } } }""");
        var host = svc.Get<ICaddyHost>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(ct));
        Assert.Contains("exited during startup", ex.Message);
        Assert.Contains("no_such_handler", ex.Message);
        var status = await host.GetStatusAsync(ct);
        Assert.Equal(CaddyRunState.Stopped, status.State);
        Assert.Contains("no_such_handler", status.LastError);
    }

    [Fact]
    public async Task OnlyTheSwapOwnerMayStartCaddyDuringABinarySwap()
    {
        using var env = new TempEnvironment();
        using var svc = new PlatformServices(env, 12189, "{}");
        var support = svc.Get<CaddyHostSupport>();
        var host = svc.Get<ICaddyHost>();
        Task<Exception?> OtherFlow()
        {
            using (ExecutionContext.SuppressFlow())
                return Task.Run(async () =>
                {
                    try { await host.StartAsync(); return null; }
                    catch (Exception ex) { return ex; }
                });
        }

        using (support.BeginBinarySwap())
        {
            support.ThrowIfBinarySwapInProgress(); // the owner flow is allowed
            var ex = await OtherFlow();
            Assert.IsType<InvalidOperationException>(ex);
            Assert.Contains("being updated", ex!.Message);
        }
        // After the swap other flows get the normal error (binary missing), not the swap error.
        var after = await OtherFlow();
        Assert.IsType<InvalidOperationException>(after);
        Assert.DoesNotContain("being updated", after!.Message);
    }

    [Fact]
    public async Task StatusWithoutBinaryIsNotInstalled()
    {
        using var env = new TempEnvironment();
        using var svc = new PlatformServices(env, 12139, "{}");
        var status = await svc.Get<ICaddyHost>().GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.False(status.BinaryInstalled);
        Assert.Equal(CaddyRunState.NotInstalled, status.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.Get<ICaddyHost>().StartAsync(TestContext.Current.CancellationToken));
    }
}

[Trait("Category", "Caddy")]
public class BinaryInspectionTests
{
    [Fact]
    public async Task InspectsInstalledBinaryAndCachesByTimestamp()
    {
        var ct = TestContext.Current.CancellationToken;
        using var env = new TempEnvironment();
        DevCaddy.InstallInto(env.Paths);
        using var svc = new PlatformServices(env, 12149, "{}");
        var bin = svc.Get<ICaddyBinaryManager>();

        var installed = await bin.GetInstalledAsync(ct);
        Assert.NotNull(installed);
        Assert.Equal("v2.11.4", installed.Version);
        Assert.Equal(env.Paths.CaddyExe, installed.Path);
        Assert.Empty(installed.Plugins);
        Assert.Equal(132, installed.Modules.Count);
        Assert.Contains("http.handlers.reverse_proxy", installed.Modules);
        Assert.DoesNotContain("layer4", installed.Modules);
        Assert.NotNull(installed.InstalledAt);
        Assert.Same(installed, await bin.GetInstalledAsync(ct));

        var (code, output) = await bin.RunCaddyAsync(["version"], ct: ct);
        Assert.Equal(0, code);
        Assert.StartsWith("v2.11.4", output);

        // stdin support: adapt a Caddyfile from stdin
        var (adaptCode, json) = await bin.RunCaddyAsync(["adapt", "--config", "-", "--adapter", "caddyfile"], ":8080 {\n  respond \"hi\"\n}\n", ct);
        Assert.Equal(0, adaptCode);
        Assert.Contains("static_response", json);

        var cfg = Path.Combine(env.Root, "valid.json");
        await File.WriteAllTextAsync(cfg, DevCaddy.MinimalConfig(12150, 18181, Path.Combine(env.Root, "x.log")), ct);
        Assert.Equal(0, (await bin.RunCaddyAsync(["validate", "--config", cfg], ct: ct)).ExitCode);

        File.Delete(env.Paths.CaddyExe);
        Assert.Null(await bin.GetInstalledAsync(ct));
        await Assert.ThrowsAsync<FileNotFoundException>(() => bin.RunCaddyAsync(["version"], ct: ct));
    }

    [Fact]
    public void CopiesDevBinaryWhenMissing()
    {
        Assert.SkipWhen(DevCaddy.Find() is null, "No development Caddy binary.");
        using var env = new TempEnvironment();
        using var svc = new PlatformServices(env, 12159, "{}");
        var bin = svc.Get<CaddyBinaryManager>();
        var old = Environment.GetEnvironmentVariable("CM_DEV_CADDY");
        Environment.SetEnvironmentVariable("CM_DEV_CADDY", DevCaddy.Find());
        try
        {
            Assert.True(bin.TryCopyDevBinary());
            Assert.True(File.Exists(env.Paths.CaddyExe));
            Assert.Equal("dev-copy", bin.ReadMetadata()?.Source);
            Assert.False(bin.TryCopyDevBinary());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CM_DEV_CADDY", old);
        }
    }
}

public class ArchiveExtractionTests
{
    [Fact]
    public void ExtractsCaddyExeFromTheWindowsZip()
    {
        using var env = new TempEnvironment();
        var payload = "fake-binary"u8.ToArray();

        var zip = Path.Combine(env.Root, "caddy_2.11.4_windows_amd64.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            z.CreateEntry("LICENSE").Open().Dispose();
            using var s = z.CreateEntry("caddy.exe").Open();
            s.Write(payload);
        }
        var outExe = Path.Combine(env.Root, "out.exe");
        CaddyBinaryManager.ExtractBinary(zip, new CaddyPlatform("windows", "amd64"), outExe);
        Assert.Equal(payload, File.ReadAllBytes(outExe));

        var empty = Path.Combine(env.Root, "readme-only.zip");
        using (var z = ZipFile.Open(empty, ZipArchiveMode.Create)) z.CreateEntry("README.md").Open().Dispose();
        var ex = Assert.Throws<InvalidOperationException>(() => CaddyBinaryManager.ExtractBinary(empty, new CaddyPlatform("windows", "arm64"), outExe));
        Assert.Contains("caddy_<version>_windows_arm64.zip", ex.Message);
    }
}

public class InstallJobGuardTests
{
    [Fact]
    public void RejectsInvalidVersion()
    {
        using var env = new TempEnvironment();
        using var svc = new PlatformServices(env, 12169, "{}");
        var bin = svc.Get<ICaddyBinaryManager>();
        Assert.Throws<ArgumentException>(() => bin.StartInstallOrUpdate("latest-and-greatest"));
    }

    [Fact]
    public async Task ValidatesPluginListsOffline()
    {
        using var env = new TempEnvironment();
        using var svc = new PlatformServices(env, 12179, "{}");
        var bin = svc.Get<CaddyBinaryManager>();
        var (_, error) = await PlatformEndpoints.ValidatePluginsAsync(["not a package", "github.com/caddyserver/caddy/v2"], bin, TestContext.Current.CancellationToken);
        Assert.NotNull(error);
        var (plugins, none) = await PlatformEndpoints.ValidatePluginsAsync([" ", ""], bin, TestContext.Current.CancellationToken);
        Assert.Null(none);
        Assert.Empty(plugins);
    }

    [Fact]
    public void WindowsServiceDefinitionQuotesPaths()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "with space"));
#pragma warning disable CA1416
        var def = WindowsServiceCaddyHost.Definition(paths);
        var mgr = PlatformCli.ManagerServiceDefinition(@"C:\Program Files\Caddy Proxy Manager\CaddyManager.exe");
#pragma warning restore CA1416
        Assert.Equal($"\"{paths.CaddyExe}\" run --config \"{paths.CaddyConfigFile}\"", def.BinaryPathName);
        Assert.Equal("Caddy", def.Name);
        Assert.Equal("auto", def.StartType);
        Assert.Equal([5000, 5000, 30000], def.RestartDelaysMs);
        Assert.Equal(86400, def.FailureResetSeconds);
        Assert.Contains($"XDG_DATA_HOME={paths.CaddyStorageDir}", def.Environment!);
        Assert.Contains($"XDG_CONFIG_HOME={Path.Combine(paths.DataDir, "caddy", "config")}", def.Environment!);
        Assert.Equal("\"C:\\Program Files\\Caddy Proxy Manager\\CaddyManager.exe\"", mgr.BinaryPathName);
        Assert.Equal("delayed-auto", mgr.StartType);
        Assert.Equal(AppPaths.ManagerServiceName, mgr.Name);
    }
}

public class CliTests
{
    [Fact]
    public async Task IgnoresNonPlatformArguments()
    {
        Assert.Null(await PlatformCli.TryRunAsync([]));
        Assert.Null(await PlatformCli.TryRunAsync(["reset-password", "a@b.c"]));
        Assert.Null(await PlatformCli.TryRunAsync(["--urls", "http://*:5000"]));
    }

    [Fact]
    public async Task VersionAndHelpSucceed()
    {
        Assert.Equal(0, await PlatformCli.TryRunAsync(["version"]));
        Assert.Equal(0, await PlatformCli.TryRunAsync(["help"]));
    }

    [Fact]
    public async Task WindowsOnlyVerbsFailElsewhere()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Non-Windows behaviour.");
        Assert.Equal(1, await PlatformCli.TryRunAsync(["install", "--ui-port", "8081"]));
        Assert.Equal(1, await PlatformCli.TryRunAsync(["uninstall", "--purge"]));
        Assert.Equal(1, await PlatformCli.TryRunAsync(["service-status"]));
        Assert.Equal(1, await PlatformCli.TryRunAsync(["uninstall-caddy-service"]));
    }
}
