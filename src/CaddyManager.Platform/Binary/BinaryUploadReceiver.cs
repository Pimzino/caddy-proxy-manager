using System.Security.Cryptography;
using System.Text;
using CaddyManager.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace CaddyManager.Platform.Binary;

/// <summary>A problem with an upload request; <see cref="StatusCode"/> is 400 or 413.</summary>
public sealed class BinaryUploadException(string message, int statusCode = StatusCodes.Status400BadRequest, string field = "file")
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Field { get; } = field;
}

/// <summary>The uploaded file, streamed to disk.</summary>
public sealed record ReceivedUpload(string FilePath, string FileName, long Length, string Sha512, string? ExpectedSha512);

/// <summary>
/// Streams a multipart/form-data upload (field "file" = caddy.exe or a release archive, optional field "sha512") straight
/// into a new directory under the staging directory, hashing it on the way. Nothing is buffered in memory or in %TEMP%.
/// On any failure the partial file is deleted.
/// </summary>
public static class BinaryUploadReceiver
{
    /// <summary>Largest accepted file (Caddy builds with many plugins are ~100 MB; release archives ~20 MB).</summary>
    public const long MaxFileBytes = 200L * 1024 * 1024;
    /// <summary>Request body limit: the file plus room for the multipart framing and the form fields.</summary>
    public const long MaxRequestBytes = MaxFileBytes + 1024 * 1024;

    public static async Task<ReceivedUpload> ReceiveAsync(HttpRequest request, string stagingRoot, long maxFileBytes, CancellationToken ct)
    {
        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
            size.MaxRequestBodySize = maxFileBytes + 1024 * 1024;

        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType)
            || !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            throw new BinaryUploadException("Upload the file as multipart/form-data with a field named 'file' (and optionally 'sha512').");
        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary) || boundary.Length > 200)
            throw new BinaryUploadException("The multipart request has no valid boundary.");

        var dir = Path.Combine(stagingRoot, "upload-" + Entity.NewId());
        string? filePath = null, fileName = null, expected = null, sha = null;
        long length = 0;
        try
        {
            var reader = new MultipartReader(boundary, request.Body) { HeadersCountLimit = 16, HeadersLengthLimit = 16 * 1024 };
            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(ct)) is not null)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var cd) || !cd.IsFormDisposition() && !cd.IsFileDisposition())
                    continue;
                var name = HeaderUtilities.RemoveQuotes(cd.Name).Value ?? "";
                if (cd.IsFileDisposition() && name.Equals("file", StringComparison.OrdinalIgnoreCase))
                {
                    if (filePath is not null) throw new BinaryUploadException("Upload exactly one file.");
                    fileName = SafeFileName(HeaderUtilities.RemoveQuotes(cd.FileNameStar.HasValue ? cd.FileNameStar : cd.FileName).Value);
                    Directory.CreateDirectory(dir);
                    filePath = Path.Combine(dir, fileName);
                    (length, sha) = await CopyAsync(section.Body, filePath, maxFileBytes, ct);
                }
                else if (!cd.IsFileDisposition() && name.Equals("sha512", StringComparison.OrdinalIgnoreCase))
                {
                    expected = await ReadSmallAsync(section.Body, 1024, ct);
                }
                // Any other part is ignored (and skipped by the reader).
            }
        }
        catch (Exception ex) when (ex is BinaryUploadException or IOException or InvalidDataException or BadHttpRequestException or OperationCanceledException)
        {
            TryDelete(dir);
            throw ex switch
            {
                BinaryUploadException => ex,
                BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } =>
                    new BinaryUploadException($"The upload is larger than {maxFileBytes / 1048576} MB.", StatusCodes.Status413PayloadTooLarge),
                InvalidDataException => new BinaryUploadException("The multipart request is malformed: " + ex.Message),
                _ => ex,
            };
        }

        if (filePath is null || fileName is null || sha is null)
        {
            TryDelete(dir);
            throw new BinaryUploadException("No file was uploaded (form field 'file'). Choose caddy.exe or the release .zip.");
        }
        if (length == 0)
        {
            TryDelete(dir);
            throw new BinaryUploadException("The uploaded file is empty.");
        }
        string? normalized;
        try
        {
            normalized = ExecutableFormat.NormalizeSha512(expected);
        }
        catch (ArgumentException ex)
        {
            TryDelete(dir);
            throw new BinaryUploadException(ex.Message, field: "sha512");
        }
        if (normalized is not null && !string.Equals(normalized, sha, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(dir);
            throw new BinaryUploadException(
                $"SHA-512 mismatch: the uploaded file {fileName} has {sha[..16]}…, expected {normalized[..16]}…. " +
                "The file is corrupted or not the one listed in the checksums file; nothing was changed.", field: "sha512");
        }
        return new ReceivedUpload(filePath, fileName, length, sha, normalized);
    }

    private static async Task<(long Length, string Sha512)> CopyAsync(Stream body, string path, long max, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        await using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await body.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > max)
                throw new BinaryUploadException($"The file is larger than {max / 1048576} MB.", StatusCodes.Status413PayloadTooLarge);
            hash.AppendData(buffer, 0, read);
            await fs.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return (total, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static async Task<string> ReadSmallAsync(Stream body, int max, CancellationToken ct)
    {
        using var reader = new StreamReader(body, Encoding.UTF8);
        var buffer = new char[max + 1];
        var n = await reader.ReadBlockAsync(buffer.AsMemory(), ct);
        if (n > max) throw new BinaryUploadException("The 'sha512' field is too long.", field: "sha512");
        return new string(buffer, 0, n);
    }

    /// <summary>Keeps only the file name part with safe characters (it is shown in logs and job titles).</summary>
    internal static string SafeFileName(string? name)
    {
        var n = Path.GetFileName((name ?? "").Replace('\\', '/').Split('/').Last()).Trim();
        var sb = new StringBuilder();
        foreach (var c in n)
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_');
        var s = sb.ToString().Trim('.');
        if (s.Length > 100) s = s[^100..];
        return s.Length == 0 ? "upload.bin" : s;
    }

    /// <summary>Removes upload directories older than <paramref name="age"/> (left behind by a crash or a locked file).</summary>
    public static void CleanStale(string stagingRoot, TimeSpan age)
    {
        if (!Directory.Exists(stagingRoot)) return;
        foreach (var dir in Directory.EnumerateDirectories(stagingRoot, "upload-*"))
            if (DateTime.UtcNow - Directory.GetCreationTimeUtc(dir) > age) TryDelete(dir);
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort (e.g. an antivirus scanner holds the file): CleanStale removes it on a later upload.
        }
    }
}
