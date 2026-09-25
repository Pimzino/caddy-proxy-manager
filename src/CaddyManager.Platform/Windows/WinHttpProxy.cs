using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace CaddyManager.Platform.Windows;

/// <summary>The machine-wide WinHTTP proxy (netsh winhttp set proxy).</summary>
/// <param name="Proxy">Proxy server list, or null for direct access.</param>
/// <param name="Bypass">Bypass list, or null.</param>
public sealed record WinHttpProxySetting(string? Proxy, string? Bypass)
{
    public bool IsDirect => string.IsNullOrWhiteSpace(Proxy);
}

/// <summary>
/// Reads the default WinHTTP proxy with WinHttpGetDefaultProxyConfiguration instead of parsing the (localised) output of
/// "netsh winhttp show proxy": "If the registry contains a list of proxy servers, the dwAccessType member of pProxyInfo
/// is set to WINHTTP_ACCESS_TYPE_NAMED_PROXY. Otherwise, it is set to WINHTTP_ACCESS_TYPE_NO_PROXY." The strings are
/// allocated by WinHTTP and freed with GlobalFree.
/// https://learn.microsoft.com/en-us/windows/win32/api/winhttp/nf-winhttp-winhttpgetdefaultproxyconfiguration
/// </summary>
[SupportedOSPlatform("windows")]
public static class WinHttpProxy
{
    private const uint AccessTypeNamedProxy = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINHTTP_PROXY_INFO
    {
        public uint dwAccessType;
        public IntPtr lpszProxy;
        public IntPtr lpszProxyBypass;
    }

    [DllImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinHttpGetDefaultProxyConfiguration(ref WINHTTP_PROXY_INFO info);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr mem);

    /// <summary>Throws Win32Exception when the configuration cannot be read.</summary>
    public static WinHttpProxySetting Read()
    {
        var info = new WINHTTP_PROXY_INFO();
        if (!WinHttpGetDefaultProxyConfiguration(ref info))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var proxy = info.lpszProxy == IntPtr.Zero ? null : Marshal.PtrToStringUni(info.lpszProxy);
            var bypass = info.lpszProxyBypass == IntPtr.Zero ? null : Marshal.PtrToStringUni(info.lpszProxyBypass);
            return info.dwAccessType == AccessTypeNamedProxy && !string.IsNullOrWhiteSpace(proxy)
                ? new WinHttpProxySetting(proxy, string.IsNullOrWhiteSpace(bypass) ? null : bypass)
                : new WinHttpProxySetting(null, null);
        }
        finally
        {
            if (info.lpszProxy != IntPtr.Zero) GlobalFree(info.lpszProxy);
            if (info.lpszProxyBypass != IntPtr.Zero) GlobalFree(info.lpszProxyBypass);
        }
    }
}
