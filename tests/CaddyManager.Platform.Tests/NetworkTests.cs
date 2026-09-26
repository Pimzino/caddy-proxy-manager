using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using CaddyManager.Platform.Binary;

namespace CaddyManager.Platform.Tests;

/// <summary>
/// Tests that talk to GitHub / caddyserver.com and download real release archives.
/// Filter them out with: dotnet test --filter "Category!=Network"
/// </summary>
[Trait("Category", "Network")]
public class NetworkTests
{
    private static async Task<JobInfo> WaitForJobAsync(IJobRunner jobs, string id, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var j = jobs.Get(id)!;
            if (j.State != JobState.Running) return j;
            await Task.Delay(500);
        }
        throw new TimeoutException($"Job {id} did not finish within {timeout}.");
    }

    [Fact]
    public async Task GetsLatestReleaseAndPersistsCheck()
    {
        var ct = TestContext.Current.CancellationToken;
        using var env = new TempEnvironment();
        using var svc = new PlatformServices(env, 12209, "{}");
        var bin = svc.Get<ICaddyBinaryManager>();

        var latest = await bin.GetLatestAsync(force: true, ct);
        Assert.NotNull(latest);
        Assert.True(CaddyVersion.Compare(latest.Version, "v2.11.4") >= 0, latest.Version);
        Assert.StartsWith("https://github.com/caddyserver/caddy/releases/tag/", latest.Url);
        Assert.NotNull(latest.PublishedAt);

        var settings = env.Store.GetSettings<BinarySettings>();
        Assert.Equal(latest.Version, settings.LatestKnownVersion);
        Assert.NotNull(settings.LastCheckedAt);
        Assert.Same(latest, await bin.GetLatestAsync(false, ct)); // cached

        var overview = await bin.GetOverviewAsync(ct);
        Assert.Null(overview.Installed);
        Assert.False(overview.UpdateAvailable);
        Assert.Equal(CaddyPlatform.Current.ToString(), overview.Platform);
    }

    [Fact]
    public async Task LoadsAndFiltersPluginCatalog()
    {
        var ct = TestContext.Current.CancellationToken;
        using var env = new TempEnvironment();
        using var svc = new PlatformServices(env, 12219, "{}");
        var bin = svc.Get<ICaddyBinaryManager>();

        var all = await bin.GetPluginCatalogAsync(null, ct);
        Assert.InRange(all.Count, 50, 200);
        Assert.True(all.SequenceEqual(all.OrderByDescending(p => p.Downloads).ThenBy(p => p.Path, StringComparer.OrdinalIgnoreCase)));

        var l4 = await bin.GetPluginCatalogAsync("caddy-l4", ct);
        Assert.Contains(l4, p => p.Path == "github.com/mholt/caddy-l4");
        var byModule = await bin.GetPluginCatalogAsync("layer4.handlers.proxy", ct);
        Assert.Contains(byModule, p => p.Path == "github.com/mholt/caddy-l4");

        var (_, error) = await PlatformEndpoints.ValidatePluginsAsync(["github.com/example/does-not-exist-cpm"], svc.Get<CaddyBinaryManager>(), ct);
        Assert.NotNull(error);
        var (ok, none) = await PlatformEndpoints.ValidatePluginsAsync(["github.com/mholt/caddy-l4"], svc.Get<CaddyBinaryManager>(), ct);
        Assert.Null(none);
        Assert.Equal(["github.com/mholt/caddy-l4"], ok);
    }

    [Fact]
    public async Task InstallsThenUpdatesWithBackupAndRestart()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only: downloads and runs the Windows Caddy release.");
        var ct = TestContext.Current.CancellationToken;
        const int adminPort = 12229;
        using var env = new TempEnvironment();
        var httpPort = DevCaddy.FreeTcpPort();
        using var svc = new PlatformServices(env, adminPort, DevCaddy.MinimalConfig(adminPort, httpPort, Path.Combine(env.Root, "caddy-runtime.log")));
        var bin = svc.Get<CaddyBinaryManager>();
        var host = svc.Get<ICaddyHost>();
        var jobs = svc.Get<IJobRunner>();
        try
        {
            // 1. Fresh install of a pinned release for this OS/arch (SHA-512 verified), then Caddy is started + config applied.
            var job = bin.StartInstallOrUpdate("v2.11.4");
            Assert.Throws<InvalidOperationException>(() => bin.StartInstallOrUpdate("v2.11.4")); // one job at a time
            var done = await WaitForJobAsync(jobs, job.Id, TimeSpan.FromMinutes(5));
            Assert.True(done.State == JobState.Succeeded, string.Join("\n", done.Log));
            Assert.Contains(done.Log, l => l.Contains("SHA-512 checksum verified"));
            Assert.True(File.Exists(env.Paths.CaddyExe));
            Assert.False(File.Exists(env.Paths.CaddyExeBackup));
            var meta = bin.ReadMetadata();
            Assert.Equal("github-release", meta?.Source);
            Assert.Equal("v2.11.4", meta?.Version);
            Assert.Equal(128, meta?.Sha512?.Length);
            Assert.Equal("v2.11.4", (await bin.GetInstalledAsync(ct))?.Version);
            Assert.Contains("caddy installed", svc.Config.Applied);
            var status = await host.GetStatusAsync(ct);
            Assert.Equal(CaddyRunState.Running, status.State);
            Assert.True(status.AdminReachable);
            Assert.Empty(Directory.GetFileSystemEntries(env.Paths.CaddyStagingDir));

            // 2. Update (same version, exercises the full flow): validate config, stop, keep .previous, swap, start.
            var pidBefore = status.ProcessId;
            var update = await WaitForJobAsync(jobs, bin.StartInstallOrUpdate("v2.11.4").Id, TimeSpan.FromMinutes(5));
            Assert.True(update.State == JobState.Succeeded, string.Join("\n", update.Log));
            Assert.Contains(update.Log, l => l.Contains("Validating the current configuration"));
            Assert.Contains(update.Log, l => l.Contains("Stopping Caddy"));
            Assert.True(File.Exists(env.Paths.CaddyExeBackup));
            var after = await host.GetStatusAsync(ct);
            Assert.Equal(CaddyRunState.Running, after.State);
            Assert.NotEqual(pidBefore, after.ProcessId);
            Assert.Contains(svc.Events.Events, e => e.Category == "update" && e.Message.Contains("updated"));
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RollsBackWhenTheNewBinaryDoesNotComeUp()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only: downloads and runs the Windows Caddy release.");
        var ct = TestContext.Current.CancellationToken;
        const int adminPort = 12239;
        using var env = new TempEnvironment();
        var httpPort = DevCaddy.FreeTcpPort();
        using var svc = new PlatformServices(env, adminPort, DevCaddy.MinimalConfig(adminPort, httpPort, Path.Combine(env.Root, "caddy-runtime.log")));
        var bin = svc.Get<CaddyBinaryManager>();
        var host = svc.Get<ICaddyHost>();
        var jobs = svc.Get<IJobRunner>();
        DevCaddy.InstallInto(env.Paths);
        var original = await File.ReadAllBytesAsync(env.Paths.CaddyExe, ct);
        try
        {
            await host.StartAsync(ct);
            Assert.Equal(CaddyRunState.Running, (await host.GetStatusAsync(ct)).State);

            svc.Admin.ForceUnreachable = false;
            var job = bin.StartInstallOrUpdate("v2.11.4");
            // Once the old Caddy has been stopped, make the admin API look dead so the new binary "fails" to come up.
            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (DateTime.UtcNow < deadline && !(jobs.Get(job.Id)!.Log.Any(l => l.Contains("Starting Caddy")))) await Task.Delay(50, ct);
            svc.Admin.ForceUnreachable = true;

            var done = await WaitForJobAsync(jobs, job.Id, TimeSpan.FromMinutes(5));
            Assert.Equal(JobState.Failed, done.State);
            Assert.Contains("previous version was restored", done.Error);
            Assert.Contains(done.Log, l => l.Contains("Rolling back"));
            Assert.Equal(original, await File.ReadAllBytesAsync(env.Paths.CaddyExe, ct));
            Assert.Null(bin.ReadMetadata()); // metadata of the failed binary was removed with it
            Assert.Contains(svc.Events.Events, e => e.Key == "caddy-update-failed" && e.Severity == EventSeverity.Error);
        }
        finally
        {
            svc.Admin.ForceUnreachable = false;
            await host.StopAsync(CancellationToken.None);
        }
    }
}
