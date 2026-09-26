using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using CaddyManager.Telemetry.Resources;
using CaddyManager.Telemetry.Traffic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaddyManager.Telemetry;

/// <summary>IServerTelemetry of THIS server: facts (cached 30 s), the sampler's ring buffer and traffic reports.</summary>
public sealed class ServerTelemetry(
    AppPaths paths,
    IStore store,
    ResourceSampler sampler,
    TrafficIngestion ingestion,
    TrafficStore trafficStore,
    IServiceProvider services,
    IOptions<TelemetryOptions> options,
    TimeProvider time,
    ILogger<ServerTelemetry> logger) : IServerTelemetry
{
    private static readonly DateTime ProcessStart = SafeProcessStart();
    private readonly SemaphoreSlim _infoLock = new(1, 1);
    private ServerInfo? _info;
    private DateTime _infoAt;

    public async Task<ServerInfo> GetInfoAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (_info is { } cached && now - _infoAt < options.Value.InfoCacheDuration) return cached;
        await _infoLock.WaitAsync(ct);
        try
        {
            if (_info is { } again && now - _infoAt < options.Value.InfoCacheDuration) return again;
            _info = await CollectInfoAsync(now, ct);
            _infoAt = now;
            return _info;
        }
        finally { _infoLock.Release(); }
    }

    public IReadOnlyList<ResourceSample> GetSamples(DateTime? since = null) => sampler.GetSamples(since);

    public Task<TrafficReport> GetTrafficAsync(TrafficQuery query, CancellationToken ct = default)
    {
        // Include what was read since the last periodic save (rate limited: viewers poll this).
        ingestion.FlushForReport();
        string? disabled = null;
        try
        {
            var s = store.GetSettings<CaddySettings>();
            // The stats sink is part of the generated (managed) configuration only.
            if (!s.TrafficStatsEnabled) disabled = TrafficReports.DisabledNote;
            else if (s.Mode != ConfigMode.Managed) disabled = TrafficReports.CaddyfileNote;
        }
        catch (Exception ex) { logger.LogWarning(ex, "Could not read Caddy settings for traffic statistics"); }
        var host = string.IsNullOrWhiteSpace(query.Host) ? null : ingestion.ResolveHostFilter(AccessLogParser.NormalizeHost(query.Host));
        var report = new TrafficReports(trafficStore, ingestion.PendingBlobs).Build(query, host, time.GetUtcNow().UtcDateTime, disabled,
            ingestion.LastIngestAt, ingestion.MalformedLines, ingestion.FilesMissed);
        return Task.FromResult(report);
    }

    private async Task<ServerInfo> CollectInfoAsync(DateTime now, CancellationToken ct)
    {
        var hostName = Environment.MachineName;
        string? domain = null, fqdn = null;
        try
        {
            var props = IPGlobalProperties.GetIPGlobalProperties();
            hostName = string.IsNullOrWhiteSpace(props.HostName) ? hostName : props.HostName;
            // On Windows DomainName is the AD/primary DNS domain of the computer (empty when not joined). Elsewhere it
            // is the resolver's search domain, which is not a domain membership.
            if (OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(props.DomainName)) domain = props.DomainName;
            if (!string.IsNullOrWhiteSpace(props.DomainName)) fqdn = $"{hostName}.{props.DomainName}";
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException) { }
        if (fqdn is null)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(2));
                var entry = await Dns.GetHostEntryAsync(hostName, cts.Token);
                if (entry.HostName.Contains('.')) fqdn = entry.HostName;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException) { }
        }

        CaddyStatus? status = null;
        InstalledBinary? binary = null;
        if (services.GetService<ICaddyHost>() is { } host)
        {
            try { status = await WithTimeout(host.GetStatusAsync, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { logger.LogDebug(ex, "Caddy status unavailable"); }
        }
        if (services.GetService<ICaddyBinaryManager>() is { } binaries)
        {
            try { binary = await WithTimeout(binaries.GetInstalledAsync, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { logger.LogDebug(ex, "Installed Caddy binary unavailable"); }
        }

        long totalMemory = SystemMetrics.ReadMemory()?.TotalBytes ?? 0;
        if (totalMemory <= 0) totalMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

        return new ServerInfo
        {
            Hostname = hostName,
            Fqdn = fqdn,
            Os = RuntimeInformation.OSDescription,
            IsWindows = OperatingSystem.IsWindows(),
            Architecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            ManagerVersion = (Assembly.GetEntryAssembly() ?? typeof(ServerTelemetry).Assembly).GetName().Version?.ToString(3) ?? "",
            CaddyVersion = binary?.Version is { Length: > 0 } v ? v : status?.Version,
            CaddyState = status?.State ?? CaddyRunState.Unknown,
            CaddyStartedAt = status?.StartedAt,
            CaddyPlugins = binary?.Plugins.ToList() ?? [],
            ProcessorCount = Environment.ProcessorCount,
            TotalMemoryBytes = totalMemory,
            SystemUptimeSeconds = Environment.TickCount64 / 1000,
            ManagerUptimeSeconds = ProcessStart == default ? 0 : (long)Math.Max(0, (DateTime.UtcNow - ProcessStart).TotalSeconds),
            DataDir = paths.DataDir,
            IpAddresses = IpAddresses(),
            Domain = domain,
            CollectedAt = now,
        };
    }

    private static async Task<T> WithTimeout<T>(Func<CancellationToken, Task<T>> call, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        return await call(cts.Token);
    }

    /// <summary>Unicast addresses of up, non-loopback interfaces (IPv6 link-local omitted), IPv4 first.</summary>
    private static List<string> IpAddresses()
    {
        var result = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var a in nic.GetIPProperties().UnicastAddresses)
                {
                    var ip = a.Address;
                    if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal) continue;
                    if (!result.Contains(ip)) result.Add(ip);
                }
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException) { }
        return result.OrderBy(ip => ip.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).Select(ip => ip.ToString()).ToList();
    }

    private static DateTime SafeProcessStart()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            return p.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return default;
        }
    }
}
