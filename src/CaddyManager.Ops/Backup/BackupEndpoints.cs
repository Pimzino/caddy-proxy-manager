using System.Text.Json;
using System.Text.Json.Nodes;
using CaddyManager.Core;
using CaddyManager.Ops.Settings;
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

            var password = form["password"].ToString();
            var tmpDir = Path.Combine(paths.BackupDir, "tmp");
            try
            {
                Directory.CreateDirectory(tmpDir);
                await using var upload = new FileStream(Path.Combine(tmpDir, $"upload-{Guid.NewGuid():N}.zip"), FileMode.CreateNew,
                    FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
                await file.CopyToAsync(upload, ct);
                upload.Position = 0;

                BackupManifest manifest;
                var encrypted = BackupEncryption.IsEncrypted(upload);
                if (encrypted)
                {
                    // Decrypt into a plain archive, then run the normal validation/staging on it.
                    await using var plain = new FileStream(Path.Combine(tmpDir, $"decrypted-{Guid.NewGuid():N}.zip"), FileMode.CreateNew,
                        FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
                    BackupEncryption.Decrypt(upload, plain, password, RestoreStager.MaxTotalUncompressedBytes, RestoreStager.MaxEntries, ct);
                    plain.Position = 0;
                    manifest = RestoreStager.Stage(paths, plain);
                }
                else manifest = RestoreStager.Stage(paths, upload);

                audit.Record("restoreStaged", "system", null, file.FileName,
                    $"Backup from {manifest.Machine} created {manifest.CreatedAt:u} (manager {manifest.ManagerVersion})" +
                    (encrypted ? ", encrypted" : "") + "; applied on next start");
                logger.LogWarning("Backup restore staged from {File} (created {Created:u} on {Machine}); restart the manager service to apply it",
                    file.FileName, manifest.CreatedAt, manifest.Machine);
                return Results.Ok(new
                {
                    restartRequired = true,
                    manifest,
                    message = "The backup was validated and will be applied when the Caddy Proxy Manager service restarts.",
                });
            }
            catch (BackupPasswordException ex)
            {
                audit.Record("restoreFailed", "system", null, file.FileName, ex.Message);
                return ApiResults.BadRequest(ex.Message, new Dictionary<string, string[]> { ["password"] = [ex.Message] });
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

        MapScheduled(app);
    }

    // ------------------------------------------------------------------ scheduled backups

    private static void MapScheduled(IEndpointRouteBuilder app)
    {
        var settings = app.MapGroup("/api/settings/backup").RequireAuthorization(Policies.Admin);
        settings.MapGet("/", (IStore store, ScheduledBackups runner, TimeProvider time) =>
            Results.Json(ToWire(store.GetSettings<BackupSettings>(), store, runner, time)));

        settings.MapPut("/", (JsonObject body, IStore store, ISecretProtector secrets, AppPaths paths, ScheduledBackups runner,
            IAuditLog audit, TimeProvider time) =>
        {
            var current = store.GetSettings<BackupSettings>();
            BackupSettings next;
            try { next = SettingsWire.Apply(current, body, secrets); }
            catch (SettingsInputException ex) { return ApiResults.BadRequest(ex.Message); }

            next.Directory = string.IsNullOrWhiteSpace(next.Directory) ? null : next.Directory.Trim();
            if (next.Directory is not null && SamePath(next.Directory, paths.BackupDir)) next.Directory = null;

            var v = new Validator();
            v.Require(next.HourLocal is >= 0 and <= 23, "hourLocal", "The hour must be between 0 and 23 (server local time).");
            v.Require(next.Keep is >= 1 and <= 365, "keep", "Keep between 1 and 365 backups.");
            if (body.TryGetPropertyValue("password", out var pw) && pw is JsonValue pv && pv.TryGetValue<string>(out var pwText) &&
                pwText.Length is > 0 and < 12)
                v.Add("password", "The backup password must be at least 12 characters long.");
            if (next.Directory is not null)
            {
                if (!Path.IsPathFullyQualified(next.Directory))
                    v.Add("directory", @"Enter an absolute local path (D:\Backups\CPM) or a UNC path (\\fileserver\backups\cpm).");
                else if (IsInside(next.Directory, paths.DataDir) && !IsInside(next.Directory, paths.BackupDir))
                    v.Add("directory", $"Backups cannot be written inside the data directory {paths.DataDir} (other than {paths.BackupDir}): they would be included in later backups.");
                else if (next.Directory != current.Directory && runner.ProbeWritable(next.Directory) is { } probeError)
                    v.Add("directory", probeError);
            }
            if (!v.IsValid) return v.ToResult();

            store.SaveSettings(next);
            var changes = new List<string>();
            if (current.Enabled != next.Enabled) changes.Add(next.Enabled ? "scheduled backups enabled" : "scheduled backups disabled");
            if (current.Directory != next.Directory) changes.Add($"directory {runner.EffectiveDirectory(next)}");
            if (SettingsWire.TouchesSecret(body, "password"))
                changes.Add(string.IsNullOrEmpty(next.PasswordProtected) ? "encryption password removed" : "encryption password set");
            audit.Record("updated", "settings", "backup", "Backup settings", changes.Count > 0 ? string.Join("; ", changes) : null);
            return Results.Json(ToWire(next, store, runner, time));
        });

        var backups = app.MapGroup("/api/backups").RequireAuthorization(Policies.Admin);
        backups.MapGet("/", (IStore store, ScheduledBackups runner, ILogger<BackupService> logger) =>
        {
            var s = store.GetSettings<BackupSettings>();
            try { return Results.Ok(runner.List(s)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var dir = runner.EffectiveDirectory(s);
                logger.LogWarning(ex, "Listing backups in {Dir} failed", dir);
                return ApiResults.Failed("Backup directory not accessible", ScheduledBackups.Describe(ex, dir));
            }
        });

        backups.MapGet("/{name}", (string name, IStore store, ScheduledBackups runner, IAuditLog audit) =>
        {
            var file = runner.Resolve(store.GetSettings<BackupSettings>(), name);
            if (file is null) return ApiResults.NotFound($"Backup '{name}'");
            audit.Record("downloaded", "backup", null, name);
            var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous);
            return Results.File(stream, "application/zip", name);
        });

        backups.MapPost("/run", async (ScheduledBackups runner, CancellationToken ct) =>
        {
            try
            {
                var name = await runner.RunAsync(scheduled: false, ct);
                return Results.Ok(new { name });
            }
            catch (BackupRunException ex)
            {
                return ApiResults.Failed("Backup failed", ex.Message);
            }
        });
    }

    private static JsonObject ToWire(BackupSettings s, IStore store, ScheduledBackups runner, TimeProvider time)
    {
        var st = store.GetSettings<BackupStatus>();
        var node = SettingsWire.ToWire(s);
        node["directory"] = runner.EffectiveDirectory(s);
        node["defaultDirectory"] = runner.EffectiveDirectory(new BackupSettings());
        node["encryptionNote"] = BackupEncryption.FormatNote;
        var status = JsonSerializer.SerializeToNode(st, JsonDefaults.Api)!.AsObject();
        status.Remove("lastScheduledRunAt");
        foreach (var (k, v) in status.ToList()) node[k] = v?.DeepClone();
        if (ScheduledBackups.Next(s, st, time.GetUtcNow(), time.LocalTimeZone) is { } next) node["nextRunAt"] = next;
        return node;
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private static bool IsInside(string path, string root)
    {
        try
        {
            var p = Path.GetFullPath(path).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var r = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return p.StartsWith(r, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch { return false; }
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
