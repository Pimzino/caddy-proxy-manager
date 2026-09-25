using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using LiteDB;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace CaddyManager.Ops.Backup;

/// <summary>
/// Backup restore in two phases, because the live LiteDB file cannot be replaced while it is open:
/// 1. <see cref="Stage"/> (API): validate the uploaded archive and extract it to DataDir\restore-pending.
/// 2. <see cref="ApplyPendingRestore"/> (next start, BEFORE LiteStore is opened — host hook or
///    "CaddyManager.exe apply-restore" with the service stopped): swap the files in, keeping the
///    replaced files under DataDir\backups\pre-restore-&lt;timestamp&gt;.
/// </summary>
public static class RestoreStager
{
    public const string PendingDirName = "restore-pending";
    private const string ReadyMarker = ".ready";

    /// <summary>Outcome of the last ApplyPendingRestore call in this process (logged by the Ops startup service).</summary>
    public static string? LastOutcome { get; private set; }
    public static bool LastOutcomeFailed { get; private set; }
    /// <summary>DataDir the last outcome applies to.</summary>
    public static string? LastOutcomeDataDir { get; private set; }

    public static string PendingDir(AppPaths paths) => Path.Combine(paths.DataDir, PendingDirName);
    public static bool HasPendingRestore(AppPaths paths) => File.Exists(Path.Combine(PendingDir(paths), ReadyMarker));

    /// <summary>
    /// Validates a backup archive and stages it for the next start. Throws <see cref="InvalidDataException"/>
    /// with an administrator-readable message when the archive is not a usable backup.
    /// </summary>
    public static BackupManifest Stage(AppPaths paths, Stream zipStream)
    {
        var staging = Path.Combine(paths.DataDir, PendingDirName + ".tmp-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            ZipArchive zip;
            try { zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true); }
            catch (InvalidDataException) { throw new InvalidDataException("The uploaded file is not a valid ZIP archive."); }

            BackupManifest manifest;
            using (zip)
            {
                var manifestEntry = zip.GetEntry(BackupService.ManifestEntry)
                    ?? throw new InvalidDataException("The archive has no manifest.json — it is not a Caddy Proxy Manager backup.");
                using (var ms = manifestEntry.Open())
                {
                    try { manifest = JsonSerializer.Deserialize<BackupManifest>(ms, JsonDefaults.Api) ?? throw new JsonException("empty"); }
                    catch (JsonException) { throw new InvalidDataException("manifest.json in the archive is not valid."); }
                }
                if (!string.Equals(manifest.Product, AppPaths.ProductName, StringComparison.Ordinal))
                    throw new InvalidDataException($"The archive was created by '{manifest.Product}', not {AppPaths.ProductName}.");
                if (manifest.FormatVersion > 1)
                    throw new InvalidDataException($"The backup format version {manifest.FormatVersion} is newer than this version of {AppPaths.ProductName} supports. Update the manager first.");
                if (zip.GetEntry(BackupService.DbEntry) is null)
                    throw new InvalidDataException("The archive does not contain manager.db.");

                Directory.CreateDirectory(staging);
                var root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry
                    var name = entry.FullName.Replace('\\', '/');
                    var allowed = name is BackupService.DbEntry or BackupService.ManifestEntry or BackupService.CaddyConfigEntry ||
                                  name.StartsWith(BackupService.CertificatesPrefix, StringComparison.Ordinal) ||
                                  name.StartsWith(BackupService.CaddyStoragePrefix, StringComparison.Ordinal);
                    if (!allowed) continue;
                    var dest = Path.GetFullPath(Path.Combine(staging, name.Replace('/', Path.DirectorySeparatorChar)));
                    if (!dest.StartsWith(root, StringComparison.Ordinal))
                        throw new InvalidDataException($"The archive contains an unsafe path: {entry.FullName}");
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    entry.ExtractToFile(dest, overwrite: true);
                }
            }

            ValidateDatabase(Path.Combine(staging, BackupService.DbEntry));

