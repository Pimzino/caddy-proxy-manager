using System.Text.Json;
using System.Text.Json.Nodes;
using CaddyManager.Config.Certificates;
using CaddyManager.Config.Validation;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CaddyManager.Config.Endpoints;

public sealed record CertificatePemInput(string? Name, string? CertPem, string? KeyPem);
public sealed record CertificatePathInput(string? Name, string? CertPath, string? KeyPath);
public sealed record CertificatePfxPathInput(string? Name, string? PfxPath, string? PfxPassword);
public sealed record CertificateWindowsStoreInput(string? Name, string? StoreLocation, string? StoreName, string? Thumbprint, string? Subject);
public sealed record CertificateUpdateInput(string? Name, string? Notes);
/// <summary>JSON body of POST /api/certificates/{id}/replace: re-point to PEM files (certPath + keyPath) or to a PFX (pfxPath).</summary>
public sealed record CertificateRepointInput(string? Name, string? CertPath, string? KeyPath, string? PfxPath, string? PfxPassword);

internal static class CertificateEndpoints
{
    private const long MaxUploadBytes = 2 * 1024 * 1024;

    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/certificates").RequireAuthorization(Policies.Viewer);

        g.MapGet("/", async (ICertificateInventory inventory, CancellationToken ct) => Results.Ok(await inventory.ListAsync(ct)));

        g.MapGet("/internal-root", (CertificateInventory inventory) =>
            File.Exists(inventory.InternalRootPath)
                ? Results.File(inventory.InternalRootPath, "application/x-pem-file", "caddy-local-root.crt")
                : ApiResults.NotFound("Caddy's internal root CA (it is created the first time a host uses Internal TLS)"));

        g.MapPost("/upload", async (HttpContext http, IStore store, CertificateFileStore files) =>
        {
            var (parsed, name, problem) = await ReadUploadAsync(http);
            if (problem is not null) return problem;
            return await CreateAsync(http, store, files, parsed!, name, CertificateSource.Uploaded);
        }).RequireAuthorization(Policies.Operator).RejectOnManagedNode();

        g.MapPost("/pem", async (CertificatePemInput? body, HttpContext http, IStore store, CertificateFileStore files) =>
        {
            if (body is null) return ApiResults.BadRequest("certPem and keyPem are required.");
            ParsedCertificate parsed;
            try
            {
                parsed = CertificateParser.FromPem(body.CertPem ?? "", body.KeyPem);
            }
            catch (CertificateImportException ex)
            {
                return ApiResults.BadRequest(ex.Message);
            }
            return await CreateAsync(http, store, files, parsed, body.Name, CertificateSource.Uploaded);
        }).RequireAuthorization(Policies.Operator).RejectOnManagedNode();

        // ---- path-based sources: administrators only (Caddy runs as LocalSystem and can read almost any file)

        g.MapPost("/path", async (CertificatePathInput? body, HttpContext http, IStore store, CertificateFileStore files, AppPaths paths) =>
        {
            if (body is null) return ApiResults.BadRequest("certPath and keyPath are required.");
            var certPath = body.CertPath?.Trim() ?? "";
            var keyPath = body.KeyPath?.Trim() ?? "";
            if (CheckPemPaths(certPath, keyPath, paths, files.StoreRoot) is { } bad) return bad;
            ParsedCertificate parsed;
            try
            {
                parsed = CertificateParser.FromFiles(certPath, keyPath);
            }
            catch (CertificateImportException ex)
            {
                return ApiResults.BadRequest(ex.Message);
            }
            var result = await CreateAsync(http, store, files, parsed, body.Name, CertificateSource.FilePath, c =>
            {
                c.CertPath = certPath;
                c.KeyPath = keyPath;
                c.LastSyncedAt = DateTime.UtcNow;
            });
            http.RequestServices.GetService<CertificateWatcher>()?.Poke();
            return result;
        }).RequireAuthorization(Policies.Admin).RejectOnManagedNode();

