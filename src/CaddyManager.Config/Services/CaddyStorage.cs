using CaddyManager.Core;
using CaddyManager.Core.Models;

namespace CaddyManager.Config.Services;

/// <summary>Where Caddy keeps certificates, ACME accounts and its internal CA for the configured storage backend.</summary>
public static class CaddyStorage
{
    /// <summary>Folders of a certmagic file_system storage that are carried over when switching to shared storage.</summary>
    public static readonly string[] CopiedFolders = ["certificates", "acme", "pki", "ocsp"];

    /// <summary>The file_system root Caddy uses, or null when the backend is not a folder (Redis / custom module).</summary>
    public static string? FileSystemRoot(CaddySettings s, AppPaths paths) => s.StorageBackend switch
    {
        StorageBackend.Local => paths.CaddyStorageDir,
        StorageBackend.FileSystem => string.IsNullOrWhiteSpace(s.StoragePath) ? paths.CaddyStorageDir : s.StoragePath.Trim(),
        _ => null,
    };

    /// <summary>Display name of a non-folder backend, e.g. "Redis".</summary>
    public static string BackendName(StorageBackend b) => b switch
    {
        StorageBackend.Redis => "Redis",
        StorageBackend.Custom => "custom",
        StorageBackend.FileSystem => "shared folder",
        _ => "local",
    };

    /// <summary>Caddy's internal root CA certificate within a file_system root.</summary>
    public static string InternalRootPath(string root) => Path.Combine(root, "pki", "authorities", "local", "root.crt");

    /// <summary>
    /// Copies certificates/, acme/, pki/ and ocsp/ from <paramref name="from"/> to <paramref name="to"/> when the folder
    /// does not exist at the destination yet (never merges into or overwrites existing data). Returns the folders copied.
    /// </summary>
    public static List<string> CopyMissingFolders(string from, string to)
    {
        var copied = new List<string>();
        if (string.Equals(Path.GetFullPath(from).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(to).TrimEnd(Path.DirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return copied;
        foreach (var name in CopiedFolders)
        {
            var src = Path.Combine(from, name);
            var dst = Path.Combine(to, name);
            if (!Directory.Exists(src) || Directory.Exists(dst)) continue;
            // Copy into a temporary folder first so an interrupted copy never looks like complete storage.
            var tmp = dst + ".cpm-copy-" + Guid.NewGuid().ToString("N")[..8];
            try
            {
                CopyDirectory(src, tmp);
                Directory.Move(tmp, dst);
            }
            catch
            {
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
            copied.Add(name);
        }
        return copied;
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.EnumerateFiles(src))
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: false);
        foreach (var dir in Directory.EnumerateDirectories(src))
            CopyDirectory(dir, Path.Combine(dst, Path.GetFileName(dir)));
    }

    /// <summary>
    /// Creates the folder if needed and writes + removes a test file. Returns null when the manager can write there,
    /// otherwise the reason. (Caddy runs as the same account as the manager on Windows: LocalSystem, i.e. DOMAIN\HOST$ on a share.)
    /// </summary>
    public static string? CheckWritable(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, ".cpm-write-test-" + Guid.NewGuid().ToString("N")[..8]);
            File.WriteAllText(probe, "caddy proxy manager storage write test");
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return ex.Message;
        }
    }
}
