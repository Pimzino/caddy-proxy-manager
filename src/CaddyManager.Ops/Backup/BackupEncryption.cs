using System.IO.Compression;
using ICSharpCode.SharpZipLib.Zip;
using ZipFile = ICSharpCode.SharpZipLib.Zip.ZipFile;

namespace CaddyManager.Ops.Backup;

/// <summary>The backup password is missing or wrong.</summary>
internal sealed class BackupPasswordException(string message) : Exception(message);

/// <summary>
/// Password protection of backup archives with WinZip-compatible AES-256 entry encryption (SharpZipLib).
/// Windows Explorer's built-in ZIP support only understands the legacy ZipCrypto scheme and CANNOT open these
/// archives; use 7-Zip, WinZip or WinRAR, or restore them through Administration → Backup (which asks for the password).
/// Every entry, including manifest.json, is encrypted.
/// </summary>
internal static class BackupEncryption
{
    public const string FormatNote =
        "Encrypted backups use WinZip AES-256. Windows Explorer cannot open them: use 7-Zip/WinZip/WinRAR, or restore through the web UI with the password. " +
        "Keep the password safe — it cannot be recovered from the backup.";

    /// <summary>Re-packs a plain backup zip into an AES-256 encrypted zip.</summary>
    public static void Encrypt(Stream plainZip, Stream output, string password, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("A password is required.", nameof(password));
        using var src = new ZipArchive(plainZip, ZipArchiveMode.Read, leaveOpen: true);
        using var zos = new ZipOutputStream(output) { IsStreamOwner = false };
        zos.SetLevel(6);
        zos.Password = password;
        var buffer = new byte[81920];
        foreach (var e in src.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(e.Name)) continue; // directory entry
            var ze = new ZipEntry(ZipEntry.CleanName(e.FullName))
            {
                DateTime = e.LastWriteTime.LocalDateTime,
                AESKeySize = 256,
                Size = e.Length,
            };
            zos.PutNextEntry(ze);
            using (var s = e.Open())
            {
                int n;
                while ((n = s.Read(buffer, 0, buffer.Length)) > 0) zos.Write(buffer, 0, n);
            }
            zos.CloseEntry();
        }
        zos.Finish();
    }

    /// <summary>True when the stream is a ZIP archive with at least one encrypted entry. Not-a-zip → false (reported by the stager).</summary>
    public static bool IsEncrypted(Stream zip)
    {
        var start = zip.Position;
        try
        {
            using var zf = new ZipFile(zip, leaveOpen: true);
            foreach (ZipEntry e in zf)
                if (e.IsCrypted) return true;
            return false;
        }
        catch (Exception ex) when (ex is ZipException or IOException or ArgumentException or NotSupportedException)
        {
            return false;
        }
        finally
        {
            zip.Position = start;
        }
    }

    /// <summary>
    /// Decrypts an encrypted backup into a plain zip (for <see cref="RestoreStager.Stage(CaddyManager.Core.AppPaths, Stream)"/>),
    /// enforcing the same entry-count and expanded-size limits as the stager.
    /// </summary>
    public static void Decrypt(Stream encrypted, Stream plainOutput, string? password, long maxTotalBytes, int maxEntries, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(password))
            throw new BackupPasswordException("This backup is encrypted. Enter the backup password.");
        ZipFile zf;
        try { zf = new ZipFile(encrypted, leaveOpen: true) { Password = password }; }
        catch (ZipException) { throw new InvalidDataException("The uploaded file is not a valid ZIP archive."); }
        using (zf)
        {
            if (zf.Count > maxEntries)
                throw new InvalidDataException($"The archive contains more than {maxEntries:N0} entries; it is not a Caddy Proxy Manager backup.");
            using var dst = new ZipArchive(plainOutput, ZipArchiveMode.Create, leaveOpen: true);
            var buffer = new byte[81920];
            long total = 0;
            foreach (ZipEntry e in zf)
            {
                ct.ThrowIfCancellationRequested();
                if (!e.IsFile) continue;
                var entry = dst.CreateEntry(e.Name, CompressionLevel.Fastest);
                try
                {
                    using var input = zf.GetInputStream(e);
                    using var output = entry.Open();
                    int n;
                    while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        total += n;
                        if (total > maxTotalBytes)
                            throw new InvalidDataException($"The archive expands to more than {maxTotalBytes / 1024 / 1024:N0} MB; it is not a Caddy Proxy Manager backup.");
                        output.Write(buffer, 0, n);
                    }
                }
                catch (ZipException ex) when (IsPasswordError(ex))
                {
                    throw new BackupPasswordException("The backup password is incorrect.");
                }
                catch (ZipException ex)
                {
                    throw new InvalidDataException($"The encrypted backup is damaged or uses an unsupported format ({ex.Message}).");
                }
            }
        }
    }

    private static bool IsPasswordError(ZipException ex) =>
        ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("Auth", StringComparison.OrdinalIgnoreCase);
}
