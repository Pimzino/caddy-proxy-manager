using System.Text.RegularExpressions;
using CaddyManager.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CaddyManager.Ops.Logs;

internal static partial class LogEndpoints
{
    private const int DefaultLines = 500;

    // Caddy's rolled files look like "<name>-2026-09-25T10-00-00.000.log" (optionally .gz).
    [GeneratedRegex(@"-\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2}(\.\d+)?$")]
    private static partial Regex RolledSuffix();

    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/logs");

        // caddy.log can quote credentials (a DNS provider's error text or request URL with the API key), and viewers may
        // read it: every line is scrubbed of the configured secrets (Config's ISecretScrubber) before filtering.
        g.MapGet("/caddy", (int? lines, string? q, AppPaths paths, HttpContext http) =>
                Tail(paths.CaddyProcessLog, lines, q, Scrubber(http)))
            .RequireAuthorization(Policies.Viewer);

        g.MapGet("/access", (string? host, int? lines, string? q, AppPaths paths) =>
        {
            var hosts = AccessLogHosts(paths);
            if (string.IsNullOrWhiteSpace(host))
                return Results.Ok(new { lines = Array.Empty<string>(), file = "", hosts });
            var match = hosts.FirstOrDefault(h => string.Equals(h, host.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is null)
                return Results.Ok(new { lines = Array.Empty<string>(), file = "", hosts });
            var file = Path.Combine(paths.AccessLogDir, match + ".log");
            return Results.Ok(new { lines = SafeRead(file, lines, q), file, hosts });
        }).RequireAuthorization(Policies.Viewer);

        g.MapGet("/manager", (int? lines, string? q, AppPaths paths) =>
        {
            var file = LatestManagerLog(paths) ?? Path.Combine(paths.ManagerLogDir, $"manager-{DateTime.Now:yyyyMMdd}.log");
            return Tail(file, lines, q);
        }).RequireAuthorization(Policies.Admin);
    }

    private static IResult Tail(string file, int? lines, string? q, Func<string, string>? transform = null) =>
        Results.Ok(new { lines = SafeRead(file, lines, q, transform), file });

    /// <summary>The Config module's secret scrubber as a line transform (null when that module is absent).</summary>
    private static Func<string, string>? Scrubber(HttpContext http) =>
        http.RequestServices.GetService(typeof(ISecretScrubber)) is ISecretScrubber s ? s.Scrub : null;

    private static List<string> SafeRead(string file, int? lines, string? q, Func<string, string>? transform = null)
    {
        try
        {
            return LogTail.Read(file, lines ?? DefaultLines, q, transform: transform);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [$"[Could not read {file}: {ex.Message}]"];
        }
    }

    /// <summary>Host names with an active access log (file names without ".log"; rolled archives excluded).</summary>
    internal static List<string> AccessLogHosts(AppPaths paths)
    {
        if (!Directory.Exists(paths.AccessLogDir)) return [];
        return Directory.EnumerateFiles(paths.AccessLogDir, "*.log")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n) && !n.StartsWith("._", StringComparison.Ordinal) && !RolledSuffix().IsMatch(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static string? LatestManagerLog(AppPaths paths)
    {
        if (!Directory.Exists(paths.ManagerLogDir)) return null;
        return Directory.EnumerateFiles(paths.ManagerLogDir, "manager-*.log")
            .Where(f => !Path.GetFileName(f).StartsWith("._", StringComparison.Ordinal))
            .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
            .FirstOrDefault();
    }
}
