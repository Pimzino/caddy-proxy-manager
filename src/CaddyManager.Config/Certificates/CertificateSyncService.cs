using CaddyManager.Core;
using CaddyManager.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Config.Certificates;

/// <summary>Outcome of one synchronisation of a certificate with its external source.</summary>
/// <param name="FilesWritten">The PEM files in the certificate store were (re)written (PfxFile / WindowsStore).</param>
/// <param name="ThumbprintChanged">A different certificate than before is now in use.</param>
public sealed record CertificateSyncResult(Certificate Certificate, bool FilesWritten, bool ThumbprintChanged, string? Error)
{
    public bool Success => Error is null;
}

/// <summary>
/// Re-reads certificates that come from outside the manager: FilePath (PEM files renewed by other tooling), PfxFile
/// (re-converted to PEM in the store) and WindowsStore (re-selected and re-exported when a newer/different certificate
/// is found). Failures are stored in LastSyncError and raised as warnings (key cert-sync:&lt;id&gt;, alert rule
/// certificateExpiry); the first success afterwards raises a Recovered event.
/// </summary>
public sealed class CertificateSyncService(
    IStore store,
    CertificateFileStore files,
    ISecretProtector secrets,
    IWindowsCertificateSource windows,
    IServiceProvider services,
    ILogger<CertificateSyncService> logger)
{
    public const string EventKeyPrefix = "cert-sync:";
    public const string AlertRule = "certificateExpiry";

    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Exclusive access to certificate rows and store files, for API operations that rewrite or delete them
    /// (replace, delete) so they never interleave with a background sync. Do not call SyncAsync while holding it.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        return new Releaser(_lock);
    }

    private sealed class Releaser(SemaphoreSlim s) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) s.Release();
        }
    }

    /// <summary>Synchronises one certificate. Returns null when it does not exist or has no external source.</summary>
    public async Task<CertificateSyncResult?> SyncAsync(string certificateId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var col = store.Col<Certificate>();
            var c = col.FindById(certificateId);
            if (c is null || c.Source == CertificateSource.Uploaded) return null;
            var hadError = c.LastSyncError is not null;
            var before = c.Thumbprint;
            try
            {
                var written = false;
                switch (c.Source)
                {
                    case CertificateSource.FilePath:
                        SetMetadata(c, CertificateParser.FromFiles(c.CertPath, c.KeyPath).Metadata);
                        break;
                    case CertificateSource.PfxFile:
                    {
                        if (string.IsNullOrWhiteSpace(c.SourcePath)) throw new CertificateImportException("The PFX path is not set.");
                        var parsed = CertificateParser.FromPfxFile(c.SourcePath, Password(c));
                        written = WriteIfChanged(c, parsed);
                        SetMetadata(c, parsed.Metadata);
                        break;
                    }
                    case CertificateSource.WindowsStore:
                        written = SyncFromWindowsStore(c);
                        break;
                }
                var changed = !string.Equals(before, c.Thumbprint, StringComparison.OrdinalIgnoreCase);
                c.LastSyncedAt = DateTime.UtcNow;
                c.LastSyncError = null;
                if (changed || written) c.UpdatedAt = DateTime.UtcNow;
                col.Update(c);
                if (changed) logger.LogInformation("Certificate {Name} ({Id}) now uses {Thumbprint}, valid until {NotAfter:u}", c.Name, c.Id, c.Thumbprint, c.NotAfter);
                if (hadError)
                    Raise(EventSeverity.Recovered, $"Certificate '{c.Name}' is synchronised with its source again.", Describe(c), c.Id);
                return new CertificateSyncResult(c, written, changed, null);
            }
            catch (CertificateImportException ex)
            {
                c.LastSyncError = ex.Message;
                col.Update(c);
                logger.LogWarning("Certificate {Name} ({Id}) could not be synchronised: {Error}", c.Name, c.Id, ex.Message);
                Raise(EventSeverity.Warning, $"Certificate '{c.Name}' could not be synchronised with its source.",
                    $"{Describe(c)}\n\n{ex.Message}", c.Id);
                return new CertificateSyncResult(c, false, false, ex.Message);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Selects the store certificate and exports it when it differs from the files in the store. Returns true when files were written.</summary>
    private bool SyncFromWindowsStore(Certificate c)
    {
        if (!windows.IsSupported)
            throw new CertificateImportException("The Windows certificate store is only available when Caddy Proxy Manager runs on Windows.");
        var where = $"{c.StoreLocation}\\{c.StoreName}";
        var chosen = WindowsStoreSelector.Select(windows.List(c.StoreLocation, c.StoreName), c.StoreThumbprint, c.StoreSubject, DateTime.UtcNow, where);
        var same = string.Equals(WindowsStoreNames.NormalizeThumbprint(chosen.Thumbprint), WindowsStoreNames.NormalizeThumbprint(c.Thumbprint), StringComparison.Ordinal);
        if (same && File.Exists(c.CertPath) && File.Exists(c.KeyPath)) return false; // nothing new: no need to export the key again
        var parsed = windows.Export(c.StoreLocation, c.StoreName, chosen.Thumbprint);
        var written = WriteIfChanged(c, parsed);
        SetMetadata(c, parsed.Metadata);
        return written;
    }

    private bool WriteIfChanged(Certificate c, ParsedCertificate parsed)
    {
        if (CertificateFileStore.FilesMatch(c, parsed)) return false;
        (c.CertPath, c.KeyPath) = files.Write(c.Id, parsed);
        return true;
    }

    private string? Password(Certificate c)
    {
        if (string.IsNullOrEmpty(c.PfxPasswordProtected)) return null;
        try
        {
            return secrets.Unprotect(c.PfxPasswordProtected);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException or ArgumentException or IOException)
        {
            throw new CertificateImportException("The stored PFX password could not be decrypted (was the database restored from another server?). Re-point the certificate to its PFX with the password.");
        }
    }

    internal static void SetMetadata(Certificate c, CertificateMetadata m)
    {
        c.Subjects = m.Subjects;
        c.Issuer = m.Issuer;
        c.NotBefore = m.NotBefore;
        c.NotAfter = m.NotAfter;
        c.Thumbprint = m.Thumbprint;
    }

    internal static string Describe(Certificate c) => c.Source switch
    {
        CertificateSource.FilePath => $"Source: files {c.CertPath} and {c.KeyPath}",
        CertificateSource.PfxFile => $"Source: PFX file {c.SourcePath}",
        CertificateSource.WindowsStore => $"Source: Windows certificate store {c.StoreLocation}\\{c.StoreName}, " +
                                          (string.IsNullOrEmpty(c.StoreThumbprint) ? $"subject '{c.StoreSubject}'" : $"thumbprint {c.StoreThumbprint}"),
        _ => "Source: uploaded",
    };

    private void Raise(EventSeverity severity, string message, string details, string id)
    {
        try
        {
            services.GetService<IEventSink>()?.Raise(severity, "certificate", message, details, EventKeyPrefix + id, AlertRule);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not raise certificate sync event");
        }
    }
}
