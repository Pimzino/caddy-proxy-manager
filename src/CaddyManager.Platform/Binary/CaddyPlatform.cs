using System.Runtime.InteropServices;

namespace CaddyManager.Platform.Binary;

/// <summary>
/// Maps the current OS/architecture to Caddy's naming schemes:
/// Go (GOOS/GOARCH, used by the caddyserver.com build API) and goreleaser release asset names
/// (caddy_2.11.4_windows_amd64.zip, caddy_2.11.4_mac_arm64.tar.gz, caddy_2.11.4_linux_amd64.tar.gz).
/// </summary>
public sealed record CaddyPlatform(string GoOs, string GoArch)
{
    public static CaddyPlatform Current { get; } = Detect();

    public bool IsWindows => GoOs == "windows";

    /// <summary>"windows/amd64" — shown in the UI.</summary>
    public override string ToString() => $"{GoOs}/{GoArch}";

    /// <summary>OS token in release asset names (goreleaser uses "mac" for darwin).</summary>
    public string ReleaseOs => GoOs == "darwin" ? "mac" : GoOs;

    /// <summary>Arch token in release asset names ("armv7" for 32-bit ARM).</summary>
    public string ReleaseArch => GoArch == "arm" ? "armv7" : GoArch;

    public string ArchiveExtension => IsWindows ? "zip" : "tar.gz";

    public string BinaryName => IsWindows ? "caddy.exe" : "caddy";

    /// <summary>Release asset file name, e.g. caddy_2.11.4_windows_amd64.zip.</summary>
    public string ReleaseAssetName(CaddyVersion version) =>
        $"caddy_{version.Bare}_{ReleaseOs}_{ReleaseArch}.{ArchiveExtension}";

    public static string ChecksumsAssetName(CaddyVersion version) => $"caddy_{version.Bare}_checksums.txt";

    public static string ReleaseDownloadUrl(CaddyVersion version, string asset) =>
        $"https://github.com/caddyserver/caddy/releases/download/{version}/{asset}";

    /// <summary>caddyserver.com custom build URL with the given plugin packages.</summary>
    public string BuildServerUrl(IEnumerable<string> packages)
    {
        var q = string.Join("", packages.Select(p => "&p=" + Uri.EscapeDataString(p)));
        return $"https://caddyserver.com/api/download?os={GoOs}&arch={GoArch}{q}";
    }

    private static CaddyPlatform Detect()
    {
        var os = OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsMacOS() ? "darwin"
            : OperatingSystem.IsFreeBSD() ? "freebsd"
            : "linux";
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "amd64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "386",
            Architecture.Arm => "arm",
            Architecture.S390x => "s390x",
            Architecture.Ppc64le => "ppc64le",
            Architecture.RiscV64 => "riscv64",
            var other => other.ToString().ToLowerInvariant(),
        };
        return new CaddyPlatform(os, arch);
    }
}
