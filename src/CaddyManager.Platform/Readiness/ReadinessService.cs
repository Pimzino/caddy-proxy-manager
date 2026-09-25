using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using CaddyManager.Platform.Hosting;
using CaddyManager.Platform.Infrastructure;
using CaddyManager.Platform.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace CaddyManager.Platform.Readiness;

/// <summary>
/// Server readiness checks (System, Firewall, Network, Domain, Ports, Connectivity, DNS, Caddy), automatic fixes
/// and the domain GPO firewall script. On non-Windows hosts the Windows-only categories are reported as Skipped.
/// The last report is persisted to DataDir/readiness.json.
/// </summary>
public sealed partial class ReadinessService(
    AppPaths paths,
    IStore store,
    ICaddyHost host,
    PowerShellRunner powershell,
    OutboundHttp http,
    IServiceProvider services,
    ILogger<ReadinessService> logger) : IReadinessService
{
    public const string ReportFileName = "readiness.json";
    private const string ClockUrl = "https://acme-v02.api.letsencrypt.org/directory";
    private const string PublicIpUrl = "https://api.ipify.org";
    private const long GiB = 1024L * 1024 * 1024;

    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly Lock _loadLock = new();
    private ReadinessReport? _last;
    private bool _loaded;

    private string ReportFile => Path.Combine(paths.DataDir, ReportFileName);

    private sealed record Context(
        CaddySettings Caddy,
        UiSettings Ui,
        int UiPort,
        List<RequiredFirewallRule> Rules,
        List<SiteHost> EnabledHosts,
        List<string> AcmeDomains);

    // ------------------------------------------------------------------ report persistence

    public ReadinessReport? LastReport
    {
        get
        {
            if (_loaded) return _last;
            lock (_loadLock)
            {
                if (_loaded) return _last;
                try
                {
                    if (File.Exists(ReportFile))
                        _last = JsonSerializer.Deserialize<ReadinessReport>(File.ReadAllText(ReportFile), JsonDefaults.Api);
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    logger.LogWarning("Could not read the previous readiness report {File}: {Error}", ReportFile, ex.Message);
                }
                _loaded = true;
                return _last;
            }
        }
    }

    private void Save(ReadinessReport report)
    {
        _last = report;
        _loaded = true;
        try
        {
            var tmp = ReportFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(report, JsonDefaults.Api));
            File.Move(tmp, ReportFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not save the readiness report: {Error}", ex.Message);
        }
    }

    // ------------------------------------------------------------------ run

    public async Task<ReadinessReport> RunAsync(CancellationToken ct = default)
    {
        await _runGate.WaitAsync(ct);
        try
        {
            var sw = Stopwatch.StartNew();
            var ctx = BuildContext();
            var windows = OperatingSystem.IsWindows();

            var tcpPorts = new[] { ctx.Caddy.HttpPort, ctx.Caddy.HttpsPort };
            var udpPorts = ctx.Caddy.EnableHttp3 ? new[] { ctx.Caddy.HttpsPort } : [];
            var sysTask = windows ? Capture(() => RunSystemFactsAsync(tcpPorts, udpPorts, ct)) : Task.FromResult<(SystemFacts?, string?)>((null, null));
            var fwTask = windows ? Capture(() => RunFirewallFactsAsync(ctx.Rules.Select(r => r.Port), ct)) : Task.FromResult<(FirewallFacts?, string?)>((null, null));
            var statusTask = host.GetStatusAsync(ct);
            var clockTask = ClockCheckAsync(ct);
            var connTask = ConnectivityChecksAsync(ctx, ct);
            var dnsTask = DnsChecksAsync(ctx, ct);
            var fqdnTask = FqdnAsync();
            await Task.WhenAll(sysTask, fwTask, statusTask, clockTask, connTask, dnsTask, fqdnTask);

            var (sys, sysError) = sysTask.Result;
            var (fw, fwError) = fwTask.Result;
            var status = statusTask.Result;

            var checks = new List<ReadinessCheck>();
            checks.AddRange(SystemChecks(ctx));
            checks.Add(clockTask.Result);
            if (windows)
            {
                checks.AddRange(FirewallChecks(ctx, fw, fwError, sys));
                checks.AddRange(NetworkChecks(sys, sysError));
                checks.AddRange(DomainChecks(ctx, sys, sysError));
                checks.AddRange(WindowsPortChecks(ctx, sys, sysError, status));
            }
            else
            {
                checks.Add(Skipped("firewall.windows", "Firewall", "Windows Defender Firewall", "Firewall checks run on Windows only."));
                checks.Add(Skipped("network.profiles", "Network", "Network connection profiles", "Network profile checks run on Windows only."));
                checks.Add(Skipped("domain.membership", "Domain", "Active Directory domain", "Domain checks run on Windows only."));
                checks.AddRange(PortChecksPortable(ctx, status));
            }
            checks.AddRange(connTask.Result);
            if (windows) checks.Add(WinHttpProxyCheck(sys));
            checks.AddRange(dnsTask.Result);
            checks.AddRange(await CaddyChecksAsync(ctx, status, ct));

            var report = new ReadinessReport
            {
                RanAt = DateTime.UtcNow,
                Machine = new MachineInfo
                {
                    Hostname = Environment.MachineName,
                    Fqdn = fqdnTask.Result,
                    OsDescription = OsDescription(),
                    IsWindows = windows,
                    DomainJoined = sys?.Domain.PartOfDomain ?? false,
                    Domain = sys?.Domain.PartOfDomain == true ? sys.Domain.Domain : null,
                    ComputerDn = sys?.Domain.ComputerDn,
                    IpAddresses = LocalAddresses().Select(a => a.ToString()).ToList(),
                    NetworkProfiles = sys is null ? new() : WindowsFactsParser.ToProfileInfos(sys.Profiles),
                },
                Checks = checks,
            };
            Save(report);
            logger.LogInformation("Readiness checks finished in {Ms} ms: {Pass} pass, {Warn} warn, {Fail} fail",
                sw.ElapsedMilliseconds, report.Pass, report.Warn, report.Fail);
            return report;
        }
        finally
        {
            _runGate.Release();
        }
    }

    private static async Task<(T?, string?)> Capture<T>(Func<Task<T>> run) where T : class
    {
        try
        {
            return (await run(), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, ex.Message);
        }
    }

    private async Task<SystemFacts> RunSystemFactsAsync(int[] tcp, int[] udp, CancellationToken ct) =>
        WindowsFactsParser.ParseSystem(await powershell.RunJsonAsync(ReadinessScripts.SystemFacts(tcp, udp), ct: ct));

    private async Task<FirewallFacts> RunFirewallFactsAsync(IEnumerable<int> ports, CancellationToken ct) =>
        WindowsFactsParser.ParseFirewall(await powershell.RunJsonAsync(ReadinessScripts.FirewallFacts(ports), ct: ct));

    private Context BuildContext()
    {
        var caddy = store.GetSettings<CaddySettings>();
        var ui = store.GetSettings<UiSettings>();
        var uiPort = int.TryParse(Environment.GetEnvironmentVariable("CM_UI_PORT"), out var p) && p is > 0 and < 65536 ? p : ui.Port;
        List<SiteHost> hosts;
        try
        {
            hosts = store.Col<SiteHost>().Find(h => h.Enabled).ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not read hosts for readiness checks: {Error}", ex.Message);
            hosts = new();
        }
        var acmeDomains = hosts.Where(h => h.Tls == TlsMode.Acme)
            .SelectMany(h => h.Domains)
            .Select(d => d.Trim().TrimEnd('.').ToLowerInvariant())
            .Where(d => d.Length > 0 && !d.StartsWith("*.", StringComparison.Ordinal))
            .Distinct().Take(50).ToList();
        return new Context(caddy, ui, uiPort, RequiredRules(paths, caddy, ui, uiPort), hosts, acmeDomains);
    }

    /// <summary>Inbound rules the product needs with the current settings.</summary>
    public static List<RequiredFirewallRule> RequiredRules(AppPaths paths, CaddySettings caddy, UiSettings ui, int uiPort)
    {
        var list = new List<RequiredFirewallRule>
        {
            new()
            {
                CheckId = $"firewall.tcp{caddy.HttpPort}", DisplayName = "Caddy Proxy Manager - HTTP (TCP-In)", Protocol = "TCP",
                Port = caddy.HttpPort, Purpose = "Caddy HTTP (ACME HTTP-01 challenges, HTTP→HTTPS redirects)",
                Program = paths.CaddyExe, Service = AppPaths.CaddyServiceName,
            },
            new()
            {
                CheckId = $"firewall.tcp{caddy.HttpsPort}", DisplayName = "Caddy Proxy Manager - HTTPS (TCP-In)", Protocol = "TCP",
                Port = caddy.HttpsPort, Purpose = "Caddy HTTPS", Program = paths.CaddyExe, Service = AppPaths.CaddyServiceName,
            },
        };
        if (caddy.EnableHttp3)
            list.Add(new()
            {
                CheckId = $"firewall.udp{caddy.HttpsPort}", DisplayName = "Caddy Proxy Manager - HTTP/3 (UDP-In)", Protocol = "UDP",
                Port = caddy.HttpsPort, Purpose = "Caddy HTTP/3 (QUIC)", Program = paths.CaddyExe, Service = AppPaths.CaddyServiceName,
            });
        var uiLoopback = IPAddress.TryParse(ui.BindAddress, out var bind) && IPAddress.IsLoopback(bind);
        if (!uiLoopback)
        {
            list.Add(new()
            {
                CheckId = $"firewall.tcp{uiPort}", DisplayName = "Caddy Proxy Manager - Management UI (TCP-In)", Protocol = "TCP",
                Port = uiPort, Purpose = "Caddy Proxy Manager web UI", Program = Environment.ProcessPath, Service = AppPaths.ManagerServiceName,
            });
            if (ui.HttpsEnabled)
                list.Add(new()
                {
                    CheckId = $"firewall.tcp{ui.HttpsPort}", DisplayName = "Caddy Proxy Manager - Management UI HTTPS (TCP-In)", Protocol = "TCP",
                    Port = ui.HttpsPort, Purpose = "Caddy Proxy Manager web UI (HTTPS)", Program = Environment.ProcessPath,
                    Service = AppPaths.ManagerServiceName,
                });
        }
        return list.DistinctBy(r => r.CheckId).ToList();
    }

    // ------------------------------------------------------------------ System

    private IEnumerable<ReadinessCheck> SystemChecks(Context ctx)
    {
        yield return OsCheck();
        yield return IdentityCheck();
        yield return DiskCheck();
        if (OperatingSystem.IsWindows()) yield return PendingRebootCheck();
    }

    private static string OsDescription()
    {
        if (OperatingSystem.IsWindows())
        {
            var (product, display, build, ubr, _) = WindowsVersion();
            return $"{product ?? "Windows"}{(display is null ? "" : " " + display)} (build {build}{(ubr > 0 ? "." + ubr : "")})";
        }
        return RuntimeInformation.OSDescription;
    }

    private static (string? Product, string? Display, int Build, int Ubr, string? InstallationType) WindowsVersion()
    {
        if (!OperatingSystem.IsWindows()) return (null, null, 0, 0, null);
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var build = Environment.OSVersion.Version.Build;
        return (k?.GetValue("ProductName") as string, k?.GetValue("DisplayVersion") as string, build,
            k?.GetValue("UBR") is int u ? u : 0, k?.GetValue("InstallationType") as string);
    }

    private static ReadinessCheck OsCheck()
    {
        if (!OperatingSystem.IsWindows())
            return new ReadinessCheck
            {
                Id = "system.os", Category = "System", Title = "Operating system", Status = CheckStatus.Info,
                Summary = $"{RuntimeInformation.OSDescription} — development platform (production target: Windows Server 2025).",
            };
        var (product, display, build, ubr, installType) = WindowsVersion();
        var isServer = installType?.StartsWith("Server", StringComparison.OrdinalIgnoreCase) == true;
        var name = $"{product ?? "Windows"} (build {build}{(ubr > 0 ? "." + ubr : "")}{(installType is null ? "" : ", " + installType)})";
        var (status, summary) = isServer switch
        {
            true when build >= 26100 => (CheckStatus.Pass, $"{name} — Windows Server 2025 or newer."),
            true when build >= 17763 => (CheckStatus.Warn, $"{name} — works, but the tested target is Windows Server 2025 (build 26100)."),
            true => (CheckStatus.Warn, $"{name} — old Windows Server release; Windows Server 2025 is recommended."),
            false => (CheckStatus.Warn, $"{name} — not a server edition. Fine for testing; use Windows Server 2025 in production."),
        };
        return new ReadinessCheck
        {
            Id = "system.os", Category = "System", Title = "Operating system", Status = status, Summary = summary,
            Details = display is null ? null : $"Version {display}",
        };
    }

    private static ReadinessCheck IdentityCheck()
    {
        var isService = WindowsServiceHelpers.IsWindowsService();
        if (!OperatingSystem.IsWindows())
            return new ReadinessCheck
            {
                Id = "system.identity", Category = "System", Title = "Manager account", Status = CheckStatus.Info,
                Summary = $"Running as '{Environment.UserName}' in console mode (development).",
            };
        using var id = WindowsIdentity.GetCurrent();
        var status = id.IsSystem && isService ? CheckStatus.Pass : isService ? CheckStatus.Warn : CheckStatus.Info;
        var summary = status switch
        {
            CheckStatus.Pass => "Running as the Windows service 'CaddyProxyManager' under LocalSystem.",
            CheckStatus.Warn => $"Running as a service under '{id.Name}'. LocalSystem is expected (it needs rights to manage services, firewall and ProgramData).",
            _ => $"Running interactively as '{id.Name}' (console mode). Install the service with 'CaddyManager.exe install' for production.",
        };
        return new ReadinessCheck
        {
            Id = "system.identity", Category = "System", Title = "Manager account", Status = status, Summary = summary,
            Remediation = status == CheckStatus.Warn ? "sc.exe config CaddyProxyManager obj= LocalSystem, then restart the service." : null,
        };
    }

    private ReadinessCheck DiskCheck()
    {
        try
        {
            var drive = DriveFor(paths.DataDir);
            var free = drive.AvailableFreeSpace;
            var status = free < 1 * GiB ? CheckStatus.Fail : free < 5 * GiB ? CheckStatus.Warn : CheckStatus.Pass;
            return new ReadinessCheck
            {
                Id = "system.disk", Category = "System", Title = "Free disk space", Status = status,
                Summary = $"{free / (double)GiB:0.0} GB free of {drive.TotalSize / (double)GiB:0.0} GB on {drive.Name} (data directory {paths.DataDir}).",
                Remediation = status == CheckStatus.Pass ? null : "Free up space: logs are in " + Path.Combine(paths.DataDir, "logs") + ". Caddy needs space for certificates, logs and updates.",
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return new ReadinessCheck
            {
                Id = "system.disk", Category = "System", Title = "Free disk space", Status = CheckStatus.Warn,
                Summary = "Could not determine free disk space: " + ex.Message,
            };
        }
    }

    private static DriveInfo DriveFor(string path)
    {
        var full = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows()) return new DriveInfo(Path.GetPathRoot(full)!);
        // Unix: the mount point with the longest matching prefix.
        return DriveInfo.GetDrives()
            .Where(d => d.IsReady && full.StartsWith(d.RootDirectory.FullName, StringComparison.Ordinal))
            .OrderByDescending(d => d.RootDirectory.FullName.Length)
            .First();
    }

    private static ReadinessCheck PendingRebootCheck()
    {
        var reasons = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
                if (k is not null) reasons.Add("Component Based Servicing");
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
                if (k is not null) reasons.Add("Windows Update");
            using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager"))
                if (k?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 }) reasons.Add("pending file rename operations");
        }
        return new ReadinessCheck
        {
            Id = "system.reboot", Category = "System", Title = "Pending reboot",
            Status = reasons.Count > 0 ? CheckStatus.Info : CheckStatus.Pass,
            Summary = reasons.Count > 0 ? $"A reboot is pending ({string.Join(", ", reasons)})." : "No reboot pending.",
            Remediation = reasons.Count > 0 ? "Plan a reboot in a maintenance window; Caddy and the manager start automatically afterwards." : null,
        };
    }

    private async Task<ReadinessCheck> ClockCheckAsync(CancellationToken ct)
    {
        const string id = "system.clock", title = "System clock";
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var t0 = DateTime.UtcNow;
            using var resp = await http.Client.GetAsync(ClockUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var t1 = DateTime.UtcNow;
            if (resp.Headers.Date is not { } serverDate)
                return Info(id, "System", title, "Could not compare the clock: no Date header from acme-v02.api.letsencrypt.org.");
            var local = t0 + (t1 - t0) / 2;
            var skew = local - serverDate.UtcDateTime;
            var abs = Math.Abs(skew.TotalSeconds);
            var status = abs > 300 ? CheckStatus.Fail : abs > 30 ? CheckStatus.Warn : CheckStatus.Pass;
            return new ReadinessCheck
            {
                Id = id, Category = "System", Title = title, Status = status,
                Summary = status == CheckStatus.Pass
                    ? $"Clock is in sync (offset {skew.TotalSeconds:+0;-0;0}s vs Let's Encrypt)."
                    : $"Clock is off by {skew.TotalSeconds:+0;-0}s compared with Let's Encrypt. TLS and ACME fail when the clock is wrong.",
                Remediation = status == CheckStatus.Pass ? null
                    : "Domain members sync from the domain hierarchy: w32tm /resync /force (check 'w32tm /query /status'). Standalone: w32tm /config /manualpeerlist:time.windows.com /syncfromflags:manual /update.",
                Script = status == CheckStatus.Pass ? null : "w32tm /resync /force\nw32tm /query /status",
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Info(id, "System", title, $"Could not compare the clock with acme-v02.api.letsencrypt.org: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ Firewall

    private IEnumerable<ReadinessCheck> FirewallChecks(Context ctx, FirewallFacts? fw, string? fwError, SystemFacts? sys)
    {
        if (fw is null)
        {
            yield return new ReadinessCheck
            {
                Id = "firewall.collect", Category = "Firewall", Title = "Windows Defender Firewall", Status = CheckStatus.Warn,
                Summary = "Could not read the firewall configuration.", Details = fwError,
                Remediation = "Make sure the manager runs as LocalSystem and the NetSecurity PowerShell module is available.",
            };
            yield break;
        }

        yield return new ReadinessCheck
        {
            Id = "firewall.service", Category = "Firewall", Title = "Firewall service (mpssvc)",
            Status = fw.Service.Equals("Running", StringComparison.OrdinalIgnoreCase) ? CheckStatus.Pass : CheckStatus.Fail,
            Summary = fw.Service.Equals("Running", StringComparison.OrdinalIgnoreCase)
                ? "Windows Defender Firewall service is running."
                : $"Windows Defender Firewall service is {fw.Service}. Firewall rules (and some network features) depend on it.",
            Remediation = fw.Service.Equals("Running", StringComparison.OrdinalIgnoreCase) ? null : "Start the service: Start-Service mpssvc (and set it to Automatic).",
            Script = fw.Service.Equals("Running", StringComparison.OrdinalIgnoreCase) ? null : "Set-Service mpssvc -StartupType Automatic\nStart-Service mpssvc",
        };

        var active = FirewallEvaluator.ActiveProfiles(sys?.Profiles ?? []);
        foreach (var p in fw.Profiles)
        {
            var enabled = p.Enabled.Equals("True", StringComparison.OrdinalIgnoreCase);
            var isActive = active.Contains(p.Name, StringComparer.OrdinalIgnoreCase);
            yield return new ReadinessCheck
            {
                Id = $"firewall.profile.{p.Name.ToLowerInvariant()}", Category = "Firewall", Title = $"Firewall profile: {p.Name}",
                Status = enabled ? CheckStatus.Pass : isActive ? CheckStatus.Warn : CheckStatus.Info,
                Summary = (enabled ? $"{p.Name} profile is enabled (default inbound: {p.DefaultInboundAction})." : $"{p.Name} profile is disabled — inbound traffic is not filtered.")
                          + (isActive ? " Active." : " Not active."),
                Remediation = enabled ? null : "Keep the firewall enabled and allow only the required ports: Set-NetFirewallProfile -Name " + p.Name + " -Enabled True",
            };
        }

        var ignored = active.Where(p => FirewallEvaluator.LocalRulesIgnored(fw, p)).ToList();
        yield return new ReadinessCheck
        {
            Id = "firewall.localrules", Category = "Firewall", Title = "Local firewall rules honoured",
            Status = ignored.Count == 0 ? CheckStatus.Pass : CheckStatus.Warn,
            Summary = ignored.Count == 0
                ? "Local firewall rules are applied (Group Policy does not disable them)."
                : $"Group Policy sets 'Apply local firewall rules: No' for {string.Join(", ", ignored)}: rules created on this server are ignored.",
            Remediation = ignored.Count == 0 ? null : "Deploy the rules through a domain GPO — run the GPO script (Domain check) on a DC / admin workstation.",
            Script = ignored.Count == 0 ? null : BuildGpoScript(sys),
        };

        foreach (var rule in ctx.Rules)
            yield return FirewallEvaluator.Evaluate(rule, fw, active);
    }

    // ------------------------------------------------------------------ Network

    private IEnumerable<ReadinessCheck> NetworkChecks(SystemFacts? sys, string? error)
    {
        if (sys is null)
        {
            yield return new ReadinessCheck
            {
                Id = "network.collect", Category = "Network", Title = "Network profiles", Status = CheckStatus.Warn,
                Summary = "Could not read network information.", Details = error,
            };
            yield break;
        }
        if (sys.Profiles.Count == 0)
        {
            yield return Info("network.profiles", "Network", "Network profiles", "No connected network profiles were found.");
            yield break;
        }
        var joined = sys.Domain.PartOfDomain;
        foreach (var p in sys.Profiles)
        {
            var id = $"network.profile.{p.InterfaceIndex}";
            var title = $"Network profile: {p.InterfaceAlias}";
            var desc = $"'{p.InterfaceAlias}' ({p.Name}) is {p.Category}.";
            if (joined)
            {
                var ok = p.Category.Equals("DomainAuthenticated", StringComparison.OrdinalIgnoreCase);
                yield return new ReadinessCheck
                {
                    Id = id, Category = "Network", Title = title, Status = ok ? CheckStatus.Pass : CheckStatus.Warn,
                    Summary = ok ? desc : desc + " A domain-joined server should be DomainAuthenticated: Network Location Awareness could not reach a domain controller, so the Domain firewall profile (and GPO rules scoped to it) do not apply.",
                    Remediation = ok ? null : "Check that this adapter's DNS servers are the domain controllers and that a DC is reachable, then restart NLA: Restart-Service NlaSvc -Force. Adding a DNS suffix / fixing time sync can also help.",
                    Script = ok ? null : $"Get-DnsClientServerAddress -InterfaceIndex {p.InterfaceIndex}\nnltest /dsgetdc:{sys.Domain.Domain}\nRestart-Service NlaSvc -Force\nGet-NetConnectionProfile -InterfaceIndex {p.InterfaceIndex}",
                };
            }
            else
            {
                var isPublic = p.Category.Equals("Public", StringComparison.OrdinalIgnoreCase);
                yield return new ReadinessCheck
                {
                    Id = id, Category = "Network", Title = title, Status = isPublic ? CheckStatus.Warn : CheckStatus.Pass,
                    Summary = isPublic ? desc + " The Public profile is the most restrictive; servers usually use Private." : desc,
                    Remediation = isPublic ? "Set the network category to Private (click Fix)." : null,
                    Script = isPublic ? $"Set-NetConnectionProfile -InterfaceIndex {p.InterfaceIndex} -NetworkCategory Private" : null,
                    Fixable = isPublic,
                };
            }
        }
    }

    // ------------------------------------------------------------------ Domain

    private IEnumerable<ReadinessCheck> DomainChecks(Context ctx, SystemFacts? sys, string? error)
    {
        if (sys is null)
        {
            yield return new ReadinessCheck
            {
                Id = "domain.membership", Category = "Domain", Title = "Active Directory domain", Status = CheckStatus.Warn,
                Summary = "Could not determine domain membership.", Details = error,
            };
            yield break;
        }
        var d = sys.Domain;
        if (!d.PartOfDomain)
        {
            yield return Info("domain.membership", "Domain", "Active Directory domain",
                $"Not domain joined (workgroup '{d.Domain}'). Firewall rules are managed locally.");
            yield break;
        }
        yield return new ReadinessCheck
        {
            Id = "domain.membership", Category = "Domain", Title = "Active Directory domain", Status = CheckStatus.Info,
            Summary = $"Joined to {d.Domain}." + (d.ComputerDn is null ? "" : $" Computer account: {d.ComputerDn}"),
            Details = d.DnError is null ? null : "The computer's distinguished name could not be resolved: " + d.DnError,
        };
        yield return new ReadinessCheck
        {
            Id = "domain.gpo", Category = "Domain", Title = "Firewall rules via Group Policy", Status = CheckStatus.Info,
            Summary = $"Recommended for domain members: deploy the inbound rules with the GPO '{GpoScriptBuilder.DefaultGpoName}' linked to " +
                      (GpoScriptBuilder.ParentDn(d.ComputerDn) is { } ou ? ou : "the server's OU") + ".",
            Details = "Rules in a GPO apply even when Group Policy disables local firewall rules, and survive server rebuilds. " +
                      "Run the script on a domain controller or an admin workstation with RSAT (GroupPolicy module), then run 'gpupdate /target:computer /force' here.",
            Remediation = "Download the GPO script (Readiness page → GPO script) and run it as a Domain Admin.",
            Script = BuildGpoScript(sys),
        };
        if (ctx.EnabledHosts.Any(h => h.Tls == TlsMode.Internal))
            yield return new ReadinessCheck
            {
                Id = "domain.internalca", Category = "Domain", Title = "Internal CA root distribution", Status = CheckStatus.Info,
                Summary = "Some hosts use Caddy's internal CA. Distribute its root certificate to clients via Group Policy so browsers trust those sites.",
                Details = $"Root certificate: {InternalRootPath} (download from Certificates → Internal root CA).",
                Remediation = "GPO: Computer Configuration → Policies → Windows Settings → Security Settings → Public Key Policies → Trusted Root Certification Authorities → Import. Or publish to AD (Enterprise Admins): certutil -dspublish -f root.crt RootCA",
                Script = $"certutil -dspublish -f \"{InternalRootPath}\" RootCA",
            };
    }

    private string InternalRootPath => Path.Combine(paths.CaddyStorageDir, "pki", "authorities", "local", "root.crt");

    // ------------------------------------------------------------------ Ports

    private IEnumerable<ReadinessCheck> WindowsPortChecks(Context ctx, SystemFacts? sys, string? error, CaddyStatus status)
    {
        if (sys is null)
        {
            yield return new ReadinessCheck
            {
                Id = "ports.collect", Category = "Ports", Title = "Port availability", Status = CheckStatus.Warn,
                Summary = "Could not list listening ports.", Details = error,
            };
            yield break;
        }
        var wanted = new List<(string Proto, int Port)> { ("TCP", ctx.Caddy.HttpPort), ("TCP", ctx.Caddy.HttpsPort) };
        if (ctx.Caddy.EnableHttp3) wanted.Add(("UDP", ctx.Caddy.HttpsPort));
        foreach (var (proto, port) in wanted.Distinct())
        {
            var listeners = sys.Listeners.Where(l => l.Protocol.Equals(proto, StringComparison.OrdinalIgnoreCase) && l.Port == port).ToList();
            yield return PortCheck(proto, port, listeners, sys.HttpSysUrls, status);
        }

        var w3 = sys.W3svc;
        yield return new ReadinessCheck
        {
            Id = "ports.iis", Category = "Ports", Title = "IIS (W3SVC)",
            Status = w3 is null ? CheckStatus.Pass : w3.Equals("Running", StringComparison.OrdinalIgnoreCase) ? CheckStatus.Warn : CheckStatus.Info,
            Summary = w3 is null ? "IIS is not installed."
                : w3.Equals("Running", StringComparison.OrdinalIgnoreCase)
                    ? $"IIS (W3SVC) is running. Make sure no IIS site is bound to port {ctx.Caddy.HttpPort}/{ctx.Caddy.HttpsPort}, or proxy IIS through Caddy on another port."
                    : $"IIS is installed but W3SVC is {w3}.",
            Remediation = w3?.Equals("Running", StringComparison.OrdinalIgnoreCase) == true
                ? "Move IIS bindings to other ports (e.g. 8080) or stop it: Stop-Service W3SVC; Set-Service W3SVC -StartupType Disabled"
                : null,
            Script = w3?.Equals("Running", StringComparison.OrdinalIgnoreCase) == true
                ? "Import-Module WebAdministration\nGet-WebBinding | Format-Table protocol, bindingInformation, ItemXPath"
                : null,
        };
    }

    private ReadinessCheck PortCheck(string proto, int port, List<PortListener> listeners, List<string> httpSysUrls, CaddyStatus status)
    {
        var id = $"ports.{proto.ToLowerInvariant()}{port}";
        var title = $"{proto} {port} available for Caddy";
        if (listeners.Count == 0)
            return new ReadinessCheck
            {
                Id = id, Category = "Ports", Title = title,
                Status = status.State == CaddyRunState.Running ? CheckStatus.Info : CheckStatus.Pass,
                Summary = status.State == CaddyRunState.Running
                    ? $"Nothing listens on {proto} {port} (Caddy is running but has no site using this port yet)."
                    : $"{proto} {port} is free.",
            };
        bool IsCaddy(PortListener l) =>
            (l.ProcessPath is not null && string.Equals(Path.GetFullPath(l.ProcessPath), Path.GetFullPath(paths.CaddyExe), StringComparison.OrdinalIgnoreCase))
            || string.Equals(l.ProcessName, "caddy", StringComparison.OrdinalIgnoreCase)
            || (status.ProcessId is int pid && l.ProcessId == pid);
        var others = listeners.Where(l => !IsCaddy(l)).ToList();
        if (others.Count == 0)
            return new ReadinessCheck
            {
                Id = id, Category = "Ports", Title = title, Status = CheckStatus.Pass,
                Summary = $"{proto} {port} is used by Caddy (PID {listeners[0].ProcessId}).",
            };
        var httpSys = others.Where(l => l.IsHttpSys).ToList();
        if (httpSys.Count > 0)
        {
            var portUrls = httpSysUrls.Where(u => UrlUsesPort(u, port)).Take(15).ToList();
            return new ReadinessCheck
            {
                Id = id, Category = "Ports", Title = title, Status = CheckStatus.Fail,
                Summary = $"{proto} {port} is held by http.sys (PID 4 'System'): a Windows component registered a URL on this port — typically IIS (W3SVC), WinRM, SQL Server Reporting Services, ADFS, WSUS or Windows Admin Center.",
                Details = portUrls.Count > 0 ? "http.sys registrations on this port:\n" + string.Join('\n', portUrls) : "Run the script to list the http.sys request queues and their URLs.",
                Remediation = "Identify the owner with 'netsh http show servicestate view=requestq', then move that service to another port or stop it.",
                Script = "netsh http show servicestate view=requestq verbose=no\nnetsh http show urlacl\nGet-Service W3SVC, WinRM, ReportServer*, adfssrv -ErrorAction SilentlyContinue",
            };
        }
        var who = string.Join(", ", others.Select(l => $"{l.ProcessName ?? "unknown"} (PID {l.ProcessId}{(l.ProcessPath is null ? "" : ", " + l.ProcessPath)}) on {l.Address}").Distinct());
        return new ReadinessCheck
        {
            Id = id, Category = "Ports", Title = title, Status = CheckStatus.Fail,
            Summary = $"{proto} {port} is already in use by {who}. Caddy cannot bind it.",
            Remediation = "Stop or reconfigure that program, or change Caddy's ports in Settings → Caddy.",
            Script = $"Get-Process -Id {string.Join(",", others.Select(o => o.ProcessId).Distinct())} | Format-List Id, ProcessName, Path",
        };
    }

    private static bool UrlUsesPort(string line, int port)
    {
        var m = UrlPort().Match(line);
        if (!m.Success) return false;
        if (m.Groups["port"].Success) return m.Groups["port"].Value == port.ToString();
        return (m.Groups["scheme"].Value.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80) == port;
    }

    [GeneratedRegex(@"(?<scheme>https?)://[^/:\s]+(?::(?<port>\d+))?", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPort();

    private IEnumerable<ReadinessCheck> PortChecksPortable(Context ctx, CaddyStatus status)
    {
        IPEndPoint[] tcp = [], udp = [];
        try
        {
            var props = IPGlobalProperties.GetIPGlobalProperties();
            tcp = props.GetActiveTcpListeners();
            udp = props.GetActiveUdpListeners();
        }
        catch (NetworkInformationException ex)
        {
            logger.LogDebug(ex, "Listing listeners failed");
        }
        var wanted = new List<(string Proto, int Port, IPEndPoint[] Eps)> { ("TCP", ctx.Caddy.HttpPort, tcp), ("TCP", ctx.Caddy.HttpsPort, tcp) };
        if (ctx.Caddy.EnableHttp3) wanted.Add(("UDP", ctx.Caddy.HttpsPort, udp));
        foreach (var (proto, port, eps) in wanted.DistinctBy(w => (w.Proto, w.Port)))
        {
            var used = eps.Where(e => e.Port == port).ToList();
            var running = status.State == CaddyRunState.Running;
            yield return new ReadinessCheck
            {
                Id = $"ports.{proto.ToLowerInvariant()}{port}", Category = "Ports", Title = $"{proto} {port} available for Caddy",
                Status = used.Count == 0 ? CheckStatus.Pass : running ? CheckStatus.Info : CheckStatus.Warn,
                Summary = used.Count == 0 ? $"{proto} {port} is free."
                    : running ? $"{proto} {port} is in use (on {string.Join(", ", used)}), probably by Caddy (process ownership is not available on this OS)."
                    : $"{proto} {port} is in use by another process (on {string.Join(", ", used)}) while Caddy is stopped.",
                Remediation = used.Count > 0 && !running ? (OperatingSystem.IsMacOS() ? $"Find it with: lsof -nP -i{proto}:{port}" : $"Find it with: ss -lptn 'sport = :{port}'") : null,
            };
        }
    }

    // ------------------------------------------------------------------ Connectivity

    private async Task<List<ReadinessCheck>> ConnectivityChecksAsync(Context ctx, CancellationToken ct)
    {
        var acmeNeeded = ctx.EnabledHosts.Any(h => h.Tls == TlsMode.Acme);
        var proxy = store.GetSettings<BinarySettings>().OutboundProxy;
        var targets = new (string Id, string Host, string Purpose, bool Required)[]
        {
            ("connectivity.letsencrypt", "acme-v02.api.letsencrypt.org", "ACME certificates (Let's Encrypt)", acmeNeeded),
            ("connectivity.github", "api.github.com", "Caddy update checks and downloads (GitHub)", false),
            ("connectivity.caddyserver", "caddyserver.com", "plugin catalog and custom builds", false),
        };
        var tasks = targets.Select(async t =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(6));
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(t.Host, 443, cts.Token);
                return new ReadinessCheck
                {
                    Id = t.Id, Category = "Connectivity", Title = $"Outbound HTTPS to {t.Host}", Status = CheckStatus.Pass,
                    Summary = $"Connected to {t.Host}:443 in {sw.ElapsedMilliseconds} ms ({t.Purpose}).",
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                var reason = ex is OperationCanceledException ? "timed out after 6s" : ex.Message;
                return new ReadinessCheck
                {
                    Id = t.Id, Category = "Connectivity", Title = $"Outbound HTTPS to {t.Host}",
                    Status = t.Required ? CheckStatus.Fail : CheckStatus.Warn,
                    Summary = $"Cannot connect to {t.Host}:443 ({reason}). Needed for {t.Purpose}.",
                    Details = proxy is { Length: > 0 }
                        ? $"An outbound proxy is configured for downloads ({OutboundHttp.RedactProxy(proxy)}); direct connections may be blocked by design. Caddy itself needs direct access for ACME unless HTTPS_PROXY is set in the Caddy service environment."
                        : null,
                    Remediation = $"Allow outbound TCP 443 from this server to {t.Host} (perimeter firewall / proxy), and check DNS resolution.",
                    Script = $"Test-NetConnection {t.Host} -Port 443\nResolve-DnsName {t.Host}",
                };
            }
        });
        return (await Task.WhenAll(tasks)).ToList();
    }

    private static ReadinessCheck WinHttpProxyCheck(SystemFacts? sys)
    {
        var text = sys?.WinHttpProxy;
        if (string.IsNullOrWhiteSpace(text))
            return Info("connectivity.proxy", "Connectivity", "WinHTTP proxy", "Could not read the WinHTTP proxy configuration.");
        var direct = text.Contains("Direct access", StringComparison.OrdinalIgnoreCase) || text.Contains("no proxy", StringComparison.OrdinalIgnoreCase);
        return new ReadinessCheck
        {
            Id = "connectivity.proxy", Category = "Connectivity", Title = "WinHTTP proxy",
            Status = direct ? CheckStatus.Pass : CheckStatus.Info,
            Summary = direct ? "No WinHTTP proxy configured (direct access)." : "A WinHTTP proxy is configured. Caddy does not use it; set HTTPS_PROXY in the Caddy service environment if ACME must go through a proxy, and configure the outbound proxy for updates in Settings.",
            Details = text,
        };
    }

    // ------------------------------------------------------------------ DNS

    private async Task<List<ReadinessCheck>> DnsChecksAsync(Context ctx, CancellationToken ct)
    {
        if (ctx.AcmeDomains.Count == 0)
            return [Info("dns.none", "DNS", "DNS for ACME hosts", "No enabled hosts use ACME certificates; nothing to check.")];

        var local = LocalAddresses().ToHashSet();
        var publicIp = await PublicIpAsync(ct);
        using var gate = new SemaphoreSlim(8);
        var tasks = ctx.AcmeDomains.Select(async domain =>
        {
            await gate.WaitAsync(ct);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(8));
                var addresses = await Dns.GetHostAddressesAsync(domain, cts.Token);
                var list = string.Join(", ", addresses.Select(a => a.ToString()));
                var matches = addresses.Any(a => local.Contains(a) || (publicIp is not null && a.Equals(publicIp)));
                return new ReadinessCheck
                {
                    Id = $"dns.{domain}", Category = "DNS", Title = $"DNS {domain}",
                    Status = addresses.Length == 0 ? CheckStatus.Fail : matches ? CheckStatus.Pass : CheckStatus.Warn,
                    Summary = addresses.Length == 0 ? $"{domain} has no A/AAAA records."
                        : matches ? $"{domain} resolves to {list} (this server)."
                        : $"{domain} resolves to {list}, which is not an address of this server (local: {string.Join(", ", local.Take(6))}{(publicIp is null ? "" : $"; public: {publicIp}")}).",
                    Details = matches || addresses.Length == 0 ? null
                        : "Fine when a load balancer / NAT forwards ports 80 and 443 from that address to this server; otherwise ACME validation will fail.",
                    Remediation = matches ? null : $"Point the A/AAAA record of {domain} at this server's public address (or the NAT/load balancer that forwards 80/443 to it).",
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return new ReadinessCheck
                {
                    Id = $"dns.{domain}", Category = "DNS", Title = $"DNS {domain}", Status = CheckStatus.Fail,
                    Summary = $"{domain} does not resolve ({(ex is OperationCanceledException ? "timeout" : ex.Message)}). ACME certificates cannot be issued.",
                    Remediation = $"Create an A/AAAA record for {domain} pointing at this server's public address.",
                    Script = $"Resolve-DnsName {domain} -Type A\nResolve-DnsName {domain} -Server 1.1.1.1",
                };
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        return (await Task.WhenAll(tasks)).ToList();
    }

    private async Task<IPAddress?> PublicIpAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var text = await http.Client.GetStringAsync(PublicIpUrl, cts.Token);
            return IPAddress.TryParse(text.Trim(), out var ip) ? ip : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogDebug("Public IP lookup failed: {Error}", ex.Message);
            return null;
        }
    }

    private static IEnumerable<IPAddress> LocalAddresses()
    {
        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { yield break; }
        foreach (var nic in nics)
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var u in nic.GetIPProperties().UnicastAddresses)
                if (!IPAddress.IsLoopback(u.Address) && !u.Address.IsIPv6LinkLocal) yield return u.Address;
        }
    }

    private static async Task<string?> FqdnAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var entry = await Dns.GetHostEntryAsync(Dns.GetHostName(), cts.Token);
            return entry.HostName;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ Caddy

    private async Task<List<ReadinessCheck>> CaddyChecksAsync(Context ctx, CaddyStatus status, CancellationToken ct)
    {
        var list = new List<ReadinessCheck>
        {
            new()
            {
                Id = "caddy.binary", Category = "Caddy", Title = "Caddy binary",
                Status = status.BinaryInstalled ? CheckStatus.Pass : CheckStatus.Fail,
                Summary = status.BinaryInstalled ? $"Installed: {status.Version ?? "unknown version"} at {status.BinaryPath}." : $"Caddy is not installed ({status.BinaryPath}).",
                Remediation = status.BinaryInstalled ? null : "Install Caddy (click Fix, or Caddy page → Install). Without Internet access, copy caddy.exe to the path above.",
                Fixable = !status.BinaryInstalled,
            },
        };

        if (host.HostMode == WindowsServiceCaddyHost.Mode && OperatingSystem.IsWindows())
            list.Add(await CaddyServiceCheckAsync(status, ct));
        else
            list.Add(Info("caddy.service", "Caddy", "Caddy service", "Process host mode (development): Caddy runs as a child process of the manager."));

        var running = status.State == CaddyRunState.Running;
        list.Add(new ReadinessCheck
        {
            Id = "caddy.running", Category = "Caddy", Title = "Caddy running",
            Status = running ? CheckStatus.Pass : CheckStatus.Fail,
            Summary = running
                ? $"Caddy is running{(status.ProcessId is int pid ? $" (PID {pid})" : "")}{(status.StartedAt is { } s ? $" since {s:yyyy-MM-dd HH:mm} UTC" : "")}."
                : $"Caddy is {status.State}.",
            Details = status.LastError,
            Remediation = running ? null : "Start Caddy (click Fix). If it stops again, check the Caddy log (Logs → Caddy).",
            Fixable = !running && status.BinaryInstalled,
        });

        var (loopback, listenDesc) = AdminListenIsLoopback(ctx.Caddy.AdminListen);
        CheckStatus adminStatus;
        string adminSummary;
        if (!loopback)
        {
            adminStatus = CheckStatus.Fail;
            adminSummary = $"The admin API listens on {listenDesc}. Anyone who can reach it can reconfigure Caddy.";
        }
        else if (!running)
        {
            adminStatus = CheckStatus.Skipped;
            adminSummary = $"Admin API configured on {listenDesc} (loopback); Caddy is not running.";
        }
        else if (status.AdminReachable)
        {
            adminStatus = CheckStatus.Pass;
            adminSummary = $"Admin API reachable on {listenDesc} (loopback only).";
        }
        else
        {
            adminStatus = CheckStatus.Warn;
            adminSummary = $"Caddy runs but its admin API on {listenDesc} does not answer; configuration changes cannot be applied live.";
        }
        list.Add(new ReadinessCheck
        {
            Id = "caddy.admin", Category = "Caddy", Title = "Caddy admin API", Status = adminStatus, Summary = adminSummary,
            Remediation = !loopback ? "Set the admin listen address to 127.0.0.1:2019 in Settings → Caddy." :
                adminStatus == CheckStatus.Warn ? "Check that nothing else uses the admin port and restart Caddy." : null,
        });
        return list;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private async Task<ReadinessCheck> CaddyServiceCheckAsync(CaddyStatus status, CancellationToken ct)
    {
        const string id = "caddy.service", title = "Caddy Windows service";
        var reg = WindowsServiceManager.ReadRegistry(AppPaths.CaddyServiceName);
        if (reg is null || !status.ServiceInstalled)
            return new ReadinessCheck
            {
                Id = id, Category = "Caddy", Title = title, Status = CheckStatus.Fail,
                Summary = "The Windows service 'Caddy' is not registered.",
                Remediation = status.BinaryInstalled ? "Register it (click Fix, or Caddy page → Install service)." : "Install Caddy first.",
                Fixable = status.BinaryInstalled,
            };
        var problems = new List<string>();
        var expected = WindowsServiceCaddyHost.Definition(paths);
        if (reg.Start != 2) problems.Add($"start type is {reg.StartTypeDisplay} (Automatic expected)");
        if (!reg.IsLocalSystem) problems.Add($"runs as '{reg.ObjectName}' (LocalSystem expected)");
        if (!string.Equals(reg.ImagePath?.Trim(), expected.BinaryPathName, StringComparison.OrdinalIgnoreCase))
            problems.Add($"binary path is '{reg.ImagePath}' (expected '{expected.BinaryPathName}')");
        if (expected.Environment is { } env && !env.All(e => reg.Environment.Contains(e, StringComparer.OrdinalIgnoreCase)))
            problems.Add("XDG_DATA_HOME/XDG_CONFIG_HOME environment is missing");
        try
        {
            var failure = await WindowsServiceManager.QueryFailureAsync(AppPaths.CaddyServiceName, ct);
            if (failure is null || failure.RestartActions == 0) problems.Add("no restart-on-failure recovery actions");
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            problems.Add("recovery settings could not be read: " + ex.Message);
        }
        return new ReadinessCheck
        {
            Id = id, Category = "Caddy", Title = title,
            Status = problems.Count == 0 ? CheckStatus.Pass : CheckStatus.Warn,
            Summary = problems.Count == 0
                ? $"Registered: {reg.StartTypeDisplay}, LocalSystem, restarts on failure."
                : "The Caddy service configuration needs repair: " + string.Join("; ", problems) + ".",
            Remediation = problems.Count == 0 ? null : "Click Fix to re-apply the service configuration (binary path, Automatic start, recovery actions, environment).",
            Script = problems.Count == 0 ? null : "sc.exe qc Caddy\nsc.exe qfailure Caddy",
            Fixable = problems.Count > 0,
        };
    }

    /// <summary>Checks that the admin listen address is loopback or a unix socket.</summary>
    public static (bool Loopback, string Display) AdminListenIsLoopback(string? listen)
    {
        var l = (listen ?? "").Trim();
        if (l.Length == 0) return (true, "localhost:2019 (Caddy default)");
        if (l.StartsWith("unix/", StringComparison.OrdinalIgnoreCase)) return (true, l);
        var addr = l.StartsWith("tcp/", StringComparison.OrdinalIgnoreCase) ? l[4..] : l;
        var colon = addr.LastIndexOf(':');
        var hostPart = (colon >= 0 ? addr[..colon] : addr).Trim('[', ']');
        if (hostPart.Length == 0) return (false, l + " (all interfaces)");
        if (hostPart.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return (true, l);
        return IPAddress.TryParse(hostPart, out var ip) && IPAddress.IsLoopback(ip) ? (true, l) : (false, l);
    }

    // ------------------------------------------------------------------ Fixes

    public async Task<string> FixAsync(string checkId, CancellationToken ct = default)
    {
        checkId = (checkId ?? "").Trim();
        var ctx = BuildContext();

        if (checkId.StartsWith("firewall.", StringComparison.Ordinal) && ctx.Rules.FirstOrDefault(r => r.CheckId == checkId) is { } rule)
        {
            RequireWindows();
            var fw = LastReport?.Checks.FirstOrDefault(c => c.Id == checkId);
            if (fw is { Fixable: false, Status: not CheckStatus.Pass } && fw.Remediation is not null)
                throw new InvalidOperationException(fw.Remediation);
            await powershell.RunJsonAsync(ReadinessScripts.CreateFirewallRule(rule), ct: ct);
            return $"Created the firewall rule '{rule.DisplayName}' (inbound {rule.Protocol} {rule.Port}, group '{ReadinessScripts.FirewallGroup}').";
        }

        var netMatch = Regex.Match(checkId, @"^network\.profile\.(\d+)$");
        if (netMatch.Success)
        {
            RequireWindows();
            var index = int.Parse(netMatch.Groups[1].Value);
            var report = LastReport;
            if (report?.Machine.DomainJoined == true)
                throw new InvalidOperationException("This server is domain joined; its network category is decided by Network Location Awareness and cannot be changed to Private. See the remediation of the check.");
            var result = await powershell.RunJsonAsync(ReadinessScripts.SetNetworkPrivate(index), ct: ct);
            var alias = result.TryGetProperty("interfaceAlias", out var a) ? a.GetString() : index.ToString();
            return $"The network '{alias}' is now Private.";
        }

        switch (checkId)
        {
            case "caddy.binary":
            {
                var bin = services.GetRequiredService<ICaddyBinaryManager>();
                var job = bin.StartInstallOrUpdate();
                return $"Started installing Caddy (job {job.Id}). Follow the progress on the Caddy page.";
            }
            case "caddy.service":
                if (host.HostMode != WindowsServiceCaddyHost.Mode)
                    throw new InvalidOperationException("Caddy runs in process mode here; there is no Windows service to repair.");
                await host.InstallServiceAsync(ct);
                return "The Caddy service is registered with Automatic start, LocalSystem and restart-on-failure recovery.";
            case "caddy.running":
                await host.StartAsync(ct);
                return "Caddy started.";
        }

        if (LastReport?.Checks.Any(c => c.Id == checkId) == true || KnownPrefixes.Any(p => checkId.StartsWith(p, StringComparison.Ordinal)))
            throw new InvalidOperationException($"The check '{checkId}' has no automatic fix. Follow its remediation steps.");
        throw new KeyNotFoundException($"Unknown readiness check '{checkId}'.");
    }

    private static readonly string[] KnownPrefixes = ["system.", "firewall.", "network.", "domain.", "ports.", "connectivity.", "dns.", "caddy."];

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("This fix is only available on Windows.");
    }

    // ------------------------------------------------------------------ GPO script

    public string BuildGpoScript() => BuildGpoScript(null);

    private string BuildGpoScript(SystemFacts? sys)
    {
        var ctx = BuildContext();
        var machine = LastReport?.Machine;
        string? domain = sys is not null ? (sys.Domain.PartOfDomain ? sys.Domain.Domain : null)
            : machine is { DomainJoined: true } ? machine.Domain : null;
        if (domain is null && sys is null && machine is null)
        {
            try
            {
                var d = IPGlobalProperties.GetIPGlobalProperties().DomainName;
                if (!string.IsNullOrWhiteSpace(d)) domain = d;
            }
            catch (NetworkInformationException) { }
        }
        var dn = sys?.Domain.ComputerDn ?? machine?.ComputerDn;
        return GpoScriptBuilder.Build(new GpoScriptInput
        {
            Domain = domain,
            ComputerName = Environment.MachineName,
            ComputerDn = dn,
            Rules = ctx.Rules,
            InternalCaUsed = ctx.EnabledHosts.Any(h => h.Tls == TlsMode.Internal),
            InternalRootPath = InternalRootPath,
            ProductVersion = typeof(ReadinessService).Assembly.GetName().Version?.ToString(3) ?? "",
        });
    }

    // ------------------------------------------------------------------ helpers

    private static ReadinessCheck Info(string id, string category, string title, string summary) => new()
    {
        Id = id, Category = category, Title = title, Status = CheckStatus.Info, Summary = summary,
    };

    private static ReadinessCheck Skipped(string id, string category, string title, string summary) => new()
    {
        Id = id, Category = category, Title = title, Status = CheckStatus.Skipped, Summary = summary,
    };
}
