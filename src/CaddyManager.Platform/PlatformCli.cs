using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using CaddyManager.Core;
using CaddyManager.Core.Infrastructure;
using CaddyManager.Core.Models;
using CaddyManager.Platform.Hosting;
using CaddyManager.Platform.Infrastructure;
using CaddyManager.Platform.Readiness;
using CaddyManager.Platform.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaddyManager.Platform;

/// <summary>Command line verbs handled before the web host starts (install, uninstall, ...).</summary>
public static class PlatformCli
{
    public const string ManagerDescription =
        "Caddy Proxy Manager: web UI that installs, configures and monitors the Caddy web server / reverse proxy.";

    private static readonly TimeSpan ServiceTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Returns an exit code if args were a Platform CLI verb, otherwise null.</summary>
    public static async Task<int?> TryRunAsync(string[] args)
    {
        if (args.Length == 0) return null;
        var rest = args[1..];
        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "install": return await InstallAsync(rest);
                case "uninstall": return await UninstallAsync(rest);
                case "service-status": return await ServiceStatusAsync();
                case "uninstall-caddy-service": return await UninstallCaddyServiceAsync();
                case "configure": return Configure(rest);
                case "version" or "--version": return Version();
                case "help" or "--help" or "-h" or "/?": return Help();
                default: return null;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }

    // ------------------------------------------------------------------ help / version

    private static int Help()
    {
        Console.WriteLine($"""
            {AppPaths.ProductName} {PlatformEndpoints.ProductVersion()}

            Usage: CaddyManager.exe [verb] [options]
              (no verb)             Run the manager (as the Windows service, or in the console for testing).

            Verbs:
              install [--ui-port N] [--bind ADDR] [--no-start]
                                    Install or repair the manager as the Windows service '{AppPaths.ManagerServiceName}':
                                    copies this exe to %ProgramFiles%\{AppPaths.ProductName}, registers the service
                                    (LocalSystem, automatic delayed start, restart on failure), opens the UI port in the
                                    firewall and starts it. Requires an elevated prompt.
              uninstall [--purge]   Stop and remove the '{AppPaths.ManagerServiceName}' and '{AppPaths.CaddyServiceName}' services and the
                                    firewall rules of the group '{ReadinessScripts.FirewallGroup}'. --purge also deletes all data
                                    in %ProgramData%\CaddyProxyManager (database, certificates, Caddy storage, logs).
              service-status        Show the state of both services.
              reset-password        Reset a UI user's password (handled by the Ops module).
              version               Print the version.
              help                  Show this help.
            """);
        return 0;
    }

    private static int Version()
    {
        Console.WriteLine($"{AppPaths.ProductName} {PlatformEndpoints.ProductVersion()} ({RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription}, {RuntimeInformation.OSArchitecture})");
        return 0;
    }

    private static int WindowsOnly(string verb)
    {
        Console.Error.WriteLine($"'{verb}' is only available on Windows. On other platforms run the manager in the console (development mode).");
        return 1;
    }

    // ------------------------------------------------------------------ install

