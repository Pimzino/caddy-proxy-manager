using System.Runtime.InteropServices;

namespace CaddyManager.Platform.Binary;

/// <summary>
/// Caddy's naming for this server: Go (GOOS/GOARCH, used by the caddyserver.com build API) and the release asset names
/// (caddy_2.11.4_windows_amd64.zip). The product runs on 64-bit Windows only, so the OS is always "windows"; the record
/// can still describe another platform, e.g. the target of an uploaded binary that is refused.
/// </summary>
public sealed record CaddyPlatform(string GoOs, string GoArch)
{
    public static CaddyPlatform Current { get; } = new("windows", RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "amd64",
        Architecture.Arm64 => "arm64",
        var other => other.ToString().ToLowerInvariant(),
    });

    /// <summary>"windows/amd64" — shown in the UI.</summary>
    public override string ToString() => $"{GoOs}/{GoArch}";

    public const string BinaryName = "caddy.exe";

    /// <summary>Release asset file name, e.g. caddy_2.11.4_windows_amd64.zip.</summary>
    public string ReleaseAssetName(CaddyVersion version) => $"caddy_{version.Bare}_{GoOs}_{GoArch}.zip";

    /// <summary>The release asset to download for this platform, with a version placeholder (for messages).</summary>
    public string ReleaseAssetPattern => $"caddy_<version>_{GoOs}_{GoArch}.zip";

    public static string ChecksumsAssetName(CaddyVersion version) => $"caddy_{version.Bare}_checksums.txt";

    public static string ReleaseDownloadUrl(CaddyVersion version, string asset) =>
        $"https://github.com/caddyserver/caddy/releases/download/{version}/{asset}";

    /// <summary>caddyserver.com custom build URL with the given plugin packages.</summary>
    public string BuildServerUrl(IEnumerable<string> packages)
    {
        var q = string.Join("", packages.Select(p => "&p=" + Uri.EscapeDataString(p)));
        return $"https://caddyserver.com/api/download?os={GoOs}&arch={GoArch}{q}";
    }
}
