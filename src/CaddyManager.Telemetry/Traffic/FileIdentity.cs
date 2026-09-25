using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CaddyManager.Telemetry.Traffic;

/// <summary>
/// Identity of an open file that survives renames: Windows volume serial + file index (GetFileInformationByHandle),
/// Unix device + inode (fstat). Caddy's writer rotates by renaming the active file and creating a new one at the same
/// path (docs/research/round3-accesslog.md §3), so "the path names a different file" = a different identity.
/// Fallback when the native call is unavailable: the creation time (also preserved by a rename).
/// </summary>
internal static class FileIdentity
{
    public static string Of(SafeFileHandle handle)
    {
        try
        {
            if (OperatingSystem.IsWindows()) { if (Windows(handle) is { } w) return w; }
            else if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) { if (Unix(handle) is { } u) return u; }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        return "ct:" + File.GetCreationTimeUtc(handle).Ticks;
    }

    /// <summary>Identity of the file currently at <paramref name="path"/>; null when it does not exist or cannot be opened.</summary>
    public static string? OfPath(string path)
    {
        try
        {
            using var h = OpenShared(path);
            return Of(h);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Opens for reading while letting Caddy keep writing, rename and delete the file. Without FileShare.Delete Caddy's
    /// rotation rename fails on Windows and the log entry is lost (research §3).
    /// </summary>
    public static SafeFileHandle OpenShared(string path) =>
        File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    // ------------------------------------------------------------------ Windows

    // FILETIME members are two DWORDs (4-byte alignment): Pack = 4 keeps the 64-bit fields at the native offsets.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    // https://learn.microsoft.com/windows/win32/api/fileapi/nf-fileapi-getfileinformationbyhandle
    // "The identifier that is stored in the nFileIndexHigh and nFileIndexLow members is called the file ID ... In the NTFS
    // file system, a file keeps the same file ID until it is deleted." Combined with the volume serial it identifies a file.
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);

    private static string? Windows(SafeFileHandle handle) =>
        GetFileInformationByHandle(handle, out var i)
            ? $"win:{i.VolumeSerialNumber:x8}:{i.FileIndexHigh:x8}{i.FileIndexLow:x8}"
            : null;

    // ------------------------------------------------------------------ Unix (development)

    // struct stat starts with the device and has st_ino at byte offset 8 on every platform we develop on:
    //   macOS (64-bit inode layout, the only one on arm64): int32 st_dev, uint16 st_mode, uint16 st_nlink, uint64 st_ino
    //   Linux x86_64 and aarch64 (glibc/musl): uint64 st_dev, uint64 st_ino
    // x86_64 macOS exports the 64-bit-inode variant as "fstat$INODE64".
    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStat(int fd, byte[] buffer);

    // glibc's "libc.so" is a linker script that dlopen cannot load; name the real library (glibc >= 2.33 exports fstat).
    [DllImport("libc.so.6", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStatGlibc(int fd, byte[] buffer);

    [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static extern int FStatInode64(int fd, byte[] buffer);

    private static string? Unix(SafeFileHandle handle)
    {
        var buffer = new byte[512];
        var fd = (int)handle.DangerousGetHandle();
        int rc;
        if (OperatingSystem.IsMacOS()) rc = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? FStatInode64(fd, buffer) : FStat(fd, buffer);
        else
        {
            try { rc = FStatGlibc(fd, buffer); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { rc = FStat(fd, buffer); } // musl
        }
        if (rc != 0) return null;
        long dev = OperatingSystem.IsMacOS() ? BitConverter.ToInt32(buffer, 0) : BitConverter.ToInt64(buffer, 0);
        var ino = BitConverter.ToUInt64(buffer, 8);
        return $"unix:{dev:x}:{ino:x}";
    }
}