        g.MapPost("/pfx-path", async (CertificatePfxPathInput? body, HttpContext http, IStore store, CertificateFileStore files, AppPaths paths, ISecretProtector secrets) =>
        {
            if (body is null) return ApiResults.BadRequest("pfxPath is required.");
            var pfxPath = body.PfxPath?.Trim() ?? "";
            if (PathGuard.CheckCertificateFile(pfxPath, "PFX", PathGuard.PfxFileExtensions, paths, files.StoreRoot) is { } err)
                return Field("pfxPath", err);
            ParsedCertificate parsed;
            try
            {
                parsed = CertificateParser.FromPfxFile(pfxPath, body.PfxPassword);
            }
            catch (CertificateImportException ex)
            {
                return ApiResults.BadRequest(ex.Message);
            }
            var result = await CreateAsync(http, store, files, parsed, body.Name, CertificateSource.PfxFile, c =>
            {
                c.SourcePath = pfxPath;
                c.PfxPasswordProtected = string.IsNullOrEmpty(body.PfxPassword) ? null : secrets.Protect(body.PfxPassword);
                c.LastSyncedAt = DateTime.UtcNow;
            });
            http.RequestServices.GetService<CertificateWatcher>()?.Poke();
            return result;
        }).RequireAuthorization(Policies.Admin).RejectOnManagedNode();

        g.MapGet("/windows-store", (string? location, string? store, IWindowsCertificateSource windows) =>
        {
            var loc = WindowsStoreNames.NormalizeLocation(location);
            var name = WindowsStoreNames.NormalizeStoreName(store);
            if (loc is null) return Field("location", "Store location must be LocalMachine or CurrentUser.");
            if (name is null) return Field("store", "Store name may contain letters, digits, spaces, '.', '_' and '-' (e.g. My or WebHosting).");
            try
            {
                return Results.Ok(windows.List(loc, name));
            }
            catch (CertificateImportException ex)
            {
                return ApiResults.BadRequest(ex.Message);
            }
        }).RequireAuthorization(Policies.Admin);

        g.MapPost("/windows-store", async (CertificateWindowsStoreInput? body, HttpContext http, IStore store, CertificateFileStore files, IWindowsCertificateSource windows) =>
        {
            if (body is null) return ApiResults.BadRequest("A thumbprint or a subject is required.");
            var loc = WindowsStoreNames.NormalizeLocation(body.StoreLocation);
            var storeName = WindowsStoreNames.NormalizeStoreName(body.StoreName);
            var hasThumb = !string.IsNullOrWhiteSpace(body.Thumbprint);
            var hasSubject = !string.IsNullOrWhiteSpace(body.Subject);
            var v = new Validator();
            if (loc is null) v.Add("storeLocation", "Store location must be LocalMachine or CurrentUser.");
            if (storeName is null) v.Add("storeName", "Store name may contain letters, digits, spaces, '.', '_' and '-' (e.g. My or WebHosting).");
            if (hasThumb == hasSubject) v.Add("thumbprint", "Specify exactly one of thumbprint (pin one certificate) or subject (follow renewals).");
            if (hasThumb && WindowsStoreNames.NormalizeThumbprint(body.Thumbprint).Length != 40)
                v.Add("thumbprint", "A thumbprint is 40 hexadecimal characters (SHA-1).");
            if (!v.IsValid) return v.ToResult();
            if (!windows.IsSupported)
                return ApiResults.BadRequest("The Windows certificate store is only available when Caddy Proxy Manager runs on Windows.");

            ParsedCertificate parsed;
            var where = $"{loc}\\{storeName}";
            try
            {
                var chosen = WindowsStoreSelector.Select(windows.List(loc!, storeName!), body.Thumbprint, body.Subject, DateTime.UtcNow, where);
                parsed = windows.Export(loc!, storeName!, chosen.Thumbprint);
            }
            catch (CertificateImportException ex)
            {
                return ApiResults.BadRequest(ex.Message);
            }
            return await CreateAsync(http, store, files, parsed, body.Name, CertificateSource.WindowsStore, c =>
            {
                c.StoreLocation = loc!;
                c.StoreName = storeName!;
                c.StoreThumbprint = hasThumb ? WindowsStoreNames.NormalizeThumbprint(body.Thumbprint) : null;
                c.StoreSubject = hasSubject ? body.Subject!.Trim() : null;
                c.LastSyncedAt = DateTime.UtcNow;
            });
        }).RequireAuthorization(Policies.Admin).RejectOnManagedNode();

