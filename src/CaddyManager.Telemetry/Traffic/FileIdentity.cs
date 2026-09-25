using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CaddyManager.Telemetry.Traffic;

/// <summary>
/// Identity of an open file that survives renames: volume serial + file index (GetFileInformationByHandle). Caddy's
/// writer rotates by renaming the active file and creating a new one at the same path (docs/research/round3-accesslog.md
/// §3), so "the path names a different file" = a different identity. Elsewhere (development machines only) and when the
/// call fails: the creation time, which a rename also preserves.
/// </summary>
internal static class FileIdentity
{
    public static string Of(SafeFileHandle handle)
    {
        try
        {
            if (OperatingSystem.IsWindows() && Windows(handle) is { } w) return w;
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
}
