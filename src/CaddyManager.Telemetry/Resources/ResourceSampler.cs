using System.Diagnostics;
using System.Net.NetworkInformation;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using CaddyManager.Telemetry.Traffic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaddyManager.Telemetry.Resources;

/// <summary>
/// Takes a <see cref="ResourceSample"/> every <see cref="TelemetryOptions.SampleInterval"/> (SPEC: 2 s) into a ring buffer
/// covering <see cref="TelemetryOptions.SampleWindow"/> (10 minutes). Rates (CPU %, network bytes/s, requests/s) are deltas
/// against the previous sample. Every probe is isolated: a failure yields 0/null for that value, never an exception.
/// </summary>
public sealed class ResourceSampler : BackgroundService
{
    private readonly AppPaths _paths;
    private readonly IStore _store;
    private readonly TrafficIngestion _ingestion;
    private readonly IServiceProvider _services;
    private readonly TelemetryOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ResourceSampler> _logger;
    private readonly object _ringLock = new();
    private readonly ResourceSample[] _ring;
    private int _ringStart, _ringCount;

    // Previous readings for rate calculations (touched only by the sampling thread).
    private DateTime _prevAt;
    private CpuTimes? _prevCpu;
    private Dictionary<string, (long Rx, long Tx)>? _prevNet;
    private TimeSpan? _prevManagerCpu;
    private (int Pid, TimeSpan Cpu)? _prevCaddyCpu;
    private CaddyStatus? _caddyStatus;
    private DateTime _caddyStatusAt = DateTime.MinValue;
    private (int Http, int Https)? _ports;
    private DateTime _portsAt = DateTime.MinValue;
    private List<(string Name, string Label)>? _disks;
    private DateTime _disksAt = DateTime.MinValue;
    private int _failuresLogged;

    public ResourceSampler(AppPaths paths, IStore store, TrafficIngestion ingestion, IServiceProvider services,
        IOptions<TelemetryOptions> options, TimeProvider time, ILogger<ResourceSampler> logger)
    {
        _paths = paths;
        _store = store;
        _ingestion = ingestion;
        _services = services;
        _options = options.Value;
        _time = time;
        _logger = logger;
        var capacity = (int)Math.Max(1, Math.Ceiling(_options.SampleWindow / _options.SampleInterval));
        _ring = new ResourceSample[capacity];
    }

    /// <summary>Samples in the ring, oldest first; only those taken after <paramref name="since"/> when given.</summary>
    public IReadOnlyList<ResourceSample> GetSamples(DateTime? since = null)
    {
        var sinceUtc = since is { } s ? (s.Kind == DateTimeKind.Local ? s.ToUniversalTime() : DateTime.SpecifyKind(s, DateTimeKind.Utc)) : (DateTime?)null;
        lock (_ringLock)
        {
            var list = new List<ResourceSample>(_ringCount);
            for (var i = 0; i < _ringCount; i++)
            {
                var sample = _ring[(_ringStart + i) % _ring.Length];
                if (sinceUtc is null || sample.At > sinceUtc) list.Add(sample);
            }
            return list;
        }
    }

    /// <summary>The newest sample, if any.</summary>
    public ResourceSample? Latest
    {
        get
        {
            lock (_ringLock) return _ringCount == 0 ? null : _ring[(_ringStart + _ringCount - 1) % _ring.Length];
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Prime the counters so the first stored sample already has rates.
        await SampleAsync(store: false, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.SampleInterval, _time, stoppingToken);
                await SampleAsync(store: true, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (_failuresLogged++ < 5) _logger.LogWarning(ex, "Resource sample failed");
            }
        }
    }

    /// <summary>Takes one sample (internal for tests). The first call only primes the rate counters when store is false.</summary>
    internal async Task<ResourceSample> SampleAsync(bool store, CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var elapsed = _prevAt == default ? 0 : (now - _prevAt).TotalSeconds;

        var cpu = SystemMetrics.ReadCpuTimes();
        var cpuPercent = SystemMetrics.Percent(_prevCpu, cpu);
        _prevCpu = cpu ?? _prevCpu;

        var memory = SystemMetrics.ReadMemory();
        var (rxRate, txRate) = NetworkRates(elapsed);
        var (managerCpu, managerMemory) = ManagerProcess(elapsed);
        var (caddyCpu, caddyMemory) = await CaddyProcessAsync(now, elapsed, ct);
        var requestsPerSecond = RequestsPerSecond(now);

        var sample = new ResourceSample
        {
            At = now,
            CpuPercent = Math.Round(cpuPercent, 2),
            MemoryUsedBytes = memory?.UsedBytes ?? 0,
            MemoryTotalBytes = memory?.TotalBytes ?? 0,
            Disks = Disks(now),
            NetworkRxBytesPerSec = rxRate,
            NetworkTxBytesPerSec = txRate,
            CaddyCpuPercent = caddyCpu,
            CaddyMemoryBytes = caddyMemory,
            ManagerCpuPercent = managerCpu,
            ManagerMemoryBytes = managerMemory,
            ActiveConnections = ActiveConnections(now),
            RequestsPerSecond = requestsPerSecond,
        };
        _prevAt = now;
        if (store)
        {
            lock (_ringLock)
            {
                if (_ringCount < _ring.Length) _ring[(_ringStart + _ringCount++) % _ring.Length] = sample;
                else
                {
                    _ring[_ringStart] = sample;
                    _ringStart = (_ringStart + 1) % _ring.Length;
                }
            }
        }
        return sample;
    }

