using System.Security.Claims;
using CaddyManager.Core;
using CaddyManager.Core.Models;
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
        auth.MapPost("/login", async (LoginRequest req, HttpContext ctx, IStore store, IAuditLog audit, ILogger<SetupState> logger) =>
        {
            var email = UserRules.NormalizeEmail(req.Email);
            var user = string.IsNullOrEmpty(email) ? null : UserRules.FindByEmail(store, email);
            var ok = Passwords.Verify(req.Password ?? "", user?.PasswordHash ?? DummyHash.Value) && user is not null;
            var ip = CurrentUser.FormatIp(ctx.Connection.RemoteIpAddress);
            if (!ok)
            {
                logger.LogWarning("Failed sign-in for {Email} from {Ip}", email, ip);
                audit.Record("loginFailed", "user", user?.Id, email, "Invalid credentials");
                return Results.Problem(title: "Invalid credentials", detail: "The e-mail address or password is incorrect.",
                    statusCode: StatusCodes.Status401Unauthorized);
            }
            if (user!.Disabled)
            {
                audit.Record("loginFailed", "user", user.Id, user.Email, "Account disabled");
                return Results.Problem(title: "Invalid credentials", detail: "This account is disabled. Ask an administrator to enable it.",
                    statusCode: StatusCodes.Status401Unauthorized);
            }
            user.LastLoginAt = DateTime.UtcNow;
            store.Col<User>().Update(user);
            await AuthSetup.SignInAsync(ctx, user);
            ctx.User = CpmClaims.CreatePrincipal(user, AuthSetup.Scheme);
            audit.Record("login", "user", user.Id, user.Email);
            return Results.Ok(UserDto.From(user));
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

            var isSelf = user.Id == current.UserId;
            if (isSelf && disabled && !user.Disabled)
                return ApiResults.Conflict("You cannot disable your own account.");
            if (isSelf && role != UserRole.Admin && user.Role == UserRole.Admin)
                return ApiResults.Conflict("You cannot remove the admin role from your own account.");
            var losesAdmin = user.Role == UserRole.Admin && !user.Disabled && (role != UserRole.Admin || disabled);
            if (losesAdmin && UserRules.EnabledAdminCount(store) <= 1)
                return ApiResults.Conflict("This is the last enabled administrator. Create or enable another administrator first.");
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
            if (user.Role == UserRole.Admin && !user.Disabled && UserRules.EnabledAdminCount(store) <= 1)
                return ApiResults.Conflict("This is the last enabled administrator and cannot be deleted.");
            store.Col<User>().Delete(user.Id);
            cache.Invalidate(user.Id);
            audit.Record("deleted", "user", user.Id, user.Email);
            return Results.NoContent();
        });
    }
}
