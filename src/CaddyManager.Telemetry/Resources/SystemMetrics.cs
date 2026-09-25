using System.Globalization;
using System.Runtime.InteropServices;

namespace CaddyManager.Telemetry.Resources;

/// <summary>Cumulative whole-machine CPU time counters (any unit); CPU% = Δbusy / Δtotal.</summary>
internal readonly record struct CpuTimes(ulong Busy, ulong Total);

/// <summary>Physical memory in bytes.</summary>
internal readonly record struct MemoryInfo(long UsedBytes, long TotalBytes);

/// <summary>
/// Whole-machine CPU and memory counters per OS. Every method returns null when the platform call fails or is not
/// available — callers report 0 rather than throwing (SPEC: the sampler never throws).
/// </summary>
internal static class SystemMetrics
{
    public static CpuTimes? ReadCpuTimes()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return WindowsCpu();
            if (OperatingSystem.IsLinux()) return LinuxCpu();
            if (OperatingSystem.IsMacOS()) return MacCpu();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException or FormatException) { }
        return null;
    }

    public static MemoryInfo? ReadMemory()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return WindowsMemory();
            if (OperatingSystem.IsLinux()) return LinuxMemory();
            if (OperatingSystem.IsMacOS()) return MacMemory();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException or FormatException) { }
        return null;
    }

    /// <summary>CPU% between two readings, clamped to 0–100; 0 when there is no previous reading.</summary>
    public static double Percent(CpuTimes? previous, CpuTimes? current)
    {
        if (previous is not { } p || current is not { } c || c.Total <= p.Total || c.Busy < p.Busy) return 0;
        return Math.Clamp(100.0 * (c.Busy - p.Busy) / (c.Total - p.Total), 0, 100);
    }

    // ------------------------------------------------------------------ Windows

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

    // ------------------------------------------------------------------ Linux (development/CI)

    // /proc/stat first line: "cpu user nice system idle iowait irq softirq steal guest guest_nice" (guest time is already
    // included in user/nice). https://man7.org/linux/man-pages/man5/proc_stat.5.html
    private static CpuTimes? LinuxCpu()
    {
        using var reader = new StreamReader("/proc/stat");
        var line = reader.ReadLine();
        if (line is null || !line.StartsWith("cpu ", StringComparison.Ordinal)) return null;
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        ulong total = 0, idle = 0;
        for (var i = 1; i < parts.Length && i <= 8; i++)
        {
            var v = ulong.Parse(parts[i], CultureInfo.InvariantCulture);
            total += v;
            if (i is 4 or 5) idle += v; // idle + iowait
        }
        return new CpuTimes(total - idle, total);
    }

    // MemAvailable is the kernel's estimate of memory available without swapping. https://man7.org/linux/man-pages/man5/proc_meminfo.5.html
    private static MemoryInfo? LinuxMemory()
    {
        long total = -1, available = -1;
        foreach (var line in File.ReadLines("/proc/meminfo"))
        {
            if (line.StartsWith("MemTotal:", StringComparison.Ordinal)) total = KiB(line);
            else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal)) available = KiB(line);
            if (total >= 0 && available >= 0) break;
        }
        return total > 0 && available >= 0 ? new MemoryInfo(total - available, total) : null;

        static long KiB(string line) =>
            long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture) * 1024;
    }

    // ------------------------------------------------------------------ macOS (development)

    private const string LibSystem = "/usr/lib/libSystem.dylib";
    private const int HostCpuLoadInfo = 3;   // HOST_CPU_LOAD_INFO, <mach/host_info.h>
    private const int HostVmInfo64 = 4;      // HOST_VM_INFO64, <mach/host_info.h>
    private const int VmStatistics64Count = 40; // sizeof(vm_statistics64_data_t) / sizeof(integer_t) = 160 / 4

    [DllImport(LibSystem)]
    private static extern uint mach_host_self();

    [DllImport(LibSystem)]
    private static extern int host_statistics(uint host, int flavor, [Out] int[] info, ref uint count);

    [DllImport(LibSystem)]
    private static extern int host_statistics64(uint host, int flavor, [Out] int[] info, ref uint count);

    [DllImport(LibSystem)]
    private static extern int host_page_size(uint host, out nuint pageSize);

    [DllImport(LibSystem)]
    private static extern int sysctlbyname(string name, out long value, ref nint length, IntPtr newValue, nint newLength);

    // mach_host_self() returns a send right each call; take it once.
    private static uint? _machHost;
    private static uint MachHost => _machHost ??= mach_host_self();

    // host_cpu_load_info: natural_t cpu_ticks[CPU_STATE_MAX] = { USER, SYSTEM, IDLE, NICE } (<mach/machine.h>).
    private static CpuTimes? MacCpu()
    {
        var ticks = new int[4];
        uint count = 4;
        if (host_statistics(MachHost, HostCpuLoadInfo, ticks, ref count) != 0) return null;
        ulong user = (uint)ticks[0], system = (uint)ticks[1], idle = (uint)ticks[2], nice = (uint)ticks[3];
        var total = user + system + idle + nice;
        return new CpuTimes(total - idle, total);
    }

    // vm_statistics64 (<mach/vm_statistics.h>) as 32-bit words: free_count [0], active_count [1], inactive_count [2],
    // wire_count [3], ..., compressor_page_count at byte 128 [32]. "Used" follows Activity Monitor's Memory Used
    // closely enough for a dashboard: active + wired + compressed pages. Total = sysctl hw.memsize.
    private static MemoryInfo? MacMemory()
    {
        long total = 0;
        nint len = sizeof(long);
        if (sysctlbyname("hw.memsize", out total, ref len, IntPtr.Zero, 0) != 0 || total <= 0) return null;
        var info = new int[VmStatistics64Count];
        uint count = VmStatistics64Count;
        if (host_statistics64(MachHost, HostVmInfo64, info, ref count) != 0 || count < 33) return null;
        if (host_page_size(MachHost, out var pageSize) != 0 || pageSize == 0) return null;
        var pages = (ulong)(uint)info[1] + (uint)info[3] + (uint)info[32];
        var used = (long)Math.Min(pages * pageSize, (ulong)total);
        return new MemoryInfo(used, total);
    }
}
