using System.Threading.Channels;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Config.Certificates;

/// <summary>
/// Keeps certificates from external sources current:
/// <list type="bullet">
/// <item>FilePath (PEM files renewed by other tooling, possibly on a share): metadata refreshed and, when an enabled host
/// uses the certificate, the config re-applied so Caddy reloads the files.</item>
/// <item>PfxFile: the .pfx/.p12 is re-converted to PEM in the store when it changes.</item>
/// <item>WindowsStore: re-selected and re-exported every 15 minutes (follows AD CS autoenrollment renewals); the config
/// is re-applied only when a different certificate is now in use.</item>
/// </list>
/// FileSystemWatcher events are debounced; a 5-minute poll covers shares and missed events.
/// </summary>
public sealed class CertificateWatcher(
    IStore store,
    ICaddyConfigService config,
    CertificateSyncService sync,
    ILogger<CertificateWatcher> logger,
    Endpoints.ConfigMutationGate? gate = null) : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan WindowsStoreInterval = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(5);

    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, Fingerprint> _snapshot = new();
    private DateTime _lastWindowsSync = DateTime.MinValue;

    private sealed record Fingerprint(FileState A, FileState B);
    private sealed record FileState(bool Exists, DateTime LastWriteUtc, long Length);

    /// <summary>Ask the watcher to re-scan now (e.g. after a file-path certificate was added).</summary>
    public void Poke() => _signal.Writer.TryWrite(true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // No initial snapshot: the first scan compares file times with the last sync, so renewals that happened
            // while the service was stopped are picked up too.
            RefreshWatchers();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Certificate watcher initialisation failed");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                cts.CancelAfter(PollInterval);
                try
                {
                    await _signal.Reader.ReadAsync(cts.Token);
                    // Let writers finish (certbot/win-acme write several files in sequence).
                    await Task.Delay(Debounce, stoppingToken);
                    while (_signal.Reader.TryRead(out _)) { }
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // poll interval elapsed
                }
                await CheckAsync(stoppingToken);
                if (DateTime.UtcNow - _lastWindowsSync >= WindowsStoreInterval)
                    await SyncWindowsStoreAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Certificate watcher iteration failed");
            }
        }

        foreach (var w in _watchers.Values) w.Dispose();
        _watchers.Clear();
    }

    /// <summary>Certificates backed by files the watcher monitors (FilePath: cert + key; PfxFile: the PFX).</summary>
    private List<Certificate> WatchedCertificates() =>
        store.Col<Certificate>().FindAll().Where(c => c.Source is CertificateSource.FilePath or CertificateSource.PfxFile).ToList();

    private static IEnumerable<string> WatchedFiles(Certificate c) => c.Source == CertificateSource.PfxFile
        ? [c.SourcePath ?? ""]
        : [c.CertPath, c.KeyPath];

    /// <summary>One scan: detect changed files, re-read/re-convert, re-apply when an enabled host uses a changed certificate.</summary>
    internal async Task<bool> CheckAsync(CancellationToken ct)
    {
        var certs = WatchedCertificates();
        var current = TakeSnapshot(certs);
        var changed = certs.Where(c => _snapshot.TryGetValue(c.Id, out var before)
            ? before != current[c.Id]
            : ModifiedSinceLastSync(c, current[c.Id])).ToList();
        _snapshot = current;
        RefreshWatchers();
        if (changed.Count == 0) return false;

        var reload = new List<Certificate>();
        foreach (var c in changed)
        {
            var r = await sync.SyncAsync(c.Id, ct);
            if (r is null) continue;
            if (r.Success) logger.LogInformation("Certificate {Name} changed on disk; expires {NotAfter:u}", r.Certificate.Name, r.Certificate.NotAfter);
            // FilePath: Caddy reads the referenced files itself, so any change needs a reload.
            // PfxFile: only when new PEM files were written to the store.
            if (c.Source == CertificateSource.FilePath || r.FilesWritten) reload.Add(r.Certificate);
        }
        return await ReapplyIfUsedAsync(reload, "Certificate files changed on disk", ct);
    }

    /// <summary>Re-syncs every Windows-store certificate; re-applies when one now uses a different certificate.</summary>
    internal async Task<bool> SyncWindowsStoreAsync(CancellationToken ct)
    {
        _lastWindowsSync = DateTime.UtcNow;
        var ids = store.Col<Certificate>().FindAll().Where(c => c.Source == CertificateSource.WindowsStore).Select(c => c.Id).ToList();
        var reload = new List<Certificate>();
        foreach (var id in ids)
        {
            var r = await sync.SyncAsync(id, ct);
            if (r is { Success: true, FilesWritten: true }) reload.Add(r.Certificate);
        }
        return await ReapplyIfUsedAsync(reload, "Certificate renewed in the Windows certificate store", ct);
    }

    private async Task<bool> ReapplyIfUsedAsync(List<Certificate> certs, string reasonPrefix, CancellationToken ct)
    {
        if (certs.Count == 0) return false;
        var ids = certs.Select(c => c.Id).ToHashSet();
        var used = store.Col<SiteHost>().FindAll().Any(h => h.Enabled && h.Tls == TlsMode.Custom && h.CertificateId is not null && ids.Contains(h.CertificateId));
        if (!used) return false;

        var names = string.Join(", ", certs.Select(c => c.Name));
        // Do not interleave with an API transaction (persist → apply → rollback).
        if (gate is not null) await gate.Lock.WaitAsync(ct);
        ApplyResult result;
        try
        {
            result = await config.ApplyAsync($"{reasonPrefix}: {names}", ct);
        }
        finally
        {
            gate?.Lock.Release();
        }
        if (result.Success) logger.LogInformation("Re-applied configuration after certificate change ({Names})", names);
        else logger.LogWarning("Re-applying configuration after certificate change failed: {Error}", result.Error);
        return true;
    }

    /// <summary>A certificate seen for the first time: were its files written after it was last read?</summary>
    private static bool ModifiedSinceLastSync(Certificate c, Fingerprint fp)
    {
        var since = c.LastSyncedAt ?? c.UpdatedAt;
        return (fp.A.Exists && fp.A.LastWriteUtc > since) || (fp.B.Exists && fp.B.LastWriteUtc > since);
    }

    private static Dictionary<string, Fingerprint> TakeSnapshot(IEnumerable<Certificate> certs) =>
        certs.ToDictionary(c => c.Id, c =>
        {
            var files = WatchedFiles(c).ToList();
            return new Fingerprint(State(files[0]), files.Count > 1 ? State(files[1]) : new FileState(false, default, 0));
        });

    private static FileState State(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return new FileState(false, default, 0);
            var fi = new FileInfo(path);
            return fi.Exists ? new FileState(true, fi.LastWriteTimeUtc, fi.Length) : new FileState(false, default, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new FileState(false, default, 0);
        }
    }

    private void RefreshWatchers()
    {
        var dirs = WatchedCertificates()
            .SelectMany(WatchedFiles)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(Path.GetDirectoryName)
            .Where(d => !string.IsNullOrEmpty(d))
            .Select(d => d!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var gone in _watchers.Keys.Where(k => !dirs.Contains(k)).ToList())
        {
            _watchers[gone].Dispose();
            _watchers.Remove(gone);
        }
        foreach (var dir in dirs.Where(d => !_watchers.ContainsKey(d)))
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                var w = new FileSystemWatcher(dir)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
                    IncludeSubdirectories = false,
                };
                w.Changed += OnFsEvent;
                w.Created += OnFsEvent;
                w.Deleted += OnFsEvent;
                w.Renamed += OnFsEvent;
                w.Error += (_, e) => logger.LogDebug(e.GetException(), "FileSystemWatcher error for {Dir}", dir);
                w.EnableRaisingEvents = true;
                _watchers[dir] = w;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                logger.LogInformation("Cannot watch {Dir} ({Error}); relying on the 5-minute poll", dir, ex.Message);
            }
        }
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e) => _signal.Writer.TryWrite(true);

    public override void Dispose()
    {
        foreach (var w in _watchers.Values) w.Dispose();
        _watchers.Clear();
        base.Dispose();
    }
}
