using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using CaddyManager.Core.Contracts;

namespace CaddyManager.Platform.Windows;

/// <summary>One access control entry of a directory, reduced to what the readiness check needs.</summary>
public sealed record AclEntry(string Sid, string Account, bool Allow, string Rights, bool Inherited);

/// <summary>
/// The data directory holds the database, the secret key, private keys and the setup token: only SYSTEM and
/// Administrators may access it. Reads/evaluates its DACL and restricts it.
/// </summary>
public static class DataDirAcl
{
    public const string CheckId = "system.datadir";

    /// <summary>Well-known principals that include ordinary (non-admin) users.</summary>
    public static readonly IReadOnlyDictionary<string, string> BroadPrincipals = new Dictionary<string, string>
    {
        ["S-1-5-32-545"] = @"BUILTIN\Users",
        ["S-1-5-11"] = @"NT AUTHORITY\Authenticated Users",
        ["S-1-1-0"] = "Everyone",
        ["S-1-5-32-546"] = @"BUILTIN\Guests",
        ["S-1-5-4"] = @"NT AUTHORITY\INTERACTIVE",
        ["S-1-5-7"] = @"NT AUTHORITY\ANONYMOUS LOGON",
    };

    /// <summary>icacls commands that apply the same restriction as the automatic fix (for copy/paste).</summary>
    public static string Script(string dir) =>
        $"icacls \"{dir}\" /inheritance:r /grant:r \"*S-1-5-18:(OI)(CI)F\" \"*S-1-5-32-544:(OI)(CI)F\" " +
        "/remove:g *S-1-5-32-545 *S-1-5-11 *S-1-1-0 *S-1-5-32-546 *S-1-5-4 *S-1-5-7 *S-1-3-0\n" +
        $"icacls \"{dir}\"";

    public static ReadinessCheck Evaluate(string dir, bool isProtected, IReadOnlyList<AclEntry> entries)
    {
        var broad = entries.Where(e => e.Allow && BroadPrincipals.ContainsKey(e.Sid)).ToList();
        var problems = new List<string>();
        if (!isProtected)
            problems.Add("it inherits permissions from its parent (ProgramData grants Users read and create rights there)");
        foreach (var e in broad.DistinctBy(e => e.Sid))
            problems.Add($"{BroadPrincipals[e.Sid]} is granted {e.Rights}{(e.Inherited ? " (inherited)" : "")}");
        var details = "Current ACL:\n" + string.Join('\n', entries.Select(e =>
            $"  {(e.Allow ? "Allow" : "Deny ")} {e.Account} ({e.Sid}): {e.Rights}{(e.Inherited ? " [inherited]" : "")}"));
        if (problems.Count == 0)
            return new ReadinessCheck
            {
                Id = CheckId, Category = "System", Title = "Data directory permissions", Status = CheckStatus.Pass,
                Summary = $"{dir} is protected from inheritance and not accessible to ordinary users.",
                Details = details,
            };
        return new ReadinessCheck
        {
            Id = CheckId, Category = "System", Title = "Data directory permissions", Status = CheckStatus.Warn,
            Summary = $"{dir} is readable by non-administrators: " + string.Join("; ", problems) +
                      ". It contains the database, the secret key, private keys and the setup token.",
            Details = details,
            Remediation = "Click Fix to restrict it to SYSTEM and Administrators (inheritance removed), or run the script in an elevated prompt.",
            Script = Script(dir),
            Fixable = true,
        };
    }

    [SupportedOSPlatform("windows")]
    public static (bool Protected, List<AclEntry> Entries) Read(string dir)
    {
        var sec = new DirectoryInfo(dir).GetAccessControl(AccessControlSections.Access);
        var list = new List<AclEntry>();
        foreach (FileSystemAccessRule r in sec.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            var sid = (SecurityIdentifier)r.IdentityReference;
            string account;
            try { account = sid.Translate(typeof(NTAccount)).Value; }
            catch (IdentityNotMappedException) { account = sid.Value; }
            list.Add(new AclEntry(sid.Value, account, r.AccessControlType == AccessControlType.Allow, r.FileSystemRights.ToString(), r.IsInherited));
        }
        return (sec.AreAccessRulesProtected, list);
    }

    /// <summary>Restricts the directory to SYSTEM and Administrators (full control, inherited by everything below).</summary>
    [SupportedOSPlatform("windows")]
    public static void Harden(string dir)
    {
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(dir).SetAccessControl(sec);
    }
}
