using CaddyManager.Config.Certificates;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CaddyManager.Config.Endpoints;

public sealed record CertificatePemInput(string? Name, string? CertPem, string? KeyPem);
public sealed record CertificatePathInput(string? Name, string? CertPath, string? KeyPath);
public sealed record CertificateUpdateInput(string? Name, string? Notes);

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
            return await CreateAsync(http, store, files, parsed!, name, CertificateSource.Uploaded, null, null);
        }).RequireAuthorization(Policies.Operator);

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
            return await CreateAsync(http, store, files, parsed, body.Name, CertificateSource.Uploaded, null, null);
        }).RequireAuthorization(Policies.Operator);

        g.MapPost("/path", async (CertificatePathInput? body, HttpContext http, IStore store, CertificateFileStore files) =>
        {
            if (body is null) return ApiResults.BadRequest("certPath and keyPath are required.");
            var certPath = body.CertPath?.Trim() ?? "";
            var keyPath = body.KeyPath?.Trim() ?? "";
            ParsedCertificate parsed;
            try
            {
                parsed = CertificateParser.FromFiles(certPath, keyPath);
            }
            catch (CertificateImportException ex)
            {
                return ApiResults.BadRequest(ex.Message);
            }
            var result = await CreateAsync(http, store, files, parsed, body.Name, CertificateSource.FilePath, certPath, keyPath);
            http.RequestServices.GetService<CertificateWatcher>()?.Poke();
            return result;
        }).RequireAuthorization(Policies.Operator);

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
                    return Results.Ok(new { item = updated, apply });
                });
        }).RequireAuthorization(Policies.Operator);

        g.MapPost("/{id}/replace", async (string id, HttpContext http, IStore store, CertificateFileStore files) =>
        {
            var col = store.Col<Certificate>();
            var existing = col.FindById(id);
            if (existing is null) return ApiResults.NotFound("Certificate");

            // A file-path certificate may be re-pointed with JSON { certPath, keyPath }.
            if (http.Request.HasJsonContentType())
            {
                var input = await http.Request.ReadFromJsonAsync<CertificatePathInput>(http.RequestAborted);
                if (input is null) return ApiResults.BadRequest("certPath and keyPath are required.");
                ParsedCertificate p;
                try
                {
                    p = CertificateParser.FromFiles(input.CertPath?.Trim() ?? "", input.KeyPath?.Trim() ?? "");
                }
                catch (CertificateImportException ex)
                {
                    return ApiResults.BadRequest(ex.Message);
                }
                var repointed = col.FindById(id);
                repointed.Source = CertificateSource.FilePath;
                repointed.CertPath = input.CertPath!.Trim();
                repointed.KeyPath = input.KeyPath!.Trim();
                if (!string.IsNullOrWhiteSpace(input.Name)) repointed.Name = input.Name.Trim();
                SetMetadata(repointed, p.Metadata);
                var r = await CommitReplaceAsync(http, col, existing, repointed, p.Metadata, restoreFiles: null,
                    afterCommit: existing.Source == CertificateSource.Uploaded ? () => files.DeleteFiles(existing) : null);
                http.RequestServices.GetService<CertificateWatcher>()?.Poke();
                return r;
            }

            var (parsed, name, problem) = await ReadUploadAsync(http);
            if (problem is not null) return problem;
            var backup = existing.Source == CertificateSource.Uploaded ? CertificateFileStore.Backup(existing) : null;
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
        }).RequireAuthorization(Policies.Operator);

        g.MapDelete("/{id}", async (string id, HttpContext http, IStore store, CertificateFileStore files) =>
        {
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
        }).RequireAuthorization(Policies.Operator);
    }

    private static void SetMetadata(Certificate c, CertificateMetadata m)
    {
        c.Subjects = m.Subjects;
        c.Issuer = m.Issuer;
        c.NotBefore = m.NotBefore;
        c.NotAfter = m.NotAfter;
        c.Thumbprint = m.Thumbprint;
        c.UpdatedAt = DateTime.UtcNow;
    }

    private static List<string> ExpiryWarnings(CertificateMetadata m)
    {
        var list = new List<string>();
        if (m.NotAfter < DateTime.UtcNow) list.Add($"The certificate expired on {m.NotAfter:yyyy-MM-dd}. Browsers will reject it.");
        else if (m.NotBefore > DateTime.UtcNow) list.Add($"The certificate is not valid before {m.NotBefore:yyyy-MM-dd HH:mm} UTC.");
        return list;
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
                ConfigTransaction.Audit(http, "replaced", "certificate", updated.Id, updated.Name, $"thumbprint {existing.Thumbprint} → {updated.Thumbprint}");
                return Results.Ok(new { item = updated, apply = apply.WithWarnings(ExpiryWarnings(meta)) });
            });
    }

    private static async Task<IResult> CreateAsync(HttpContext http, IStore store, CertificateFileStore files,
        ParsedCertificate parsed, string? name, CertificateSource source, string? certPath, string? keyPath)
    {
        var cert = new Certificate
        {
            Id = Entity.NewId(),
            Name = string.IsNullOrWhiteSpace(name) ? parsed.Metadata.Subjects.FirstOrDefault() ?? "certificate" : name.Trim(),
            Source = source,
        };
        if (cert.Name.Length > 200) cert.Name = cert.Name[..200];
        SetMetadata(cert, parsed.Metadata);

        if (source == CertificateSource.Uploaded)
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
        else
        {
            cert.CertPath = certPath!;
            cert.KeyPath = keyPath!;
        }

        var col = store.Col<Certificate>();
        return await ConfigTransaction.RunAsync(http, $"Certificate added: {cert.Name}",
            persist: () => col.Insert(cert),
            rollback: () =>
            {
                col.Delete(cert.Id);
                if (source == CertificateSource.Uploaded) files.DeleteFolder(cert.Id);
            },
            onSuccess: apply =>
            {
                ConfigTransaction.Audit(http, "created", "certificate", cert.Id, cert.Name,
                    $"{(source == CertificateSource.Uploaded ? "uploaded" : "file path")}; subjects: {string.Join(", ", cert.Subjects)}; expires {cert.NotAfter:yyyy-MM-dd}");
                return Results.Ok(new { item = cert, apply = apply.WithWarnings(ExpiryWarnings(parsed.Metadata)) });
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
