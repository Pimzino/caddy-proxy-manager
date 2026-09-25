using System.Net.Mail;
using System.Security.Cryptography;
using CaddyManager.Core;
using CaddyManager.Core.Models;

namespace CaddyManager.Ops.Auth;

/// <summary>Wire shape of a user. Never includes the password hash or security stamp.</summary>
/// <remarks><c>externalSource</c> is "ldap" for directory accounts (no local password; role from group mapping) and omitted for local accounts.</remarks>
public sealed record UserDto(string Id, string Email, string Name, UserRole Role, bool Disabled, DateTime? LastLoginAt, DateTime CreatedAt,
    string? ExternalSource = null)
{
    public static UserDto From(User u) => new(u.Id, u.Email, u.Name, u.Role, u.Disabled, u.LastLoginAt, u.CreatedAt, u.ExternalSource);
}

public static class PasswordPolicy
{
    public const int MinLength = 12;
    public const int MaxLength = 256;

    /// <summary>Returns an error message, or null when the password is acceptable.</summary>
    public static string? Check(string? password, string? email = null)
    {
        if (string.IsNullOrEmpty(password)) return "Password is required.";
        if (password.Length < MinLength) return $"Password must be at least {MinLength} characters long.";
        if (password.Length > MaxLength) return $"Password must be at most {MaxLength} characters long.";
        if (string.IsNullOrWhiteSpace(password)) return "Password cannot consist of whitespace only.";
        if (!string.IsNullOrEmpty(email) && string.Equals(password.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase))
            return "Password must not be the same as the e-mail address.";
        return null;
    }

    /// <summary>A random 20-character password (no ambiguous characters) for CLI resets.</summary>
    public static string Generate(int length = 20)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789-_.!@#%";
        var chars = new char[length];
        for (var i = 0; i < length; i++) chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return new string(chars);
    }
}

/// <summary>User persistence rules shared by the API, setup flow and the CLI.</summary>
internal static class UserRules
{
    public static string NormalizeEmail(string? email) => (email ?? "").Trim().ToLowerInvariant();

    public static bool IsValidEmail(string email) =>
        email.Length is > 2 and <= 254 && MailAddress.TryCreate(email, out var a) && a.Address == email && email.Contains('@');

    public static User? FindByEmail(IStore store, string email)
    {
        var norm = NormalizeEmail(email);
        return store.Col<User>().FindOne(u => u.Email == norm);
    }

    /// <summary>
    /// Enabled local administrators. Directory administrators do not count: at least one local administrator must remain
    /// so the server can be managed when the directory is unreachable.
    /// </summary>
    public static int EnabledAdminCount(IStore store) =>
        store.Col<User>().Count(u => u.Role == UserRole.Admin && !u.Disabled && u.ExternalSource == null);
}
