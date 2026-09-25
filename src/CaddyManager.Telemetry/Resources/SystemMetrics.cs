using System.Runtime.InteropServices;

namespace CaddyManager.Telemetry.Resources;

/// <summary>Cumulative whole-machine CPU time counters (any unit); CPU% = Δbusy / Δtotal.</summary>
internal readonly record struct CpuTimes(ulong Busy, ulong Total);

/// <summary>Physical memory in bytes.</summary>
internal readonly record struct MemoryInfo(long UsedBytes, long TotalBytes);

/// <summary>
/// Whole-machine CPU and memory counters. Windows only (the product is Windows-only): elsewhere, and whenever the
/// platform call fails, every method returns null and callers report 0 rather than throwing (SPEC: the sampler never throws).
/// </summary>
internal static class SystemMetrics
{
    public static CpuTimes? ReadCpuTimes()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return WindowsCpu();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        return null;
    }

    public static MemoryInfo? ReadMemory()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return WindowsMemory();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        return null;
    }

    /// <summary>CPU% between two readings, clamped to 0–100; 0 when there is no previous reading.</summary>
    public static double Percent(CpuTimes? previous, CpuTimes? current)
    {
        if (previous is not { } p || current is not { } c || c.Total <= p.Total || c.Busy < p.Busy) return 0;
        return Math.Clamp(100.0 * (c.Busy - p.Busy) / (c.Total - p.Total), 0, 100);
    }

    // GetSystemTimes: kernel time INCLUDES idle time. https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-getsystemtimes
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    // https://learn.microsoft.com/windows/win32/api/sysinfoapi/nf-sysinfoapi-globalmemorystatusex
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    private static CpuTimes? WindowsCpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        var total = (ulong)(kernel + user);
        return new CpuTimes(total - (ulong)idle, total);
    }

    private static MemoryInfo? WindowsMemory()
    {
        var m = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref m)) return null;
        return new MemoryInfo((long)(m.TotalPhys - m.AvailPhys), (long)m.TotalPhys);
    }
}
