using System.Text.Json;
using System.Text.RegularExpressions;
using CaddyManager.Core.Contracts;

namespace CaddyManager.Platform.Binary;

/// <summary>A module compiled into a Caddy binary.</summary>
public sealed record CaddyModuleInfo(string Name, string Package, bool Standard, string? Version = null);

/// <summary>Parsers for Caddy CLI output and the release / package-registry payloads.</summary>
public static partial class CaddyOutputParser
{
    /// <summary>The standard Caddy distribution package. Anything else is a plugin.</summary>
    public const string StandardPackage = "github.com/caddyserver/caddy/v2";

    [GeneratedRegex(@"^\s*(?<kind>Standard|Non-standard|Unknown) modules:\s*\d+\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SectionFooter();

    [GeneratedRegex(@"^(?<hash>[0-9a-fA-F]{64,128})\s+\*?(?<file>\S+)\s*$")]
    private static partial Regex ChecksumLine();

    /// <summary>
    /// Parses `caddy version` output, e.g. "v2.11.4 h1:XKxk...=" → "v2.11.4".
    /// Custom builds may print "v2.11.4 h1:..." as well; development builds print "(devel)".
    /// Returns null when the output has no recognisable version token.
    /// </summary>
    public static string? ParseVersion(string output)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var token = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            if (CaddyVersion.TryParse(token, out var v)) return v.ToString();
            if (token.StartsWith('(')) return token; // "(devel)"
        }
        return null;
    }

    /// <summary>Parses `caddy list-modules --json` (Caddy ≥ 2.9): [{module_name, module_type, version, package_url}].</summary>
    public static List<CaddyModuleInfo> ParseModulesJson(string json)
    {
        var start = json.IndexOf('[');
        if (start < 0) throw new FormatException("list-modules --json output contains no JSON array.");
        using var doc = JsonDocument.Parse(json[start..]);
        var list = new List<CaddyModuleInfo>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var name = Str(e, "module_name");
            if (string.IsNullOrEmpty(name)) continue;
            var type = Str(e, "module_type") ?? "";
            var pkg = Str(e, "package_url") ?? "";
            list.Add(new CaddyModuleInfo(name, pkg,
                string.Equals(type, "standard", StringComparison.OrdinalIgnoreCase), Str(e, "version")));
        }
        return list;
    }

    /// <summary>
    /// Parses `caddy list-modules --packages` text output. Modules are printed in sections
    /// (standard, non-standard, unknown), each terminated by a footer like "  Standard modules: 132".
    /// A line is "&lt;module&gt; &lt;package&gt;" (older versions may omit the package).
    /// </summary>
    public static List<CaddyModuleInfo> ParseModulesText(string output)
    {
        var list = new List<CaddyModuleInfo>();
        var pending = new List<(string Name, string Package)>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;
            var footer = SectionFooter().Match(line);
            if (footer.Success)
            {
                var standard = footer.Groups["kind"].Value.Equals("Standard", StringComparison.OrdinalIgnoreCase);
                list.AddRange(pending.Select(p => new CaddyModuleInfo(p.Name, p.Package, standard)));
                pending.Clear();
                continue;
            }
            if (char.IsWhiteSpace(line[0])) continue; // any other indented text
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            pending.Add((parts[0], parts.Length > 1 ? parts[1] : ""));
        }
        // No footer after the last section (unexpected format): classify by package path.
        list.AddRange(pending.Select(p => new CaddyModuleInfo(p.Name, p.Package, p.Package == StandardPackage || p.Package == "")));
        return list;
    }

    /// <summary>Distinct non-standard package paths (the installed "plugins").</summary>
    public static List<string> PluginPackages(IEnumerable<CaddyModuleInfo> modules) =>
        modules.Where(m => !m.Standard && !string.IsNullOrEmpty(m.Package))
            .Select(m => m.Package)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Parses a goreleaser checksums file ("&lt;sha512&gt;  &lt;filename&gt;" per line). Keys are file names.</summary>
    public static Dictionary<string, string> ParseChecksums(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var m = ChecksumLine().Match(raw.Trim());
            if (m.Success) map[m.Groups["file"].Value] = m.Groups["hash"].Value.ToLowerInvariant();
        }
        return map;
    }

    /// <summary>Parses a GitHub "release" object (api.github.com/repos/{o}/{r}/releases/latest).</summary>
    public static ReleaseInfo ParseGitHubRelease(string json, int maxNotesLength = 20000)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var tag = Str(r, "tag_name");
        if (string.IsNullOrWhiteSpace(tag)) throw new FormatException("GitHub release response has no tag_name.");
        DateTime? published = r.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String
                              && p.TryGetDateTime(out var dt) ? dt.ToUniversalTime() : null;
        var notes = Str(r, "body");
        if (notes is { Length: > 0 } && notes.Length > maxNotesLength) notes = notes[..maxNotesLength] + "\n\n…";
        return new ReleaseInfo
        {
            Version = CaddyVersion.TryParse(tag, out var v) ? v.ToString() : tag,
            PublishedAt = published,
            Url = Str(r, "html_url") ?? $"https://github.com/caddyserver/caddy/releases/tag/{tag}",
            Notes = notes,
        };
    }

    /// <summary>
    /// Parses https://caddyserver.com/api/packages → { status_code, result: [{ path, repo, downloads, listed, available, modules: [{ name, ... }] }] }.
    /// Unlisted / unavailable packages are skipped.
    /// </summary>
    public static List<PluginPackage> ParsePackageCatalog(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("status_code", out var sc) && sc.ValueKind == JsonValueKind.Number && sc.GetInt32() != 200)
            throw new FormatException($"Package registry returned status_code {sc.GetInt32()}.");
        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
            throw new FormatException("Package registry response has no 'result' array.");

        var list = new List<PluginPackage>();
        foreach (var e in result.EnumerateArray())
        {
            var path = Str(e, "path");
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (e.TryGetProperty("listed", out var listed) && listed.ValueKind == JsonValueKind.False) continue;
            if (e.TryGetProperty("available", out var avail) && avail.ValueKind == JsonValueKind.False) continue;
            long downloads = 0;
            if (e.TryGetProperty("downloads", out var d) && d.ValueKind == JsonValueKind.Number) d.TryGetInt64(out downloads);
            var modules = new List<string>();
            if (e.TryGetProperty("modules", out var mods) && mods.ValueKind == JsonValueKind.Array)
                foreach (var m in mods.EnumerateArray())
                    if (Str(m, "name") is { Length: > 0 } n) modules.Add(n);
            list.Add(new PluginPackage { Path = path, Repo = Str(e, "repo"), Downloads = downloads, Modules = modules });
        }
        return list;
    }

    /// <summary>Strips an optional "@version" suffix from a Go package path.</summary>
    public static string PackageWithoutVersion(string package)
    {
        var at = package.IndexOf('@');
        return (at >= 0 ? package[..at] : package).Trim();
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