            var pending = PendingDir(paths);
            if (Directory.Exists(pending)) Directory.Delete(pending, recursive: true);
            Directory.Move(staging, pending);
            File.WriteAllText(Path.Combine(pending, ReadyMarker), DateTime.UtcNow.ToString("O"));
            return manifest;
        }
        catch
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { /* best effort */ }
            throw;
        }
    }

    private static void ValidateDatabase(string dbFile)
    {
        try
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = dbFile, Connection = ConnectionType.Direct, ReadOnly = true },
                new BsonMapper { EnumAsInteger = false });
            var users = db.GetCollection<User>(nameof(User)).FindAll().ToList();
            if (!users.Any(u => u.Role == UserRole.Admin && !u.Disabled))
                throw new InvalidDataException("The database in the backup has no enabled administrator account; restoring it would lock everyone out.");
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex)
        {
            throw new InvalidDataException($"manager.db in the archive cannot be opened: {ex.Message}");
        }
    }

    /// <summary>
    /// Applies a staged restore. Must run BEFORE the LiteStore is opened. Never throws; returns a message
    /// describing what happened (null when nothing was pending). On failure the previous files are put back
    /// and the staged folder is renamed to restore-failed-&lt;timestamp&gt; so it is not retried at every start.
    /// </summary>
    public static string? ApplyPendingRestore(AppPaths paths)
    {
        var pending = PendingDir(paths);
        if (!HasPendingRestore(paths)) return null;

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var safety = Path.Combine(paths.BackupDir, "pre-restore-" + stamp);
        var log = new StringBuilder();
        var dbFile = paths.DbFile;
        var dbLog = DbLogFile(dbFile);
        var movedDb = false;
        LastOutcomeDataDir = paths.DataDir;
        try
        {
            Directory.CreateDirectory(safety);
            var manifest = ReadManifest(pending);

            // 1. Database (+ LiteDB log file) — keep the current one for rollback.
            Directory.CreateDirectory(Path.GetDirectoryName(dbFile)!);
            if (File.Exists(dbFile)) { File.Move(dbFile, Path.Combine(safety, "manager.db")); movedDb = true; }
            if (File.Exists(dbLog)) File.Move(dbLog, Path.Combine(safety, Path.GetFileName(dbLog)));
            File.Copy(Path.Combine(pending, BackupService.DbEntry), dbFile, overwrite: true);
            log.AppendLine("Database restored.");

            // 2. caddy.json
            var cfg = Path.Combine(pending, BackupService.CaddyConfigEntry);
            if (File.Exists(cfg))
            {
                if (File.Exists(paths.CaddyConfigFile)) File.Copy(paths.CaddyConfigFile, Path.Combine(safety, "caddy.json"), overwrite: true);
                Directory.CreateDirectory(Path.GetDirectoryName(paths.CaddyConfigFile)!);
                File.Copy(cfg, paths.CaddyConfigFile, overwrite: true);
                log.AppendLine("caddy.json restored.");
            }

            // 3. Certificate store (same location as when backed up, if reachable; else the default store).
            var certSrc = Path.Combine(pending, "certificates");
            if (Directory.Exists(certSrc))
            {
                var target = paths.DefaultCertificateStore;
                if (manifest is { CertificateStoreIsDefault: false } && !string.IsNullOrWhiteSpace(manifest.CertificateStorePath) &&
                    CanUse(manifest.CertificateStorePath))
                    target = manifest.CertificateStorePath;
                var n = CopyTree(certSrc, target);
                log.AppendLine($"{n} certificate file(s) restored to {target}.");
            }

            // 4. Caddy storage (ACME account/certificates, internal CA).
            var storageSrc = Path.Combine(pending, "caddy-data");
            if (Directory.Exists(storageSrc))
            {
                var n = CopyTree(storageSrc, paths.CaddyStorageDir);
                log.AppendLine($"{n} Caddy storage file(s) restored.");
            }

            Directory.Delete(pending, recursive: true);
            log.Append($"Previous files kept in {safety}.");
            LastOutcomeFailed = false;
            return LastOutcome = "Backup restore applied. " + log.ToString().Replace(Environment.NewLine, " ").Trim();
        }
        catch (Exception ex)
        {
            try
            {
                if (movedDb && File.Exists(Path.Combine(safety, "manager.db")))
                {
                    if (File.Exists(dbFile)) File.Delete(dbFile);
                    File.Move(Path.Combine(safety, "manager.db"), dbFile);
                    var savedLog = Path.Combine(safety, Path.GetFileName(dbLog));
                    if (File.Exists(savedLog)) File.Move(savedLog, dbLog, overwrite: true);
                }
            }
            catch { /* report original error */ }
            try { Directory.Move(pending, Path.Combine(paths.DataDir, "restore-failed-" + stamp)); } catch { /* best effort */ }
            LastOutcomeFailed = true;
            return LastOutcome = $"Backup restore FAILED and the previous database was kept: {ex.Message}. " +
                                 $"The staged files were moved to restore-failed-{stamp}.";
        }
    }

    internal static string DbLogFile(string dbFile) =>
        Path.Combine(Path.GetDirectoryName(dbFile)!, Path.GetFileNameWithoutExtension(dbFile) + "-log" + Path.GetExtension(dbFile));

    private static BackupManifest? ReadManifest(string dir)
    {
        try { return JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(Path.Combine(dir, BackupService.ManifestEntry)), JsonDefaults.Api); }
        catch { return null; }
    }

    private static bool CanUse(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            return true;
        }
        catch { return false; }
    }

    private static int CopyTree(string src, string dst)
    {
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file);
            var target = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            count++;
        }
        return count;
    }
}
