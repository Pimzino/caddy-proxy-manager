using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Cluster;

/// <summary>Answer of the `sync` RPC.</summary>
public sealed record SyncResult
{
    /// <summary>Revision applied on the node after this call (the previous one when the new bundle is pending or failed).</summary>
    public string? AppliedRevision { get; init; }
    public ApplyResult Apply { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
    /// <summary>The bundle is stored but waits for a Caddy rebuild with the desired plugins (job <see cref="JobId"/>).</summary>
    public bool Pending { get; init; }
    public string? JobId { get; init; }
}

/// <summary>
/// Node side of replication: replaces the replicated collections with the bundle's content (one LiteDB transaction),
/// writes certificates through ICertificateMaterialStore (same ids, source Uploaded), re-protects secrets with this node's
/// ISecretProtector, keeps this node's NodeLocalProperties, stores the desired plugins and applies the configuration.
/// When Caddy rejects it, the previous data is restored (Caddy itself keeps running the previous config).
/// </summary>
public sealed class NodeSync(
    IStore store,
    ISecretProtector secrets,
    ClusterService cluster,
    IServiceProvider services,
    TimeProvider time,
    ILogger<NodeSync> logger)
{
    private const string RebuildJobKind = "caddy-install";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (string Revision, DateTime At, string Error)? _failedRebuild;

    public async Task<SyncResult> ApplyAsync(JsonObject bundle, string revision, bool force, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await ApplyCoreAsync(bundle, revision, force, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SyncResult> ApplyCoreAsync(JsonObject bundle, string revision, bool force, CancellationToken ct)
    {
        var canonical = ClusterBundle.Canonicalize(bundle);
        if (ClusterBundle.Revision(canonical) != revision)
            return Failed("The configuration bundle is damaged (its revision does not match its content).");

        var state = cluster.Settings;
        var primary = state.PrimaryName ?? "the primary";
        if (!force && state.AppliedRevision == revision && state.PendingRevision is null)
            return new SyncResult { AppliedRevision = revision, Apply = new ApplyResult { Success = true }, Warnings = ["Already applied."] };
        if (state.PendingRevision == revision && state.PendingJobId is { } running && services.GetService<IJobRunner>()?.Get(running) is { State: JobState.Running })
            return new SyncResult
            {
                AppliedRevision = state.AppliedRevision, Apply = new ApplyResult { Success = true }, Pending = true, JobId = running,
                Warnings = ["Caddy is still being rebuilt with the required plugins."],
            };

        BundleContent content;
        try { content = BundleContent.Parse(canonical); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException or FormatException)
        {
            return Failed("The configuration bundle could not be read: " + ex.Message);
        }

        var material = services.GetService<ICertificateMaterialStore>();
        if (content.Certificates.Count > 0 && material is null)
            return Failed("This server cannot store replicated certificates (the certificate store service is not available).");

        // ---- snapshot for rollback
        var db = store.Database;
        var before = new Snapshot(
            store.Col<SiteHost>().FindAll().ToList(),
            store.Col<StreamHost>().FindAll().ToList(),
            store.Col<AccessList>().FindAll().ToList(),
            store.Col<Certificate>().FindAll().ToList(),
            store.GetSettings<CaddySettings>(),
            store.GetSettings<BinarySettings>());
        var fileBackups = before.Certificates
            .Where(c => content.Certificates.Any(b => b.Id == c.Id))
            .Select(c => (c.Id, Cert: TryRead(c.CertPath), Key: TryRead(c.KeyPath), c.CertPath, c.KeyPath))
            .ToList();

        // ---- certificates (files first: the store rows point at them)
        var certificates = new List<Certificate>();
        var written = new List<string>();
        try
        {
            foreach (var b in content.Certificates)
            {
                var (certPath, keyPath) = material!.WritePem(b.Id, b.CertPem, b.KeyPem);
                written.Add(b.Id);
                certificates.Add(new Certificate
                {
                    Id = b.Id, Name = b.Name, Notes = b.Notes, Source = CertificateSource.Uploaded, CertPath = certPath, KeyPath = keyPath,
                    Subjects = b.Subjects, Issuer = b.Issuer, NotBefore = b.NotBefore, NotAfter = b.NotAfter, Thumbprint = b.Thumbprint,
                    CreatedAt = b.CreatedAt, UpdatedAt = time.GetUtcNow().UtcDateTime,
                });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RestoreFiles(material!, fileBackups, written, before.Certificates);
            return Failed("Replicated certificates could not be written to this server's certificate store: " + ex.Message);
        }

        // ---- settings: replicated values + this node's local ones + secrets re-protected here
        CaddySettings caddy;
        try
        {
            caddy = content.CaddySettings.Deserialize<CaddySettings>(JsonDefaults.Storage) ?? new CaddySettings();
        }
        catch (JsonException ex)
        {
            RestoreFiles(material, fileBackups, written, before.Certificates);
            return Failed("The replicated Caddy settings could not be read: " + ex.Message);
        }
        foreach (var p in ClusterBundle.NodeLocalProperties) p.SetValue(caddy, p.GetValue(before.Caddy));
        foreach (var p in ClusterBundle.SecretProperties)
            p.SetValue(caddy, content.CaddySecrets.TryGetValue(ClusterBundle.JsonName(p), out var plain) && plain.Length > 0 ? secrets.Protect(plain) : null);
        var binary = store.GetSettings<BinarySettings>();
        binary.Plugins = content.Plugins;

        // ---- replace the replicated collections in one transaction
        try
        {
            Replace(db, content.Hosts, content.Streams, content.AccessLists, certificates);
            store.SaveSettings(caddy);
            store.SaveSettings(binary);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Storing the replicated configuration failed");
            RestoreData(db, before);
            RestoreFiles(material, fileBackups, written, before.Certificates);
            return Failed("The replicated configuration could not be stored: " + ex.Message);
        }

        var removedCertificates = before.Certificates.Where(c => content.Certificates.All(b => b.Id != c.Id)).ToList();
        var warnings = new List<string>();

        // ---- plugins: rebuild Caddy first when the installed binary lacks desired packages
        var binaries = services.GetService<ICaddyBinaryManager>();
        if (binaries is not null && content.Plugins.Count > 0)
        {
            var installed = await SafeInstalledAsync(binaries, ct);
            var missing = installed is null ? [] : Missing(content.Plugins, installed.Plugins);
            if (missing.Count > 0)
            {
                if (_failedRebuild is { } failed && failed.Revision == revision && time.GetUtcNow().UtcDateTime - failed.At < TimeSpan.FromMinutes(10))
                    return Failed("Rebuilding Caddy with the required plugins failed: " + failed.Error);
                JobInfo job;
                try { job = binaries.StartInstallOrUpdate(); }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
                {
                    return Failed("Caddy could not be rebuilt with the required plugins: " + ex.Message);
                }
                cluster.UpdateSettings(s =>
                {
                    s.PendingRevision = revision;
                    s.PendingJobId = job.Id;
                    s.LastSyncError = null;
                });
                _ = Task.Run(() => FinishAfterRebuildAsync(job.Id, revision, primary, removedCertificates));
                var note = $"Caddy is being rebuilt with {string.Join(", ", missing)}; the configuration is applied when that finishes.";
                logger.LogInformation("Cluster sync {Revision} waits for Caddy rebuild job {Job}", Short(revision), job.Id);
                return new SyncResult
                {
                    AppliedRevision = state.AppliedRevision, Pending = true, JobId = job.Id,
                    Apply = new ApplyResult { Success = true, Warnings = [note] }, Warnings = [note],
                };
            }
        }

        var config = services.GetRequiredService<ICaddyConfigService>();
        var apply = await config.ApplyAsync($"Cluster sync from {primary}", ct);
        if (!apply.Success)
        {
            logger.LogWarning("Cluster sync {Revision} rejected: {Error}", Short(revision), apply.Error);
            RestoreData(db, before);
            RestoreFiles(material, fileBackups, written, before.Certificates);
            cluster.UpdateSettings(s => s.LastSyncError = apply.Error);
            Audit("syncFailed", revision, primary, apply.Error);
            return new SyncResult { AppliedRevision = state.AppliedRevision, Apply = apply, Warnings = warnings };
        }

        Complete(revision, primary, removedCertificates, material);
        return new SyncResult { AppliedRevision = revision, Apply = apply, Warnings = warnings };
    }

    private void Complete(string revision, string primary, List<Certificate> removedCertificates, ICertificateMaterialStore? material)
    {
        foreach (var c in removedCertificates.Where(c => c.Source != CertificateSource.FilePath))
        {
            try { material?.Delete(c.Id); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogWarning(ex, "Could not delete certificate files of {Id}", c.Id); }
        }
        cluster.UpdateSettings(s =>
        {
            s.AppliedRevision = revision;
            s.AppliedAt = time.GetUtcNow().UtcDateTime;
            s.PendingRevision = null;
            s.PendingJobId = null;
            s.LastSyncError = null;
        });
        Audit("synced", revision, primary, null);
        logger.LogInformation("Cluster configuration {Revision} from {Primary} applied", Short(revision), primary);
    }

    /// <summary>Waits for the rebuild job, then applies the stored bundle (the platform also re-applies after an install).</summary>
    private async Task FinishAfterRebuildAsync(string jobId, string revision, string primary, List<Certificate> removedCertificates)
    {
        var jobs = services.GetRequiredService<IJobRunner>();
        JobInfo? job;
        while ((job = jobs.Get(jobId)) is { State: JobState.Running })
            await Task.Delay(cluster.Options.PendingJobPoll);
        await _gate.WaitAsync();
        try
        {
            if (cluster.Settings.PendingRevision != revision) return; // superseded by a newer sync
            if (job is not { State: JobState.Succeeded })
            {
                var error = job?.Error ?? "the rebuild job was lost";
                _failedRebuild = (revision, time.GetUtcNow().UtcDateTime, error);
                cluster.UpdateSettings(s =>
                {
                    s.PendingRevision = null;
                    s.PendingJobId = null;
                    s.LastSyncError = "Rebuilding Caddy with the required plugins failed: " + error;
                });
                Audit("syncFailed", revision, primary, error);
                return;
            }
            var apply = await services.GetRequiredService<ICaddyConfigService>().ApplyAsync($"Cluster sync from {primary}");
            if (!apply.Success)
            {
                cluster.UpdateSettings(s =>
                {
                    s.PendingRevision = null;
                    s.PendingJobId = null;
                    s.LastSyncError = apply.Error;
                });
                Audit("syncFailed", revision, primary, apply.Error);
                return;
            }
            Complete(revision, primary, removedCertificates, services.GetService<ICertificateMaterialStore>());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Applying cluster configuration {Revision} after the Caddy rebuild failed", Short(revision));
        }
        finally
        {
            _gate.Release();
        }
    }

    private static List<string> Missing(List<string> desired, List<string> installed)
    {
        static string Package(string p) => (p.Split('@')[0]).Trim().TrimEnd('/').ToLowerInvariant();
        var have = installed.Select(Package).ToHashSet();
        return desired.Where(d => !have.Contains(Package(d))).ToList();
    }

    private async Task<InstalledBinary?> SafeInstalledAsync(ICaddyBinaryManager binaries, CancellationToken ct)
    {
        try { return await binaries.GetInstalledAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read the installed Caddy plugins");
            return null;
        }
    }

    private sealed record Snapshot(
        List<SiteHost> Hosts, List<StreamHost> Streams, List<AccessList> AccessLists, List<Certificate> Certificates,
        CaddySettings Caddy, BinarySettings Binary);

    private void Replace(LiteDB.ILiteDatabase db, List<SiteHost> hosts, List<StreamHost> streams, List<AccessList> lists, List<Certificate> certs)
    {
        db.BeginTrans();
        try
        {
            store.Col<SiteHost>().DeleteAll();
            store.Col<StreamHost>().DeleteAll();
            store.Col<AccessList>().DeleteAll();
            store.Col<Certificate>().DeleteAll();
            if (hosts.Count > 0) store.Col<SiteHost>().InsertBulk(hosts);
            if (streams.Count > 0) store.Col<StreamHost>().InsertBulk(streams);
            if (lists.Count > 0) store.Col<AccessList>().InsertBulk(lists);
            if (certs.Count > 0) store.Col<Certificate>().InsertBulk(certs);
            db.Commit();
        }
        catch
        {
            db.Rollback();
            throw;
        }
    }

    private void RestoreData(LiteDB.ILiteDatabase db, Snapshot s)
    {
        try
        {
            Replace(db, s.Hosts, s.Streams, s.AccessLists, s.Certificates);
            store.SaveSettings(s.Caddy);
            store.SaveSettings(s.Binary);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Restoring the configuration after a failed cluster sync failed");
        }
    }

    private void RestoreFiles(ICertificateMaterialStore? material, List<(string Id, byte[]? Cert, byte[]? Key, string CertPath, string KeyPath)> backups,
        List<string> written, List<Certificate> previous)
    {
        foreach (var b in backups.Where(b => written.Contains(b.Id)))
        {
            try
            {
                if (b.Cert is not null) File.WriteAllBytes(b.CertPath, b.Cert);
                if (b.Key is not null) File.WriteAllBytes(b.KeyPath, b.Key);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogWarning(ex, "Could not restore certificate {Id}", b.Id); }
        }
        foreach (var id in written.Where(id => previous.All(p => p.Id != id)))
        {
            try { material?.Delete(id); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogWarning(ex, "Could not remove certificate {Id}", id); }
        }
    }

    private void Audit(string action, string revision, string primary, string? error)
    {
        services.GetService<IAuditLog>()?.Record(action, "cluster", Short(revision), $"Configuration from {primary}",
            error is null ? $"Revision {revision}" : $"Revision {revision}: {error}");
    }

    private SyncResult Failed(string error, string? applied = null) =>
        new() { AppliedRevision = applied ?? cluster.Settings.AppliedRevision, Apply = new ApplyResult { Success = false, Error = error } };

    private static byte[]? TryRead(string path)
    {
        try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static string Short(string revision) => revision.Length > 12 ? revision[..12] : revision;
}