    // ------------------------------------------------------------------ network

    /// <summary>
    /// Bytes per second over up, non-loopback interfaces. GetIPStatistics is supported on Windows in .NET 10
    /// (only Android is excluded: https://learn.microsoft.com/dotnet/api/system.net.networkinformation.networkinterface.getipstatistics);
    /// GetIPv4Statistics is the fallback should a platform throw PlatformNotSupportedException.
    /// </summary>
    private (double Rx, double Tx) NetworkRates(double elapsed)
    {
        var current = new Dictionary<string, (long Rx, long Tx)>(StringComparer.Ordinal);
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                try
                {
                    IPInterfaceStatistics s;
                    try { s = nic.GetIPStatistics(); }
                    catch (PlatformNotSupportedException)
                    {
                        var v4 = nic.GetIPv4Statistics();
                        current[nic.Id] = (v4.BytesReceived, v4.BytesSent);
                        continue;
                    }
                    current[nic.Id] = (s.BytesReceived, s.BytesSent);
                }
                catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException) { }
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException) { return (0, 0); }

        var prev = _prevNet;
        _prevNet = current;
        return NetworkDelta(prev, current, elapsed);
    }

    /// <summary>
    /// Rate from two readings of cumulative per-interface counters (keyed by NetworkInterface.Id). Only interfaces present
    /// in both readings contribute: an interface that comes up (link regained, VPN connected, vEthernet re-created) brings
    /// its whole since-boot count, which is not traffic of this interval; one whose counters went backwards (reset) adds 0.
    /// </summary>
    internal static (double Rx, double Tx) NetworkDelta(IReadOnlyDictionary<string, (long Rx, long Tx)>? previous,
        IReadOnlyDictionary<string, (long Rx, long Tx)> current, double elapsed)
    {
        if (previous is null || elapsed <= 0) return (0, 0);
        long rx = 0, tx = 0;
        foreach (var (id, now) in current)
        {
            if (!previous.TryGetValue(id, out var before)) continue;
            rx += Math.Max(0, now.Rx - before.Rx);
            tx += Math.Max(0, now.Tx - before.Tx);
        }
        return (Math.Round(rx / elapsed, 1), Math.Round(tx / elapsed, 1));
    }

    // ------------------------------------------------------------------ processes

    /// <summary>CPU% of a process = Δ TotalProcessorTime / (Δ wall time × processor count), i.e. share of the whole machine.</summary>
    private static double ProcessCpuPercent(TimeSpan previous, TimeSpan current, double elapsed)
    {
        if (elapsed <= 0 || current < previous) return 0;
        return Math.Round(Math.Clamp(100.0 * (current - previous).TotalSeconds / (elapsed * Environment.ProcessorCount), 0, 100), 2);
    }

    private (double Cpu, long Memory) ManagerProcess(double elapsed)
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            var cpuTime = p.TotalProcessorTime;
            var cpu = _prevManagerCpu is { } prev ? ProcessCpuPercent(prev, cpuTime, elapsed) : 0;
            _prevManagerCpu = cpuTime;
            return (cpu, p.WorkingSet64);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return (0, 0);
        }
    }

    private async Task<(double? Cpu, long? Memory)> CaddyProcessAsync(DateTime now, double elapsed, CancellationToken ct)
    {
        var status = await CaddyStatusAsync(now, ct);
        if (status is not { State: CaddyRunState.Running, ProcessId: { } pid })
        {
            _prevCaddyCpu = null;
            return (null, null);
        }
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited)
            {
                _prevCaddyCpu = null;
                return (null, null);
            }
            var cpuTime = p.TotalProcessorTime;
            var cpu = _prevCaddyCpu is { } prev && prev.Pid == pid ? ProcessCpuPercent(prev.Cpu, cpuTime, elapsed) : 0;
            _prevCaddyCpu = (pid, cpuTime);
            return (cpu, p.WorkingSet64);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Not running any more (ArgumentException) or not accessible.
            _prevCaddyCpu = null;
            return (null, null);
        }
    }

    /// <summary>ICaddyHost status (for the PID), cached; null when the Platform module is not registered or the call fails.</summary>
    private async Task<CaddyStatus?> CaddyStatusAsync(DateTime now, CancellationToken ct)
    {
        if (now - _caddyStatusAt < _options.CaddyStatusCacheDuration) return _caddyStatus;
        _caddyStatusAt = now;
        var host = _services.GetService<ICaddyHost>();
        if (host is null) return _caddyStatus = null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            _caddyStatus = await host.GetStatusAsync(cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _caddyStatus = null;
        }
        return _caddyStatus;
    }

    // ------------------------------------------------------------------ connections, requests

    /// <summary>Established TCP connections whose local port is Caddy's HTTP or HTTPS port (server side of each connection).</summary>
    private int? ActiveConnections(DateTime now)
    {
        try
        {
            if (_ports is null || now - _portsAt > TimeSpan.FromSeconds(30))
            {
                var s = _store.GetSettings<CaddySettings>();
                _ports = (s.HttpPort, s.HttpsPort);
                _portsAt = now;
            }
            var (http, https) = _ports.Value;
            var count = 0;
            foreach (var c in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections())
            {
                if (c.State != TcpState.Established) continue;
                var port = c.LocalEndPoint.Port;
                if (port == http || port == https) count++;
            }
            return count;
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Requests per second by log timestamp over the sample interval. The window ends IngestInterval + 1 s in the past: Caddy writes
    /// each entry when the request completes and the ingester reads the log every IngestInterval, so the newest seconds
    /// are not complete yet.
    /// </summary>
    private double RequestsPerSecond(DateTime now)
    {
        // The histogram has one-second resolution: average over whole seconds, at least one, covering the interval.
        var endSecond = (now - _options.IngestInterval - TimeSpan.FromSeconds(1)).Ticks / TimeSpan.TicksPerSecond;
        var seconds = Math.Max(1, (long)Math.Ceiling(_options.SampleInterval.TotalSeconds));
        var to = new DateTime(endSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        return Math.Round(_ingestion.RequestsBetween(to.AddSeconds(-seconds), to) / (double)seconds, 2);
    }

    // ------------------------------------------------------------------ disks

    private List<DiskUsage> Disks(DateTime now)
    {
        try
        {
            if (_disks is null || now - _disksAt > TimeSpan.FromMinutes(1))
            {
                _disks = DiskVolumes(_paths.DataDir);
                _disksAt = now;
            }
            var result = new List<DiskUsage>();
            foreach (var (name, label) in _disks)
            {
                try
                {
                    var d = new DriveInfo(name);
                    if (!d.IsReady) continue;
                    result.Add(new DiskUsage { Name = name, Label = label, TotalBytes = d.TotalSize, FreeBytes = d.AvailableFreeSpace });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The volume holding the data directory ("Data (…)") and the system volume ("System"), deduplicated.</summary>
    internal static List<(string Name, string Label)> DiskVolumes(string dataDir)
    {
        var drives = DriveInfo.GetDrives().Where(SafeIsReady).Select(d => d.Name).ToList();
        var dataRoot = VolumeOf(drives, dataDir);
        var systemPath = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.Windows) is { Length: > 0 } w ? w : "C:\\"
            : "/";
        var systemRoot = VolumeOf(drives, systemPath);
        var list = new List<(string, string)>();
        var dataLabel = $"Data ({dataDir})";
        if (dataRoot is not null && dataRoot == systemRoot) list.Add((dataRoot, "System, " + dataLabel));
        else
        {
            if (dataRoot is not null) list.Add((dataRoot, dataLabel));
            if (systemRoot is not null) list.Add((systemRoot, "System"));
        }
        return list;

        static bool SafeIsReady(DriveInfo d)
        {
            try { return d.IsReady; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    /// <summary>Longest mount point / drive root that contains the path.</summary>
    private static string? VolumeOf(List<string> roots, string path)
    {
        var full = Path.GetFullPath(path);
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string? best = null;
        foreach (var r in roots)
        {
            var root = r.EndsWith(Path.DirectorySeparatorChar) ? r : r + Path.DirectorySeparatorChar;
            var matches = full.StartsWith(root, cmp) || string.Equals(full + Path.DirectorySeparatorChar, root, cmp);
            if (matches && (best is null || r.Length > best.Length)) best = r;
        }
        return best;
    }
}
