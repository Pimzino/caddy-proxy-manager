using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaddyManager.Platform.Tests;

/// <summary>
/// The manager's local control pipe (tray / "CaddyManager.exe caddy ...") against a real Windows named pipe, with a fake
/// Caddy host. Ways it could fail, each checked here:
///  1. Anyone but SYSTEM / elevated Administrators can use it (the DACL admits others, or network clients), or an
///     administrator's process can add instances of it (only the server's own account may).
///  1b. The manager cannot add the next instance itself (then it stops answering after the first request).
///  2. A second server (a squatter, or a second manager) can serve the same name alongside the manager, or take the
///     name in the gap between one request and the next.
///  3. A command reaches Caddy but is audited as "system" (the watchdog would then restart a stopped Caddy) or without
///     the caller's Windows name.
///  4. A failed start/stop is reported as success, or not audited.
///  5. An unknown or oversized command is executed or wedges the server (later requests no longer answered).
/// Runs on the elevated Windows CI runner; skipped elsewhere and when a real manager already serves the pipe.
/// </summary>
[Trait("Category", "WindowsE2E")]
public class LocalControlE2ETests
{
    private sealed class FakeHost : ICaddyHost
    {
        public List<string> Calls { get; } = [];
        public Exception? FailWith { get; set; }
        public CaddyRunState State { get; private set; } = CaddyRunState.Running;
        public string HostMode => "fake";
        public Task<CaddyStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(new CaddyStatus { State = State });
        public Task InstallServiceAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task UninstallServiceAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StartAsync(CancellationToken ct = default) => Do("start", CaddyRunState.Running);
        public Task StopAsync(CancellationToken ct = default) => Do("stop", CaddyRunState.Stopped);
        public Task RestartAsync(CancellationToken ct = default) => Do("restart", CaddyRunState.Running);

        private Task Do(string call, CaddyRunState after)
        {
            Calls.Add(call);
            if (FailWith is { } ex) return Task.FromException(ex);
            State = after;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAudit : IAuditLog
    {
        public List<(string User, string Action, string ObjectType, string? Details)> Entries { get; } = [];
        public void Record(string action, string objectType, string? objectId = null, string? objectName = null, string? details = null) =>
            Entries.Add(("system", action, objectType, details));
        public void RecordAs(string userName, string action, string objectType, string? objectId = null, string? objectName = null, string? details = null) =>
            Entries.Add((userName, action, objectType, details));
    }

    private static async Task<string?> SendAsync(string line)
    {
        await using var pipe = new NamedPipeClientStream(".", AppPaths.LocalControlPipe, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(10_000);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await LocalControl.WriteLineAsync(pipe, line, cts.Token);
        return await LocalControl.ReadLineAsync(pipe, cts.Token);
    }

    [Fact]
    public async Task Elevated_admin_controls_caddy_through_the_manager_and_is_audited_by_name()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        using (var id = WindowsIdentity.GetCurrent())
            Assert.SkipUnless(new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator), "Needs an elevated process (the Windows CI runner is).");
        try
        {
            using var probe = LocalControlService.Create();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"The pipe {AppPaths.LocalControlPipe} is already served on this machine (a running manager).");
        }

