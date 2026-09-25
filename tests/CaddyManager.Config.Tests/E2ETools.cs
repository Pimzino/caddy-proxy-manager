using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace CaddyManager.Config.Tests;

/// <summary>
/// External tools of the Round 3 end-to-end tests, kept in the repository's gitignored .dev/bin (CI downloads them first
/// with .github/scripts/get-e2e-tools.ps1; on a developer machine this class downloads them on first use):
/// - Pebble (Let's Encrypt's test ACME CA), a pinned release verified against the SHA-256 digest GitHub publishes for the asset;
/// - a Caddy build of the tested version with github.com/caddy-dns/rfc2136 and github.com/caddy-dns/cloudflare from
///   caddyserver.com's build server (version pinned with &amp;version=; no checksum exists for custom builds, so the binary
///   is verified by running `caddy version` and `caddy list-modules`).
/// Set CPM_E2E_NO_DOWNLOAD=1 to never download. Overrides: CPM_TEST_PEBBLE, CPM_TEST_CADDY_DNS, CPM_TEST_CADDY_PLUGINS.
/// </summary>
public static class E2ETools
{
    public const string PebbleVersion = "v2.10.1";

    /// <summary>The Caddy release the product is verified with: CaddyVersion.Tested (read from the Platform source, as get-caddy.ps1 does).</summary>
    public static string CaddyVersion
    {
        get
        {
            if (_caddyVersion is not null) return _caddyVersion;
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CaddyManager.sln"))) dir = dir.Parent;
            var file = dir is null ? null : Path.Combine(dir.FullName, "src", "CaddyManager.Platform", "Binary", "CaddyVersion.cs");
            var m = file is not null && File.Exists(file)
                ? System.Text.RegularExpressions.Regex.Match(File.ReadAllText(file), "public const string Tested = \"(v\\d+\\.\\d+\\.\\d+)\";")
                : null;
            return _caddyVersion = m is { Success: true } ? m.Groups[1].Value : "v2.11.4";
        }
    }
    private static string? _caddyVersion;
    public static readonly string[] DnsBuildPackages = ["github.com/caddy-dns/rfc2136", "github.com/caddy-dns/cloudflare"];

    private static readonly object Gate = new();
    private static (string? Path, string? Reason)? _pebble;
    private static (string? Path, string? Reason)? _caddyDns;
    private static (string? Path, string? Reason)? _caddyCloudflare;

    private static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;
    private static string GoOs => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "darwin" : "linux";
    private static string GoArch => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "amd64";

