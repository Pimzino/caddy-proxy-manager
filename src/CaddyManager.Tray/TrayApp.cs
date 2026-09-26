using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using static CaddyManager.Tray.Native;

namespace CaddyManager.Tray;

internal static class Program
{
    [STAThread]
    private static int Main() => OperatingSystem.IsWindows() ? new TrayApp().Run() : 1;
}

/// <summary>
/// The notification-area icon: left click opens the management UI; the right-click menu shows the state of Caddy and of
/// the management service and starts / stops / restarts them. Those actions run "CaddyManager.exe caddy|manager ..."
/// elevated (UAC prompt), which routes Caddy control through the manager so it is audited and respected by the watchdog.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class TrayApp
{
    // Must match CaddyManager.Core.AppPaths (not referenced, to keep this exe small).
    private const string ProductName = "Caddy Proxy Manager";
    private const string ManagerService = "CaddyProxyManager";
    private const string CaddyService = "Caddy";
    private const string RegistryKey = @"SOFTWARE\Caddy Proxy Manager";
    private const string UiUrlValue = "UiUrl";
    private const string ManagerExe = "CaddyManager.exe";
    private const string WindowClass = "CaddyProxyManagerTray";

    private const uint WM_TRAY = WM_APP + 1, WM_ACTION_DONE = WM_APP + 2;
    private const uint IconId = 1;
    private const int CmdOpen = 1, CmdCaddyStart = 11, CmdCaddyStop = 12, CmdCaddyRestart = 13,
        CmdManagerStart = 21, CmdManagerStop = 22, CmdManagerRestart = 23, CmdExit = 99;

    private enum Svc { NotInstalled, Stopped, Starting, Stopping, Running, Unknown }

    private IntPtr _hwnd, _icon;
    private uint _taskbarCreated;
    private WndProc? _wndProc; // referenced so the delegate outlives the window
    private Svc _caddy = Svc.Unknown, _manager = Svc.Unknown;
    private string? _busy;
    private (bool Ok, string Text)? _result;

    public int Run()
    {
        using var single = new Mutex(true, @"Local\CaddyProxyManager.Tray", out var first);
        if (!first) return 0; // already running in this session

        // Restarted after an installer update closed it (not after crashes or hangs).
        RegisterApplicationRestart(null, RESTART_NO_CRASH | RESTART_NO_HANG | RESTART_NO_REBOOT);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _wndProc = WndProc;
        var instance = GetModuleHandle(null);
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = instance,
            lpszClassName = WindowClass,
        };
        if (RegisterClassEx(ref wc) == 0) return 2;
        // A hidden top-level window (not message-only): the installer's Restart Manager can then ask it to close.
        _hwnd = CreateWindowEx(WS_EX_TOOLWINDOW, WindowClass, ProductName, WS_OVERLAPPED, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) return 3;

        RefreshStatus();
        AddIcon(); // fails while Explorer is not up yet; retried on TaskbarCreated
        SetTimer(_hwnd, 1, 5000, IntPtr.Zero);
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        return 0;
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_TRAY:
                // NOTIFYICON_VERSION_4: the event is LOWORD(lParam); for WM_CONTEXTMENU the anchor point is in wParam.
                var ev = (uint)((long)lParam & 0xFFFF);
                if (ev == WM_CONTEXTMENU) ShowMenu((short)((long)wParam & 0xFFFF), (short)(((long)wParam >> 16) & 0xFFFF));
                else if (ev is NIN_SELECT or NIN_KEYSELECT) OpenUi();
                return IntPtr.Zero;
            case WM_TIMER:
                if (_busy is null) RefreshStatus();
                UpdateIcon();
                return IntPtr.Zero;
            case WM_ACTION_DONE:
                _busy = null;
                RefreshStatus();
                UpdateIcon();
                if (_result is { } r && r.Text.Length > 0) Balloon(r.Text, !r.Ok);
                _result = null;
                return IntPtr.Zero;
            case WM_QUERYENDSESSION:
                return 1; // sign-out, shutdown, or Restart Manager (installer update): let it go
            case WM_ENDSESSION:
                if (wParam != IntPtr.Zero) RemoveIcon();
                return IntPtr.Zero;
            case WM_CLOSE:
                DestroyWindow(hwnd);
                return IntPtr.Zero;
            case WM_DESTROY:
                RemoveIcon();
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            // Explorer restarted (or started after us): the notification area forgot our icon.
            if (_icon != IntPtr.Zero) { DestroyIcon(_icon); _icon = IntPtr.Zero; }
            AddIcon();
            return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    // ------------------------------------------------------------------ icon

    private NOTIFYICONDATA Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = IconId,
        uFlags = flags,
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };

    private void AddIcon()
    {
        if (_icon == IntPtr.Zero) _icon = LoadTrayIcon();
        var d = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        d.uCallbackMessage = WM_TRAY;
        d.hIcon = _icon;
        d.szTip = Tooltip();
        if (!Shell_NotifyIcon(NIM_ADD, ref d)) return;
        d.uVersion = NOTIFYICON_VERSION_4; // must follow every NIM_ADD
        Shell_NotifyIcon(NIM_SETVERSION, ref d);
    }

    private void UpdateIcon()
    {
        var d = Data(NIF_TIP | NIF_SHOWTIP);
        d.szTip = Tooltip();
        if (!Shell_NotifyIcon(NIM_MODIFY, ref d)) AddIcon();
    }

    private void RemoveIcon()
    {
        var d = Data(0);
        Shell_NotifyIcon(NIM_DELETE, ref d);
    }

    private void Balloon(string text, bool error)
    {
        var d = Data(NIF_INFO);
        d.szInfoTitle = ProductName;
        d.szInfo = text.Length > 255 ? text[..252] + "..." : text;
        d.dwInfoFlags = (error ? NIIF_ERROR : NIIF_INFO) | NIIF_RESPECT_QUIET_TIME;
        Shell_NotifyIcon(NIM_MODIFY, ref d);
    }

    private string Tooltip()
    {
        var tip = $"{ProductName}\nCaddy: {Describe(_caddy)}\nManagement UI: {Describe(_manager)}";
        if (_busy is not null) tip += $"\n{_busy}";
        return tip.Length > 127 ? tip[..127] : tip;
    }

    /// <summary>The small-icon size for the system DPI, cut from the embedded app.ico (16–256 px images).</summary>
    private static IntPtr LoadTrayIcon()
    {
        var size = GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForSystem());
        if (size <= 0) size = GetSystemMetrics(SM_CXSMICON);
        using var s = typeof(TrayApp).Assembly.GetManifestResourceStream("app.ico");
        if (s is null) return IntPtr.Zero;
        var ico = new byte[s.Length];
        s.ReadExactly(ico);
        // ICONDIR: reserved, type, count; then 16-byte entries (width 0 = 256, ..., bytesInRes @8, offset @12).
        int count = BitConverter.ToUInt16(ico, 4), best = -1, bestWidth = 0;
        for (var i = 0; i < count; i++)
        {
            var w = ico[6 + 16 * i] == 0 ? 256 : ico[6 + 16 * i];
            var better = best < 0 || (w >= size ? bestWidth < size || w < bestWidth : w > bestWidth);
            if (better) { best = i; bestWidth = w; }
        }
        if (best < 0) return IntPtr.Zero;
        var length = BitConverter.ToInt32(ico, 6 + 16 * best + 8);
        var offset = BitConverter.ToInt32(ico, 6 + 16 * best + 12);
        return CreateIconFromResourceEx(ico[offset..(offset + length)], (uint)length, true, 0x00030000, size, size, 0);
    }

    // ------------------------------------------------------------------ menu

    private void ShowMenu(int x, int y)
    {
        if (_busy is null) RefreshStatus();
        var menu = CreatePopupMenu();
        try
        {
            void Item(int id, string text, bool enabled = true) =>
                AppendMenu(menu, MF_STRING | (enabled ? 0 : MF_GRAYED), (UIntPtr)(uint)id, text);
            void Separator() => AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            var idle = _busy is null;

            Item(CmdOpen, "Open management UI");
            SetMenuDefaultItem(menu, CmdOpen, 0);
            if (_busy is not null) Item(0, _busy, enabled: false);
            Separator();
            Item(0, $"Caddy: {Describe(_caddy)}", enabled: false);
            Item(CmdCaddyStart, "Start Caddy", idle && _caddy == Svc.Stopped);
            Item(CmdCaddyStop, "Stop Caddy", idle && _caddy is Svc.Running or Svc.Starting);
            Item(CmdCaddyRestart, "Restart Caddy", idle && _caddy == Svc.Running);
            Separator();
            Item(0, $"Management UI: {Describe(_manager)}", enabled: false);
            Item(CmdManagerStart, "Start management UI", idle && _manager == Svc.Stopped);
            Item(CmdManagerStop, "Stop management UI", idle && _manager is Svc.Running or Svc.Starting);
            Item(CmdManagerRestart, "Restart management UI", idle && _manager == Svc.Running);
            Separator();
            Item(CmdExit, "Exit (hide this icon)");

            // A notification-area menu must own the foreground, or it will not close when the user clicks elsewhere.
            SetForegroundWindow(_hwnd);
            var align = GetSystemMetrics(SM_MENUDROPALIGNMENT) != 0 ? TPM_RIGHTALIGN : TPM_LEFTALIGN;
            var cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY | TPM_BOTTOMALIGN | align, x, y, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            Execute(cmd);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void Execute(int cmd)
    {
        switch (cmd)
        {
            case CmdOpen: OpenUi(); break;
            case CmdCaddyStart: RunElevated("caddy start", "Starting Caddy..."); break;
            case CmdCaddyStop: RunElevated("caddy stop", "Stopping Caddy..."); break;
            case CmdCaddyRestart: RunElevated("caddy restart", "Restarting Caddy..."); break;
            case CmdManagerStart: RunElevated("manager start", "Starting the management UI..."); break;
            case CmdManagerStop: RunElevated("manager stop", "Stopping the management UI..."); break;
            case CmdManagerRestart: RunElevated("manager restart", "Restarting the management UI..."); break;
            case CmdExit: DestroyWindow(_hwnd); break;
        }
    }

    // ------------------------------------------------------------------ actions

    private void OpenUi()
    {
        RefreshStatus();
        if (_manager != Svc.Running)
        {
            Balloon(_manager == Svc.NotInstalled
                ? "The management service is not installed."
                : "The management UI is not running. Start it from this icon's menu (right-click).", error: true);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(UiUrl()) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Balloon("Could not open the browser: " + ex.Message, error: true);
        }
    }

    /// <summary>The URL the manager recorded at start-up (HKLM, admin-writable); only http(s) is ever opened.</summary>
    private static string UiUrl()
    {
        using var key = Registry.LocalMachine.OpenSubKey(RegistryKey);
        if (key?.GetValue(UiUrlValue) is string url && Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return uri.AbsoluteUri;
        // Before the manager's first start: the port the installer recorded.
        var port = key?.GetValue("UiPort") is string p && int.TryParse(p, out var n) && n is > 0 and < 65536 ? n : 81;
        return $"http://localhost:{port}/";
    }

    /// <summary>
    /// Runs CaddyManager.exe (next to this exe) elevated: Windows shows the UAC prompt. The result line comes back
    /// through a temp file and is shown as a notification; a declined prompt is silent.
    /// </summary>
    private void RunElevated(string verb, string busyText)
    {
        _busy = busyText;
        UpdateIcon();
        var messageFile = Path.Combine(Path.GetTempPath(), $"cpm-tray-{Guid.NewGuid():N}.txt");
        var worker = new Thread(() =>
        {
            (bool Ok, string Text) result;
            try
            {
                var exe = Path.Combine(AppContext.BaseDirectory, ManagerExe);
                using var p = Process.Start(new ProcessStartInfo(exe, $"{verb} --message-file \"{messageFile}\"")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = AppContext.BaseDirectory,
                }) ?? throw new InvalidOperationException($"{ManagerExe} did not start.");
                p.WaitForExit();
                var text = File.Exists(messageFile) ? File.ReadAllText(messageFile).Trim() : "";
                result = (p.ExitCode == 0, text.Length > 0 ? text : p.ExitCode == 0 ? "Done." : $"Failed (exit code {p.ExitCode}).");
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                result = (true, ""); // the user declined the UAC prompt
            }
            catch (Exception ex)
            {
                result = (false, ex.Message);
            }
            finally
            {
                try { File.Delete(messageFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            _result = result;
            PostMessage(_hwnd, WM_ACTION_DONE, IntPtr.Zero, IntPtr.Zero);
        }) { IsBackground = true, Name = "tray action" };
        worker.Start();
    }

    // ------------------------------------------------------------------ service status (any user may query)

    private void RefreshStatus()
    {
        _caddy = Query(CaddyService);
        _manager = Query(ManagerService);
    }

    private static Svc Query(string name)
    {
        var scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) return Svc.Unknown;
        try
        {
            var service = OpenService(scm, name, SERVICE_QUERY_STATUS);
            if (service == IntPtr.Zero) return Marshal.GetLastWin32Error() == ERROR_SERVICE_DOES_NOT_EXIST ? Svc.NotInstalled : Svc.Unknown;
            try
            {
                if (!QueryServiceStatus(service, out var st)) return Svc.Unknown;
                return st.dwCurrentState switch
                {
                    1 => Svc.Stopped,
                    2 or 5 => Svc.Starting,
                    3 or 6 => Svc.Stopping,
                    4 => Svc.Running,
                    _ => Svc.Unknown, // paused
                };
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(scm); }
    }

    private static string Describe(Svc s) => s switch
    {
        Svc.Running => "running",
        Svc.Stopped => "stopped",
        Svc.Starting => "starting",
        Svc.Stopping => "stopping",
        Svc.NotInstalled => "not installed",
        _ => "unknown",
    };
}
