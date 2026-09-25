namespace CaddyManager.Core;

/// <summary>bcrypt hashing. Used for UI users and for Caddy basic-auth accounts (Caddy accepts bcrypt).</summary>
public static class Passwords
{
    public static string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12);
    public static bool Verify(string password, string hash)
    {
        try { return BCrypt.Net.BCrypt.Verify(password, hash); }
        catch { return false; }
    }
}
