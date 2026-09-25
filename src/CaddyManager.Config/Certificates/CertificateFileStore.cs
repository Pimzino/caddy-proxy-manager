using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Config.Certificates;

/// <summary>Writes uploaded certificates to the shared certificate store: &lt;store&gt;/&lt;certId&gt;/fullchain.pem + privkey.pem.</summary>
public sealed class CertificateFileStore(IStore store, AppPaths paths, ILogger<CertificateFileStore> logger)
{
    public const string ChainFileName = "fullchain.pem";
    public const string KeyFileName = "privkey.pem";

    public string StoreRoot
    {
        get
        {
            var custom = store.GetSettings<CaddySettings>().CertificateStorePath;
            return string.IsNullOrWhiteSpace(custom) ? paths.DefaultCertificateStore : custom.Trim();
        }
    }

    /// <summary>Snapshot of existing files so a failed transaction can restore them.</summary>
    public sealed record FileBackup(string CertPath, byte[]? Cert, string KeyPath, byte[]? Key);

    /// <summary>Writes the PEM files for a certificate and returns their paths.</summary>
    public (string CertPath, string KeyPath) Write(string certId, ParsedCertificate parsed)
    {
        var dir = Path.Combine(StoreRoot, certId);
        try
        {
            Directory.CreateDirectory(dir);
            RestrictAcl(dir);
            var certPath = Path.Combine(dir, ChainFileName);
            var keyPath = Path.Combine(dir, KeyFileName);
            WriteAtomic(keyPath, parsed.PrivateKeyPem);
            WriteAtomic(certPath, parsed.FullChainPem);
            logger.LogInformation("Certificate {Id} written to {Dir}", certId, dir);
            return (certPath, keyPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CertificateImportException($"The certificate could not be written to the certificate store ({dir}): {ex.Message}");
        }
    }

    public static FileBackup Backup(Certificate c) =>
        new(c.CertPath, TryRead(c.CertPath), c.KeyPath, TryRead(c.KeyPath));

    public void Restore(FileBackup b)
    {
        try
        {
            if (b.Cert is not null) WriteAtomic(b.CertPath, b.Cert);
            if (b.Key is not null) WriteAtomic(b.KeyPath, b.Key);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Could not restore certificate files {Cert}", b.CertPath);
        }
    }

    /// <summary>Deletes the store folder of an uploaded certificate (never touches file-path certificates).</summary>
    public void DeleteFiles(Certificate c)
    {
        if (c.Source != CertificateSource.Uploaded) return;
        var dir = Path.GetDirectoryName(c.CertPath);
        if (string.IsNullOrEmpty(dir) || !string.Equals(Path.GetFileName(dir), c.Id, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            logger.LogInformation("Deleted certificate files in {Dir}", dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete certificate folder {Dir}", dir);
        }
    }

    /// <summary>Deletes a folder written for a certificate that was never committed.</summary>
    public void DeleteFolder(string certId)
    {
        var dir = Path.Combine(StoreRoot, certId);
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete certificate folder {Dir}", dir);
        }
    }

    private static byte[]? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteAtomic(string file, string content) => WriteAtomic(file, System.Text.Encoding.ASCII.GetBytes(content));

    private static void WriteAtomic(string file, byte[] content)
    {
        var tmp = file + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllBytes(tmp, content);
        File.Move(tmp, file, overwrite: true);
    }

    /// <summary>
    /// Windows: restrict the folder to SYSTEM and Administrators (inheritance disabled). Skipped for UNC paths,
    /// where the share's own permissions must grant the computer accounts access.
    /// </summary>
    private void RestrictAcl(string dir)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (dir.StartsWith(@"\\", StringComparison.Ordinal))
        {
            logger.LogDebug("Certificate store {Dir} is a UNC path; leaving share ACLs unchanged", dir);
            return;
        }
        try
        {
            ApplyWindowsAcl(dir);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or IOException or PrivilegeNotHeldException)
        {
            logger.LogWarning(ex, "Could not restrict the ACL of {Dir}", dir);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyWindowsAcl(string dir)
    {
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[]
                 {
                     new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                 })
        {
            sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        }
        new DirectoryInfo(dir).SetAccessControl(sec);
    }
}
