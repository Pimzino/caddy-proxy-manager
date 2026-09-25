using System.Collections.Concurrent;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;

namespace CaddyManager.Ops.Tests;

public sealed class FakeCaddyHost : ICaddyHost
{
    public CaddyStatus Status { get; set; } = new()
    {
        BinaryInstalled = true, ServiceInstalled = true, State = CaddyRunState.Running, AdminReachable = true,
        Version = "v2.11.4", HostMode = "process",
    };
    public Exception? Throw { get; set; }
    public TimeSpan Delay { get; set; }
    public int StartCalls;
    /// <summary>When set, StartAsync switches the status to running.</summary>
    public bool StartMakesHealthy { get; set; }

    public string HostMode => "process";

    public async Task<CaddyStatus> GetStatusAsync(CancellationToken ct = default)
    {
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, CancellationToken.None);
        if (Throw is not null) throw Throw;
        return Status;
    }

    public Task InstallServiceAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task UninstallServiceAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task StartAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref StartCalls);
        if (StartMakesHealthy) Status = Status with { State = CaddyRunState.Running, AdminReachable = true };
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task RestartAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class FakeBinaryManager : ICaddyBinaryManager
{
    public Exception? Throw { get; set; }
    public bool Hang { get; set; }

    public Task<InstalledBinary?> GetInstalledAsync(CancellationToken ct = default) =>
        Task.FromResult<InstalledBinary?>(new InstalledBinary { Version = "v2.11.4" });

    public Task<ReleaseInfo?> GetLatestAsync(bool force = false, CancellationToken ct = default) =>
        Task.FromResult<ReleaseInfo?>(new ReleaseInfo { Version = "v2.11.4" });

    public async Task<BinaryOverview> GetOverviewAsync(CancellationToken ct = default)
    {
        if (Hang) await Task.Delay(Timeout.Infinite, CancellationToken.None);
        if (Throw is not null) throw Throw;
        return new BinaryOverview { Installed = new InstalledBinary { Version = "v2.11.4" }, Platform = "windows/amd64" };
    }

    public JobInfo StartInstallOrUpdate(string? version = null) => new() { Id = "job", Kind = "caddy-install" };
    public Task<(int ExitCode, string Output)> RunCaddyAsync(IEnumerable<string> args, string? stdin = null, CancellationToken ct = default) =>
        Task.FromResult((0, ""));
    public Task<List<PluginPackage>> GetPluginCatalogAsync(string? query = null, CancellationToken ct = default) =>
        Task.FromResult(new List<PluginPackage>());
}

public sealed class FakeCertificateInventory : ICertificateInventory
{
    public List<CertificateInfo> Certificates { get; set; } = new();
    public Exception? Throw { get; set; }

    public Task<List<CertificateInfo>> ListAsync(CancellationToken ct = default) =>
        Throw is not null ? Task.FromException<List<CertificateInfo>>(Throw) : Task.FromResult(Certificates.ToList());
}

public sealed class FakeReadiness : IReadinessService
{
    public ReadinessReport? LastReport { get; set; }
    public ReadinessReport NextReport { get; set; } = new() { RanAt = DateTime.UtcNow };
    public int Runs;
    public Exception? ThrowOnLastReport { get; set; }

    ReadinessReport? IReadinessService.LastReport => ThrowOnLastReport is not null ? throw ThrowOnLastReport : LastReport;

    public Task<ReadinessReport> RunAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref Runs);
        LastReport = NextReport;
        return Task.FromResult(NextReport);
    }

    public Task<string> FixAsync(string checkId, CancellationToken ct = default) => Task.FromResult("fixed");
    public string BuildGpoScript() => "";
}

public sealed class FakeAdminClient : ICaddyAdminClient
{
    public List<UpstreamHealth> Upstreams { get; set; } = new();
    public bool Reachable { get; set; } = true;
    public Exception? Throw { get; set; }
    public string BaseUrl => "http://127.0.0.1:2019";

    public Task<bool> IsReachableAsync(CancellationToken ct = default) => Task.FromResult(Reachable);
    public Task<string?> GetConfigAsync(CancellationToken ct = default) => Task.FromResult<string?>("{}");
    public Task LoadAsync(string json, CancellationToken ct = default) => Task.CompletedTask;
    public Task<(string Json, List<string> Warnings)> AdaptCaddyfileAsync(string caddyfile, CancellationToken ct = default) =>
        Task.FromResult(("{}", new List<string>()));

    public Task<List<UpstreamHealth>> GetUpstreamsAsync(CancellationToken ct = default) =>
        Throw is not null ? Task.FromException<List<UpstreamHealth>>(Throw) : Task.FromResult(Upstreams.ToList());

    public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class FakeNotifier : INotifier
{
    public ConcurrentQueue<(string Subject, string Body)> Sent { get; } = new();
    public bool Fail { get; set; }
    public int Calls;

    public Task<List<string>> SendAsync(string subject, string body, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Calls);
        if (Fail) return Task.FromResult(new List<string> { "SMTP: connection refused" });
        Sent.Enqueue((subject, body));
        return Task.FromResult(new List<string>());
    }
}

/// <summary>Controllable clock for cooldown tests (timers are not used by the code under test here).</summary>
public sealed class ManualTimeProvider(DateTimeOffset start, TimeZoneInfo? zone = null) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public override TimeZoneInfo LocalTimeZone => zone ?? base.LocalTimeZone;
    public void Advance(TimeSpan by) => _now = _now.Add(by);
}
