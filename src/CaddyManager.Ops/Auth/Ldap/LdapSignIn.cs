using CaddyManager.Core;
using CaddyManager.Core.Models;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Ops.Auth.Ldap;

/// <summary>
/// Sign-in with a directory account: authenticates through <see cref="LdapAuthenticator"/> and provisions / updates the
/// local <see cref="User"/> record (ExternalSource "ldap", no local password, role from group mapping at each sign-in).
/// Directory accounts are matched by ExternalId only — never linked to a local account, even with the same e-mail address.
/// </summary>
internal sealed class LdapSignIn(IStore store, ISecretProtector secrets, LdapAuthenticator authenticator, UserSnapshotCache cache,
    ILogger<LdapSignIn> logger)
{
    public const string Source = "ldap";

    public LdapSettings Settings() => store.GetSettings<LdapSettings>();

    public Task<LdapAuthResult> AuthenticateAsync(LdapSettings s, string? login, string? password, CancellationToken ct)
    {
        string? bindPassword = null;
        if (!string.IsNullOrEmpty(s.BindPasswordProtected))
        {
            try { bindPassword = secrets.Unprotect(s.BindPasswordProtected); }
            catch (Exception ex)
            {
                logger.LogError(ex, "The stored LDAP bind password cannot be decrypted (database restored from another server?)");
                return Task.FromResult(LdapAuthResult.Failed(
                    "The stored bind password cannot be decrypted on this server (for example after restoring a backup from another server). Enter it again in Settings → LDAP."));
            }
        }
        return authenticator.AuthenticateAsync(s, bindPassword, login, password, ct);
    }

    public sealed record ProvisionResult(User? User, string? Error, bool Disabled = false);

    /// <summary>Creates or updates the user for a successful directory sign-in. Serialised to keep e-mail uniqueness checks atomic.</summary>
    public ProvisionResult Provision(LdapAuthResult r)
    {
        if (r.Outcome != LdapOutcome.Success || r.Role is null || r.ExternalId is null || r.Email is null)
            throw new InvalidOperationException("Only successful directory sign-ins can be provisioned.");
        lock (ProvisionLock)
        {
            var col = store.Col<User>();
            var now = DateTime.UtcNow;
            var existing = col.FindOne(u => u.ExternalSource == Source && u.ExternalId == r.ExternalId);
            var name = string.IsNullOrWhiteSpace(r.DisplayName) ? r.Email : r.DisplayName.Trim();

            if (existing is null)
            {
                if (UserRules.FindByEmail(store, r.Email) is { } owner)
                {
                    var why = owner.ExternalSource is null
                        ? $"a local account with the e-mail address '{r.Email}' already exists (directory accounts are never linked to local accounts)"
                        : $"another directory account (id {owner.ExternalId}) already uses the e-mail address '{r.Email}' — delete that stale user under Users";
                    return new ProvisionResult(null, why);
                }
                var user = new User
                {
                    Email = r.Email,
                    Name = name,
                    Role = r.Role.Value,
                    PasswordHash = "",
                    ExternalSource = Source,
                    ExternalId = r.ExternalId,
                    LastLoginAt = now,
                };
                col.Insert(user);
                return new ProvisionResult(user, null);
            }

            if (existing.Disabled) return new ProvisionResult(existing, null, Disabled: true);

            if (existing.Email != r.Email)
            {
                if (UserRules.FindByEmail(store, r.Email) is { } other && other.Id != existing.Id)
                    logger.LogWarning("Directory account {Dn} now has e-mail {Email}, which another user already uses; keeping {Old}",
                        r.Dn, r.Email, existing.Email);
                else existing.Email = r.Email;
            }
            existing.Name = name;
            existing.Role = r.Role.Value;
            existing.LastLoginAt = now;
            existing.UpdatedAt = now;
            col.Update(existing);
            cache.Invalidate(existing.Id);
            return new ProvisionResult(existing, null);
        }
    }

    /// <summary>
    /// The directory says the account no longer has a role: sign out its existing sessions (security stamp) so a
    /// removed group membership takes effect immediately instead of at session expiry.
    /// </summary>
    public User? RevokeSessions(LdapAuthResult r)
    {
        if (r.ExternalId is null) return null;
        lock (ProvisionLock)
        {
            var col = store.Col<User>();
            var existing = col.FindOne(u => u.ExternalSource == Source && u.ExternalId == r.ExternalId);
            if (existing is null) return null;
            existing.SecurityStamp++;
            existing.UpdatedAt = DateTime.UtcNow;
            col.Update(existing);
            cache.Invalidate(existing.Id);
            return existing;
        }
    }

    private static readonly object ProvisionLock = new();
}
