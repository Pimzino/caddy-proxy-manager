using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using CaddyManager.Core;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Telemetry.Traffic;

/// <summary>
/// Keyed 64-bit hash of a client address for the unique-client sketches: HMAC-SHA256 with a random per-installation key,
/// truncated to 64 bits. Without the key the stored hashes cannot be mapped back to addresses by hashing every IPv4
/// address (an unkeyed hash can be reversed that way in minutes). HMAC output is uniformly distributed, which HyperLogLog
/// needs.
///
/// The key is kept in <see cref="KeyFile"/> — next to, but not inside, the telemetry database — protected with
/// ISecretProtector (DPAPI LocalMachine on Windows), so a copy of the database or of the key file alone is not enough.
/// When the key cannot be read (file removed, or a data folder restored onto another machine) a new key is generated:
/// sketches written before and after that do not recognise the same client (unique clients across that moment can be
/// over-counted), nothing else changes.
/// </summary>
internal sealed class ClientHasher
{
    private const int KeyBytes = 32;
    private readonly byte[] _key;

    private ClientHasher(byte[] key) => _key = key;

    public static string KeyFile(AppPaths paths) => Path.Combine(paths.DataDir, "db", "telemetry.key");

    /// <summary>A hasher with the given 32-byte key (tests; nothing is persisted).</summary>
    public static ClientHasher FromKey(byte[] key) =>
        key.Length == KeyBytes ? new ClientHasher(key.ToArray()) : throw new ArgumentException($"The key must have {KeyBytes} bytes.", nameof(key));

    /// <summary>Writes <paramref name="key"/> as the installation's key (tests that need repeatable hashes).</summary>
    internal static void SaveKey(AppPaths paths, ISecretProtector protector, byte[] key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KeyFile(paths))!);
        File.WriteAllText(KeyFile(paths), protector.Protect(Convert.ToBase64String(key)));
    }

    /// <summary>Reads the installation's key, creating it on first use.</summary>
    public static ClientHasher LoadOrCreate(AppPaths paths, ISecretProtector protector, ILogger logger)
    {
        var file = KeyFile(paths);
        try
        {
            if (File.Exists(file))
            {
                var key = Convert.FromBase64String(protector.Unprotect(File.ReadAllText(file).Trim()));
                if (key.Length == KeyBytes) return new ClientHasher(key);
                logger.LogWarning("The traffic statistics key {File} is invalid; a new one is created", file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or CryptographicException)
        {
            logger.LogWarning("The traffic statistics key {File} cannot be read ({Message}); a new one is created. Unique " +
                "clients counted before and after this moment are not recognised as the same clients.", file, ex.Message);
        }
        var fresh = RandomNumberGenerator.GetBytes(KeyBytes);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, protector.Protect(Convert.ToBase64String(fresh)));
            File.Move(tmp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // Still usable for this run; the next start creates another key (same effect as above).
            logger.LogWarning(ex, "Could not save the traffic statistics key {File}", file);
        }
        return new ClientHasher(fresh);
    }

    public ulong Hash(string client)
    {
        Span<byte> buffer = stackalloc byte[128];
        var bytes = Encoding.UTF8.GetByteCount(client) <= buffer.Length
            ? buffer[..Encoding.UTF8.GetBytes(client, buffer)]
            : Encoding.UTF8.GetBytes(client);
        Span<byte> mac = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(_key, bytes, mac);
        return BinaryPrimitives.ReadUInt64LittleEndian(mac);
    }
}
