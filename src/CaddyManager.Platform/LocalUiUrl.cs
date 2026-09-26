using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using CaddyManager.Core;
using Microsoft.Win32;

namespace CaddyManager.Platform;

/// <summary>
/// The management UI's URL as reachable from this server, published in HKLM (<see cref="AppPaths.UiUrlValue"/>) for the
/// tray companion, which runs as the signed-in user and cannot read the settings database.
/// </summary>
public static class LocalUiUrl
{
    /// <param name="http">The HTTP listener actually bound.</param>
    /// <param name="https">The HTTPS listener, when it is running.</param>
    /// <param name="redirectToHttps">True when plain HTTP redirects to HTTPS (then HTTPS is linked directly).</param>
    /// <param name="fqdn">This server's DNS name: used for HTTPS on a wildcard bind, so the certificate name matches.</param>
    public static string For(IPEndPoint http, IPEndPoint? https, bool redirectToHttps, string fqdn)
    {
        if (redirectToHttps && https is not null)
        {
            var secureHost = IsWildcard(https.Address) ? fqdn : Host(https.Address);
            return $"https://{secureHost}:{https.Port}/";
        }
        var host = IsWildcard(http.Address) ? "localhost" : Host(http.Address);
        return $"http://{host}:{http.Port}/";
    }

    private static bool IsWildcard(IPAddress a) => a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any);

    private static string Host(IPAddress a) => a.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{a}]" : a.ToString();

    [SupportedOSPlatform("windows")]
    public static void Publish(string url)
    {
        using var key = Registry.LocalMachine.CreateSubKey(AppPaths.RegistryKey);
        key.SetValue(AppPaths.UiUrlValue, url, RegistryValueKind.String);
    }
}
