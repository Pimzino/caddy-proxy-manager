using System.Runtime.InteropServices;

namespace CaddyManager.Ops.Backup;

/// <summary>
/// The computer account LocalSystem uses on the network ("The LocalSystem account ... presents the computer's credentials to
/// remote servers": https://learn.microsoft.com/en-us/windows/win32/services/localsystem-account), e.g. CORP\WEB01$.
/// Environment.UserDomainName cannot be used for this: for the SYSTEM account it returns "NT AUTHORITY", not the domain.
/// The NetBIOS domain name comes from NetGetJoinInformation
/// (https://learn.microsoft.com/en-us/windows/win32/api/lmjoin/nf-lmjoin-netgetjoininformation).
/// </summary>
internal static class ComputerAccount
{
    private const int NetSetupDomainName = 3;

    /// <summary>"DOMAIN\COMPUTER$" on a domain member, "COMPUTER$ (not domain-joined)" otherwise.</summary>
    public static string Name() => Format(Environment.MachineName, JoinedDomain());

    internal static string Format(string machine, string? domain) =>
        string.IsNullOrWhiteSpace(domain) ? $"{machine}$ (this server is not domain-joined, so a share on another server cannot grant it access)" : $"{domain}\\{machine}$";

    /// <summary>NetBIOS name of the domain this computer is joined to, or null (workgroup, not Windows, API failure).</summary>
    public static string? JoinedDomain()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            if (NetGetJoinInformation(null, out var buffer, out var status) != 0) return null;
            try
            {
                return status == NetSetupDomainName ? Marshal.PtrToStringUni(buffer) : null;
            }
            finally
            {
                NetApiBufferFree(buffer);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetGetJoinInformation(string? server, out IntPtr nameBuffer, out int bufferType);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}
