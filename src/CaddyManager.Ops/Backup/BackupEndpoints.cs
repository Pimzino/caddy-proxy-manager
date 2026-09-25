using CaddyManager.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Ops.Backup;

internal static class BackupEndpoints
{
    private const long MaxUploadBytes = 512L * 1024 * 1024;

    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/backup").RequireAuthorization(Policies.Admin);

        g.MapGet("/", async (BackupService backups, IAuditLog audit, ILogger<BackupService> logger, CancellationToken ct) =>
        {
            try
            {
                var (stream, name) = await backups.CreateAsync(ct);
                audit.Record("backup", "system", null, name);
                return Results.File(stream, "application/zip", name);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Backup failed");
                return ApiResults.Failed("Backup failed", ex.Message);
            }
        });

        g.MapPost("/restore", async (HttpRequest request, AppPaths paths, IAuditLog audit, ILogger<BackupService> logger,
            CancellationToken ct) =>
        {
            if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeFeature)
                sizeFeature.MaxRequestBodySize = MaxUploadBytes;
            if (!request.HasFormContentType)
                return ApiResults.BadRequest("Upload the backup as multipart/form-data with a field named 'file'.");
            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0)
                return ApiResults.BadRequest("No backup file was uploaded (form field 'file').",
                    new Dictionary<string, string[]> { ["file"] = ["Choose a backup .zip file."] });
            if (file.Length > MaxUploadBytes)
                return ApiResults.BadRequest($"The backup file is larger than {MaxUploadBytes / 1024 / 1024} MB.");

            try
            {
                var tmpDir = Path.Combine(paths.BackupDir, "tmp");
                Directory.CreateDirectory(tmpDir);
                await using var upload = new FileStream(Path.Combine(tmpDir, $"upload-{Guid.NewGuid():N}.zip"), FileMode.CreateNew,
                    FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
                await file.CopyToAsync(upload, ct);
                upload.Position = 0;
                var manifest = RestoreStager.Stage(paths, upload);
                audit.Record("restoreStaged", "system", null, file.FileName,
                    $"Backup from {manifest.Machine} created {manifest.CreatedAt:u} (manager {manifest.ManagerVersion}); applied on next start");
                logger.LogWarning("Backup restore staged from {File} (created {Created:u} on {Machine}); restart the manager service to apply it",
                    file.FileName, manifest.CreatedAt, manifest.Machine);
                return Results.Ok(new
                {
                    restartRequired = true,
                    manifest,
                    message = "The backup was validated and will be applied when the Caddy Proxy Manager service restarts.",
                });
            }
            catch (InvalidDataException ex)
            {
                return ApiResults.BadRequest(ex.Message, new Dictionary<string, string[]> { ["file"] = [ex.Message] });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError(ex, "Staging backup restore failed");
                return ApiResults.Failed("Restore failed", $"The backup could not be staged: {ex.Message}");
            }
        }).DisableAntiforgery()
          .WithMetadata(new UploadLimits(MaxUploadBytes));
    }

    private sealed class UploadLimits(long multipartBodyLengthLimit) : IFormOptionsMetadata
    {
        public bool? BufferBody => null;
        public int? MemoryBufferThreshold => null;
        public long? BufferBodyLengthLimit => null;
        public int? ValueCountLimit => null;
        public int? KeyLengthLimit => null;
        public int? ValueLengthLimit => null;
        public int? MultipartBoundaryLengthLimit => null;
        public int? MultipartHeadersCountLimit => null;
        public int? MultipartHeadersLengthLimit => null;
        public long? MultipartBodyLengthLimit => multipartBodyLengthLimit;
    }
}
