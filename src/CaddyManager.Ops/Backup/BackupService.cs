using System.IO.Compression;
using System.Text.Json;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using CaddyManager.Ops.Dashboard;
using LiteDB;
using JsonSerializer = System.Text.Json.JsonSerializer;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Ops.Backup;

/// <summary>manifest.json inside a backup archive.</summary>
public sealed record BackupManifest
{
    public string Product { get; init; } = AppPaths.ProductName;
    public int FormatVersion { get; init; } = 1;
    public string ManagerVersion { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public string Machine { get; init; } = "";
    public string DataDir { get; init; } = "";
    /// <summary>Certificate store that "certificates/" was taken from (restored to the same place when reachable).</summary>
    public string CertificateStorePath { get; init; } = "";
    /// <summary>True when the store was the default DataDir\certificates (restored to the target's default store).</summary>
    public bool CertificateStoreIsDefault { get; init; } = true;
    public bool IncludesCaddyConfig { get; init; }
    public bool IncludesCaddyStorage { get; init; }
    public int CertificateFiles { get; init; }
    public List<string> Warnings { get; init; } = new();
}

/// <summary>
/// Creates backup archives: manager.db (consistent copy after a LiteDB checkpoint), the certificate store,
/// caddy.json, Caddy's storage (ACME account/certs, internal CA) and manifest.json.
/// </summary>
internal sealed class BackupService(IStore store, AppPaths paths, ILogger<BackupService> logger)
{
    public const string DbEntry = "manager.db";
    public const string ManifestEntry = "manifest.json";
    public const string CaddyConfigEntry = "caddy.json";
    public const string CertificatesPrefix = "certificates/";
    public const string CaddyStoragePrefix = "caddy-data/";

    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Writes the archive to a temp file and returns a stream that deletes it when disposed.</summary>
    public async Task<(Stream Stream, string FileName)> CreateAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        var tmpDir = Path.Combine(paths.BackupDir, "tmp");
        Directory.CreateDirectory(tmpDir);
        var zipPath = Path.Combine(tmpDir, $"backup-{Guid.NewGuid():N}.zip");
        var dbCopy = Path.Combine(tmpDir, $"db-{Guid.NewGuid():N}.db");
        try
        {
            var warnings = new List<string>();
            CopyDatabaseConsistently(dbCopy);

            var certStore = EffectiveCertificateStore();
            var certFiles = 0;
            var hasConfig = false;
            var hasStorage = false;
            await using (var fs = new FileStream(zipPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
            {
                zip.CreateEntryFromFile(dbCopy, DbEntry, CompressionLevel.Optimal);

                if (File.Exists(paths.CaddyConfigFile))
                {
                    hasConfig = AddFileShared(zip, paths.CaddyConfigFile, CaddyConfigEntry, warnings);
                }

                if (Directory.Exists(certStore))
                    certFiles = AddDirectory(zip, certStore, CertificatesPrefix, warnings, ct);

                if (Directory.Exists(paths.CaddyStorageDir))
                    hasStorage = AddDirectory(zip, paths.CaddyStorageDir, CaddyStoragePrefix, warnings, ct,
                        skip: rel => rel.StartsWith("locks/", StringComparison.OrdinalIgnoreCase)) > 0;

                var manifest = new BackupManifest
                {
                    ManagerVersion = DashboardBuilder.ManagerVersion(),
                    CreatedAt = DateTime.UtcNow,
                    Machine = Environment.MachineName,
                    DataDir = paths.DataDir,
                    CertificateStorePath = certStore,
                    CertificateStoreIsDefault = string.Equals(Path.GetFullPath(certStore).TrimEnd(Path.DirectorySeparatorChar),
                        Path.GetFullPath(paths.DefaultCertificateStore).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase),
                    IncludesCaddyConfig = hasConfig,
                    IncludesCaddyStorage = hasStorage,
                    CertificateFiles = certFiles,
                    Warnings = warnings,
                };
                var entry = zip.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
                await using var es = entry.Open();
                await JsonSerializer.SerializeAsync(es, manifest, JsonDefaults.Api, ct);
            }

            logger.LogInformation("Backup created ({Size:N0} bytes, {Certs} certificate file(s), {Warnings} warning(s))",
                new FileInfo(zipPath).Length, certFiles, warnings.Count);
            var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            var name = $"caddy-proxy-manager-{SafeName(Environment.MachineName)}-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
            return (stream, name);
        }
        catch
        {
            TryDelete(zipPath);
            throw;
        }
        finally
        {
            TryDelete(dbCopy);
            Gate.Release();
        }
    }

    /// <summary>
    /// Checkpoint the LiteDB log into the data file, copy the data file while sharing it, then verify
    /// the copy opens. Retries once if a concurrent write made the copy unreadable.
    /// </summary>
    private void CopyDatabaseConsistently(string target)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                store.Database.Checkpoint();
                using (var src = new FileStream(paths.DbFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                    src.CopyTo(dst);
                using (var check = new LiteDatabase(new ConnectionString { Filename = target, Connection = ConnectionType.Direct, ReadOnly = true }))
                    _ = check.GetCollectionNames().ToList();
                return;
            }
            catch (Exception ex) when (attempt < 3)
            {
                last = ex;
                logger.LogWarning(ex, "Database copy attempt {Attempt} for backup failed; retrying", attempt);
                Thread.Sleep(200);
            }
        }
        throw new IOException("Could not take a consistent copy of the database.", last);
    }

    private string EffectiveCertificateStore()
    {
        try
        {
            var custom = store.GetSettings<CaddySettings>().CertificateStorePath;
            if (!string.IsNullOrWhiteSpace(custom)) return custom.Trim();
        }
        catch { /* fall back to default */ }
        return paths.DefaultCertificateStore;
    }

    private static bool AddFileShared(ZipArchive zip, string file, string entryName, List<string> warnings)
    {
        try
        {
            using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            entry.LastWriteTime = File.GetLastWriteTime(file);
            using var dst = entry.Open();
            src.CopyTo(dst);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Skipped {file}: {ex.Message}");
            return false;
        }
    }

    private static int AddDirectory(ZipArchive zip, string dir, string prefix, List<string> warnings, CancellationToken ct,
        Func<string, bool>? skip = null)
    {
        var count = 0;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not list {dir}: {ex.Message}");
            return 0;
        }
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
            if (Path.GetFileName(rel).StartsWith("._", StringComparison.Ordinal) || skip?.Invoke(rel) == true) continue;
            if (AddFileShared(zip, file, prefix + rel, warnings)) count++;
        }
        return count;
    }

    internal static string SafeName(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));

    private static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); } catch { /* best effort */ }
    }
}
