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
public static partial class RestoreStager
{
    public const string PendingDirName = "restore-pending";
    private const string ReadyMarker = ".ready";

    /// <summary>Upper bounds for an uploaded archive (zip-bomb / disk-exhaustion guard).</summary>
    internal const int MaxEntries = 50_000;
    internal const long MaxTotalUncompressedBytes = 2L * 1024 * 1024 * 1024;

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
    public static BackupManifest Stage(AppPaths paths, Stream zipStream) => Stage(paths, zipStream, MaxTotalUncompressedBytes);

    internal static BackupManifest Stage(AppPaths paths, Stream zipStream, long maxTotalBytes)
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

                if (zip.Entries.Count > MaxEntries)
                    throw new InvalidDataException($"The archive contains more than {MaxEntries:N0} entries; it is not a Caddy Proxy Manager backup.");

                Directory.CreateDirectory(staging);
                var root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
                long total = 0;
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
                    total += ExtractBounded(entry, dest, maxTotalBytes - total, maxTotalBytes);
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

    /// <summary>Extracts one entry, refusing to write more than <paramref name="budget"/> bytes (declared sizes can lie).</summary>
    private static long ExtractBounded(ZipArchiveEntry entry, string dest, long budget, long limit)
    {
        if (entry.Length > budget)
            throw new InvalidDataException($"The archive expands to more than {limit / 1024 / 1024:N0} MB; it is not a Caddy Proxy Manager backup.");
        using var src = entry.Open();
        using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        long written = 0;
        int n;
        while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
        {
            written += n;
            if (written > budget)
                throw new InvalidDataException($"The archive expands to more than {limit / 1024 / 1024:N0} MB; it is not a Caddy Proxy Manager backup.");
            dst.Write(buffer, 0, n);
        }
        return written;
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
            // The restored database decides where certificates live; the manifest alone (attacker-controllable
            // in a crafted archive) must never choose an arbitrary directory for SYSTEM to write into.
            var restoredStore = ReadCertificateStorePath(Path.Combine(pending, BackupService.DbEntry));

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
                    restoredStore is not null && SamePath(restoredStore, manifest.CertificateStorePath) &&
                    CanUse(restoredStore))
                    target = restoredStore;
                var n = CopyTree(certSrc, target, IsCertificateStoreFile, out var skipped);
                log.AppendLine($"{n} certificate file(s) restored to {target}.");
                if (skipped > 0) log.AppendLine($"{skipped} unexpected file(s) in certificates/ were ignored.");
            }

            // 4. Caddy storage (ACME account/certificates, internal CA).
            var storageSrc = Path.Combine(pending, "caddy-data");
            if (Directory.Exists(storageSrc))
            {
                var n = CopyTree(storageSrc, paths.CaddyStorageDir, _ => true, out _);
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

    /// <summary>CaddySettings.CertificateStorePath from a (staged) database, or null when unset/unreadable.</summary>
    internal static string? ReadCertificateStorePath(string dbFile)
    {
        try
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = dbFile, Connection = ConnectionType.Direct, ReadOnly = true });
            var doc = db.GetCollection("settings").FindById(nameof(CaddySettings));
            if (doc is null || !doc.TryGetValue("Json", out var json) || !json.IsString) return null;
            var path = JsonSerializer.Deserialize<CaddySettings>(json.AsString, JsonDefaults.Storage)?.CertificateStorePath;
            return string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        }
        catch { return null; }
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a.Trim()).TrimEnd('\\', '/'), Path.GetFullPath(b.Trim()).TrimEnd('\\', '/'),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch { return false; }
    }

    /// <summary>Only the layout the certificate store uses: &lt;certId&gt;/&lt;name&gt;.pem (plus .crt/.key/.cer).</summary>
    internal static bool IsCertificateStoreFile(string relativePath)
    {
        var parts = relativePath.Replace('\\', '/').Split('/');
        return parts.Length == 2 && SafeSegment().IsMatch(parts[0]) && CertFileName().IsMatch(parts[1]);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$")]
    private static partial System.Text.RegularExpressions.Regex SafeSegment();

    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}\.(pem|crt|key|cer)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex CertFileName();

    private static int CopyTree(string src, string dst, Func<string, bool> include, out int skipped)
    {
        var count = 0;
        skipped = 0;
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file);
            if (!include(rel)) { skipped++; continue; }
            var target = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            count++;
        }
        return count;
    }
}
