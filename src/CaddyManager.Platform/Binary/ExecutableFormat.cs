using System.Buffers.Binary;

namespace CaddyManager.Platform.Binary;

/// <summary>What kind of file an uploaded Caddy binary / archive is (detected from its content, not its name).</summary>
public enum UploadKind { Unknown, Zip, TarGz, Executable }

/// <summary>Operating system and CPU architecture of a native executable, in Go naming (windows/amd64, darwin/arm64 ...).</summary>
public sealed record ExecutableTarget(string GoOs, IReadOnlyList<string> GoArchs)
{
    public bool Matches(CaddyPlatform platform) =>
        GoOs == platform.GoOs && GoArchs.Contains(platform.GoArch, StringComparer.Ordinal);

    public override string ToString() => $"{GoOs}/{string.Join("+", GoArchs)}";
}

/// <summary>
/// Minimal header parsing for PE (Windows), ELF (Linux/FreeBSD) and Mach-O (macOS) executables, used to reject an
/// uploaded binary built for another platform with a precise message before anything is executed or replaced.
/// </summary>
public static class ExecutableFormat
{
    /// <summary>Classifies a file by its first bytes.</summary>
    public static UploadKind DetectKind(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 4 && head[0] == 'P' && head[1] == 'K' && head[2] == 3 && head[3] == 4) return UploadKind.Zip;
        if (head.Length >= 2 && head[0] == 0x1F && head[1] == 0x8B) return UploadKind.TarGz;
        return Detect(head) is not null ? UploadKind.Executable : UploadKind.Unknown;
    }

    public static UploadKind DetectKind(string file)
    {
        var head = ReadHead(file, 4096);
        return DetectKind(head);
    }

    /// <summary>Target platform of an executable; null when the file is not a recognised executable.</summary>
    public static ExecutableTarget? Detect(string file) => Detect(ReadHead(file, 64 * 1024));

    public static ExecutableTarget? Detect(ReadOnlySpan<byte> b)
    {
        if (b.Length < 64) return null;

        // PE: "MZ" DOS header, e_lfanew at 0x3C -> "PE\0\0" + IMAGE_FILE_HEADER.Machine
        if (b[0] == 'M' && b[1] == 'Z')
        {
            var peOffset = BinaryPrimitives.ReadInt32LittleEndian(b[0x3C..]);
            if (peOffset <= 0 || peOffset > b.Length - 6) return null;
            if (b[peOffset] != 'P' || b[peOffset + 1] != 'E' || b[peOffset + 2] != 0 || b[peOffset + 3] != 0) return null;
            var machine = BinaryPrimitives.ReadUInt16LittleEndian(b[(peOffset + 4)..]);
            var arch = machine switch
            {
                0x8664 => "amd64",
                0xAA64 => "arm64",
                0x014C => "386",
                0x01C4 => "arm",
                _ => $"machine-0x{machine:x4}",
            };
            return new ExecutableTarget("windows", [arch]);
        }

        // ELF: 0x7F 'E' 'L' 'F', EI_DATA at 5 (1 = little endian), EI_OSABI at 7 (9 = FreeBSD), e_machine at 18
        if (b[0] == 0x7F && b[1] == 'E' && b[2] == 'L' && b[3] == 'F')
        {
            var little = b[5] != 2;
            var machine = little ? BinaryPrimitives.ReadUInt16LittleEndian(b[18..]) : BinaryPrimitives.ReadUInt16BigEndian(b[18..]);
            var arch = machine switch
            {
                62 => "amd64",
                183 => "arm64",
                3 => "386",
                40 => "arm",
                243 => "riscv64",
                21 => little ? "ppc64le" : "ppc64",
                22 => "s390x",
                _ => $"machine-{machine}",
            };
            return new ExecutableTarget(b[7] == 9 ? "freebsd" : "linux", [arch]);
        }

        // Mach-O 64-bit (little endian magic 0xFEEDFACF), cputype at 4
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(b);
        if (magic == 0xFEEDFACF)
            return new ExecutableTarget("darwin", [MachArch(BinaryPrimitives.ReadInt32LittleEndian(b[4..]))]);

        // Mach-O universal ("fat") binary: big endian 0xCAFEBABE, nfat_arch, then 20-byte fat_arch entries
        if (BinaryPrimitives.ReadUInt32BigEndian(b) == 0xCAFEBABE)
        {
            var count = BinaryPrimitives.ReadUInt32BigEndian(b[4..]);
            if (count is 0 or > 8 || 8 + count * 20 > b.Length) return null; // also rejects Java class files (same magic)
            var archs = new List<string>();
            for (var i = 0; i < count; i++)
                archs.Add(MachArch(BinaryPrimitives.ReadInt32BigEndian(b[(8 + i * 20)..])));
            return new ExecutableTarget("darwin", archs.Distinct().ToList());
        }
        return null;
    }

    private static string MachArch(int cpuType) => cpuType switch
    {
        0x01000007 => "amd64",
        0x0100000C => "arm64",
        7 => "386",
        _ => $"cputype-0x{cpuType:x}",
    };

    private static byte[] ReadHead(string file, int max)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[(int)Math.Min(max, fs.Length)];
        fs.ReadExactly(buffer);
        return buffer;
    }

    /// <summary>
    /// Parses and normalises an expected SHA-512 (128 hex characters; spaces and a "sha512:" prefix are tolerated).
    /// Returns null for empty input; throws ArgumentException when malformed.
    /// </summary>
    public static string? NormalizeSha512(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = input.Trim();
        if (s.StartsWith("sha512:", StringComparison.OrdinalIgnoreCase)) s = s[7..];
        s = string.Concat(s.Where(c => !char.IsWhiteSpace(c)));
        if (s.Length != 128 || !s.All(Uri.IsHexDigit))
            throw new ArgumentException(
                "The SHA-512 checksum must be 128 hexadecimal characters (as listed in caddy_<version>_checksums.txt or printed by 'Get-FileHash -Algorithm SHA512').");
        return s.ToLowerInvariant();
    }
}
