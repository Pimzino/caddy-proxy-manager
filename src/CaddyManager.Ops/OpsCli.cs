using CaddyManager.Core;
using CaddyManager.Core.Infrastructure;
using CaddyManager.Core.Models;
using CaddyManager.Ops.Auth;
using CaddyManager.Ops.Backup;

namespace CaddyManager.Ops;

/// <summary>Command line verbs handled before the web host starts (reset-password, ...).</summary>
public static class OpsCli
{
    private const string LockedMessage = "Stop the CaddyProxyManager service first";

    /// <summary>Returns an exit code if args were an Ops CLI verb, otherwise null.</summary>
    public static Task<int?> TryRunAsync(string[] args) => TryRunAsync(args, Console.Out, Console.Error);

    internal static Task<int?> TryRunAsync(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0) return Task.FromResult<int?>(null);
        var verb = args[0].TrimStart('-', '/').ToLowerInvariant();
        int? result = verb switch
        {
            "reset-password" => Run(args, stdout, stderr, ResetPassword),
            "list-users" => Run(args, stdout, stderr, ListUsers),
            "apply-restore" => ApplyRestore(args, stdout, stderr),
            _ => null,
        };
        return Task.FromResult(result);
    }

    private static int Run(string[] args, TextWriter stdout, TextWriter stderr,
        Func<Dictionary<string, string?>, LiteStore, TextWriter, TextWriter, int> action)
    {
        Dictionary<string, string?> opts;
        try { opts = ParseOptions(args); }
        catch (ArgumentException ex)
        {
            stderr.WriteLine(ex.Message);
            PrintUsage(stderr);
            return 2;
        }
        var paths = new AppPaths(opts.GetValueOrDefault("data-dir"));
        if (!File.Exists(paths.DbFile))
        {
            stderr.WriteLine($"No database found at {paths.DbFile}. Has {AppPaths.ProductName} been started at least once?");
            return 1;
        }
        LiteStore store;
        try
        {
            store = new LiteStore(paths);
        }
        catch (Exception ex) when (IsLocked(ex))
        {
            stderr.WriteLine($"{LockedMessage} (the database {paths.DbFile} is in use): net stop {AppPaths.ManagerServiceName}");
            return 1;
        }
        using (store)
        {
            try
            {
                return action(opts, store, stdout, stderr);
            }
            catch (Exception ex) when (IsLocked(ex))
            {
                stderr.WriteLine($"{LockedMessage} (the database {paths.DbFile} is in use): net stop {AppPaths.ManagerServiceName}");
                return 1;
            }
        }
    }

    private static bool IsLocked(Exception ex) =>
        ex is IOException || ex.InnerException is IOException ||
        (ex is LiteDB.LiteException le && le.Message.Contains("locked", StringComparison.OrdinalIgnoreCase));

    private static int ResetPassword(Dictionary<string, string?> opts, LiteStore store, TextWriter stdout, TextWriter stderr)
    {
        var email = UserRules.NormalizeEmail(opts.GetValueOrDefault("email"));
        if (email.Length == 0)
        {
            stderr.WriteLine("--email is required.");
            PrintUsage(stderr);
            return 2;
        }
        var user = UserRules.FindByEmail(store, email);
        if (user is null)
        {
            var all = store.Col<User>().FindAll().Select(u => u.Email).OrderBy(e => e).ToList();
            stderr.WriteLine($"No user with e-mail '{email}'." + (all.Count > 0 ? " Existing users: " + string.Join(", ", all) : " No users exist yet — use the setup page."));
            return 1;
        }

        var generated = !opts.TryGetValue("password", out var password) || string.IsNullOrEmpty(password);
        if (generated) password = PasswordPolicy.Generate();
        if (PasswordPolicy.Check(password, user.Email) is { } err)
        {
            stderr.WriteLine(err);
            return 2;
        }

        user.PasswordHash = Passwords.Hash(password!);
        user.SecurityStamp++;
        user.UpdatedAt = DateTime.UtcNow;
        var enabled = false;
        if (opts.ContainsKey("enable") && user.Disabled)
        {
            user.Disabled = false;
            enabled = true;
        }
        store.Col<User>().Update(user);
        store.Col<AuditEntry>().Insert(new AuditEntry
        {
            UserName = "cli:" + Environment.UserName,
            Action = "passwordReset",
            ObjectType = "user",
            ObjectId = user.Id,
            ObjectName = user.Email,
            Details = "Password reset from the command line" + (enabled ? "; account re-enabled" : ""),
        });

        stdout.WriteLine($"Password for {user.Email} ({CpmClaims.RoleValue(user.Role)}) has been reset. Existing sessions are signed out.");
        if (generated) stdout.WriteLine($"New password: {password}");
        if (enabled) stdout.WriteLine("The account has been re-enabled.");
        else if (user.Disabled) stdout.WriteLine("Note: this account is DISABLED. Add --enable to re-enable it.");
        return 0;
    }

    private static int ListUsers(Dictionary<string, string?> opts, LiteStore store, TextWriter stdout, TextWriter stderr)
    {
        var users = store.Col<User>().FindAll().OrderBy(u => u.Email).ToList();
        if (users.Count == 0)
        {
            stdout.WriteLine("No users exist yet. Complete setup in the web UI (token in setup-token.txt).");
            return 0;
        }
        var emailWidth = Math.Max(5, users.Max(u => u.Email.Length));
        var nameWidth = Math.Max(4, Math.Min(30, users.Max(u => u.Name.Length)));
        stdout.WriteLine($"{"EMAIL".PadRight(emailWidth)}  {"NAME".PadRight(nameWidth)}  {"ROLE",-8}  {"STATUS",-8}  LAST LOGIN (UTC)");
        foreach (var u in users)
        {
            var name = u.Name.Length > nameWidth ? u.Name[..(nameWidth - 1)] + "…" : u.Name;
            stdout.WriteLine($"{u.Email.PadRight(emailWidth)}  {name.PadRight(nameWidth)}  {CpmClaims.RoleValue(u.Role),-8}  {(u.Disabled ? "disabled" : "enabled"),-8}  {u.LastLoginAt?.ToString("yyyy-MM-dd HH:mm") ?? "never"}");
        }
        return 0;
    }

    private static int ApplyRestore(string[] args, TextWriter stdout, TextWriter stderr)
    {
        Dictionary<string, string?> opts;
        try { opts = ParseOptions(args); }
        catch (ArgumentException ex)
        {
            stderr.WriteLine(ex.Message);
            return 2;
        }
        var paths = new AppPaths(opts.GetValueOrDefault("data-dir"));
        if (!RestoreStager.HasPendingRestore(paths))
        {
            stdout.WriteLine("No staged restore found. Upload a backup under Administration → Backup first.");
            return 0;
        }
        // Make sure the database is not open (service running).
        if (File.Exists(paths.DbFile))
        {
            try
            {
                using var probe = new FileStream(paths.DbFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                stderr.WriteLine($"{LockedMessage}: net stop {AppPaths.ManagerServiceName}");
                return 1;
            }
        }
        var outcome = RestoreStager.ApplyPendingRestore(paths);
        if (RestoreStager.LastOutcomeFailed)
        {
            stderr.WriteLine(outcome);
            return 1;
        }
        stdout.WriteLine(outcome);
        return 0;
    }

    private static Dictionary<string, string?> ParseOptions(string[] args)
    {
        var opts = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Unexpected argument '{a}'.");
            var name = a[2..];
            string? value = null;
            var eq = name.IndexOf('=');
            if (eq >= 0)
            {
                value = name[(eq + 1)..];
                name = name[..eq];
            }
            else if (name is "email" or "password" or "data-dir")
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"--{name} needs a value.");
                value = args[++i];
            }
            opts[name] = value;
        }
        foreach (var k in opts.Keys)
            if (k is not ("email" or "password" or "data-dir" or "enable"))
                throw new ArgumentException($"Unknown option '--{k}'.");
        return opts;
    }

    private static void PrintUsage(TextWriter w)
    {
        w.WriteLine("Usage:");
        w.WriteLine("  CaddyManager.exe reset-password --email <email> [--password <password>] [--enable]");
        w.WriteLine("      Resets a UI user's password (a strong password is generated and printed when omitted).");
        w.WriteLine("  CaddyManager.exe list-users");
        w.WriteLine("  CaddyManager.exe apply-restore      Applies a backup staged in the UI (service must be stopped).");
        w.WriteLine($"Stop the {AppPaths.ManagerServiceName} service before running these commands.");
    }
}
