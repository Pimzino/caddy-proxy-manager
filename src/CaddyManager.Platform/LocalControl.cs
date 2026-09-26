using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using CaddyManager.Core;
using CaddyManager.Platform.Windows;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Platform;

/// <summary>
/// Caddy start/stop/restart for local Windows administrators outside the web UI (CaddyManager.exe caddy ..., which the
/// tray companion runs elevated). It goes through the manager, not straight to the Service Control Manager, so the
/// action gets the Caddy host's handling (START_PENDING nudge, refused-stop fallback), an audit entry under the
/// administrator's Windows name, and the watchdog treats a stop as intentional instead of restarting Caddy.
///
/// Protocol: the client writes one line ("caddy start|stop|restart" or "ping"), the manager answers one line
/// ("ok &lt;state&gt;" or "error &lt;message&gt;") and closes the connection.
/// </summary>
public static class LocalControl
{
    public static readonly string[] CaddyActions = ["start", "stop", "restart"];
    internal const int MaxLine = 256;

    internal static async Task<string?> ReadLineAsync(Stream s, CancellationToken ct)
    {
        var buf = new byte[1];
        var sb = new StringBuilder();
        while (sb.Length <= MaxLine)
        {
            if (await s.ReadAsync(buf, ct) == 0) return sb.Length > 0 ? sb.ToString() : null;
            if (buf[0] == '\n') return sb.ToString().TrimEnd('\r');
            sb.Append((char)buf[0]);
        }
        return null;
    }

    internal static async Task WriteLineAsync(Stream s, string line, CancellationToken ct)
    {
        await s.WriteAsync(Encoding.UTF8.GetBytes(line.ReplaceLineEndings(" ") + "\n"), ct);
        await s.FlushAsync(ct);
    }
}

/// <summary>Serves <see cref="AppPaths.LocalControlPipe"/> in the manager (Windows only).</summary>
[SupportedOSPlatform("windows")]
internal sealed class LocalControlService(ICaddyHost host, IAuditLog audit, ILogger<LocalControlService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        NamedPipeServerStream next;
        try
        {
            next = Create(first: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // FirstPipeInstance: someone else already holds the pipe name (a second manager in the console, or a
            // squatter). Never serve alongside it; the CLI then controls the Caddy service directly.
            logger.LogWarning(ex, "Local control pipe {Pipe} is unavailable; 'CaddyManager.exe caddy ...' will control the Caddy service directly.",
                AppPaths.LocalControlPipe);
            return;
        }
        while (true)
        {
            var current = next;
            await using (current)
            {
                try
                {
                    await current.WaitForConnectionAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                // The next instance exists before this one is handled and closed, so the name is never free for
                // another process to take between requests.
                try
                {
                    next = Create(first: false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogError(ex, "Local control pipe {Pipe}: could not create the next instance; local control stops.", AppPaths.LocalControlPipe);
                    return;
                }
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    timeout.CancelAfter(TimeSpan.FromMinutes(5));
                    await HandleAsync(current, timeout.Token);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    await next.DisposeAsync();
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Local control request failed.");
                }
            }
        }
    }

    /// <summary>
    /// SYSTEM and Administrators only (an elevated token; UAC-filtered tokens are refused), never remote. The first
    /// instance must be the first of its name (no squatter); further instances need FILE_CREATE_PIPE_INSTANCE, which the
    /// DACL grants only to the account running the server (LocalSystem for the manager service; Administrators get
    /// read/write only), and at most two exist (the one being handled and the next).
    /// </summary>
    internal static NamedPipeServerStream Create(bool first = true)
    {
        using var self = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        if (self.User is { } owner && !owner.IsWellKnown(WellKnownSidType.LocalSystemSid))
            security.AddAccessRule(new PipeAccessRule(owner, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(AppPaths.LocalControlPipe, PipeDirection.InOut, 2, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None), 1024, 1024, security);
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var line = (await LocalControl.ReadLineAsync(pipe, ct))?.Trim() ?? "";
        string who;
        try { who = pipe.GetImpersonationUserName(); }
        catch { who = "unknown"; }
        var user = $"{who} (Windows)";
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts is ["ping"])
        {
            await LocalControl.WriteLineAsync(pipe, "ok", ct);
            return;
        }
        if (parts is not ["caddy", var action] || !LocalControl.CaddyActions.Contains(action))
        {
            await LocalControl.WriteLineAsync(pipe, "error Unknown command.", ct);
            return;
        }

        var (auditAction, verb) = action switch
        {
            "start" => ("started", "start"),
            "stop" => ("stopped", "stop"),
            _ => ("restarted", "restart"),
        };
        logger.LogInformation("Local control: {User} asked to {Verb} Caddy.", who, verb);
        try
        {
            await (action switch
            {
                "start" => host.StartAsync(ct),
                "stop" => host.StopAsync(ct),
                _ => host.RestartAsync(ct),
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            audit.RecordAs(user, auditAction, "caddy", AppPaths.CaddyServiceName, "Caddy", $"Failed: {ex.Message} (from Windows)");
            await LocalControl.WriteLineAsync(pipe, $"error Could not {verb} Caddy: {ex.Message}", ct);
            return;
        }
        audit.RecordAs(user, auditAction, "caddy", AppPaths.CaddyServiceName, "Caddy", "From Windows (tray or CaddyManager.exe caddy)");
        var status = await host.GetStatusAsync(ct);
        await LocalControl.WriteLineAsync(pipe, $"ok {status.State}", ct);
    }
}

/// <summary>Client side of <see cref="AppPaths.LocalControlPipe"/>, used by the CLI.</summary>
[SupportedOSPlatform("windows")]
public static class LocalControlClient
{
    /// <summary>
    /// Sends one command. Null when the manager is not reachable (service not running, pipe not served, or served by a
    /// process that is not the manager service); the caller then falls back to the Service Control Manager.
    /// </summary>
    public static async Task<(bool Ok, string Message)?> TrySendAsync(string command, TimeSpan timeout, CancellationToken ct = default)
    {
        var manager = ServiceNative.QueryStatus(AppPaths.ManagerServiceName);
        if (manager is not { State: 4, ProcessId: { } managerPid }) return null; // not RUNNING
        await using var pipe = new NamedPipeClientStream(".", AppPaths.LocalControlPipe, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(TimeSpan.FromSeconds(5), ct);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        // Only talk to the manager service itself, never to a process that squats the pipe name.
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var serverPid) || serverPid != managerPid) return null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        await LocalControl.WriteLineAsync(pipe, command, cts.Token);
        var reply = await LocalControl.ReadLineAsync(pipe, cts.Token) ?? "error The manager closed the connection without an answer.";
        return reply.StartsWith("ok", StringComparison.Ordinal)
            ? (true, reply.Length > 3 ? reply[3..] : "")
            : (false, reply.StartsWith("error ", StringComparison.Ordinal) ? reply[6..] : reply);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint serverProcessId);
}
