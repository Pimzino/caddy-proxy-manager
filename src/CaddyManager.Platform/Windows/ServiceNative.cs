using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace CaddyManager.Platform.Windows;

/// <summary>
/// Structured Service Control Manager queries (QueryServiceStatusEx, QueryServiceConfig2) instead of parsing sc.exe output,
/// whose labels and messages are localised on non-English Windows (e.g. "DIENSTNAME" / "STATUS" / "TYP" on German servers).
/// https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-queryservicestatusex
/// https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-queryserviceconfig2w
/// </summary>
[SupportedOSPlatform("windows")]
public static class ServiceNative
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceConfigDelayedAutoStartInfo = 3;
    private const int ScStatusProcessInfo = 0;
    private const uint ServiceConfigFailureActions = 2;
    private const uint ServiceConfigFailureActionsFlag = 4;
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorInsufficientBuffer = 122;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS_PROCESS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
        public uint dwProcessId;
        public uint dwServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_FAILURE_ACTIONS
    {
        public uint dwResetPeriod;
        public IntPtr lpRebootMsg;
        public IntPtr lpCommand;
        public uint cActions;
        public IntPtr lpsaActions;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SC_ACTION
    {
        public int Type;
        public uint Delay;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "OpenSCManagerW")]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "OpenServiceW")]
    private static extern IntPtr OpenService(IntPtr scm, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(IntPtr service, int infoLevel, out SERVICE_STATUS_PROCESS buffer, int size, out int needed);

    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "ChangeServiceConfig2W")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2(IntPtr service, uint infoLevel, ref int info);

    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "ChangeServiceConfig2W")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2(IntPtr service, uint infoLevel, ref SERVICE_FAILURE_ACTIONS info);

    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "QueryServiceConfig2W")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig2(IntPtr service, uint infoLevel, IntPtr buffer, int size, out int needed);

    private static readonly Dictionary<uint, string> StateNames = new()
    {
        [1] = "STOPPED", [2] = "START_PENDING", [3] = "STOP_PENDING", [4] = "RUNNING",
        [5] = "CONTINUE_PENDING", [6] = "PAUSE_PENDING", [7] = "PAUSED",
    };

    /// <summary>Equivalent of `sc queryex`; null when the service does not exist.</summary>
    public static ScQueryResult? QueryStatus(string name) => WithService(name, ServiceQueryStatus, h =>
    {
        if (!QueryServiceStatusEx(h, ScStatusProcessInfo, out var s, Marshal.SizeOf<SERVICE_STATUS_PROCESS>(), out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return new ScQueryResult(
            (int)s.dwCurrentState,
            StateNames.GetValueOrDefault(s.dwCurrentState, s.dwCurrentState.ToString()),
            s.dwProcessId == 0 ? null : (int)s.dwProcessId,
            unchecked((int)s.dwWin32ExitCode),
            unchecked((int)s.dwServiceSpecificExitCode))
        {
            ServiceType = (int)s.dwServiceType,
        };
    });

    /// <summary>Equivalent of `sc qfailure` (reset period in seconds, actions with delays in ms); null when the service does not exist.</summary>
    public static ScFailureConfig? QueryFailureActions(string name) => WithService(name, ServiceQueryConfig, h =>
        WithConfig2(h, ServiceConfigFailureActions, buffer =>
        {
            var fa = Marshal.PtrToStructure<SERVICE_FAILURE_ACTIONS>(buffer);
            var actions = new List<(string, int)>();
            var size = Marshal.SizeOf<SC_ACTION>();
            for (var i = 0; i < fa.cActions && fa.lpsaActions != IntPtr.Zero; i++)
            {
                var a = Marshal.PtrToStructure<SC_ACTION>(fa.lpsaActions + i * size);
                var type = a.Type switch { 0 => "NONE", 1 => "RESTART", 2 => "REBOOT", 3 => "RUN_COMMAND", _ => a.Type.ToString() };
                actions.Add((type, (int)Math.Min(a.Delay, int.MaxValue)));
            }
            return new ScFailureConfig((int)Math.Min(fa.dwResetPeriod, int.MaxValue), actions);
        }));

    /// <summary>
    /// SERVICE_FAILURE_ACTIONS_FLAG (`sc qfailureflag`): true when recovery actions also run after the service stops with a
    /// non-zero exit code. Null when the service does not exist.
    /// </summary>
    public static bool? QueryFailureActionsFlag(string name) => WithService<bool?>(name, ServiceQueryConfig, h =>
        WithConfig2(h, ServiceConfigFailureActionsFlag, buffer => Marshal.ReadInt32(buffer) != 0));

    /// <summary>
    /// Sets or clears "Automatic (Delayed Start)" (SERVICE_CONFIG_DELAYED_AUTO_START_INFO, a BOOL). The flag only matters
    /// for auto-start services. https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-service_delayed_auto_start_info
    /// </summary>
    public static void SetDelayedAutoStart(string name, bool delayed)
    {
        var found = WithService(name, ServiceChangeConfig, h =>
        {
            var value = delayed ? 1 : 0;
            if (!ChangeServiceConfig2(h, ServiceConfigDelayedAutoStartInfo, ref value))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return true;
        });
        if (!found) throw new Win32Exception(ErrorServiceDoesNotExist);
    }

    /// <summary>
    /// Replaces the recovery actions (`sc failure`): reset period in seconds (int.MaxValue = INFINITE, as
    /// <see cref="QueryFailureActions"/> reports it) and actions "NONE" / "RESTART" / "REBOOT" / "RUN_COMMAND" with delays in
    /// ms. The reboot message and command stay unchanged (NULL). A RESTART action needs SERVICE_START on the handle.
    /// https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-service_failure_actionsw
    /// </summary>
    public static void SetFailureActions(string name, int resetPeriodSeconds, IReadOnlyList<(string Type, int DelayMs)> actions)
    {
        if (actions.Count == 0) throw new ArgumentException("At least one action is required (use NONE to disable recovery).", nameof(actions));
        var found = WithService(name, ServiceChangeConfig | ServiceStart, h =>
        {
            var size = Marshal.SizeOf<SC_ACTION>();
            var array = Marshal.AllocHGlobal(size * actions.Count);
            try
            {
                for (var i = 0; i < actions.Count; i++)
                {
                    var type = actions[i].Type switch
                    {
                        "NONE" => 0, "RESTART" => 1, "REBOOT" => 2, "RUN_COMMAND" => 3,
                        var other => int.TryParse(other, out var n) ? n : throw new ArgumentException($"Unknown recovery action '{other}'."),
                    };
                    Marshal.StructureToPtr(new SC_ACTION { Type = type, Delay = (uint)Math.Max(0, actions[i].DelayMs) }, array + i * size, false);
                }
                var fa = new SERVICE_FAILURE_ACTIONS
                {
                    dwResetPeriod = resetPeriodSeconds == int.MaxValue ? uint.MaxValue : (uint)Math.Max(0, resetPeriodSeconds),
                    lpRebootMsg = IntPtr.Zero,
                    lpCommand = IntPtr.Zero,
                    cActions = (uint)actions.Count,
                    lpsaActions = array,
                };
                if (!ChangeServiceConfig2(h, ServiceConfigFailureActions, ref fa))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(array);
            }
        });
        if (!found) throw new Win32Exception(ErrorServiceDoesNotExist);
    }

    private static T? WithService<T>(string name, uint access, Func<IntPtr, T> query)
    {
        var scm = OpenSCManager(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var svc = OpenService(scm, name, access);
            if (svc == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                if (err == ErrorServiceDoesNotExist) return default;
                throw new Win32Exception(err);
            }
            try { return query(svc); }
            finally { CloseServiceHandle(svc); }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    private static T WithConfig2<T>(IntPtr service, uint level, Func<IntPtr, T> read)
    {
        QueryServiceConfig2(service, level, IntPtr.Zero, 0, out var needed);
        var err = Marshal.GetLastWin32Error();
        if (needed <= 0) throw new Win32Exception(err == 0 ? ErrorInsufficientBuffer : err);
        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!QueryServiceConfig2(service, level, buffer, needed, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return read(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
