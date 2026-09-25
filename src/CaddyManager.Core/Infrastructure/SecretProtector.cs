using System.Security.Cryptography;
using System.Text;

namespace CaddyManager.Core.Infrastructure;

/// <summary>
/// Windows: DPAPI with LocalMachine scope (readable only on this machine — backups carry
/// secrets that must be re-entered after restore to another server).
/// Elsewhere: AES-GCM with a random key file (development only).
/// </summary>
public sealed class SecretProtector(AppPaths paths) : ISecretProtector
{
    private const string Prefix = "enc:v1:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CaddyProxyManager/secrets");
    private byte[]? _aesKey;

    public string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        var data = Encoding.UTF8.GetBytes(plaintext);
        byte[] blob;
        if (OperatingSystem.IsWindows())
            blob = ProtectedData.Protect(data, Entropy, DataProtectionScope.LocalMachine);
        else
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var cipher = new byte[data.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(Key(), 16);
            aes.Encrypt(nonce, data, cipher, tag);
            blob = [.. nonce, .. tag, .. cipher];
        }
        return Prefix + Convert.ToBase64String(blob);
    }

    public string Unprotect(string protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return "";
        if (!protectedValue.StartsWith(Prefix, StringComparison.Ordinal)) return protectedValue;
        var blob = Convert.FromBase64String(protectedValue[Prefix.Length..]);
        if (OperatingSystem.IsWindows())
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.LocalMachine));
        var nonce = blob[..12];
        var tag = blob[12..28];
        var cipher = blob[28..];
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(Key(), 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    private byte[] Key()
    {
        if (_aesKey is not null) return _aesKey;
        var file = paths.SecretsKeyFile;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        if (!File.Exists(file)) File.WriteAllBytes(file, RandomNumberGenerator.GetBytes(32));
        return _aesKey = File.ReadAllBytes(file);
    }
}
