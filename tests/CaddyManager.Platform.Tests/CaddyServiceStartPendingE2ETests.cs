using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.ServiceProcess;
using TimeoutException = System.TimeoutException;
using System.Text.Json.Nodes;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Platform.Hosting;
using CaddyManager.Platform.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Platform.Tests;

/// <summary>
/// Caddy v2.11.4 running as a native Windows service can stay in START_PENDING although it serves: runner.Execute reports
/// StartPending (no accepted controls) and the notify.Ready() that caddy.Run sent before Execute registered the status
/// channel is lost (open upstream PR https://github.com/caddyserver/caddy/pull/8012; code:
/// https://github.com/caddyserver/caddy/blob/v2.11.4/service_windows.go and notify/notify_windows.go). The SCM then refuses
/// every stop (ControlService: ERROR_SERVICE_CANNOT_ACCEPT_CTRL 1061 or ERROR_INVALID_SERVICE_CONTROL 1052,
/// https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-controlservice).
///
/// Ways the manager's handling can fail (each is checked below; the number is referenced in the tests):
///  1. StartAsync waits for RUNNING and throws after 30 s although Caddy serves (the admin API answers).
///  2. Something else answering on the admin address is mistaken for this service serving (start "succeeds" while the
///     service is dead or hung).
///  3. The nudge (POST /load of the unchanged config: caddy.Load always ends with notify.Ready(), which now reaches the
///     registered channel) does not move the SCM to RUNNING, or it sends Cache-Control: must-revalidate and so forces a
///     full reload of the running config.
///  4. StopAsync throws on 1061/1052 ("Windows could not stop service"), so binary updates, restarts and uninstall fail.
///  5. The fallback is unbounded: StopAsync hangs when the process ignores the admin API's /stop.
///  6. The fallback (Caddy exiting through /stop without SERVICE_STOPPED, or the kill) counts as a service failure, so the
///     SCM's recovery action restarts Caddy seconds later, e.g. in the middle of a binary swap.
///  7. The recovery actions stay disabled after the fallback (Caddy is no longer restarted after real crashes).
///  8. GetStatusAsync reports Starting (MonitorService treats that as transitional forever) or not-running (caddy-down
///     alert and auto-restart) while the admin API answers. MonitorService's healthy condition is
///     State == Running &amp;&amp; AdminReachable (src/CaddyManager.Ops/Monitoring/MonitorService.cs).
///  9. A service stuck in START_PENDING whose admin API never answers is reported as Starting forever (never alerted),
///     and StartAsync cannot recover it.
/// 10. After an intentional kill the status shows "last stopped with Win32 exit code 1067" as an error.
/// 11. The kill hits the wrong process or leaves the service process running.
/// 12. Real Caddy v2.11.4: a start/stop cycle fails, is slow, leaves the process behind, or the service is restarted by
///     the SCM after an intentional stop (research: the exit may race SERVICE_STOPPED).
/// 13. The PID that QueryServiceStatusEx reports while the service is START_PENDING "may not be valid"
///     (https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-queryservicestatusex, Remarks), so the kill
///     could hit an unrelated process that reuses it; the manager therefore only opens a process whose image name is the
///     service's executable. That check must not reject the real service process either: the "hung" case below must still
///     end with <see cref="ServiceStopPath.Killed"/> and the stub's process gone (the stub runs as cpm-probe.exe).
///
/// The stub ("caddy-stub" in <see cref="ServiceProbe"/>) reproduces the stuck state deterministically; a control step
/// proves it does (the plain SCM start times out and the stop is refused), so the tests would detect the bug. The last
/// test runs the real Caddy binary (.dev/bin/caddy.exe, CI downloads exactly CaddyVersion.Tested) as a throw-away service.
/// Runs on the elevated windows-latest CI runner, skipped elsewhere. Every test writes a JSON artifact (CPM_E2E_ARTIFACTS).
/// </summary>
[Trait("Category", "WindowsE2E")]
public class CaddyServiceStartPendingE2ETests
{
    private static void RequireElevatedWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        using var id = WindowsIdentity.GetCurrent();
        Assert.SkipUnless(new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator), "Needs an elevated process (the Windows CI runner is).");
    }

    /// <summary>What the stub models (recorded as the Caddy version of the stub tests' artifacts).</summary>
    private const string StubModel = "stub modelling Caddy v2.11.4 service_windows.go + notify_windows.go (caddy PR #8012 not merged)";

    private static string StubConfig(int port) => "{\"admin\":{\"listen\":\"127.0.0.1:" + port + "\"}}";

    private static string NewName() => "CpmPend" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>A WindowsServiceCaddyHost bound to a throw-away service name, with a real admin client and captured logs.</summary>
    private sealed class HostRig : IDisposable
    {
        public required ServiceProvider Provider { get; init; }
        public required WindowsServiceCaddyHost Host { get; init; }
        public required FakeAdminClient Admin { get; init; }
        public required ListLoggerProvider Logs { get; init; }
        public void Dispose() => Provider.Dispose();
    }

    private static HostRig NewHost(AppPaths paths, string serviceName, int adminPort, string bootConfig)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var admin = new FakeAdminClient($"http://127.0.0.1:{adminPort}");
        var logs = new ListLoggerProvider();
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(logs));
        sc.AddSingleton<ICaddyAdminClient>(admin);
        sc.AddSingleton<ICaddyConfigService>(new FakeConfigService(paths, bootConfig));
        var sp = sc.BuildServiceProvider();
        var support = new CaddyHostSupport(paths, sp, sp.GetRequiredService<ILogger<CaddyHostSupport>>());
        var host = new WindowsServiceCaddyHost(paths, support, sp.GetRequiredService<ILogger<WindowsServiceCaddyHost>>())
        {
            ServiceName = serviceName,
        };
        return new HostRig { Provider = sp, Host = host, Admin = admin, Logs = logs };
    }

    private static async Task CreateStubServiceAsync(string name, string probe, string mode, int port, string markers, CancellationToken ct)
    {
        await WindowsServiceManager.CreateOrRepairAsync(new ServiceDefinition
        {
            Name = name, DisplayName = $"CPM E2E START_PENDING stub {mode} (safe to delete)", Description = nameof(CaddyServiceStartPendingE2ETests),
            BinaryPathName = $"\"{probe}\" caddy-stub {mode} {port} \"{markers}\" {name}", StartType = "demand",
            // Short delays: a wrongly triggered recovery restart shows up within seconds (6).
            RestartDelaysMs = [1000, 1000, 1000], Environment = [$"DOTNET_ROOT={ServiceProbe.DotnetRoot}"],
        }, ct);
    }

    private static int StartCount(string markers) =>
        Directory.Exists(markers) ? Directory.GetFiles(markers, "start-*.txt").Length : 0;

    private static string Events(string markers)
    {
        var f = Path.Combine(markers, "events.log");
        try { return File.Exists(f) ? File.ReadAllText(f) : ""; }
        catch (IOException) { return ""; }
    }

    private static async Task WaitForAsync(Func<Task<bool>> done, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await done())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out after {timeout.TotalSeconds:0}s waiting for {what}.");
            await Task.Delay(200);
        }
    }

    /// <summary>ServiceController.Stop without any fallback: the Win32 error the SCM returns (0 when accepted).</summary>
    private static int RawStop(string name)
    {
        using var sc = new ServiceController(name);
        try
        {
            sc.Stop(stopDependentServices: false);
            return 0;
        }
        catch (InvalidOperationException ex) when (ex.InnerException is Win32Exception w)
        {
            return w.NativeErrorCode;
        }
    }

    private static bool ProcessGone(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static JsonArray Actions(ScFailureConfig? f) =>
        new(f?.Actions.Select(a => (JsonNode?)$"{a.Item1}/{a.Item2}").ToArray() ?? []);

    /// <summary>
    /// Covers 1, 3, 4 (clean path), 8: a service stuck in START_PENDING whose admin API answers.
    /// Control: the plain SCM start (no admin probe) times out and a plain stop is refused, so the stub reproduces the bug.
    /// </summary>
    [Fact]
    public async Task StuckStartPendingIsTreatedAsStartedNudgedToRunningAndStoppedCleanly()
    {
        RequireElevatedWindows();
        var ct = TestContext.Current.CancellationToken;
        var probe = await ServiceProbe.BuildAsync(ct);
        var name = NewName();
        var root = Path.Combine(ServiceProbe.WorkRoot, name);
        var markers = Path.Combine(root, "markers");
        var port = DevCaddy.FreeTcpPort();
        var paths = new AppPaths(root);
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.CaddyExe, "stub; the service runs cpm-probe", ct); // GetStatusAsync needs a binary
        using var rig = NewHost(paths, name, port, StubConfig(port));
        var report = E2EArtifacts.Report(nameof(StuckStartPendingIsTreatedAsStartedNudgedToRunningAndStoppedCleanly));
        report["stub"] = "caddy-stub nudge";
        report["caddyVersion"] = StubModel;
        try
        {
            await CreateStubServiceAsync(name, probe, "nudge", port, markers, ct);

            // Control: without the admin-API probe the start times out in START_PENDING (the old behaviour, 1) ...
            var sw = Stopwatch.StartNew();
            var control = await Record.ExceptionAsync(() => WindowsServiceManager.StartAsync(name, TimeSpan.FromSeconds(8), ct));
            await WaitForAsync(() => rig.Admin.IsReachableAsync(ct), TimeSpan.FromSeconds(20), "the stub's admin API");
            var stateAfterControl = WindowsServiceManager.GetStatus(name);
            // ... and a stop is refused (4).
            var refused = RawStop(name);
            report["control"] = new JsonObject
            {
                ["plainStartException"] = control?.GetType().Name, ["plainStartMessage"] = control?.Message,
                ["plainStartSeconds"] = sw.Elapsed.TotalSeconds, ["scmState"] = stateAfterControl?.ToString(), ["rawStopWin32Error"] = refused,
            };
            Assert.IsType<TimeoutException>(control);
            Assert.Equal(ServiceControllerStatus.StartPending, stateAfterControl);
            Assert.Contains(refused, new[] { 1061, 1052 });

            // (8) Status while the SCM still says START_PENDING: running and healthy for the monitor. No side effects.
            var status = await rig.Host.GetStatusAsync(ct);
            report["statusWhileStartPending"] = new JsonObject
            {
                ["state"] = status.State.ToString(), ["adminReachable"] = status.AdminReachable, ["lastError"] = status.LastError,
                ["pid"] = status.ProcessId, ["scmStateAfter"] = WindowsServiceManager.GetStatus(name)?.ToString(),
            };
            Assert.Equal(CaddyRunState.Running, status.State);
            Assert.True(status.AdminReachable);
            Assert.Null(status.LastError);
            Assert.Equal(ServiceControllerStatus.StartPending, WindowsServiceManager.GetStatus(name));

            // (1)(3) StartAsync on the stuck service returns, and its nudge brings the SCM to RUNNING without forcing a reload.
            sw.Restart();
            await rig.Host.StartAsync(ct);
            var startStuck = sw.Elapsed;
            var scmAfterStart = WindowsServiceManager.GetStatus(name);
            report["startOnStuckService"] = new JsonObject
            {
                ["seconds"] = startStuck.TotalSeconds, ["outcome"] = rig.Host.LastStart?.ToString(), ["scmState"] = scmAfterStart?.ToString(),
            };
            Assert.True(startStuck < TimeSpan.FromSeconds(20), $"StartAsync took {startStuck.TotalSeconds:0.0}s.");
            Assert.Equal(ServiceControllerStatus.Running, scmAfterStart);
            var events = Events(markers);
            Assert.Contains("admin POST /load cache-control=[]", events);
            Assert.DoesNotContain("must-revalidate", events);

            // (4) Now RUNNING: a clean SCM stop (SERVICE_STOPPED, exit code 0), no fallback.
            var pid = (await WindowsServiceManager.QueryExAsync(name, ct))!.ProcessId!.Value;
            await rig.Host.StopAsync(ct);
            var afterStop = await WindowsServiceManager.QueryExAsync(name, ct);
            report["stopAfterNudge"] = new JsonObject
            {
                ["path"] = rig.Host.LastStop?.ToString(), ["state"] = afterStop?.StateName, ["win32ExitCode"] = afterStop?.Win32ExitCode,
                ["processGone"] = ProcessGone(pid),
            };
            Assert.Equal(ServiceStopPath.Stopped, rig.Host.LastStop);
            Assert.Equal("STOPPED", afterStop?.StateName);
            Assert.Equal(0, afterStop?.Win32ExitCode);
            Assert.True(ProcessGone(pid));

            // (1)(3) A fresh start from STOPPED: the stub stays START_PENDING until nudged; StartAsync returns quickly anyway.
            sw.Restart();
            await rig.Host.StartAsync(ct);
            var freshStart = sw.Elapsed;
            report["freshStart"] = new JsonObject
            {
                ["seconds"] = freshStart.TotalSeconds, ["outcome"] = rig.Host.LastStart?.ToString(),
                ["scmState"] = WindowsServiceManager.GetStatus(name)?.ToString(),
            };
            Assert.True(freshStart < TimeSpan.FromSeconds(20), $"StartAsync took {freshStart.TotalSeconds:0.0}s.");
            Assert.Equal(ServiceStartOutcome.ServingWhileStartPending, rig.Host.LastStart?.Outcome);
            Assert.True(rig.Host.LastStart?.Reposted);
            Assert.True(rig.Host.LastStart?.ReachedRunning);
            Assert.Equal(ServiceControllerStatus.Running, WindowsServiceManager.GetStatus(name));
            await rig.Host.StopAsync(ct);
            Assert.Equal(ServiceStopPath.Stopped, rig.Host.LastStop);
        }
        finally
        {
            await WindowsServiceManager.DeleteAsync(name, CancellationToken.None);
            report["events"] = Events(markers);
            report["log"] = rig.Logs.Text;
            E2EArtifacts.Write("caddy-service-start-pending.json", report);
            try { Directory.Delete(root, true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Covers 2, 4, 5, 6, 7, 9, 10, 11, 13: stopping a service that never leaves START_PENDING.
    ///   deaf    — the admin API answers but the nudge has no effect; /stop makes it exit (host path: admin fallback).
    ///   hung    — the admin API answers but ignores /load and /stop (host path: kill by PID).
    ///   noadmin — no admin API; stopped through WindowsServiceManager.StopAsync alone (the uninstall-caddy-service path).
    /// Control (hung only): killing the process without the manager's recovery suppression makes the SCM restart it, so the
    /// "stays stopped" observation window below can detect a missing suppression (6).
    /// </summary>
    [Theory]
    [InlineData("deaf")]
    [InlineData("hung")]
    [InlineData("noadmin")]
    public async Task StopOfAServiceThatNeverLeavesStartPendingIsBoundedAndDoesNotTriggerRecovery(string mode)
    {
        RequireElevatedWindows();
        var ct = TestContext.Current.CancellationToken;
        var probe = await ServiceProbe.BuildAsync(ct);
        var name = NewName();
        var root = Path.Combine(ServiceProbe.WorkRoot, name);
        var markers = Path.Combine(root, "markers");
        var port = DevCaddy.FreeTcpPort();
        var paths = new AppPaths(root);
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.CaddyExe, "stub; the service runs cpm-probe", ct);
        using var rig = NewHost(paths, name, port, StubConfig(port));
        var report = E2EArtifacts.Report(nameof(StopOfAServiceThatNeverLeavesStartPendingIsBoundedAndDoesNotTriggerRecovery));
        report["mode"] = mode;
        report["caddyVersion"] = StubModel;
        try
        {
            await CreateStubServiceAsync(name, probe, mode, port, markers, ct);
            var original = await WindowsServiceManager.QueryFailureAsync(name, ct);
            report["failureActionsBefore"] = Actions(original);

            if (mode == "hung")
            {
                // Control for (6): a kill without suppression IS recovered by the SCM.
                using (var sc = new ServiceController(name)) sc.Start();
                await WaitForAsync(() => Task.FromResult(StartCount(markers) >= 1), TimeSpan.FromSeconds(30), "the first start");
                var victim = (await WindowsServiceManager.QueryExAsync(name, ct))!.ProcessId!.Value;
                using (var p = Process.GetProcessById(victim)) p.Kill();
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (StartCount(markers) < 2 && DateTime.UtcNow < deadline) await Task.Delay(250, ct);
                report["controlRawKillRecovered"] = StartCount(markers) >= 2;
                Assert.True(StartCount(markers) >= 2, "Control failed: the SCM did not restart the stub after a raw kill, so the recovery check below proves nothing.");
            }
            else
            {
                using var sc = new ServiceController(name);
                sc.Start();
            }

            await WaitForAsync(async () => WindowsServiceManager.GetStatus(name) == ServiceControllerStatus.StartPending
                                           && (await WindowsServiceManager.QueryExAsync(name, ct))?.ProcessId is not null,
                TimeSpan.FromSeconds(30), "START_PENDING with a process");
            if (mode != "noadmin")
                await WaitForAsync(() => rig.Admin.IsReachableAsync(ct), TimeSpan.FromSeconds(20), "the stub's admin API");
            else
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            var startsBefore = StartCount(markers);
            var pid = (await WindowsServiceManager.QueryExAsync(name, ct))!.ProcessId!.Value;
            // (13) What the image-name check compares: the process's name and the service's ImagePath.
            using (var sp = Process.GetProcessById(pid)) report["serviceProcessName"] = sp.ProcessName;
            report["serviceImagePath"] = WindowsServiceManager.ReadRegistry(name)?.ImagePath;
            report["serviceImageFile"] = WindowsServiceManager.ImageFileName(WindowsServiceManager.ReadRegistry(name)?.ImagePath);

            // (8)(9) Status: serving → Running; no admin API → still Starting (fresh), not an error yet.
            var status = await rig.Host.GetStatusAsync(ct);
            report["statusBeforeStop"] = new JsonObject { ["state"] = status.State.ToString(), ["adminReachable"] = status.AdminReachable, ["lastError"] = status.LastError };
            Assert.Equal(mode == "noadmin" ? CaddyRunState.Starting : CaddyRunState.Running, status.State);

            // (4)(5)(11) The stop.
            var sw = Stopwatch.StartNew();
            ServiceStopPath path;
            if (mode == "noadmin")
            {
                path = await WindowsServiceManager.StopAsync(name, TimeSpan.FromSeconds(10), ct);
            }
            else
            {
                await rig.Host.StopAsync(ct);
                path = rig.Host.LastStop!.Value;
            }
            var elapsed = sw.Elapsed;
            var after = await WindowsServiceManager.QueryExAsync(name, ct);
            report["stop"] = new JsonObject
            {
                ["path"] = path.ToString(), ["seconds"] = elapsed.TotalSeconds, ["state"] = after?.StateName,
                ["win32ExitCode"] = after?.Win32ExitCode, ["processGone"] = ProcessGone(pid),
            };
            Assert.Equal(mode == "deaf" ? ServiceStopPath.Fallback : ServiceStopPath.Killed, path);
            Assert.True(elapsed < TimeSpan.FromSeconds(60), $"The stop took {elapsed.TotalSeconds:0.0}s.");
            Assert.Equal("STOPPED", after?.StateName);
            Assert.True(ProcessGone(pid), $"The service process {pid} is still running.");

            // (6) No recovery restart: the recovery delay is 1 s, watch for 8 s.
            await Task.Delay(TimeSpan.FromSeconds(8), ct);
            var stateLater = WindowsServiceManager.GetStatus(name);
            report["afterObservationWindow"] = new JsonObject { ["state"] = stateLater?.ToString(), ["starts"] = StartCount(markers), ["startsBeforeStop"] = startsBefore };
            Assert.Equal(ServiceControllerStatus.Stopped, stateLater);
            Assert.Equal(startsBefore, StartCount(markers));

            // (7) Recovery actions restored exactly.
            var restored = await WindowsServiceManager.QueryFailureAsync(name, ct);
            report["failureActionsAfter"] = Actions(restored);
            Assert.NotNull(restored);
            Assert.Equal(original!.Actions, restored.Actions);
            Assert.Equal(original.ResetPeriodSeconds, restored.ResetPeriodSeconds);

            // (10) The host's status after its own stop: Stopped, no "exit code 1067" error.
            if (mode != "noadmin")
            {
                var stopped = await rig.Host.GetStatusAsync(ct);
                report["statusAfterStop"] = new JsonObject { ["state"] = stopped.State.ToString(), ["lastError"] = stopped.LastError };
                Assert.Equal(CaddyRunState.Stopped, stopped.State);
                Assert.Null(stopped.LastError);
            }
        }
        finally
        {
            await WindowsServiceManager.DeleteAsync(name, CancellationToken.None);
            report["events"] = Events(markers);
            report["log"] = rig.Logs.Text;
            E2EArtifacts.Write($"caddy-service-stuck-stop-{mode}.json", report);
            try { Directory.Delete(root, true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Covers 2 and 9: a START_PENDING service whose admin API never answers is not "started", and when another process
    /// answers on the admin address before the start, that answer is not taken as this service serving.
    /// </summary>
    [Fact]
    public async Task AdminAnswersFromElsewhereOrNotAtAllAreNotTakenAsStarted()
    {
        RequireElevatedWindows();
        var ct = TestContext.Current.CancellationToken;
        var probe = await ServiceProbe.BuildAsync(ct);
        var name = NewName();
        var root = Path.Combine(ServiceProbe.WorkRoot, name);
        var markers = Path.Combine(root, "markers");
        var port = DevCaddy.FreeTcpPort();
        var paths = new AppPaths(root);
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.CaddyExe, "stub; the service runs cpm-probe", ct);
        using var rig = NewHost(paths, name, port, StubConfig(port));
        var report = E2EArtifacts.Report(nameof(AdminAnswersFromElsewhereOrNotAtAllAreNotTakenAsStarted));
        report["caddyVersion"] = StubModel;
        MiniHttpServer? impostor = null;
        try
        {
            await CreateStubServiceAsync(name, probe, "noadmin", port, markers, ct);

            // (2) Another process answers on the admin address; the service itself never leaves START_PENDING.
            impostor = new MiniHttpServer((_, _) => (200, "{}"u8.ToArray()), port);
            Assert.True(await rig.Admin.IsReachableAsync(ct));
            var sw = Stopwatch.StartNew();
            var ex = await Record.ExceptionAsync(() => rig.Host.StartAsync(ct));
            report["withImpostor"] = new JsonObject
            {
                ["exception"] = ex?.GetType().Name, ["message"] = ex?.Message, ["seconds"] = sw.Elapsed.TotalSeconds,
                ["impostorRequests"] = string.Join(" | ", impostor.Requests),
            };
            Assert.NotNull(ex);
            Assert.Contains("did not reach the Running state", ex.Message);
            Assert.DoesNotContain(impostor.Requests, r => r.StartsWith("POST", StringComparison.Ordinal)); // never nudged/stopped someone else
            impostor.Dispose();

            // (9) No admin API at all: Starting while fresh; after the grace it is Unknown with an explanation (the monitor
            // alerts), and StartAsync replaces the hung instance instead of waiting on it.
            var status = await rig.Host.GetStatusAsync(ct);
            report["statusFresh"] = new JsonObject { ["state"] = status.State.ToString(), ["lastError"] = status.LastError };
            Assert.Equal(CaddyRunState.Starting, status.State);
            rig.Host.StuckStartGrace = TimeSpan.FromSeconds(1);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            status = await rig.Host.GetStatusAsync(ct);
            report["statusAfterGrace"] = new JsonObject { ["state"] = status.State.ToString(), ["lastError"] = status.LastError };
            Assert.Equal(CaddyRunState.Unknown, status.State);
            Assert.Contains("START_PENDING", status.LastError);

            var oldPid = (await WindowsServiceManager.QueryExAsync(name, ct))!.ProcessId!.Value;
            var startsBefore = StartCount(markers);
            ex = await Record.ExceptionAsync(() => rig.Host.StartAsync(ct));
            report["restartOfHungInstance"] = new JsonObject
            {
                ["exception"] = ex?.GetType().Name, ["oldProcessGone"] = ProcessGone(oldPid), ["starts"] = StartCount(markers),
                ["startsBefore"] = startsBefore, ["outcome"] = rig.Host.LastStart?.ToString(),
            };
            Assert.True(ProcessGone(oldPid), "The hung instance was not replaced.");
            Assert.True(StartCount(markers) > startsBefore, "No new instance was started.");
            Assert.NotNull(ex); // the new instance is hung too (noadmin), so the start still fails, after replacing it
        }
        finally
        {
            impostor?.Dispose();
            await WindowsServiceManager.DeleteAsync(name, CancellationToken.None);
            report["events"] = Events(markers);
            report["log"] = rig.Logs.Text;
            E2EArtifacts.Write("caddy-service-start-pending-no-admin.json", report);
            try { Directory.Delete(root, true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Covers 12 with the real Caddy binary as a throw-away service registered by the host itself (the product's service
    /// definition): 10 start/stop cycles, each must start (serving on HTTP), report Running, stop through the SCM and leave
    /// no process behind; an invalid /load in between must leave the service Running (caddy.Load: notify.Error then
    /// notify.Ready). Records per cycle whether the START_PENDING race happened and each stop's exit code.
    /// </summary>
    [Fact]
    public async Task RealCaddyServiceStartsAndStopsReliably()
    {
        RequireElevatedWindows();
        var ct = TestContext.Current.CancellationToken;
        var name = NewName();
        var root = Path.Combine(ServiceProbe.WorkRoot, name);
        var paths = new AppPaths(root);
        paths.EnsureCreated();
        DevCaddy.InstallInto(paths);
        var adminPort = DevCaddy.FreeTcpPort();
        var httpPort = DevCaddy.FreeTcpPort();
        using var rig = NewHost(paths, name, adminPort, DevCaddy.MinimalConfig(adminPort, httpPort, Path.Combine(root, "caddy-runtime.log")));
        var report = E2EArtifacts.Report(nameof(RealCaddyServiceStartsAndStopsReliably));
        var cycles = new JsonArray();
        report["cycles"] = cycles;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            var version = await Infrastructure.ProcessRunner.RunAsync(paths.CaddyExe, ["version"], new Infrastructure.ProcessOptions { Timeout = TimeSpan.FromSeconds(20) }, ct);
            report["caddyVersion"] = version.StdOut.Trim();
            await rig.Host.InstallServiceAsync(ct);
            report["serviceBinaryPath"] = WindowsServiceManager.ReadRegistry(name)?.ImagePath;

            for (var i = 1; i <= 10; i++)
            {
                var sw = Stopwatch.StartNew();
                await rig.Host.StartAsync(ct);
                var startSeconds = sw.Elapsed.TotalSeconds;
                var status = await rig.Host.GetStatusAsync(ct);
                var body = await http.GetStringAsync($"http://127.0.0.1:{httpPort}/", ct);
                var pid = status.ProcessId;
                var cycle = new JsonObject
                {
                    ["cycle"] = i, ["startSeconds"] = startSeconds, ["startOutcome"] = rig.Host.LastStart?.ToString(),
                    ["state"] = status.State.ToString(), ["adminReachable"] = status.AdminReachable,
                    ["scmStateAfterStart"] = WindowsServiceManager.GetStatus(name)?.ToString(), ["http"] = body,
                };
                cycles.Add(cycle);
                Assert.Equal(CaddyRunState.Running, status.State);
                Assert.True(status.AdminReachable);
                Assert.Equal("hello", body);
                Assert.Equal(ServiceControllerStatus.Running, WindowsServiceManager.GetStatus(name));

                if (i == 5)
                {
                    // An invalid config is rejected by Caddy; record the SCM states it passes through.
                    var states = new List<string>();
                    // Same admin address (a failed load must never move the admin API), plus a handler that does not exist.
                    var invalid = "{\"admin\":{\"listen\":\"127.0.0.1:" + adminPort + "\"},\"apps\":{\"http\":{\"servers\":{\"x\":{\"listen\":[\"127.0.0.1:" +
                                  httpPort + "\"],\"routes\":[{\"handle\":[{\"handler\":\"cpm_no_such_handler\"}]}]}}}}}";
                    using var content = new StringContent(invalid, System.Text.Encoding.UTF8, "application/json");
                    var post = http.PostAsync($"http://127.0.0.1:{adminPort}/load", content, ct);
                    var until = DateTime.UtcNow.AddSeconds(3);
                    while (DateTime.UtcNow < until)
                    {
                        var s = WindowsServiceManager.GetStatus(name)?.ToString() ?? "null";
                        if (states.Count == 0 || states[^1] != s) states.Add(s);
                        await Task.Delay(20, ct);
                    }
                    using var resp = await post;
                    cycle["invalidLoad"] = new JsonObject { ["httpStatus"] = (int)resp.StatusCode, ["scmStates"] = string.Join(" > ", states) };
                    Assert.False(resp.IsSuccessStatusCode);
                    Assert.Equal(ServiceControllerStatus.Running, WindowsServiceManager.GetStatus(name));
                    Assert.Equal(CaddyRunState.Running, (await rig.Host.GetStatusAsync(ct)).State);
                }

                sw.Restart();
                await rig.Host.StopAsync(ct);
                var q = await WindowsServiceManager.QueryExAsync(name, ct);
                cycle["stopSeconds"] = sw.Elapsed.TotalSeconds;
                cycle["stopPath"] = rig.Host.LastStop?.ToString();
                cycle["stateAfterStop"] = q?.StateName;
                cycle["win32ExitCode"] = q?.Win32ExitCode;
                cycle["processGone"] = pid is int p ? ProcessGone(p) : null;
                Assert.Equal("STOPPED", q?.StateName);
                Assert.True(pid is int alive && ProcessGone(alive), "The Caddy process is still running after the stop.");
                Assert.Equal(ServiceStopPath.Stopped, rig.Host.LastStop); // clean SCM stop, never the fallback
                Assert.True(startSeconds < 20, $"Cycle {i}: StartAsync took {startSeconds:0.0}s.");
            }

            // (12) The SCM must not restart Caddy after the intentional stop (recovery delay 5 s in the product definition).
            await Task.Delay(TimeSpan.FromSeconds(8), ct);
            report["stateAfterObservationWindow"] = WindowsServiceManager.GetStatus(name)?.ToString();
            Assert.Equal(ServiceControllerStatus.Stopped, WindowsServiceManager.GetStatus(name));
            // Cycles in which RUNNING did not follow by itself and the config had to be re-posted (the #8012 race).
            report["raceObservedInCycles"] = cycles.Count(c => c!["startOutcome"]?.ToString().Contains("Reposted = True", StringComparison.Ordinal) == true);
        }
        finally
        {
            await WindowsServiceManager.DeleteAsync(name, CancellationToken.None);
            report["log"] = rig.Logs.Text;
            E2EArtifacts.Write("caddy-service-real-cycles.json", report);
            try { Directory.Delete(root, true); } catch { /* best effort */ }
        }
    }
}

/// <summary>Captures log lines (for the E2E artifacts).</summary>
public sealed class ListLoggerProvider : ILoggerProvider
{
    private readonly List<string> _lines = [];

    public string Text
    {
        get { lock (_lines) return string.Join("\n", _lines); }
    }

    public ILogger CreateLogger(string categoryName) => new L(this, categoryName);

    public void Dispose() { }

    private sealed class L(ListLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var line = $"{DateTime.UtcNow:HH:mm:ss.fff} {logLevel} {category.Split('.')[^1]}: {formatter(state, exception)}" +
                       (exception is null ? "" : $" [{exception.GetType().Name}: {exception.Message}]");
            lock (owner._lines) owner._lines.Add(line);
        }
    }
}
