using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CaddyManager.Telemetry.Traffic;

/// <summary>
/// Identity of an open file that survives renames: volume serial + file index (GetFileInformationByHandle). Caddy's
/// writer rotates by renaming the active file and creating a new one at the same path (docs/research/round3-accesslog.md
/// §3), so "the path names a different file" = a different identity. Elsewhere (development machines only): the creation
/// time, which a rename also preserves. A failing call on Windows throws IOException (never a different identity).
/// </summary>
internal enum PathState { Present, Missing, Unavailable }

internal static class FileIdentity
{
    public static string Of(SafeFileHandle handle)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return Windows(handle);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        return "ct:" + File.GetCreationTimeUtc(handle).Ticks;
    }

    /// <summary>
    /// Identity of the file currently at <paramref name="path"/>. <see cref="PathState.Missing"/> only when nothing is there
    /// (file or directory not found); any other failure (a sharing violation by a third-party tool, access denied, a
    /// failing GetFileInformationByHandle) is <see cref="PathState.Unavailable"/>: the caller must not conclude that the file
    /// was rotated away and should simply ask again later.
    /// </summary>
    public static PathState OfPath(string path, out string? id)
    {
        id = null;
        try
        {
            using var h = OpenShared(path);
            id = Of(h);
            return PathState.Present;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return PathState.Missing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PathState.Unavailable;
        }
    }

    /// <summary>True for "nothing at this path" (as opposed to a file that exists but cannot be opened right now).</summary>
    public static bool IsMissing(Exception ex) => ex is FileNotFoundException or DirectoryNotFoundException;

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

    // A failing call is an I/O error, not "another file": falling back to a different identity scheme here would look like a
    // rotation and make the tailer read the live file again from the start.
    private static string Windows(SafeFileHandle handle) =>
        GetFileInformationByHandle(handle, out var i)
            ? $"win:{i.VolumeSerialNumber:x8}:{i.FileIndexHigh:x8}{i.FileIndexLow:x8}"
            : throw new IOException($"GetFileInformationByHandle failed (error {Marshal.GetLastPInvokeError()}).");
}