        var report = E2EArtifacts.Report(nameof(Elevated_admin_controls_caddy_through_the_manager_and_is_audited_by_name));
        var steps = new JsonArray();
        report["steps"] = steps;
        var host = new FakeHost();
        var audit = new FakeAudit();
        var service = new LocalControlService(host, audit, NullLogger<LocalControlService>.Instance);
        await service.StartAsync(CancellationToken.None);
        try
        {
            // .NET 10 runs ExecuteAsync on a background thread: wait until the pipe answers
            var ready = await SendAsync("ping");
            steps.Add(new JsonObject { ["request"] = "ping (ready)", ["reply"] = ready });
            Assert.Equal("ok", ready);

            // (2) while the manager serves the pipe, nobody else can create an instance of it (also right after a request,
            //     when the previous server instance has just been closed)
            var squat = Record.Exception(() => LocalControlService.Create());
            steps.Add(new JsonObject { ["check"] = "second server refused", ["exception"] = squat?.GetType().Name });
            Assert.True(squat is IOException or UnauthorizedAccessException, $"A second server instance was allowed: {squat}");

            // (1) DACL: deny NETWORK, allow SYSTEM (full) and Administrators (read/write), nothing else
            using (var server = new NamedPipeClientStream(".", AppPaths.LocalControlPipe, PipeDirection.InOut))
            {
                await server.ConnectAsync(10_000);
                var rules = server.GetAccessControl().GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>()
                    .Select(r => (Sid: ((SecurityIdentifier)r.IdentityReference).Value, r.AccessControlType, r.PipeAccessRights)).ToList();
                steps.Add(new JsonObject
                {
                    ["check"] = "pipe DACL",
                    ["rules"] = new JsonArray(rules.Select(r => (JsonNode)$"{r.AccessControlType} {r.Sid} {r.PipeAccessRights}").ToArray()),
                });
                Assert.Contains(rules, r => r is { Sid: "S-1-5-2", AccessControlType: AccessControlType.Deny });
                // SYSTEM, Administrators (read/write, no new instances) and the server's own account (the manager runs as
                // SYSTEM; this test hosts the server as the elevated test user) - nobody else.
                using var self = WindowsIdentity.GetCurrent();
                Assert.All(rules.Where(r => r.AccessControlType == AccessControlType.Allow),
                    r => Assert.Contains(r.Sid, new[] { "S-1-5-18", "S-1-5-32-544", self.User!.Value }));
                var admins = Assert.Single(rules, r => r is { Sid: "S-1-5-32-544", AccessControlType: AccessControlType.Allow });
                Assert.False(admins.PipeAccessRights.HasFlag(PipeAccessRights.CreateNewInstance), "Administrators must not create pipe instances.");
            }
            // the connection above sent nothing: the server must still answer the next client (5)

            // (3) stop: executed by the host, audited under the caller's Windows name (not "system"), reply carries the state
            var stop = await SendAsync("caddy stop");
            steps.Add(new JsonObject { ["request"] = "caddy stop", ["reply"] = stop, ["hostCalls"] = string.Join(",", host.Calls) });
            Assert.Equal("ok Stopped", stop);
            Assert.Equal(["stop"], host.Calls);
            var entry = Assert.Single(audit.Entries);
            Assert.Equal(("stopped", "caddy"), (entry.Action, entry.ObjectType));
            Assert.Contains(Environment.UserName, entry.User, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("(Windows)", entry.User);

            var start = await SendAsync("caddy start");
            steps.Add(new JsonObject { ["request"] = "caddy start", ["reply"] = start });
            Assert.Equal("ok Running", start);

            // (4) a failure is reported as an error and audited as failed
            host.FailWith = new InvalidOperationException("boom");
            var failed = await SendAsync("caddy restart");
            steps.Add(new JsonObject { ["request"] = "caddy restart (host fails)", ["reply"] = failed, ["audit"] = audit.Entries[^1].Details });
            Assert.Equal("error Could not restart Caddy: boom", failed);
            Assert.StartsWith("Failed: boom", audit.Entries[^1].Details);
            host.FailWith = null;

            // (5) unknown and oversized commands do nothing; the server keeps answering
            var calls = host.Calls.Count;
            var unknown = await SendAsync("caddy uninstall");
            var huge = await SendAsync("caddy stop" + new string(' ', 600) + "x"); // over the 256-char limit, within the 1 KB pipe buffer
            var ping = await SendAsync("ping");
            steps.Add(new JsonObject { ["request"] = "caddy uninstall", ["reply"] = unknown });
            steps.Add(new JsonObject { ["request"] = "611-char line", ["reply"] = huge });
            steps.Add(new JsonObject { ["request"] = "ping", ["reply"] = ping });
            Assert.Equal("error Unknown command.", unknown);
            Assert.Equal("error Unknown command.", huge);
            Assert.Equal("ok", ping);
            Assert.Equal(calls, host.Calls.Count);
            report["result"] = "pass";
        }
        catch (Exception ex)
        {
            report["result"] = "fail: " + ex.Message;
            throw;
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            E2EArtifacts.Write("local-control-pipe.json", report);
        }
    }
}