        // ---- maintenance

        g.MapPut("/{id}", async (string id, CertificateUpdateInput? body, HttpContext http, IStore store) =>
        {
            if (body is null) return ApiResults.BadRequest("name is required.");
            var col = store.Col<Certificate>();
            var existing = col.FindById(id);
            if (existing is null) return ApiResults.NotFound("Certificate");
            var name = body.Name?.Trim() ?? "";
            if (name.Length == 0 || name.Length > 200)
                return new Validator().Require(false, "name", "Name is required (max 200 characters).").ToResult();
            var updated = col.FindById(id);
            updated.Name = name;
            updated.Notes = string.IsNullOrWhiteSpace(body.Notes) ? null : body.Notes.Trim();
            updated.UpdatedAt = DateTime.UtcNow;
            return await ConfigTransaction.RunAsync(http, $"Certificate updated: {name}",
                persist: () => col.Update(updated),
                rollback: () => col.Upsert(existing),
                onSuccess: apply =>
                {
                    ConfigTransaction.Audit(http, "updated", "certificate", id, name);
                    return Results.Ok(new { item = ToWire(updated), apply });
                });
        }).RequireAuthorization(Policies.Operator).RejectOnManagedNode();

        g.MapPost("/{id}/replace", async (string id, HttpContext http, IStore store, CertificateFileStore files, AppPaths paths, ISecretProtector secrets, CertificateSyncService sync) =>
        {
            using var exclusive = await sync.AcquireAsync(http.RequestAborted);
            var col = store.Col<Certificate>();
            var existing = col.FindById(id);
            if (existing is null) return ApiResults.NotFound("Certificate");

            // Re-point to files on disk / a share with JSON — administrators only.
            if (http.Request.HasJsonContentType())
            {
                if (!await EndpointSecurity.IsAdminAsync(http))
                    return EndpointSecurity.Forbidden("Only administrators can point a certificate at files on the server or a share. Upload the certificate instead.");
                CertificateRepointInput? input;
                try
                {
                    input = await http.Request.ReadFromJsonAsync<CertificateRepointInput>(http.RequestAborted);
                }
                catch (JsonException ex)
                {
                    return ApiResults.BadRequest("The request body could not be read: " + ex.Message);
                }
                if (input is null) return ApiResults.BadRequest("certPath and keyPath (or pfxPath) are required.");
                return string.IsNullOrWhiteSpace(input.PfxPath)
                    ? await RepointToFilesAsync(http, col, files, paths, existing, input)
                    : await RepointToPfxAsync(http, col, files, paths, secrets, existing, input);
            }

            var (parsed, name, problem) = await ReadUploadAsync(http);
            if (problem is not null) return problem;
            var backup = CertificateFileStore.IsStoreManaged(existing.Source) ? CertificateFileStore.Backup(existing) : null;
            (string CertPath, string KeyPath) written;
            try
            {
                written = files.Write(id, parsed!);
            }
            catch (CertificateImportException ex)
            {
                return ApiResults.Failed("Certificate store not writable", ex.Message);
            }
            var updated = col.FindById(id);
            ClearSource(updated);
            updated.Source = CertificateSource.Uploaded;
            updated.CertPath = written.CertPath;
            updated.KeyPath = written.KeyPath;
            if (!string.IsNullOrWhiteSpace(name)) updated.Name = name.Trim();
            SetMetadata(updated, parsed!.Metadata);
            return await CommitReplaceAsync(http, col, existing, updated, parsed.Metadata, restoreFiles: () =>
            {
                if (backup is not null) files.Restore(backup);
                else files.DeleteFolder(id);
            }, afterCommit: null);
        }).RequireAuthorization(Policies.Operator).RejectOnManagedNode();

