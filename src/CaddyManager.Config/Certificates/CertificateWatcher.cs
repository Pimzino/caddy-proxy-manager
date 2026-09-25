using System.Threading.Channels;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Config.Certificates;

/// <summary>
/// Watches certificates referenced by file path (renewed by other tooling, possibly on a share). When the files
/// change the metadata is refreshed and, if an enabled host uses the certificate, the config is re-applied so
/// Caddy reloads the new files. FileSystemWatcher events are debounced; a 5-minute poll covers shares and missed events.
/// </summary>
public sealed class CertificateWatcher(
    IStore store,
    ICaddyConfigService config,
    ILogger<CertificateWatcher> logger,
    Endpoints.ConfigMutationGate? gate = null) : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(5);

    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, Fingerprint> _snapshot = new();

    private sealed record Fingerprint(FileState Cert, FileState Key);
    private sealed record FileState(bool Exists, DateTime LastWriteUtc, long Length);

    /// <summary>Ask the watcher to re-scan now (e.g. after a file-path certificate was added).</summary>
    public void Poke() => _signal.Writer.TryWrite(true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _snapshot = TakeSnapshot(Certificates());
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

    private List<Certificate> Certificates() =>
        store.Col<Certificate>().FindAll().Where(c => c.Source == CertificateSource.FilePath).ToList();

    /// <summary>One scan: detect changed files, refresh metadata, re-apply when an enabled host uses a changed cert.</summary>
    internal async Task<bool> CheckAsync(CancellationToken ct)
    {
        var certs = Certificates();
        var current = TakeSnapshot(certs);
        var changed = certs.Where(c => _snapshot.TryGetValue(c.Id, out var before) && before != current[c.Id]).ToList();
        _snapshot = current;
        RefreshWatchers();
        if (changed.Count == 0) return false;

        foreach (var c in changed)
        {
            try
            {
                var parsed = CertificateParser.FromFiles(c.CertPath, c.KeyPath);
                c.Subjects = parsed.Metadata.Subjects;
                c.Issuer = parsed.Metadata.Issuer;
                c.NotBefore = parsed.Metadata.NotBefore;
                c.NotAfter = parsed.Metadata.NotAfter;
                c.Thumbprint = parsed.Metadata.Thumbprint;
                c.UpdatedAt = DateTime.UtcNow;
                store.Col<Certificate>().Update(c);
                logger.LogInformation("Certificate {Name} changed on disk; new expiry {NotAfter:u}", c.Name, c.NotAfter);
            }
            catch (CertificateImportException ex)
            {
                logger.LogWarning("Certificate {Name} changed on disk but cannot be used: {Error}", c.Name, ex.Message);
            }
        }

        var ids = changed.Select(c => c.Id).ToHashSet();
        var used = store.Col<SiteHost>().FindAll().Any(h => h.Enabled && h.Tls == TlsMode.Custom && h.CertificateId is not null && ids.Contains(h.CertificateId));
        if (!used) return false;

        var names = string.Join(", ", changed.Select(c => c.Name));
        // Do not interleave with an API transaction (persist → apply → rollback).
        if (gate is not null) await gate.Lock.WaitAsync(ct);
        ApplyResult result;
        try
        {
            result = await config.ApplyAsync($"Certificate files changed on disk: {names}", ct);
        }
        finally
        {
            gate?.Lock.Release();
        }
        if (result.Success) logger.LogInformation("Re-applied configuration after certificate change ({Names})", names);
        else logger.LogWarning("Re-applying configuration after certificate change failed: {Error}", result.Error);
        return true;
    }

    private static Dictionary<string, Fingerprint> TakeSnapshot(IEnumerable<Certificate> certs) =>
        certs.ToDictionary(c => c.Id, c => new Fingerprint(State(c.CertPath), State(c.KeyPath)));

    private static FileState State(string path)
    {
        try
        {
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
        var dirs = Certificates()
            .SelectMany(c => new[] { c.CertPath, c.KeyPath })
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
