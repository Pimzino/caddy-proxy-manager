using System.Text;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Ops.Auth.Ldap;

internal enum LdapOutcome
{
    Success,
    /// <summary>Unknown user, wrong password, or an account the directory refuses (disabled, locked, expired).</summary>
    InvalidCredentials,
    /// <summary>Valid directory credentials but no mapped group.</summary>
    NotAuthorized,
    /// <summary>The directory could not be used (unreachable, TLS, bind account, ambiguous filter...).</summary>
    Error,
}

internal sealed record LdapAuthResult
{
    public LdapOutcome Outcome { get; init; }
    public UserRole? Role { get; init; }
    public string? Dn { get; init; }
    /// <summary>objectGUID (AD), entryUUID, or the DN.</summary>
    public string? ExternalId { get; init; }
    public string? Email { get; init; }
    public string? DisplayName { get; init; }
    /// <summary>Direct memberOf values plus role groups matched through nesting.</summary>
    public List<string> Groups { get; init; } = new();
    /// <summary>Administrator-facing explanation (never shown to the signing-in user; never contains the password).</summary>
    public string? Message { get; init; }

    public static LdapAuthResult Invalid(string reason) => new() { Outcome = LdapOutcome.InvalidCredentials, Message = reason };
    public static LdapAuthResult Failed(string message) => new() { Outcome = LdapOutcome.Error, Message = message };
}

/// <summary>
/// Verifies a user against LDAP / Active Directory:
/// 1. bind with the service account (or, without one, directly as DOMAIN\user / UPN);
/// 2. find the user with the user filter under the base DN (exactly one entry);
/// 3. verify the password with a bind as the user's DN on a separate connection;
/// 4. map group membership to a role (admin &gt; operator &gt; viewer), following nested groups with
///    LDAP_MATCHING_RULE_IN_CHAIN (1.2.840.113556.1.4.1941) when enabled.
/// </summary>
internal sealed class LdapAuthenticator(ILdapConnector connector, ILogger<LdapAuthenticator> logger)
{
    public const string InChainRule = "1.2.840.113556.1.4.1941";
    internal const int MaxLoginLength = 256;

    private static readonly string[] UserAttributes =
        ["distinguishedName", "objectGUID", "entryUUID", "mail", "userPrincipalName", "displayName", "cn", "sAMAccountName", "memberOf", "userAccountControl"];

    /// <summary>Runs the (synchronous) directory conversation off the request thread with an overall timeout.</summary>
    public async Task<LdapAuthResult> AuthenticateAsync(LdapSettings s, string? bindPassword, string? login, string? password, CancellationToken ct)
    {
        var perOperation = Math.Clamp(s.TimeoutSeconds, 1, 120);
        // connect + service bind + search + user connect + user bind + up to 3 group checks
        var overall = TimeSpan.FromSeconds(perOperation * 4 + 5);
        try
        {
            return await Task.Run(() => Authenticate(s, bindPassword, login, password), ct).WaitAsync(overall, ct);
        }
        catch (TimeoutException)
        {
            return LdapAuthResult.Failed($"The directory did not complete the sign-in within {overall.TotalSeconds:0} seconds. Check that {s.Server}:{s.Port} is reachable.");
        }
    }