    /// <summary>The repository's .dev/bin (created), found by walking up from the test binaries to CaddyManager.sln.</summary>
    public static string? DevBin
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CaddyManager.sln"))) dir = dir.Parent;
            if (dir is null) return null;
            var bin = Path.Combine(dir.FullName, ".dev", "bin");
            Directory.CreateDirectory(bin);
            return bin;
        }
    }

    private static bool DownloadsAllowed => Environment.GetEnvironmentVariable("CPM_E2E_NO_DOWNLOAD") is not ("1" or "true");

    /// <summary>Pebble binary, or null with the reason it could not be obtained.</summary>
    public static (string? Path, string? Reason) Pebble()
    {
        lock (Gate) return _pebble ??= Locate("CPM_TEST_PEBBLE", Exe("pebble"), DownloadPebble);
    }

    /// <summary>Caddy with caddy-dns/rfc2136 (+ cloudflare), or null with the reason.</summary>
    public static (string? Path, string? Reason) CaddyDns()
    {
        lock (Gate) return _caddyDns ??= Locate("CPM_TEST_CADDY_DNS", Exe("caddy-rfc2136"), DownloadCaddyDns, verify: p => VerifyCaddy(p, "dns.providers.rfc2136"));
    }

    /// <summary>A Caddy binary with dns.providers.cloudflare: the dev plugins build (.dev/bin/caddy-plugins) or the DNS build.</summary>
    public static (string? Path, string? Reason) CaddyCloudflare()
    {
        lock (Gate)
        {
            if (_caddyCloudflare is { } cached) return cached;
            var env = Environment.GetEnvironmentVariable("CPM_TEST_CADDY_PLUGINS");
            foreach (var candidate in new[] { env, DevBin is { } b ? Path.Combine(b, Exe("caddy-plugins")) : null })
                if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate) && VerifyCaddy(candidate, "dns.providers.cloudflare") is null)
                    return (_caddyCloudflare = (candidate, null)).Value;
        }
        var dns = CaddyDns();
        lock (Gate)
            return (_caddyCloudflare = dns.Path is not null && VerifyCaddy(dns.Path, "dns.providers.cloudflare") is null
                ? dns
                : (null, "No Caddy binary with dns.providers.cloudflare (.dev/bin/caddy-plugins or the DNS test build): " + (dns.Reason ?? "module missing"))).Value;
    }

    private static (string? Path, string? Reason) Locate(string envVar, string fileName, Func<string, string?> download, Func<string, string?>? verify = null)
    {
        var env = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrEmpty(env))
            return File.Exists(env) ? (env, null) : (null, $"{envVar} points to a missing file: {env}");
        var bin = DevBin;
        if (bin is null) return (null, "The repository root (CaddyManager.sln) was not found above the test binaries.");
        var path = Path.Combine(bin, fileName);
        if (File.Exists(path) && (verify?.Invoke(path) is null)) return (path, null);
        if (!DownloadsAllowed) return (null, $"{fileName} is not in .dev/bin and CPM_E2E_NO_DOWNLOAD is set (run .github/scripts/get-e2e-tools.ps1).");
        try
        {
            var error = download(path);
            if (error is null && verify?.Invoke(path) is { } bad) error = bad;
            return error is null ? (path, null) : (null, error);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException or UnauthorizedAccessException)
        {
            return (null, $"{fileName} could not be downloaded: {ex.Message}");
        }
    }

    private static HttpClient Http()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("caddy-proxy-manager-tests");
        return c;
    }

    private static string? DownloadPebble(string dest)
    {
        using var http = Http();
        var ext = OperatingSystem.IsWindows() ? "zip" : "tar.gz";
        var assetName = $"pebble-{GoOs}-{GoArch}.{ext}";
        var release = JsonNode.Parse(http.GetStringAsync($"https://api.github.com/repos/letsencrypt/pebble/releases/tags/{PebbleVersion}").GetAwaiter().GetResult())!;
        var asset = release["assets"]!.AsArray().FirstOrDefault(a => a!["name"]!.GetValue<string>() == assetName);
        if (asset is null) return $"Pebble {PebbleVersion} has no asset {assetName}.";
        var digest = asset["digest"]?.GetValue<string>();
        if (digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal)) return $"GitHub publishes no SHA-256 digest for {assetName}; refusing to run it unverified.";
        var bytes = http.GetByteArrayAsync(asset["browser_download_url"]!.GetValue<string>()).GetAwaiter().GetResult();
        var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (actual != digest["sha256:".Length..].ToLowerInvariant()) return $"SHA-256 of {assetName} does not match GitHub's digest ({actual} vs {digest}).";

        var exeName = Exe("pebble");
        byte[]? exe = null;
        if (ext == "zip")
        {
            using var zip = new ZipArchive(new MemoryStream(bytes));
            var entry = zip.Entries.FirstOrDefault(e => e.Name == exeName);
            if (entry is not null) { using var s = entry.Open(); using var ms = new MemoryStream(); s.CopyTo(ms); exe = ms.ToArray(); }
        }
        else
        {
            using var gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
            using var tar = new TarReader(gz);
            while (tar.GetNextEntry() is { } e)
            {
                if (e.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile && Path.GetFileName(e.Name) == exeName && e.DataStream is not null)
                {
                    using var ms = new MemoryStream();
                    e.DataStream.CopyTo(ms);
                    exe = ms.ToArray();
                    break;
                }
            }
        }
        if (exe is null) return $"{assetName} does not contain {exeName}.";
        WriteExecutable(dest, exe);
        return null;
    }

    private static string? DownloadCaddyDns(string dest)
    {
        using var http = Http();
        var url = $"https://caddyserver.com/api/download?os={GoOs}&arch={GoArch}&version={CaddyVersion}" + string.Concat(DnsBuildPackages.Select(p => "&p=" + p));
        var bytes = http.GetByteArrayAsync(url).GetAwaiter().GetResult();
        if (bytes.Length < 1_000_000) return $"The Caddy build server returned {bytes.Length} bytes for {url}.";
        WriteExecutable(dest, bytes);
        return null;
    }

    private static void WriteExecutable(string dest, byte[] content)
    {
        var tmp = dest + ".download";
        File.WriteAllBytes(tmp, content);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.Move(tmp, dest, overwrite: true);
    }

    /// <summary>Null when the binary runs, reports the tested Caddy version and has the module; otherwise why not.</summary>
    public static string? VerifyCaddy(string path, string module)
    {
        var version = Run(path, "version");
        if (version is null || !version.StartsWith(CaddyVersion + " ", StringComparison.Ordinal))
            return $"{path} does not report Caddy {CaddyVersion} (got '{version?.Trim()}').";
        var modules = Run(path, "list-modules");
        if (modules is null || !modules.Split('\n').Any(l => l.Trim() == module)) return $"{path} does not include the module {module}.";
        return null;
    }

    /// <summary>`caddy list-modules` lines matching the prefix (for artifacts).</summary>
    public static List<string> Modules(string caddy, string prefix) =>
        (Run(caddy, "list-modules") ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith(prefix, StringComparison.Ordinal)).ToList();

    public static string? Run(string exe, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(60_000)) { p.Kill(true); return null; }
            return stdout.Result + stderr.Result;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}

/// <summary>Skips unless Pebble and the rfc2136 Caddy build are available (downloaded on first use when missing).</summary>
public sealed class Dns01FactAttribute : FactAttribute
{
    public Dns01FactAttribute()
    {
        var pebble = E2ETools.Pebble();
        var caddy = E2ETools.CaddyDns();
        if (pebble.Path is null || caddy.Path is null)
            Skip = "DNS-01 E2E tools unavailable: " + string.Join(" ", new[] { pebble.Reason, caddy.Reason }.Where(r => r is not null));
    }
}

/// <summary>Skips unless a Caddy binary with dns.providers.cloudflare is available.</summary>
public sealed class CloudflareCaddyFactAttribute : FactAttribute
{
    public CloudflareCaddyFactAttribute()
    {
        if (E2ETools.CaddyCloudflare() is { Path: null } r) Skip = r.Reason;
    }
}
