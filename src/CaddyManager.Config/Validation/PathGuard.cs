using System.Text.RegularExpressions;
using CaddyManager.Core;

namespace CaddyManager.Config.Validation;

/// <summary>A rejected path: 400 (not allowed for anyone) or 403 (administrators only).</summary>
public sealed record PathProblem(int Status, string Message);

/// <summary>
/// Guards file system paths an operator/administrator can make Caddy (running as LocalSystem) read: static site roots
/// and certificate files referenced by path. Paths are compared lexically in canonical form (Windows and Unix syntax
/// are understood on every OS) and again after resolving symbolic links / junctions on the local machine.
/// </summary>
public static partial class PathGuard
{
    public static readonly string[] CertificateFileExtensions = [".pem", ".crt", ".cer", ".key"];
    public static readonly string[] PfxFileExtensions = [".pfx", ".p12"];

    [GeneratedRegex(@"^[A-Za-z]:[\\/]", RegexOptions.CultureInvariant)]
    private static partial Regex DriveRegex();

    [GeneratedRegex(@"~\d", RegexOptions.CultureInvariant)]
    private static partial Regex ShortNameRegex();

    private enum Style { Windows, Unc, Unix }

    private sealed record Canon(Style Style, string Value, int Depth, string? UncShare);

    // ------------------------------------------------------------------ static roots

    /// <summary>
    /// Static site root: never the data folder, Caddy storage, the certificate store, the program folder, %WINDIR%,
    /// %ProgramFiles% (or a folder inside or above any of them) nor a drive root — 400 for everyone. UNC paths are
    /// reserved for administrators (403 for operators); administrative shares (C$) are refused for everyone.
    /// </summary>
    public static PathProblem? CheckStaticRoot(string? root, AppPaths paths, string certificateStore, bool isAdmin, string? sharedStorage = null)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        if (!TryCanonical(root, out var canon, out var error)) return new PathProblem(400, $"Root folder: {error}");
        if (canon.Depth == 0)
            return new PathProblem(400, $"The root folder '{root.Trim()}' is a drive root. Serving a whole drive would expose every file on it; choose a dedicated folder such as D:\\www\\example.");
        if (canon.Style == Style.Unc && canon.UncShare is { } share && share.EndsWith('$'))
            return new PathProblem(400, $"The root folder '{root.Trim()}' is on the administrative share '{share}'. Use a dedicated share for web content.");

