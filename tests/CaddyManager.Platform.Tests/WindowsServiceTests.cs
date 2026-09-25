using System.Security.Principal;
using CaddyManager.Platform.Windows;

namespace CaddyManager.Platform.Tests;

/// <summary>
/// End-to-end check of the sc.exe argument building against the real Service Control Manager: quoted paths with
/// spaces inside binPath=, delayed-auto start, LocalSystem, recovery actions, the REG_MULTI_SZ environment, repair
/// and delete. Runs on the (elevated) Windows CI runner; registers a throw-away service that is removed again.
/// </summary>
[Trait("Category", "WindowsService")]
public class WindowsServiceManagerTests
{
    private static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    [Fact]
    public async Task CreatesRepairsAndDeletesAServiceWithQuotedPaths()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        Assert.SkipUnless(IsElevated(), "Needs an elevated process (the Windows CI runner is).");
        var ct = TestContext.Current.CancellationToken;
        var name = "CpmTest" + Guid.NewGuid().ToString("N")[..8];
        var def = new ServiceDefinition
        {
            Name = name,
            DisplayName = "CPM test service (safe to delete)",
            Description = "Created by CaddyManager.Platform.Tests and deleted at the end of the test.",
            BinaryPathName = "\"C:\\Program Files\\CPM Test Dir\\missing.exe\" run --config \"C:\\ProgramData\\CPM Test\\caddy.json\"",
            StartType = "delayed-auto",
            RestartDelaysMs = [5000, 10000, 30000],
            Environment = ["XDG_DATA_HOME=C:\\ProgramData\\CPM Test\\caddy\\data", "XDG_CONFIG_HOME=C:\\ProgramData\\CPM Test\\caddy\\config"],
        };
        try
        {
            var created = await WindowsServiceManager.CreateOrRepairAsync(def, ct);
            Assert.Contains(created, c => c.StartsWith("Registered", StringComparison.Ordinal));
            Assert.True(WindowsServiceManager.Exists(name));

            var reg = WindowsServiceManager.ReadRegistry(name);
            Assert.NotNull(reg);
            Assert.Equal(def.BinaryPathName, reg.ImagePath); // the quotes survived ArgumentList -> sc.exe -> SCM
            Assert.Equal(2, reg.Start);
            Assert.True(reg.DelayedAutoStart);
            Assert.True(reg.IsLocalSystem, reg.ObjectName);
            Assert.Equal(def.DisplayName, reg.DisplayName);
            Assert.Equal(def.Environment, reg.Environment);

            var failure = await WindowsServiceManager.QueryFailureAsync(name, ct);
            Assert.NotNull(failure);
            Assert.Equal(86400, failure.ResetPeriodSeconds);
            Assert.Equal([("RESTART", 5000), ("RESTART", 10000), ("RESTART", 30000)], failure.Actions);

            var q = await WindowsServiceManager.QueryExAsync(name, ct);
            Assert.NotNull(q);
            Assert.Equal(1, q.State); // STOPPED
            Assert.Null(q.ProcessId);

            // Idempotent: nothing to change the second time.
            Assert.Empty(await WindowsServiceManager.CreateOrRepairAsync(def, ct));

            // Repair after someone changed the start type and the environment.
            var sc = await WindowsServiceManager.ScAsync(["config", name, "start=", "demand"], ct);
            Assert.Equal(0, sc.ExitCode);
            WindowsServiceManager.SetEnvironment(name, ["OTHER=1"]);
            var repaired = await WindowsServiceManager.CreateOrRepairAsync(def, ct);
            Assert.Contains(repaired, c => c.StartsWith("Repaired", StringComparison.Ordinal));
            Assert.Contains(repaired, c => c.StartsWith("Updated environment", StringComparison.Ordinal));
            reg = WindowsServiceManager.ReadRegistry(name)!;
            Assert.True(reg.DelayedAutoStart);
            Assert.Equal(def.BinaryPathName, reg.ImagePath);
            Assert.Equal(def.Environment, reg.Environment);

            // Starting a service whose executable does not exist fails with Windows' reason.
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => WindowsServiceManager.StartAsync(name, TimeSpan.FromSeconds(15), ct));
            Assert.Contains(name, ex.Message);
            // Stopping a stopped service is a no-op.
            await WindowsServiceManager.StopAsync(name, TimeSpan.FromSeconds(5), ct);
        }
        finally
        {
            await WindowsServiceManager.DeleteAsync(name, CancellationToken.None);
        }
        Assert.False(WindowsServiceManager.Exists(name));
        Assert.Null(WindowsServiceManager.GetStatus(name));
        // Deleting a missing service is a no-op too.
        await WindowsServiceManager.DeleteAsync(name, ct);
    }
}