    internal LdapAuthResult Authenticate(LdapSettings s, string? bindPassword, string? login, string? password)
    {
        login = (login ?? "").Trim();
        if (login.Length == 0 || login.Length > MaxLoginLength || login.Any(char.IsControl))
            return LdapAuthResult.Invalid("empty or invalid user name");
        // An LDAP simple bind with an empty password is an "unauthenticated bind" that many servers accept:
        // it must never count as a successful sign-in.
        if (string.IsNullOrEmpty(password)) return LdapAuthResult.Invalid("empty password");

        var slash = login.LastIndexOf('\\');
        var searchName = slash >= 0 ? login[(slash + 1)..] : login;
        if (searchName.Length == 0) return LdapAuthResult.Invalid("empty or invalid user name");

        try
        {
            using var session = connector.Connect(s);
            var verified = false;
            if (!string.IsNullOrWhiteSpace(s.BindDn))
            {
                try { session.Bind(s.BindDn.Trim(), bindPassword ?? ""); }
                catch (LdapInvalidCredentialsException ex)
                {
                    return LdapAuthResult.Failed($"The bind account '{s.BindDn.Trim()}' was rejected by the directory ({ex.Reason}). " +
                                                 "Check the bind DN and bind password in Settings → LDAP.");
                }
            }
            else
            {
                // No service account: AD accepts DOMAIN\user and UPNs as simple-bind names.
                if (slash < 0 && !login.Contains('@'))
                    return LdapAuthResult.Invalid("no bind account is configured, so the user name must be DOMAIN\\user or a UPN (user@domain)");
                try { session.Bind(login, password); }
                catch (LdapInvalidCredentialsException ex) { return LdapAuthResult.Invalid(ex.Reason); }
                verified = true;
            }

            var filter = s.UserFilter.Replace("{0}", EscapeFilterValue(searchName), StringComparison.Ordinal);
            List<LdapEntry> found;
            try
            {
                found = session.Search(s.BaseDn.Trim(), filter, LdapScope.Subtree, UserAttributes, sizeLimit: 2);
            }
            catch (LdapTooManyResultsException)
            {
                return LdapAuthResult.Failed($"More than one directory entry matches '{searchName}' with the user filter. Make the user filter more specific.");
            }
            if (found.Count == 0) return LdapAuthResult.Invalid($"no entry matches the user filter under '{s.BaseDn}'");
            if (found.Count > 1)
                return LdapAuthResult.Failed($"More than one directory entry matches '{searchName}' with the user filter. Make the user filter more specific.");
            var user = found[0];

            if (int.TryParse(user.First("userAccountControl"), out var uac) && (uac & 0x2) != 0)
                return LdapAuthResult.Invalid("account disabled");

            if (!verified)
            {
                using var userSession = connector.Connect(s);
                try { userSession.Bind(user.Dn, password); }
                catch (LdapInvalidCredentialsException ex) { return LdapAuthResult.Invalid(ex.Reason); }
            }

            var groups = user.All("memberOf").ToList();
            UserRole? role = null;
            foreach (var (r, groupDn) in RoleGroups(s))
            {
                if (!IsMember(session, s, user, groupDn)) continue;
                role = r;
                if (!groups.Any(g => SameDn(g, groupDn))) groups.Add(groupDn.Trim());
                break;
            }

            var email = EmailFor(user, searchName);
            var result = new LdapAuthResult
            {
                Outcome = role is null ? LdapOutcome.NotAuthorized : LdapOutcome.Success,
                Role = role,
                Dn = user.Dn,
                ExternalId = ExternalIdFor(user),
                Email = email,
                DisplayName = user.First("displayName") ?? user.First("cn") ?? user.First("sAMAccountName") ?? searchName,
                Groups = groups,
                Message = role is null
                    ? "The account is valid but is not a member of the admin, operator or viewer group configured in Settings → LDAP. " +
                      "Note that the primary group (usually 'Domain Users') is not reported through memberOf."
                    : null,
            };
            if (email is null)
                return result with
                {
                    Outcome = LdapOutcome.Error,
                    Message = "The directory account has no mail or userPrincipalName attribute, so it cannot be mapped to a user.",
                };
            return result;
        }
        catch (LdapDirectoryException ex)
        {
            logger.LogWarning("LDAP sign-in for {Login} could not be completed: {Message}", login, ex.Message);
            return LdapAuthResult.Failed(ex.Message);
        }
        catch (LdapInvalidCredentialsException ex)
        {
            return LdapAuthResult.Invalid(ex.Reason);
        }
    }

    private static IEnumerable<(UserRole Role, string GroupDn)> RoleGroups(LdapSettings s)
    {
        if (!string.IsNullOrWhiteSpace(s.AdminGroupDn)) yield return (UserRole.Admin, s.AdminGroupDn);
        if (!string.IsNullOrWhiteSpace(s.OperatorGroupDn)) yield return (UserRole.Operator, s.OperatorGroupDn);
        if (!string.IsNullOrWhiteSpace(s.ViewerGroupDn)) yield return (UserRole.Viewer, s.ViewerGroupDn);
    }

    private static bool IsMember(ILdapSession session, LdapSettings s, LdapEntry user, string groupDn)
    {
        if (user.All("memberOf").Any(g => SameDn(g, groupDn))) return true;
        if (!s.NestedGroups) return false;
        // Base search on the user object: matches only when the user is a (transitive) member of the group.
        var filter = $"(memberOf:{InChainRule}:={EscapeFilterValue(groupDn.Trim())})";
        return session.Search(user.Dn, filter, LdapScope.Base, ["distinguishedName"], sizeLimit: 1).Count > 0;
    }

    private static string? ExternalIdFor(LdapEntry user)
    {
        if (user.ObjectGuid is { Length: 16 } g) return new Guid(g).ToString();
        return user.First("entryUUID") ?? user.Dn.ToLowerInvariant();
    }

    private static string? EmailFor(LdapEntry user, string searchName)
    {
        foreach (var candidate in new[] { user.First("mail"), user.First("userPrincipalName") })
        {
            var e = UserRules.NormalizeEmail(candidate);
            if (UserRules.IsValidEmail(e)) return e;
        }
        // Fall back to sAMAccountName@dns.domain derived from the DN's DC= components.
        var dc = string.Join('.', user.Dn.Split(',').Select(p => p.Trim())
            .Where(p => p.StartsWith("DC=", StringComparison.OrdinalIgnoreCase)).Select(p => p[3..]));
        var sam = user.First("sAMAccountName") ?? searchName;
        var fallback = UserRules.NormalizeEmail($"{sam}@{dc}");
        return dc.Length > 0 && UserRules.IsValidEmail(fallback) ? fallback : null;
    }

    /// <summary>RFC 4515 escaping of a value inserted into an LDAP filter (prevents LDAP injection).</summary>
    internal static string EscapeFilterValue(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append(@"\5c"); break;
                case '*': sb.Append(@"\2a"); break;
                case '(': sb.Append(@"\28"); break;
                case ')': sb.Append(@"\29"); break;
                case '\0': sb.Append(@"\00"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Case-insensitive DN comparison that ignores spaces around RDN separators.</summary>
    internal static bool SameDn(string a, string b) => string.Equals(NormalizeDn(a), NormalizeDn(b), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeDn(string dn) =>
        string.Join(',', dn.Split(',').Select(rdn => string.Join('=', rdn.Split('=', 2).Select(x => x.Trim()))));
}