        g.MapPost("/{id}/sync", async (string id, HttpContext http, IStore store, CertificateSyncService sync, ICaddyConfigService config) =>
        {
            var existing = store.Col<Certificate>().FindById(id);
            if (existing is null) return ApiResults.NotFound("Certificate");
            if (existing.Source == CertificateSource.Uploaded)
                return ApiResults.BadRequest("Uploaded certificates have no external source to synchronise. Use Replace to upload a renewed certificate.");

            var r = await sync.SyncAsync(id, http.RequestAborted);
            if (r is null) return ApiResults.NotFound("Certificate");
            if (!r.Success)
            {
                ConfigTransaction.Audit(http, "synced", "certificate", id, existing.Name, "failed: " + r.Error);
                return ApiResults.Failed("Certificate synchronisation failed", r.Error!);
            }

            var reload = existing.Source == CertificateSource.FilePath ? r.ThumbprintChanged : r.FilesWritten;
            var used = store.Col<SiteHost>().FindAll().Any(h => h.Enabled && h.Tls == TlsMode.Custom && h.CertificateId == id);
            var details = r.ThumbprintChanged ? $"thumbprint {existing.Thumbprint} → {r.Certificate.Thumbprint}" : "unchanged";
            if (!reload || !used)
            {
                ConfigTransaction.Audit(http, "synced", "certificate", id, r.Certificate.Name, details);
                return Results.Ok(new { item = ToWire(r.Certificate), apply = new ApplyResult { Success = true }, changed = r.ThumbprintChanged });
            }
            return await ConfigTransaction.RunAsync(http, $"Certificate synchronised: {r.Certificate.Name}",
                persist: () => { },
                rollback: () => { },
                onSuccess: apply =>
                {
                    ConfigTransaction.Audit(http, "synced", "certificate", id, r.Certificate.Name, details);
                    return Results.Ok(new { item = ToWire(r.Certificate), apply, changed = r.ThumbprintChanged });
                });
        }).RequireAuthorization(Policies.Operator).RejectOnManagedNode();

