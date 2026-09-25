using System.Security.AccessControl;
using System.Security.Principal;

namespace CaddyManager;

/// <summary>
/// Keeps DataDir (database, Data Protection key ring, certificate private keys, setup token, logs) readable
/// only by SYSTEM and Administrators. %ProgramData% grants BUILTIN\Users read + create-files on subfolders by
/// default, which would let any local user read secrets or pre-create files (e.g. a setup token or a staged
/// restore) that the LocalSystem service later trusts.
/// </summary>
internal static class DataDirSecurity
{
    /// <summary>Returns a warning when the directory could not be restricted, else null. No-op off Windows.</summary>
    public static string? EnsureRestricted(string dir)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            // Only when running as SYSTEM (the service) or elevated: an unelevated developer would lock
            // themselves out of their own data directory.
            using (var me = WindowsIdentity.GetCurrent())
            {
                if (!me.IsSystem && !new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator)) return null;
            }
            var info = new DirectoryInfo(dir);
            var current = info.GetAccessControl(AccessControlSections.Access);
            // Already protected (installer, or an administrator's deliberate custom ACL): leave it alone.
            if (current.AreAccessRulesProtected) return null;

            var sec = new DirectorySecurity();
            sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(sec);
            return null;
        }
        catch (Exception ex)
        {
            return $"Could not restrict the permissions of {dir} to SYSTEM and Administrators ({ex.Message}). " +
                   "Local users may be able to read the database and keys; run 'CaddyManager.exe install' elevated to fix it.";
        }
    }
}
