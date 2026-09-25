using System.Security.Claims;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using CaddyManager.Ops.Auth.Ldap;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Ops.Auth;

public sealed record SetupRequest(string? Token, string? Email, string? Name, string? Password);
public sealed record LoginRequest(string? Email, string? Password);
public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);
public sealed record CreateUserRequest(string? Email, string? Name, string? Role, string? Password);
public sealed record UpdateUserRequest(string? Email, string? Name, string? Role, bool? Disabled, string? Password);

internal static class AuthEndpoints
{
    // bcrypt hash of a random string: verifying against it makes unknown-user logins cost the same as real ones.
    private static readonly Lazy<string> DummyHash = new(() => Passwords.Hash(Guid.NewGuid().ToString("N")));

    private static async Task<IResult> CompleteSignInAsync(HttpContext ctx, IStore store, IAuditLog audit, User user, string? details)
    {
        if (user.ExternalSource is null)
        {
            user.LastLoginAt = DateTime.UtcNow;
            store.Col<User>().Update(user);
        }
        await AuthSetup.SignInAsync(ctx, user);
        ctx.User = CpmClaims.CreatePrincipal(user, AuthSetup.Scheme);
        audit.Record("login", "user", user.Id, user.Email, details);
        return Results.Ok(UserDto.From(user));
    }