        g.MapDelete("/{id}", async (string id, HttpContext http, IStore store, CertificateFileStore files, CertificateSyncService sync) =>
        {
            using var exclusive = await sync.AcquireAsync(http.RequestAborted);
            var col = store.Col<Certificate>();
            var existing = col.FindById(id);
            if (existing is null) return ApiResults.NotFound("Certificate");
            var users = store.Col<SiteHost>().FindAll().Where(h => h.Tls == TlsMode.Custom && h.CertificateId == id).ToList();
            if (users.Count > 0)
                return ApiResults.Conflict($"The certificate '{existing.Name}' is used by {users.Count} host(s): {string.Join(", ", users.Select(h => h.Domains.FirstOrDefault() ?? h.Id))}. Change their TLS settings first.");
            return await ConfigTransaction.RunAsync(http, $"Certificate deleted: {existing.Name}",
                persist: () => col.Delete(id),
                rollback: () => col.Upsert(existing),
                onSuccess: apply =>
                {
                    files.DeleteFiles(existing);
                    ConfigTransaction.Audit(http, "deleted", "certificate", id, existing.Name);
                    return Results.Ok(new { apply });
                });
        }).RequireAuthorization(Policies.Operator).RejectOnManagedNode();
    }

    // ------------------------------------------------------------------ wire shape

    /// <summary>The certificate as returned by the API: the protected PFX password is replaced by hasPfxPassword.</summary>
    internal static JsonObject ToWire(Certificate c)
    {
        var node = (JsonObject)JsonSerializer.SerializeToNode(c, JsonDefaults.Api)!;
        node.Remove("pfxPasswordProtected");
        node["hasPfxPassword"] = !string.IsNullOrEmpty(c.PfxPasswordProtected);
        if (c.Source != CertificateSource.WindowsStore)
        {
            node.Remove("storeLocation");
            node.Remove("storeName");
        }
        return node;
    }

    private static IResult Field(string field, string message) =>
        new Validator().Require(false, field, message).ToResult(message);

    private static IResult? CheckPemPaths(string certPath, string keyPath, AppPaths paths, string storeRoot)
    {
        var v = new Validator();
        if (PathGuard.CheckCertificateFile(certPath, "certificate", PathGuard.CertificateFileExtensions, paths, storeRoot) is { } c) v.Add("certPath", c);
        if (PathGuard.CheckCertificateFile(keyPath, "private key", PathGuard.CertificateFileExtensions, paths, storeRoot) is { } k) v.Add("keyPath", k);
        return v.IsValid ? null : v.ToResult();
    }

    private static void SetMetadata(Certificate c, CertificateMetadata m)
    {
        CertificateSyncService.SetMetadata(c, m);
        c.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Forget everything about the previous external source.</summary>
    private static void ClearSource(Certificate c)
    {
        c.SourcePath = null;
        c.PfxPasswordProtected = null;
        c.StoreLocation = WindowsStoreNames.LocalMachine;
        c.StoreName = WindowsStoreNames.DefaultStore;
        c.StoreThumbprint = null;
        c.StoreSubject = null;
        c.LastSyncError = null;
        c.LastSyncedAt = null;
    }

    private static List<string> ExpiryWarnings(CertificateMetadata m)
    {
        var list = new List<string>();
        if (m.NotAfter < DateTime.UtcNow) list.Add($"The certificate expired on {m.NotAfter:yyyy-MM-dd}. Browsers will reject it.");
        else if (m.NotBefore > DateTime.UtcNow) list.Add($"The certificate is not valid before {m.NotBefore:yyyy-MM-dd HH:mm} UTC.");
        return list;
    }

    private static async Task<IResult> RepointToFilesAsync(HttpContext http, LiteDB.ILiteCollection<Certificate> col, CertificateFileStore files,
        AppPaths paths, Certificate existing, CertificateRepointInput input)
    {
        var certPath = input.CertPath?.Trim() ?? "";
        var keyPath = input.KeyPath?.Trim() ?? "";
        if (CheckPemPaths(certPath, keyPath, paths, files.StoreRoot) is { } bad) return bad;
        ParsedCertificate p;
        try
        {
            p = CertificateParser.FromFiles(certPath, keyPath);
        }
        catch (CertificateImportException ex)
        {
            return ApiResults.BadRequest(ex.Message);
        }
        var repointed = col.FindById(existing.Id);
        ClearSource(repointed);
        repointed.Source = CertificateSource.FilePath;
        repointed.CertPath = certPath;
        repointed.KeyPath = keyPath;
        repointed.LastSyncedAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(input.Name)) repointed.Name = input.Name.Trim();
        SetMetadata(repointed, p.Metadata);

        // The old store folder is no longer needed — unless the new files live in it.
        var oldDir = CertificateFileStore.IsStoreManaged(existing.Source) ? Path.GetDirectoryName(existing.CertPath) : null;
        var keepOld = oldDir is null || IsInside(certPath, oldDir) || IsInside(keyPath, oldDir);
        var r = await CommitReplaceAsync(http, col, existing, repointed, p.Metadata, restoreFiles: null,
            afterCommit: keepOld ? null : () => files.DeleteFiles(existing));
        http.RequestServices.GetService<CertificateWatcher>()?.Poke();
        return r;
    }

    private static async Task<IResult> RepointToPfxAsync(HttpContext http, LiteDB.ILiteCollection<Certificate> col, CertificateFileStore files,
        AppPaths paths, ISecretProtector secrets, Certificate existing, CertificateRepointInput input)
    {
        var pfxPath = input.PfxPath!.Trim();
        if (PathGuard.CheckCertificateFile(pfxPath, "PFX", PathGuard.PfxFileExtensions, paths, files.StoreRoot) is { } err)
            return Field("pfxPath", err);
        // pfxPassword: absent/null keeps the stored password (PFX sources), "" = none, other = set.
        string? password;
        string? protectedPassword;
        if (input.PfxPassword is null && existing.Source == CertificateSource.PfxFile && !string.IsNullOrEmpty(existing.PfxPasswordProtected))
        {
            try
            {
                password = secrets.Unprotect(existing.PfxPasswordProtected);
            }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException or ArgumentException or IOException)
            {
                return ApiResults.BadRequest("The stored PFX password could not be decrypted; send pfxPassword again.");
            }
            protectedPassword = existing.PfxPasswordProtected;
        }
        else
        {
            password = input.PfxPassword ?? "";
            protectedPassword = string.IsNullOrEmpty(password) ? null : secrets.Protect(password);
        }

        ParsedCertificate parsed;
        try
        {
            parsed = CertificateParser.FromPfxFile(pfxPath, password);
        }
        catch (CertificateImportException ex)
        {
            return ApiResults.BadRequest(ex.Message);
        }
        var backup = CertificateFileStore.IsStoreManaged(existing.Source) ? CertificateFileStore.Backup(existing) : null;
        (string CertPath, string KeyPath) written;
        try
        {
            written = files.Write(existing.Id, parsed);
        }
        catch (CertificateImportException ex)
        {
            return ApiResults.Failed("Certificate store not writable", ex.Message);
        }
        var repointed = col.FindById(existing.Id);
        ClearSource(repointed);
        repointed.Source = CertificateSource.PfxFile;
        repointed.SourcePath = pfxPath;
        repointed.PfxPasswordProtected = protectedPassword;
        repointed.CertPath = written.CertPath;
        repointed.KeyPath = written.KeyPath;
        repointed.LastSyncedAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(input.Name)) repointed.Name = input.Name.Trim();
        SetMetadata(repointed, parsed.Metadata);
        var r = await CommitReplaceAsync(http, col, existing, repointed, parsed.Metadata, restoreFiles: () =>
        {
            if (backup is not null) files.Restore(backup);
            else files.DeleteFolder(existing.Id);
        }, afterCommit: null);
        http.RequestServices.GetService<CertificateWatcher>()?.Poke();
        return r;
    }

    private static bool IsInside(string file, string dir)
    {
        var d = dir.TrimEnd('\\', '/');
        return file.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               file.StartsWith(d + '/', StringComparison.OrdinalIgnoreCase) || file.StartsWith(d + '\\', StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<IResult> CommitReplaceAsync(HttpContext http, LiteDB.ILiteCollection<Certificate> col,
        Certificate existing, Certificate updated, CertificateMetadata meta, Action? restoreFiles, Action? afterCommit)
    {
        return await ConfigTransaction.RunAsync(http, $"Certificate replaced: {updated.Name}",
            persist: () => col.Update(updated),
            rollback: () =>
            {
                restoreFiles?.Invoke();
                col.Upsert(existing);
            },
            onSuccess: apply =>
            {
                afterCommit?.Invoke();
                ConfigTransaction.Audit(http, "replaced", "certificate", updated.Id, updated.Name,
                    $"{CertificateInventory.SourceName(updated.Source)}; thumbprint {existing.Thumbprint} → {updated.Thumbprint}");
                return Results.Ok(new { item = ToWire(updated), apply = apply.WithWarnings(ExpiryWarnings(meta)) });
            });
    }

    /// <summary>Creates a certificate. Store-managed sources (uploaded, PFX file, Windows store) get their PEM files written to the store.</summary>
    private static async Task<IResult> CreateAsync(HttpContext http, IStore store, CertificateFileStore files,
        ParsedCertificate parsed, string? name, CertificateSource source, Action<Certificate>? configure = null)
    {
        var cert = new Certificate
        {
            Id = Entity.NewId(),
            Name = string.IsNullOrWhiteSpace(name) ? parsed.Metadata.Subjects.FirstOrDefault() ?? "certificate" : name.Trim(),
            Source = source,
        };
        if (cert.Name.Length > 200) cert.Name = cert.Name[..200];
        SetMetadata(cert, parsed.Metadata);
        configure?.Invoke(cert);

        var storeManaged = CertificateFileStore.IsStoreManaged(source);
        if (storeManaged)
        {
            try
            {
                (cert.CertPath, cert.KeyPath) = files.Write(cert.Id, parsed);
            }
            catch (CertificateImportException ex)
            {
                return ApiResults.Failed("Certificate store not writable", ex.Message);
            }
        }

        var col = store.Col<Certificate>();
        return await ConfigTransaction.RunAsync(http, $"Certificate added: {cert.Name}",
            persist: () => col.Insert(cert),
            rollback: () =>
            {
                col.Delete(cert.Id);
                if (storeManaged) files.DeleteFolder(cert.Id);
            },
            onSuccess: apply =>
            {
                ConfigTransaction.Audit(http, "created", "certificate", cert.Id, cert.Name,
                    $"{CertificateInventory.SourceName(source)}; {CertificateSyncService.Describe(cert)}; subjects: {string.Join(", ", cert.Subjects)}; expires {cert.NotAfter:yyyy-MM-dd}");
                return Results.Ok(new { item = ToWire(cert), apply = apply.WithWarnings(ExpiryWarnings(parsed.Metadata)) });
            });
    }

    /// <summary>Reads multipart: name + (certFile & keyFile) or (pfxFile & pfxPassword).</summary>
    private static async Task<(ParsedCertificate? Parsed, string? Name, IResult? Problem)> ReadUploadAsync(HttpContext http)
    {
        if (!http.Request.HasFormContentType)
            return (null, null, ApiResults.BadRequest("Send multipart/form-data with 'name' and either 'certFile' + 'keyFile' or 'pfxFile' + 'pfxPassword'."));
        IFormCollection form;
        try
        {
            form = await http.Request.ReadFormAsync(http.RequestAborted);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or BadHttpRequestException)
        {
            return (null, null, ApiResults.BadRequest("The upload could not be read: " + ex.Message));
        }

        var name = form["name"].ToString();
        var pfx = form.Files.GetFile("pfxFile");
        var certFile = form.Files.GetFile("certFile");
        var keyFile = form.Files.GetFile("keyFile");
        try
        {
            if (pfx is not null)
            {
                var bytes = await ReadBytesAsync(pfx, http.RequestAborted);
                var password = form["pfxPassword"].ToString();
                return (CertificateParser.FromPfx(bytes, password), name, null);
            }
            if (certFile is not null)
            {
                var certText = System.Text.Encoding.UTF8.GetString(await ReadBytesAsync(certFile, http.RequestAborted));
                var keyText = keyFile is null ? null : System.Text.Encoding.UTF8.GetString(await ReadBytesAsync(keyFile, http.RequestAborted));
                return (CertificateParser.FromPem(certText, keyText), name, null);
            }
        }
        catch (CertificateImportException ex)
        {
            return (null, null, ApiResults.BadRequest(ex.Message));
        }
        return (null, null, new Validator()
            .Require(false, "certFile", "Upload either a PEM certificate + key ('certFile' and 'keyFile') or a PFX ('pfxFile' with 'pfxPassword').")
            .ToResult());
    }

    private static async Task<byte[]> ReadBytesAsync(IFormFile file, CancellationToken ct)
    {
        if (file.Length > MaxUploadBytes) throw new CertificateImportException($"'{file.FileName}' is too large (max 2 MB).");
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        return ms.ToArray();
    }
}