        // Also for UNC roots: the shared storage folder and the certificate store are often on a share.
        foreach (var (dir, label) in ProtectedFolders(paths, certificateStore, sharedStorage))
        {
            var relation = Relation(root, dir);
            if (relation is null) continue;
            return new PathProblem(400,
                $"The root folder '{root.Trim()}' is {relation} {label} ({dir}). Serving it would expose files the web server must never publish; choose a dedicated folder such as D:\\www\\example.");
        }
        if (canon.Style == Style.Unc && !isAdmin)
            return new PathProblem(403, "Only administrators can serve a static site from a network share (UNC path).");
        return null;
    }

    private static IEnumerable<(string Dir, string Label)> ProtectedFolders(AppPaths paths, string certificateStore, string? sharedStorage = null)
    {
        yield return (paths.DataDir, "the Caddy Proxy Manager data folder");
        yield return (paths.CaddyStorageDir, "Caddy's storage folder (certificates and keys)");
        if (!string.IsNullOrWhiteSpace(certificateStore)) yield return (certificateStore, "the certificate store");
        if (!string.IsNullOrWhiteSpace(sharedStorage)) yield return (sharedStorage, "the shared Caddy storage folder (certificates and keys)");
        if (!string.IsNullOrWhiteSpace(paths.InstallDir)) yield return (paths.InstallDir, "the Caddy Proxy Manager program folder");

        var windows = new List<string?>
        {
            Environment.GetEnvironmentVariable("WINDIR"),
            Environment.GetEnvironmentVariable("SystemRoot"),
            SafeFolder(Environment.SpecialFolder.Windows),
            @"C:\Windows",
        };
        foreach (var w in windows.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            yield return (w!, "the Windows folder");

        var programFiles = new List<string?>
        {
            Environment.GetEnvironmentVariable("ProgramFiles"),
            Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
            Environment.GetEnvironmentVariable("ProgramW6432"),
            SafeFolder(Environment.SpecialFolder.ProgramFiles),
            SafeFolder(Environment.SpecialFolder.ProgramFilesX86),
            @"C:\Program Files",
            @"C:\Program Files (x86)",
        };
        foreach (var p in programFiles.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            yield return (p!, "the Program Files folder");
    }

    private static string? SafeFolder(Environment.SpecialFolder f)
    {
        try
        {
            var v = Environment.GetFolderPath(f);
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }

    /// <summary>"the same as" / "inside" / "a parent of" when the two folders overlap (lexically or after resolving links).</summary>
    private static string? Relation(string candidate, string protectedDir)
    {
        var candidates = Forms(candidate);
        var protects = Forms(protectedDir);
        foreach (var c in candidates)
        {
            foreach (var p in protects)
            {
                if (c.Style != p.Style) continue;
                if (c.Value == p.Value) return "the same as";
                if (IsWithin(c, p)) return "inside";
                if (IsWithin(p, c)) return "a parent of";
            }
        }
        return null;
    }

    // ------------------------------------------------------------------ certificate files

    /// <summary>
    /// A certificate/key/PFX file referenced by path: absolute, one of the allowed extensions, and never inside the
    /// data folder except the configured certificate store. Returns an error message or null.
    /// </summary>
    public static string? CheckCertificateFile(string? path, string what, IReadOnlyCollection<string> allowedExtensions, AppPaths paths, string certificateStore)
    {
        if (string.IsNullOrWhiteSpace(path)) return $"The {what} path is required.";
        if (!TryCanonical(path, out var canon, out var error)) return $"The {what} path is not accepted: {error}";
        if (canon.Depth == 0) return $"The {what} path must point at a file.";
        var name = path.Trim().Replace('\\', '/');
        var ext = Path.GetExtension(name[(name.LastIndexOf('/') + 1)..]).ToLowerInvariant();
        if (!allowedExtensions.Contains(ext))
            return $"The {what} file must have one of these extensions: {string.Join(" ", allowedExtensions)}.";
        if (canon.Style == Style.Unc) return null;

        var inData = Relation(path, paths.DataDir) is "inside" or "the same as";
        var inStore = !string.IsNullOrWhiteSpace(certificateStore) && Relation(path, certificateStore) is "inside";
        if (inData && !inStore)
            return $"The {what} file is inside the Caddy Proxy Manager data folder ({paths.DataDir}). Only files in the certificate store may be referenced there.";
        return null;
    }

    // ------------------------------------------------------------------ canonical forms

    private static List<Canon> Forms(string path)
    {
        var list = new List<Canon>();
        if (TryCanonical(path, out var lex, out _)) list.Add(lex);
        // UNC paths are compared lexically only: probing a share for links can block for the SMB timeout when it is
        // unreachable, and this also runs during config generation.
        if (lex.Style == Style.Unc && list.Count > 0) return list;
        var resolved = ResolveLinks(path.Trim());
        if (resolved is not null && TryCanonical(resolved, out var real, out _) && !list.Contains(real)) list.Add(real);
        return list;
    }

    private static bool IsWithin(Canon child, Canon parent)
    {
        if (child.Style != parent.Style || child.Depth <= parent.Depth) return false;
        var sep = parent.Style == Style.Unix ? '/' : '\\';
        var prefix = parent.Value.EndsWith(sep) ? parent.Value : parent.Value + sep;
        return child.Value.StartsWith(prefix, StringComparison.Ordinal);
    }

    /// <summary>Lexical canonical form (lower case, one separator style, no '.' segments). Rejects '..', device paths, streams and 8.3 names.</summary>
    private static bool TryCanonical(string raw, out Canon canon, out string? error)
    {
        canon = new Canon(Style.Unix, "", 0, null);
        error = null;
        var p = raw.Trim();
        if (p.Length == 0) { error = "the path is empty."; return false; }
        if (p.Any(char.IsControl)) { error = "the path contains control characters."; return false; }
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            p.StartsWith("//?/", StringComparison.Ordinal) || p.StartsWith("//./", StringComparison.Ordinal) || p.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            error = @"device paths (\\?\ or \\.\) are not supported; use a normal drive letter or UNC path.";
            return false;
        }

        Style style;
        string prefix;
        string rest;
        string? share = null;
        if (p.StartsWith(@"\\", StringComparison.Ordinal) || p.StartsWith("//", StringComparison.Ordinal))
        {
            style = Style.Unc;
            var parts = p[2..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) { error = @"a UNC path needs a server and a share (\\server\share\folder)."; return false; }
            share = parts[1];
            prefix = @"\\" + parts[0].ToLowerInvariant() + @"\" + parts[1].ToLowerInvariant();
            rest = string.Join('\\', parts.Skip(2));
        }
        else if (DriveRegex().IsMatch(p))
        {
            style = Style.Windows;
            prefix = char.ToLowerInvariant(p[0]) + @":\";
            rest = p[3..];
        }
        else if (p.StartsWith('/'))
        {
            style = Style.Unix;
            prefix = "/";
            rest = p[1..];
        }
        else
        {
            error = "the path must be absolute (e.g. D:\\www\\site or \\\\server\\share\\site).";
            return false;
        }

        var segments = new List<string>();
        foreach (var rawSeg in rest.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            var seg = rawSeg;
            if (seg == ".") continue;
            if (seg == "..") { error = "'..' segments are not allowed."; return false; }
            if (style != Style.Unix)
            {
                if (seg.Contains(':')) { error = "':' is only allowed after the drive letter (alternate data streams are not supported)."; return false; }
                if (ShortNameRegex().IsMatch(seg)) { error = "8.3 short names (e.g. PROGRA~1) are not accepted; use the full folder name."; return false; }
                // Windows ignores trailing dots and spaces ("Windows." is "Windows").
                seg = seg.TrimEnd('.', ' ');
                if (seg.Length == 0) { error = "segments consisting only of dots or spaces are not allowed."; return false; }
            }
            segments.Add(seg.ToLowerInvariant());
        }

        var sep = style == Style.Unix ? "/" : "\\";
        var value = style switch
        {
            Style.Unc => segments.Count == 0 ? prefix : prefix + sep + string.Join(sep, segments),
            _ => prefix + string.Join(sep, segments),
        };
        var depth = style == Style.Unc ? segments.Count + 1 : segments.Count;
        canon = new Canon(style, value, depth, share);
        return true;
    }

    /// <summary>Resolves symbolic links / junctions along a local path. Null when the path is not native to this OS or cannot be inspected.</summary>
    internal static string? ResolveLinks(string native)
    {
        try
        {
            if (!Path.IsPathFullyQualified(native)) return null;
            var full = Path.GetFullPath(native);
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) return null;
            var parts = full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            for (var i = 0; i < parts.Length; i++)
            {
                var next = Path.Combine(current, parts[i]);
                FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
                if (!info.Exists) return Path.Combine([next, .. parts.Skip(i + 1)]);
                if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                    next = target.FullName;
                current = next;
            }
            return current;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