    public static void Map(IEndpointRouteBuilder app)
    {
        // ------------------------------------------------------------ setup
        var setup = app.MapGroup("/api/setup").AllowAnonymous();
        setup.MapGet("/status", (SetupState state, AppPaths paths) =>
            Results.Ok(new { needsSetup = state.NeedsSetup, setupTokenPath = paths.SetupTokenFile }));

        setup.MapPost("/", async (SetupRequest req, HttpContext ctx, SetupState state, IStore store, IAuditLog audit,
            UserSnapshotCache cache, ILogger<SetupState> logger) =>
        {
            if (!state.NeedsSetup)
                return Results.Problem(title: "Already set up", detail: "An administrator account already exists. Sign in instead.",
                    statusCode: StatusCodes.Status403Forbidden);
            if (!state.Verify(req.Token))
            {
                logger.LogWarning("Setup attempt with an invalid token from {Ip}", CurrentUser.FormatIp(ctx.Connection.RemoteIpAddress));
                return ApiResults.BadRequest("The setup token is not valid. Copy it from the setup-token.txt file shown on this page.",
                    new Dictionary<string, string[]> { ["token"] = ["Invalid setup token."] });
            }

            var email = UserRules.NormalizeEmail(req.Email);
            var v = new Validator();
            v.Require(UserRules.IsValidEmail(email), "email", "A valid e-mail address is required.");
            v.Require(!string.IsNullOrWhiteSpace(req.Name), "name", "Name is required.");
            if (PasswordPolicy.Check(req.Password, email) is { } pwErr) v.Add("password", pwErr);
            if (!v.IsValid) return v.ToResult();

            User user;
            lock (state.CreationLock)
            {
                if (!state.NeedsSetup)
                    return Results.Problem(title: "Already set up", detail: "An administrator account already exists. Sign in instead.",
                        statusCode: StatusCodes.Status403Forbidden);
                user = new User
                {
                    Email = email,
                    Name = req.Name!.Trim(),
                    PasswordHash = Passwords.Hash(req.Password!),
                    Role = UserRole.Admin,
                    LastLoginAt = DateTime.UtcNow,
                };
                store.Col<User>().Insert(user);
            }
            state.Complete();
            cache.Invalidate(user.Id);
            await AuthSetup.SignInAsync(ctx, user);
            ctx.User = CpmClaims.CreatePrincipal(user, AuthSetup.Scheme);
            audit.Record("setup", "user", user.Id, user.Email, "Initial administrator created");
            logger.LogInformation("Initial setup completed; administrator {Email} created", user.Email);
            return Results.Ok(UserDto.From(user));
        }).RequireLoginThrottle();

        // ------------------------------------------------------------ auth
        var auth = app.MapGroup("/api/auth");
        auth.MapPost("/login", async (LoginRequest req, HttpContext ctx, IStore store, IAuditLog audit, LdapSignIn ldap,
            ILogger<SetupState> logger, CancellationToken ct) =>
        {
            var login = (req.Email ?? "").Trim();
            var email = UserRules.NormalizeEmail(login);
            var ip = CurrentUser.FormatIp(ctx.Connection.RemoteIpAddress);

            // 1. Local accounts first (break-glass when the directory is unavailable). Directory accounts have no
            //    local password hash, so they never verify here.
            var user = string.IsNullOrEmpty(email) ? null : UserRules.FindByEmail(store, email);
            var local = user is { ExternalSource: null } ? user : null;
            var ok = Passwords.Verify(req.Password ?? "", local?.PasswordHash ?? DummyHash.Value) && local is not null;
            if (ok)
            {
                if (local!.Disabled)
                {
                    audit.Record("loginFailed", "user", local.Id, local.Email, "Account disabled");
                    return Results.Problem(title: "Invalid credentials", detail: "This account is disabled. Ask an administrator to enable it.",
                        statusCode: StatusCodes.Status401Unauthorized);
                }
                return await CompleteSignInAsync(ctx, store, audit, local, null);
            }

            // 2. LDAP / Active Directory (e-mail, UPN, DOMAIN\user or user).
            var ldapSettings = ldap.Settings();
            if (ldapSettings.Enabled && login.Length > 0 && !string.IsNullOrEmpty(req.Password))
            {
                var r = await ldap.AuthenticateAsync(ldapSettings, login, req.Password, ct);
                switch (r.Outcome)
                {
                    case LdapOutcome.Success:
                        var p = ldap.Provision(r);
                        if (p.User is null)
                        {
                            logger.LogWarning("Directory sign-in for {Login} from {Ip} refused: {Reason}", login, ip, p.Error);
                            audit.Record("loginFailed", "user", null, login, $"LDAP: {p.Error}");
                            return Results.Problem(title: "Not authorised",
                                detail: "Your directory account cannot be used here because its e-mail address belongs to another account. Ask an administrator.",
                                statusCode: StatusCodes.Status403Forbidden);
                        }
                        if (p.Disabled)
                        {
                            audit.Record("loginFailed", "user", p.User.Id, p.User.Email, "LDAP: account disabled in Caddy Proxy Manager");
                            return Results.Problem(title: "Invalid credentials", detail: "This account is disabled. Ask an administrator to enable it.",
                                statusCode: StatusCodes.Status401Unauthorized);
                        }
                        return await CompleteSignInAsync(ctx, store, audit, p.User, $"LDAP {r.Dn}; role {CpmClaims.RoleValue(p.User.Role)}");

                    case LdapOutcome.NotAuthorized:
                        var revoked = ldap.RevokeSessions(r);
                        logger.LogWarning("Directory sign-in for {Login} ({Dn}) from {Ip} refused: no mapped group", login, r.Dn, ip);
                        audit.Record("loginFailed", "user", revoked?.Id, r.Email ?? login,
                            $"LDAP: {r.Dn} is not a member of a group mapped to a role" + (revoked is null ? "" : "; existing sessions signed out"));
                        return Results.Problem(title: "Not authorised",
                            detail: "Your directory account is valid but is not a member of a group that is allowed to use Caddy Proxy Manager. Ask an administrator.",
                            statusCode: StatusCodes.Status403Forbidden);

                    case LdapOutcome.Error:
                        logger.LogError("Directory sign-in for {Login} from {Ip} failed: {Message}", login, ip, r.Message);
                        audit.Record("loginFailed", "user", null, login, $"LDAP error: {r.Message}");
                        // A local account's wrong password must not be masked as a directory outage.
                        if (local is null)
                            return Results.Problem(title: "Directory unavailable",
                                detail: "The directory server could not be used to verify your account. Try again later; local accounts can still sign in.",
                                statusCode: StatusCodes.Status503ServiceUnavailable);
                        break;

                    default:
                        logger.LogWarning("Failed directory sign-in for {Login} from {Ip}: {Reason}", login, ip, r.Message);
                        audit.Record("loginFailed", "user", null, login, $"LDAP: {r.Message}");
                        return Results.Problem(title: "Invalid credentials", detail: "The user name or password is incorrect.",
                            statusCode: StatusCodes.Status401Unauthorized);
                }
            }

            logger.LogWarning("Failed sign-in for {Email} from {Ip}", email, ip);
            audit.Record("loginFailed", "user", user?.Id, email, "Invalid credentials");
            return Results.Problem(title: "Invalid credentials", detail: "The e-mail address or password is incorrect.",
                statusCode: StatusCodes.Status401Unauthorized);
        }).AllowAnonymous().RequireLoginThrottle();

        auth.MapPost("/logout", async (HttpContext ctx, IAuditLog audit, ICurrentUser current, SessionRevocations revocations) =>
        {
            if (ctx.User.Identity?.IsAuthenticated == true)
            {
                audit.Record("logout", "user", current.UserId, current.UserName);
                // Cookies are self-contained tickets: revoke this one server-side so a copy of it stops working too.
                if (ctx.User.FindFirstValue(CpmClaims.SessionId) is { Length: > 0 } sid)
                {
                    var ticket = await ctx.AuthenticateAsync(AuthSetup.Scheme);
                    revocations.Revoke(sid, ticket.Properties?.ExpiresUtc?.UtcDateTime ?? DateTime.UtcNow.AddHours(720));
                }
            }
            await ctx.SignOutAsync(AuthSetup.Scheme);
            return Results.NoContent();
        }).AllowAnonymous();

        auth.MapGet("/me", (ClaimsPrincipal principal, IStore store) =>
            store.Col<User>().FindById(principal.FindFirstValue(CpmClaims.UserId)) is { } u
                ? Results.Ok(UserDto.From(u))
                : Results.Problem(title: "Not signed in", detail: "Sign in to use this resource.", statusCode: StatusCodes.Status401Unauthorized))
            .RequireAuthorization(Policies.Viewer);

        auth.MapPost("/change-password", async (ChangePasswordRequest req, HttpContext ctx, IStore store, IAuditLog audit,
            UserSnapshotCache cache) =>
        {
            var user = store.Col<User>().FindById(ctx.User.FindFirstValue(CpmClaims.UserId));
            if (user is null) return ApiResults.NotFound("User");
            if (user.ExternalSource is not null)
                return ApiResults.BadRequest("Your account is managed by the directory (Active Directory/LDAP). Change your password there, for example with Ctrl+Alt+Del → Change a password.");
            if (!Passwords.Verify(req.CurrentPassword ?? "", user.PasswordHash))
                return ApiResults.BadRequest("The current password is incorrect.",
                    new Dictionary<string, string[]> { ["currentPassword"] = ["The current password is incorrect."] });
            if (PasswordPolicy.Check(req.NewPassword, user.Email) is { } err)
                return ApiResults.BadRequest(err, new Dictionary<string, string[]> { ["newPassword"] = [err] });
            if (Passwords.Verify(req.NewPassword!, user.PasswordHash))
                return ApiResults.BadRequest("The new password must differ from the current one.",
                    new Dictionary<string, string[]> { ["newPassword"] = ["The new password must differ from the current one."] });

            user.PasswordHash = Passwords.Hash(req.NewPassword!);
            user.SecurityStamp++;
            user.UpdatedAt = DateTime.UtcNow;
            store.Col<User>().Update(user);
            cache.Invalidate(user.Id);
            // Other sessions are invalidated by the new stamp; keep this one signed in.
            await AuthSetup.SignInAsync(ctx, user);
            audit.Record("passwordChanged", "user", user.Id, user.Email);
            return Results.NoContent();
        }).RequireAuthorization(Policies.Viewer);

        // ------------------------------------------------------------ users (admin)
        var users = app.MapGroup("/api/users").RequireAuthorization(Policies.Admin);
        users.MapGet("/", (IStore store) =>
            store.Col<User>().FindAll().OrderBy(u => u.Email, StringComparer.OrdinalIgnoreCase).Select(UserDto.From).ToList());

        users.MapGet("/{id}", (string id, IStore store) =>
            store.Col<User>().FindById(id) is { } u ? Results.Ok(UserDto.From(u)) : ApiResults.NotFound("User"));

        users.MapPost("/", (CreateUserRequest req, IStore store, IAuditLog audit) =>
        {
            var email = UserRules.NormalizeEmail(req.Email);
            var role = CpmClaims.ParseRole(req.Role);
            var v = new Validator();
            v.Require(UserRules.IsValidEmail(email), "email", "A valid e-mail address is required.");
            v.Require(!string.IsNullOrWhiteSpace(req.Name), "name", "Name is required.");
            v.Require(role is not null, "role", "Role must be one of: viewer, operator, admin.");
            if (PasswordPolicy.Check(req.Password, email) is { } pwErr) v.Add("password", pwErr);
            if (!v.IsValid) return v.ToResult();
            if (UserRules.FindByEmail(store, email) is not null)
                return ApiResults.Conflict($"A user with the e-mail address '{email}' already exists.");

            var user = new User
            {
                Email = email,
                Name = req.Name!.Trim(),
                Role = role!.Value,
                PasswordHash = Passwords.Hash(req.Password!),
            };
            store.Col<User>().Insert(user);
            audit.Record("created", "user", user.Id, user.Email, $"Role: {CpmClaims.RoleValue(user.Role)}");
            return Results.Ok(UserDto.From(user));
        });

        users.MapPut("/{id}", (string id, UpdateUserRequest req, IStore store, IAuditLog audit, ICurrentUser current,
            UserSnapshotCache cache) =>
        {
            var user = store.Col<User>().FindById(id);
            if (user is null) return ApiResults.NotFound("User");

            var email = req.Email is null ? user.Email : UserRules.NormalizeEmail(req.Email);
            var role = req.Role is null ? user.Role : CpmClaims.ParseRole(req.Role);
            var disabled = req.Disabled ?? user.Disabled;
            var v = new Validator();
            v.Require(UserRules.IsValidEmail(email), "email", "A valid e-mail address is required.");
            v.Require(req.Name is null || !string.IsNullOrWhiteSpace(req.Name), "name", "Name is required.");
            v.Require(role is not null, "role", "Role must be one of: viewer, operator, admin.");
            if (!string.IsNullOrEmpty(req.Password) && PasswordPolicy.Check(req.Password, email) is { } pwErr) v.Add("password", pwErr);
            if (!v.IsValid) return v.ToResult();

            if (user.ExternalSource is not null)
            {
                if (!string.IsNullOrEmpty(req.Password))
                    return ApiResults.BadRequest("Directory (LDAP) accounts have no local password; the password is managed in the directory.",
                        new Dictionary<string, string[]> { ["password"] = ["Directory accounts have no local password."] });
                if (role != user.Role)
                    return ApiResults.BadRequest("The role of a directory account comes from its group membership (Settings → LDAP) and is updated at each sign-in. Disable the account to block it.",
                        new Dictionary<string, string[]> { ["role"] = ["Managed by directory group membership."] });
                if (email != user.Email || (req.Name is not null && req.Name.Trim() != user.Name))
                    return ApiResults.BadRequest("The e-mail address and name of a directory account are synchronised from the directory at each sign-in.",
                        new Dictionary<string, string[]> { [email != user.Email ? "email" : "name"] = ["Managed by the directory."] });
            }

            var isSelf = user.Id == current.UserId;
            if (isSelf && disabled && !user.Disabled)
                return ApiResults.Conflict("You cannot disable your own account.");
            if (isSelf && role != UserRole.Admin && user.Role == UserRole.Admin)
                return ApiResults.Conflict("You cannot remove the admin role from your own account.");
            var losesAdmin = user.ExternalSource is null && user.Role == UserRole.Admin && !user.Disabled && (role != UserRole.Admin || disabled);
            if (losesAdmin && UserRules.EnabledAdminCount(store) <= 1)
                return ApiResults.Conflict("This is the last enabled local administrator (the break-glass account when the directory is unavailable). Create or enable another local administrator first.");
            if (email != user.Email && UserRules.FindByEmail(store, email) is { } other && other.Id != user.Id)
                return ApiResults.Conflict($"A user with the e-mail address '{email}' already exists.");

            var changes = new List<string>();
            if (email != user.Email) changes.Add($"email {user.Email} -> {email}");
            if (role != user.Role) changes.Add($"role {CpmClaims.RoleValue(user.Role)} -> {CpmClaims.RoleValue(role!.Value)}");
            if (disabled != user.Disabled) changes.Add(disabled ? "disabled" : "enabled");
            if (req.Name is not null && req.Name.Trim() != user.Name) changes.Add("name changed");

            var bumpStamp = disabled && !user.Disabled;
            user.Email = email;
            if (req.Name is not null) user.Name = req.Name.Trim();
            user.Role = role!.Value;
            user.Disabled = disabled;
            if (!string.IsNullOrEmpty(req.Password))
            {
                user.PasswordHash = Passwords.Hash(req.Password);
                bumpStamp = true;
                changes.Add("password reset");
            }
            if (bumpStamp) user.SecurityStamp++;
            user.UpdatedAt = DateTime.UtcNow;
            store.Col<User>().Update(user);
            cache.Invalidate(user.Id);
            audit.Record("updated", "user", user.Id, user.Email, changes.Count > 0 ? string.Join("; ", changes) : null);
            return Results.Ok(UserDto.From(user));
        });

        users.MapDelete("/{id}", (string id, IStore store, IAuditLog audit, ICurrentUser current, UserSnapshotCache cache) =>
        {
            var user = store.Col<User>().FindById(id);
            if (user is null) return ApiResults.NotFound("User");
            if (user.Id == current.UserId) return ApiResults.Conflict("You cannot delete your own account.");
            if (user.ExternalSource is null && user.Role == UserRole.Admin && !user.Disabled && UserRules.EnabledAdminCount(store) <= 1)
                return ApiResults.Conflict("This is the last enabled local administrator (the break-glass account when the directory is unavailable) and cannot be deleted.");
            store.Col<User>().Delete(user.Id);
            cache.Invalidate(user.Id);
            audit.Record("deleted", "user", user.Id, user.Email);
            return Results.NoContent();
        });
    }
}
