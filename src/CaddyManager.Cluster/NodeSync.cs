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
    /// <summary>The bundle waits for a Caddy rebuild with the desired plugins (job <see cref="JobId"/>); nothing was stored yet.</summary>
    public bool Pending { get; init; }
    public string? JobId { get; init; }
}

/// <summary>
/// Node side of replication: replaces the replicated collections with the bundle's content (one LiteDB transaction),
/// writes certificates through ICertificateMaterialStore (same ids, source Uploaded), re-protects secrets with this node's
/// ISecretProtector, keeps this node's NodeLocalProperties, stores the desired plugins and applies the configuration —
/// all while holding the Config module's IConfigMutationLock, so a node-local settings change never interleaves with it.
/// When Caddy rejects the configuration, the previous data is restored (Caddy itself keeps running the previous config).
/// A bundle that needs plugins the installed Caddy lacks is only staged (in memory): the store is not touched until the
/// Caddy rebuild has succeeded, so a failed or long rebuild never leaves the store holding content Caddy does not run.
/// A failed rebuild is retried automatically only after a growing back-off (ClusterOptions.RebuildRetryBackoff).
/// </summary>
public sealed class NodeSync(
    IStore store,
    ISecretProtector secrets,
    ClusterService cluster,
    AppPaths paths,
    IServiceProvider services,
    TimeProvider time,
    ILogger<NodeSync> logger)
{
    private const string RebuildJobKind = "caddy-install";
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>A bundle waiting for the Caddy install job <see cref="JobId"/> to end.</summary>
    /// <param name="OwnPluginsKey">Plugin set the job builds when this node started it for the bundle; null for a job started otherwise.</param>
    /// <param name="PluginsBefore">Desired plugins before staging (restored when the rebuild fails).</param>
    private sealed record Staged(string Revision, BundleContent Content, string Primary, string JobId, string? OwnPluginsKey, List<string> PluginsBefore);

    private volatile Staged? _staged;
    private string? _watchedJob;
    /// <summary>Last failed rebuild: plugin set, failures in a row, next automatic attempt, error.</summary>
    private (string Plugins, int Failures, DateTime RetryAt, string Error)? _rebuildFailure;

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

    /// <summary>
    /// The revision waiting for a Caddy rebuild, as reported in `hello`. A pending revision without a staged bundle (the
    /// manager restarted while Caddy was being rebuilt) is forgotten, so the primary pushes it again.
    /// </summary>
    public string? EffectivePendingRevision()
    {
        var pending = cluster.Settings.PendingRevision;
        if (pending is null) return null;
        if (_staged is { } staged && staged.Revision == pending) return pending;
        cluster.UpdateSettings(s =>
        {
            if (s.PendingRevision != pending) return;
            s.PendingRevision = null;
            s.PendingJobId = null;
        });
        return null;
    }

    private async Task<SyncResult> ApplyCoreAsync(JsonObject bundle, string revision, bool force, CancellationToken ct)
    {
        var canonical = ClusterBundle.Canonicalize(bundle);
        if (ClusterBundle.Revision(canonical) != revision)
            return Failed("The configuration bundle is damaged (its revision does not match its content).");

        // A staged bundle whose install job has ended is settled first (applied, rebuilt again or recorded as failed).
        if (_staged is { } ended && !IsRunning(ended.JobId)) await SettleStagedAsync(ended.JobId);

        var state = cluster.Settings;
        var primary = state.PrimaryName ?? "the primary";
        if (!force && state.AppliedRevision == revision)
        {
            // What runs here is what the primary wants again (e.g. a rejected change was undone there): a staged bundle
            // and the last sync error no longer apply.
            if (_staged is not null) DropStaged();
            if (state.LastSyncError is not null || state.PendingRevision is not null)
                cluster.UpdateSettings(s =>
                {
                    s.LastSyncError = null;
                    s.LastSyncErrorRevision = null;
                    s.PendingRevision = null;
                    s.PendingJobId = null;
                });
            return new SyncResult { AppliedRevision = revision, Apply = new ApplyResult { Success = true }, Warnings = ["Already applied."] };
        }
        if (_staged is { } waiting && waiting.Revision == revision && IsRunning(waiting.JobId))
            return PendingResult(state.AppliedRevision, waiting.JobId, "Caddy is still being rebuilt with the required plugins.");

        BundleContent content;
        try { content = BundleContent.Parse(canonical); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException or FormatException)
        {
            return Failed("The configuration bundle could not be read: " + ex.Message);
        }

        if (content.Certificates.Any(c => !c.MaterialUnavailable) && services.GetService<ICertificateMaterialStore>() is null)
            return Failed("This server cannot store replicated certificates (the certificate store service is not available).");

        // ---- plugins first: a bundle that needs a Caddy rebuild is staged, the store stays untouched until the rebuild succeeded
        var missing = await MissingPluginsAsync(content.Plugins, ct);
        if (missing.Count > 0) return StageForRebuild(content, revision, primary, missing, force, state);

        if (_staged is not null) DropStaged();
        return await StoreAndApplyAsync(content, revision, primary, ct);
    }

    // ------------------------------------------------------------------ rebuild with missing plugins

    private SyncResult StageForRebuild(BundleContent content, string revision, string primary, List<string> missing, bool force, ClusterSettings state)
    {
        var pluginsBefore = _staged?.PluginsBefore ?? store.GetSettings<BinarySettings>().Plugins.ToList();
        // An install job is running (the rebuild for an earlier revision, or an update started on this server): wait for it
        // and check the plugins again when it has ended.
        var running = _staged is { } s && IsRunning(s.JobId) ? s.JobId : RunningInstallJob();
        if (running is not null)
        {
            var own = _staged is { } previous && previous.JobId == running ? previous.OwnPluginsKey : null;
            Stage(new Staged(revision, content, primary, running, own, pluginsBefore));
            return PendingResult(state.AppliedRevision, running,
                $"Caddy is being rebuilt or updated on this server; the configuration (it needs {string.Join(", ", missing)}) is applied when that has finished.");
        }
        if (!force && RebuildBackoff(content.Plugins) is { } error)
        {
            if (_staged is not null) DropStaged();
            return Failed(error, revision);
        }
        return StartRebuild(content, revision, primary, missing, pluginsBefore, state.AppliedRevision);
    }

    private SyncResult StartRebuild(BundleContent content, string revision, string primary, List<string> missing, List<string> pluginsBefore, string? applied)
    {
        var binaries = services.GetRequiredService<ICaddyBinaryManager>();
        // The install job builds the desired plugins stored in BinarySettings (restored if the rebuild fails).
        SavePlugins(content.Plugins);
        JobInfo job;
        try { job = binaries.StartInstallOrUpdate(); }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException)
        {
            SavePlugins(pluginsBefore);
            if (_staged is not null) DropStaged();
            return Failed("Caddy could not be rebuilt with the required plugins: " + ex.Message);
        }
        Stage(new Staged(revision, content, primary, job.Id, PluginsKey(content.Plugins), pluginsBefore));
        logger.LogInformation("Cluster sync {Revision} waits for Caddy rebuild job {Job}", Short(revision), job.Id);
        return PendingResult(applied, job.Id, $"Caddy is being rebuilt with {string.Join(", ", missing)}; the configuration is applied when that finishes.");
    }

    private void Stage(Staged staged)
    {
        _staged = staged;
        cluster.UpdateSettings(s =>
        {
            s.PendingRevision = staged.Revision;
            s.PendingJobId = staged.JobId;
        });
        if (_watchedJob == staged.JobId) return;
        _watchedJob = staged.JobId;
        _ = Task.Run(() => FinishAfterJobAsync(staged.JobId));
    }

    /// <summary>Forgets the staged bundle (superseded): the desired plugins it stored for its rebuild are put back.</summary>
    private void DropStaged()
    {
        if (_staged is { } staged) SavePlugins(staged.PluginsBefore);
        _staged = null;
        cluster.UpdateSettings(s =>
        {
            s.PendingRevision = null;
            s.PendingJobId = null;
        });
    }

    /// <summary>Waits for the install job to end, then settles the bundle staged for it.</summary>
    private async Task FinishAfterJobAsync(string jobId)
    {
        try
        {
            var jobs = services.GetRequiredService<IJobRunner>();
            while (jobs.Get(jobId) is { State: JobState.Running })
                await Task.Delay(cluster.Options.PendingJobPoll);
            await _gate.WaitAsync();
            try
            {
                if (_watchedJob == jobId) _watchedJob = null;
                await SettleStagedAsync(jobId);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Finishing the cluster sync after Caddy install job {Job} failed", jobId);
        }
    }

    /// <summary>
    /// Under the gate, after install job <paramref name="jobId"/> ended: applies the bundle staged for it when Caddy now has the
    /// plugins, starts the rebuild it still needs after someone else's job, or records the failed rebuild (with back-off).
    /// </summary>
    private async Task SettleStagedAsync(string jobId)
    {
        if (_staged is not { } staged || staged.JobId != jobId) return; // applied, superseded or waiting for another job
        if (cluster.Settings.Role != ClusterRole.Node)
        {
            _staged = null;
            return;
        }
        var job = services.GetService<IJobRunner>()?.Get(jobId);
        var missing = await MissingPluginsAsync(staged.Content.Plugins, CancellationToken.None);
        if (missing.Count == 0)
        {
            _rebuildFailure = null;
            await StoreAndApplyAsync(staged.Content, staged.Revision, staged.Primary, CancellationToken.None);
            _staged = null;
            return;
        }
        if (staged.OwnPluginsKey == PluginsKey(staged.Content.Plugins))
        {
            RebuildFailed(staged, job is { State: JobState.Succeeded }
                ? $"the new Caddy binary still lacks {string.Join(", ", missing)}"
                : job?.Error ?? "the rebuild job was lost");
            return;
        }
        // Another job ended (an update, or a rebuild for other plugins): rebuild for this bundle now.
        if (RebuildBackoff(staged.Content.Plugins) is { } error)
        {
            RebuildFailed(staged, error, count: false);
            return;
        }
        _staged = null;
        var result = StartRebuild(staged.Content, staged.Revision, staged.Primary, missing, staged.PluginsBefore, cluster.Settings.AppliedRevision);
        if (!result.Apply.Success) RecordFailure(staged.Revision, staged.Primary, result.Apply.Error ?? "Caddy could not be rebuilt.");
    }

    private void RebuildFailed(Staged staged, string error, bool count = true)
    {
        _staged = null;
        SavePlugins(staged.PluginsBefore);
        var message = error.StartsWith("Rebuilding Caddy", StringComparison.Ordinal) ? error : "Rebuilding Caddy with the required plugins failed: " + Scrub(error);
        if (count)
        {
            var key = PluginsKey(staged.Content.Plugins);
            var failures = _rebuildFailure is { } previous && previous.Plugins == key ? previous.Failures + 1 : 1;
            var options = cluster.Options;
            var delay = TimeSpan.FromTicks((long)Math.Min(options.RebuildRetryMaxBackoff.Ticks, options.RebuildRetryBackoff.Ticks * Math.Pow(3, failures - 1)));
            _rebuildFailure = (key, failures, time.GetUtcNow().UtcDateTime + delay, message);
            logger.LogWarning("Rebuilding Caddy for cluster configuration {Revision} failed ({Failures} in a row); next automatic attempt in {Delay}: {Error}",
                Short(staged.Revision), failures, delay, error);
        }
        RecordFailure(staged.Revision, staged.Primary, message);
    }

    private void RecordFailure(string revision, string primary, string error)
    {
        cluster.UpdateSettings(s =>
        {
            s.PendingRevision = null;
            s.PendingJobId = null;
            s.LastSyncError = error;
            s.LastSyncErrorRevision = revision;
        });
        Audit("syncFailed", revision, primary, error);
    }

    /// <summary>The error of the last failed rebuild of this plugin set while its back-off lasts; null when a rebuild may start.</summary>
    private string? RebuildBackoff(List<string> plugins) =>
        _rebuildFailure is { } failed && failed.Plugins == PluginsKey(plugins) && time.GetUtcNow().UtcDateTime < failed.RetryAt ? failed.Error : null;

    private void SavePlugins(List<string> plugins)
    {
        var binary = store.GetSettings<BinarySettings>();
        if (binary.Plugins.SequenceEqual(plugins)) return;
        binary.Plugins = plugins.ToList();
        store.SaveSettings(binary);
    }

    private bool IsRunning(string jobId) => services.GetService<IJobRunner>()?.Get(jobId) is { State: JobState.Running };

    private string? RunningInstallJob()
    {
        var jobs = services.GetService<IJobRunner>();
        if (jobs is null || !jobs.IsRunning(RebuildJobKind)) return null;
        return jobs.Recent(50).FirstOrDefault(j => j.Kind == RebuildJobKind && j.State == JobState.Running)?.Id;
    }

    private async Task<List<string>> MissingPluginsAsync(List<string> desired, CancellationToken ct)
    {
        if (desired.Count == 0 || services.GetService<ICaddyBinaryManager>() is not { } binaries) return [];
        var installed = await SafeInstalledAsync(binaries, ct);
        return installed is null ? [] : Missing(desired, installed.Plugins);
    }

    private static string PluginsKey(List<string> plugins) =>
        string.Join("\n", plugins.Select(Package).Where(p => p.Length > 0).Distinct().Order(StringComparer.Ordinal));

    // ------------------------------------------------------------------ store + apply

    /// <summary>
    /// Replaces the replicated data with the bundle's content and applies it, under the configuration mutation lock;
    /// restores the previous data (and certificate / root files) when anything fails.
    /// </summary>
    private async Task<SyncResult> StoreAndApplyAsync(BundleContent content, string revision, string primary, CancellationToken ct)
    {
        // Implemented by the Config module; optional so replication works (unserialised) without it.
        using var mutation = services.GetService<IConfigMutationLock>() is { } mutationLock ? await mutationLock.AcquireAsync(ct) : null;
        var material = services.GetService<ICertificateMaterialStore>();
        var applied = cluster.Settings.AppliedRevision;
        var warnings = new List<string>();

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
            .Where(c => content.Certificates.Any(b => b.Id == c.Id && !b.MaterialUnavailable))
            .Select(c => (c.Id, Cert: TryRead(c.CertPath), Key: TryRead(c.KeyPath), c.CertPath, c.KeyPath))
            .ToList();

        // ---- certificates (files first: the store rows point at them)
        var certificates = new List<Certificate>();
        var written = new List<string>();
        try
        {
            foreach (var b in content.Certificates)
            {
                if (b.MaterialUnavailable)
                {
                    // The primary could not read the files just now: keep this node's copy (row and files) unchanged.
                    if (before.Certificates.FirstOrDefault(c => c.Id == b.Id) is { } kept) certificates.Add(kept);
                    else warnings.Add($"Certificate '{b.Name}' is not available on the primary right now and not on this server yet; hosts using it are skipped until it is.");
                    continue;
                }
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
            RestoreFiles(material, fileBackups, written, before.Certificates);
            return Failed("Replicated certificates could not be written to this server's certificate store: " + ex.Message, revision);
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
            return Failed("The replicated Caddy settings could not be read: " + ex.Message, revision);
        }
        foreach (var p in ClusterBundle.NodeLocalProperties) p.SetValue(caddy, p.GetValue(before.Caddy));
        foreach (var p in ClusterBundle.SecretProperties)
            p.SetValue(caddy, content.CaddySecrets.TryGetValue(ClusterBundle.JsonName(p), out var plain) && plain.Length > 0 ? secrets.Protect(plain) : null);
        var binary = store.GetSettings<BinarySettings>();
        binary.Plugins = content.Plugins;

        // ---- custom ACME CA root: the primary's root certificate in this node's data folder (unless the node set its own path)
        var rootFile = ManagedAcmeRootFile;
        var rootBackup = TryRead(rootFile);
        try
        {
            var usesManagedRoot = string.IsNullOrWhiteSpace(caddy.CustomAcmeRootPath) || PathsEqual(caddy.CustomAcmeRootPath, rootFile);
            if (content.CustomAcmeRootPem is { Length: > 0 } rootPem && usesManagedRoot)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(rootFile)!);
                File.WriteAllText(rootFile, rootPem);
                caddy.CustomAcmeRootPath = rootFile;
            }
            else if (content.CustomAcmeRootPem is null && !content.CustomAcmeRootUnavailable && usesManagedRoot && caddy.CustomAcmeRootPath is not null)
            {
                caddy.CustomAcmeRootPath = null; // the primary no longer uses a custom root
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RestoreFiles(material, fileBackups, written, before.Certificates);
            return Failed($"The custom ACME root certificate could not be written to {rootFile}: {ex.Message}", revision);
        }

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
            RestoreFile(rootFile, rootBackup);
            return Failed("The replicated configuration could not be stored: " + ex.Message, revision);
        }

        var config = services.GetRequiredService<ICaddyConfigService>();
        var apply = await config.ApplyAsync($"Cluster sync from {primary}", CancellationToken.None);
        if (!apply.Success)
        {
            logger.LogWarning("Cluster sync {Revision} rejected: {Error}", Short(revision), apply.Error);
            RestoreData(db, before);
            RestoreFiles(material, fileBackups, written, before.Certificates);
            RestoreFile(rootFile, rootBackup);
            RecordFailure(revision, primary, apply.Error ?? "Caddy rejected the configuration.");
            return new SyncResult { AppliedRevision = applied, Apply = apply, Warnings = warnings };
        }

        // Certificates removed by exactly this revision (compared with what was stored before it, not an earlier bundle).
        var removedCertificates = before.Certificates.Where(c => certificates.All(k => k.Id != c.Id)).ToList();
        Complete(revision, primary, removedCertificates, material);
        return new SyncResult { AppliedRevision = revision, Apply = apply, Warnings = warnings };
    }

    /// <summary>Where a node keeps the primary's custom ACME root certificate.</summary>
    internal string ManagedAcmeRootFile => Path.Combine(paths.DataDir, "caddy", "cluster-acme-root.pem");

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
            s.LastSyncErrorRevision = null;
        });
        Audit("synced", revision, primary, null);
        logger.LogInformation("Cluster configuration {Revision} from {Primary} applied", Short(revision), primary);
    }

    private static List<string> Missing(List<string> desired, List<string> installed)
    {
        var have = installed.Select(Package).ToHashSet();
        return desired.Where(d => !have.Contains(Package(d))).ToList();
    }

    private static string Package(string p) => p.Split('@')[0].Trim().TrimEnd('/').ToLowerInvariant();

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

    private void RestoreFile(string path, byte[]? content)
    {
        try
        {
            if (content is not null) File.WriteAllBytes(path, content);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogWarning(ex, "Could not restore {Path}", path); }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a.Trim()), Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private void Audit(string action, string revision, string primary, string? error)
    {
        services.GetService<IAuditLog>()?.Record(action, "cluster", Short(revision), $"Configuration from {primary}",
            error is null ? $"Revision {revision}" : $"Revision {revision}: {error}");
    }

    private string Scrub(string text) => services.GetService<ISecretScrubber>()?.Scrub(text) ?? text;

    private static SyncResult PendingResult(string? applied, string jobId, string note) => new()
    {
        AppliedRevision = applied, Pending = true, JobId = jobId, Apply = new ApplyResult { Success = true, Warnings = [note] }, Warnings = [note],
    };

    private SyncResult Failed(string error, string? revision = null)
    {
        // A failure recorded for a revision is reported in hello (the primary throttles pushing that revision again).
        if (revision is not null)
            cluster.UpdateSettings(s =>
            {
                s.LastSyncError = error;
                s.LastSyncErrorRevision = revision;
            });
        return new SyncResult { AppliedRevision = cluster.Settings.AppliedRevision, Apply = new ApplyResult { Success = false, Error = error } };
    }

    private static byte[]? TryRead(string path)
    {
        try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static string Short(string revision) => revision.Length > 12 ? revision[..12] : revision;
}
