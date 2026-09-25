using System.Net;
using System.Security.Claims;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using Microsoft.AspNetCore.Http;

namespace CaddyManager.Ops.Auth;

/// <summary>Claim types stored in the session cookie.</summary>
internal static class CpmClaims
{
    public const string UserId = ClaimTypes.NameIdentifier;
    public const string Email = ClaimTypes.Email;
    public const string Name = ClaimTypes.Name;
    public const string Role = ClaimTypes.Role;
    public const string SecurityStamp = "cpm:stamp";
    /// <summary>Random per sign-in id, so one session can be revoked server-side at logout.</summary>
    public const string SessionId = "cpm:sid";

    public static string RoleValue(UserRole role) => role switch
    {
        UserRole.Admin => "admin",
        UserRole.Operator => "operator",
        _ => "viewer",
    };

    public static UserRole? ParseRole(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "admin" => UserRole.Admin,
        "operator" => UserRole.Operator,
        "viewer" => UserRole.Viewer,
        _ => null,
    };

    public static ClaimsPrincipal CreatePrincipal(User user, string scheme, string? sessionId = null)
    {
        var claims = new List<Claim>
        {
            new(UserId, user.Id),
            new(Email, user.Email),
            new(Name, string.IsNullOrWhiteSpace(user.Name) ? user.Email : user.Name),
            new(Role, RoleValue(user.Role)),
            new(SecurityStamp, user.SecurityStamp.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };
        if (!string.IsNullOrEmpty(sessionId)) claims.Add(new Claim(SessionId, sessionId));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, scheme, Name, Role));
    }
}

/// <summary>
/// The signed-in user of the current HTTP request; "system" for background work
/// (monitor, update checker, CLI) where no request is in flight.
/// </summary>
internal sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal =>
        accessor.HttpContext?.User is { Identity.IsAuthenticated: true } p ? p : null;

    public string? UserId => Principal?.FindFirstValue(CpmClaims.UserId);

    public string UserName
    {
        get
        {
            if (accessor.HttpContext is null) return "system";
            var p = Principal;
            if (p is null) return "anonymous";
            return p.FindFirstValue(CpmClaims.Email) ?? p.FindFirstValue(CpmClaims.Name) ?? "unknown";
        }
    }

    public UserRole? Role => CpmClaims.ParseRole(Principal?.FindFirstValue(CpmClaims.Role));

    public string? RemoteIp => FormatIp(accessor.HttpContext?.Connection.RemoteIpAddress);

    internal static string? FormatIp(IPAddress? ip)
    {
        if (ip is null) return null;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return ip.ToString();
    }
}