    private static async Task<int> InstallAsync(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return WindowsOnly("install");
        if (!IsElevated())
        {
            Console.Error.WriteLine("'install' must be run from an elevated prompt (right-click PowerShell → Run as administrator).");
            return 1;
        }

        int? uiPort = null;
        string? bind = null;
        var noStart = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--ui-port" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out var port) || port is < 1 or > 65535)
                        return Usage($"--ui-port must be a TCP port between 1 and 65535 (got '{args[i]}').");
                    uiPort = port;
                    break;
                case "--bind" when i + 1 < args.Length:
                    if (!IPAddress.TryParse(args[++i], out _))
                        return Usage($"--bind must be an IP address such as 0.0.0.0 or 127.0.0.1 (got '{args[i]}').");
                    bind = args[i];
                    break;
                case "--no-start":
                    noStart = true;
                    break;
                default:
                    return Usage($"Unknown or incomplete option '{args[i]}'.");
            }
        }

        var source = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the path of this executable.");
        var installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppPaths.ProductName);
        var target = Path.Combine(installDir, "CaddyManager.exe");
        var name = AppPaths.ManagerServiceName;

        Console.WriteLine($"Installing {AppPaths.ProductName} {PlatformEndpoints.ProductVersion()}");

        // 1. Stop a running instance (its exe and database are locked while it runs).
        if (WindowsServiceManager.Exists(name))
        {
            Console.WriteLine($"  Stopping the existing '{name}' service");
            await WindowsServiceManager.StopAsync(name, ServiceTimeout);
        }

        // 2. Program files
        if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(installDir);
            await CopyWithRetryAsync(source, target);
            foreach (var extra in Directory.EnumerateFiles(Path.GetDirectoryName(source)!, "appsettings*.json"))
                File.Copy(extra, Path.Combine(installDir, Path.GetFileName(extra)), overwrite: true);
            Console.WriteLine($"  Copied the program to {target}");
        }
        else
        {
            Console.WriteLine($"  Running from {installDir}; program files left in place");
        }

        // 3. Data directory + settings (the database is opened directly while the service is stopped)
        var paths = new AppPaths();
        paths.EnsureCreated();
        HardenDataDirectory(paths.DataDir);
        Console.WriteLine($"  Data directory: {paths.DataDir} (access limited to SYSTEM and Administrators)");
        UiSettings ui;
        using (var store = new LiteStore(paths))
        {
            ui = store.GetSettings<UiSettings>();
            if (uiPort is not null) ui.Port = uiPort.Value;
            if (bind is not null) ui.BindAddress = bind;
            store.SaveSettings(ui);
        }
        Console.WriteLine($"  UI listener: {ui.BindAddress}:{ui.Port}{(ui.HttpsEnabled ? $" and HTTPS {ui.HttpsPort}" : "")}");

        // 4. Service
        var changes = await WindowsServiceManager.CreateOrRepairAsync(ManagerServiceDefinition(target));
        foreach (var c in changes) Console.WriteLine("  " + c);
        Console.WriteLine($"  Service '{name}': LocalSystem, Automatic (Delayed Start), restart on failure");

        // 5. Firewall
        await EnsureUiFirewallRulesAsync(paths, ui);

        // 6. Start
        if (noStart)
        {
            Console.WriteLine($"  --no-start: start it later with: Start-Service {name}");
        }
        else
        {
            Console.WriteLine($"  Starting '{name}'");
            await WindowsServiceManager.StartAsync(name, ServiceTimeout);
        }

        Console.WriteLine();
        Console.WriteLine("Done.");
        var host = IPAddress.TryParse(ui.BindAddress, out var ip) && !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.IPv6Any)
            ? ui.BindAddress : Environment.MachineName;
        Console.WriteLine($"  Web UI:  http://{host}:{ui.Port}/");
        if (ui.HttpsEnabled) Console.WriteLine($"           https://{host}:{ui.HttpsPort}/");
        if (!noStart)
        {
            for (var i = 0; i < 20 && !File.Exists(paths.SetupTokenFile); i++) await Task.Delay(500);
        }
        if (File.Exists(paths.SetupTokenFile) || noStart)
        {
            Console.WriteLine($"  First-run setup token: {paths.SetupTokenFile}");
            Console.WriteLine($"           (show it with: Get-Content \"{paths.SetupTokenFile}\")");
        }
        Console.WriteLine($"  Logs:    {paths.ManagerLogDir}");
        return 0;
    }

    [SupportedOSPlatform("windows")]
    public static ServiceDefinition ManagerServiceDefinition(string exePath) => new()
    {
        Name = AppPaths.ManagerServiceName,
        DisplayName = AppPaths.ProductName,
        Description = ManagerDescription,
        BinaryPathName = $"\"{exePath}\"",
        StartType = "delayed-auto",
        RestartDelaysMs = [5000, 10000, 30000],
        FailureResetSeconds = 86400,
        Environment = null,
    };

    [SupportedOSPlatform("windows")]
    private static async Task EnsureUiFirewallRulesAsync(AppPaths paths, UiSettings ui)
    {
        var loopback = IPAddress.TryParse(ui.BindAddress, out var ip) && IPAddress.IsLoopback(ip);
        if (loopback)
        {
            Console.WriteLine("  UI is bound to loopback; no firewall rule needed");
            return;
        }
        var rules = ReadinessService.RequiredRules(paths, new CaddySettings(), ui, ui.Port)
            .Where(r => r.Service == AppPaths.ManagerServiceName).ToList();
        var ps = new PowerShellRunner(NullLogger<PowerShellRunner>.Instance);
        foreach (var rule in rules)
        {
            try
            {
                await ps.RunJsonAsync(ReadinessScripts.CreateFirewallRule(rule));
                Console.WriteLine($"  Firewall: '{rule.DisplayName}' allows inbound TCP {rule.Port}");
            }
            catch (Exception ex) when (ex is PowerShellException or TimeoutException or InvalidOperationException)
            {
                Console.Error.WriteLine($"  WARNING: could not create the firewall rule '{rule.DisplayName}': {ex.Message}");
                Console.Error.WriteLine($"           Create it manually: {ReadinessScripts.CreateFirewallRuleCommand(rule)}");
            }
        }
    }

    /// <summary>Restricts DataDir to SYSTEM and Administrators (it holds the database, keys, certificates and the setup token).</summary>
    [SupportedOSPlatform("windows")]
    private static void HardenDataDirectory(string dir)
    {
        try
        {
            var sec = new DirectorySecurity();
            sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(dir).SetAccessControl(sec);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            Console.Error.WriteLine($"  WARNING: could not restrict the permissions of {dir}: {ex.Message}");
        }
    }

    private static async Task CopyWithRetryAsync(string from, string to)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(from, to, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                await Task.Delay(500); // the stopped service process may take a moment to release the file
            }
        }
    }

    // ------------------------------------------------------------------ configure (used by the MSI)

    private static int Configure(string[] args)
    {
        int? uiPort = null;
        string? bind = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--ui-port" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out var port) || port is < 1 or > 65535)
                        return Usage($"--ui-port must be a TCP port between 1 and 65535 (got '{args[i]}').");
                    uiPort = port;
                    break;
                case "--bind" when i + 1 < args.Length:
                    if (!IPAddress.TryParse(args[++i], out _)) return Usage($"--bind must be an IP address (got '{args[i]}').");
                    bind = args[i];
                    break;
                default:
                    return Usage($"Unknown or incomplete option '{args[i]}'.");
            }
        }
        var paths = new AppPaths();
        paths.EnsureCreated();
        if (OperatingSystem.IsWindows()) HardenDataDirectory(paths.DataDir);
        using var store = new LiteStore(paths);
        var ui = store.GetSettings<UiSettings>();
        if (uiPort is not null) ui.Port = uiPort.Value;
        if (bind is not null) ui.BindAddress = bind;
        store.SaveSettings(ui);
        Console.WriteLine($"UI listener set to {ui.BindAddress}:{ui.Port} (takes effect when the service starts).");
        return 0;
    }

    // ------------------------------------------------------------------ uninstall

    private static async Task<int> UninstallAsync(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return WindowsOnly("uninstall");
        if (!IsElevated())
        {
            Console.Error.WriteLine("'uninstall' must be run from an elevated prompt (Run as administrator).");
            return 1;
        }
        var purge = false;
        foreach (var a in args)
        {
            if (a.Equals("--purge", StringComparison.OrdinalIgnoreCase)) purge = true;
            else return Usage($"Unknown option '{a}'.");
        }

        var failures = 0;
        foreach (var svc in new[] { AppPaths.ManagerServiceName, AppPaths.CaddyServiceName })
        {
            try
            {
                if (WindowsServiceManager.Exists(svc))
                {
                    await WindowsServiceManager.DeleteAsync(svc);
                    Console.WriteLine($"Removed the service '{svc}'.");
                }
                else Console.WriteLine($"Service '{svc}' is not installed.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
            {
                failures++;
                Console.Error.WriteLine($"ERROR removing the service '{svc}': {ex.Message}");
            }
        }

        try
        {
            var ps = new PowerShellRunner(NullLogger<PowerShellRunner>.Instance);
            var r = await ps.RunJsonAsync(ReadinessScripts.RemoveFirewallGroup());
            var removed = r.TryGetProperty("removed", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array ? arr.GetArrayLength() : 0;
            Console.WriteLine($"Removed {removed} firewall rule(s) of the group '{ReadinessScripts.FirewallGroup}'.");
        }
        catch (Exception ex) when (ex is PowerShellException or TimeoutException or InvalidOperationException)
        {
            failures++;
            Console.Error.WriteLine($"ERROR removing firewall rules: {ex.Message}");
            Console.Error.WriteLine($"  Remove them manually: Get-NetFirewallRule -Group '{ReadinessScripts.FirewallGroup}' | Remove-NetFirewallRule");
        }

        var paths = new AppPaths();
        if (purge)
        {
            try
            {
                if (Directory.Exists(paths.DataDir))
                {
                    await DeleteDirectoryWithRetryAsync(paths.DataDir);
                    Console.WriteLine($"Deleted {paths.DataDir} (database, certificates, Caddy storage, logs).");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures++;
                Console.Error.WriteLine($"ERROR deleting {paths.DataDir}: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine($"Data kept in {paths.DataDir} (use --purge to delete it).");
        }

        var installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppPaths.ProductName);
        if (Directory.Exists(installDir))
            Console.WriteLine($"Program files remain in {installDir}. If installed with the MSI, remove it from Apps & features; otherwise delete the folder.");
        return failures == 0 ? 0 : 1;
    }

    private static async Task DeleteDirectoryWithRetryAsync(string dir)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 10)
            {
                await Task.Delay(1000); // processes that just stopped may still hold log files
            }
        }
    }

    /// <summary>Hidden verb for the MSI uninstall custom action: stop and delete the Caddy service only.</summary>
    private static async Task<int> UninstallCaddyServiceAsync()
    {
        if (!OperatingSystem.IsWindows()) return WindowsOnly("uninstall-caddy-service");
        try
        {
            await WindowsServiceManager.DeleteAsync(AppPaths.CaddyServiceName);
            Console.WriteLine($"Service '{AppPaths.CaddyServiceName}' removed (or was not installed).");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            Console.Error.WriteLine($"Could not remove the service '{AppPaths.CaddyServiceName}': {ex.Message}");
            return 1;
        }
    }

    // ------------------------------------------------------------------ service-status

    private static async Task<int> ServiceStatusAsync()
    {
        if (!OperatingSystem.IsWindows()) return WindowsOnly("service-status");
        var allOk = true;
        foreach (var svc in new[] { AppPaths.ManagerServiceName, AppPaths.CaddyServiceName })
        {
            var reg = WindowsServiceManager.ReadRegistry(svc);
            var status = reg is null ? null : WindowsServiceManager.GetStatus(svc);
            Console.WriteLine($"{svc}");
            if (reg is null || status is null)
            {
                Console.WriteLine("  State:      NOT INSTALLED");
                allOk = false;
                continue;
            }
            var q = await WindowsServiceManager.QueryExAsync(svc);
            var failure = await WindowsServiceManager.QueryFailureAsync(svc);
            Console.WriteLine($"  Display:    {reg.DisplayName}");
            Console.WriteLine($"  State:      {status}{(q?.ProcessId is int pid ? $" (PID {pid})" : "")}");
            Console.WriteLine($"  Start type: {reg.StartTypeDisplay}");
            Console.WriteLine($"  Account:    {reg.ObjectName}");
            Console.WriteLine($"  Command:    {reg.ImagePath}");
            Console.WriteLine($"  Recovery:   {(failure is null ? "unknown" : failure.RestartActions > 0 ? $"restart ({string.Join(", ", failure.Actions.Select(a => $"{a.DelayMs / 1000}s"))}), reset after {failure.ResetPeriodSeconds / 3600}h" : "none")}");
            if (status != System.ServiceProcess.ServiceControllerStatus.Running) allOk = false;
        }
        var paths = new AppPaths();
        Console.WriteLine();
        Console.WriteLine($"Data directory: {paths.DataDir}");
        if (File.Exists(paths.CaddyExe))
        {
            try
            {
                var v = await ProcessRunner.RunAsync(paths.CaddyExe, ["version"], new ProcessOptions { Timeout = TimeSpan.FromSeconds(15) });
                Console.WriteLine($"Caddy binary:   {paths.CaddyExe} ({v.StdOut.Trim().Split(' ')[0]})");
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
            {
                Console.WriteLine($"Caddy binary:   {paths.CaddyExe} (version unknown: {ex.Message})");
            }
        }
        else
        {
            Console.WriteLine("Caddy binary:   not installed");
        }
        return allOk ? 0 : 3;
    }

    // ------------------------------------------------------------------ helpers

    [SupportedOSPlatform("windows")]
    private static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static int Usage(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine("Run 'CaddyManager.exe help' for usage.");
        return 2;
    }
}
