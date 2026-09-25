using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Ops.Auth;

/// <summary>
/// First-run setup. While no user exists, a random token is written to AppPaths.SetupTokenFile
/// (readable only by SYSTEM/Administrators on Windows) and must be presented to create the first admin.
/// </summary>
internal sealed class SetupState(IStore store, AppPaths paths, ILogger<SetupState> logger)
{
    private readonly object _lock = new();
    private string? _token;

    /// <summary>Serialises the "check no users → create admin" critical section.</summary>
    public object CreationLock { get; } = new();

    public bool NeedsSetup => store.Col<User>().Count() == 0;

    /// <summary>Creates (or re-reads) the setup token when setup is pending. Called at startup and lazily.</summary>
    public void EnsureToken(bool log = false)
    {
        lock (_lock)
        {
            if (!NeedsSetup)
            {
                DeleteTokenFile();
                return;
            }
            if (_token is null && File.Exists(paths.SetupTokenFile))
            {
                try
                {
                    var existing = File.ReadAllText(paths.SetupTokenFile).Trim();
                    if (existing.Length >= 32) _token = existing;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not read existing setup token file {File}; a new token will be generated", paths.SetupTokenFile);
                }
            }
            if (_token is null)
            {
                _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
                WriteTokenFile(_token);
            }
            else if (!File.Exists(paths.SetupTokenFile))
            {
                WriteTokenFile(_token);
            }
            if (log)
                logger.LogWarning(
                    "No administrator account exists yet. Open the web UI and complete setup with the token stored in {File}. Setup token: {Token}",
                    paths.SetupTokenFile, _token);
        }
    }

    /// <summary>Constant-time comparison of the presented token with the current one.</summary>
    public bool Verify(string? presented)
    {
        EnsureToken();
        string? expected;
        lock (_lock) expected = _token;
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(presented)) return false;
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(presented.Trim()));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    public void Complete()
    {
        lock (_lock)
        {
            _token = null;
            DeleteTokenFile();
        }
    }

    private void DeleteTokenFile()
    {
        try
        {
            if (File.Exists(paths.SetupTokenFile)) File.Delete(paths.SetupTokenFile);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete setup token file {File}. Delete it manually.", paths.SetupTokenFile);
        }
    }

    private void WriteTokenFile(string token)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(paths.SetupTokenFile)!);
            if (File.Exists(paths.SetupTokenFile)) File.Delete(paths.SetupTokenFile);
            if (OperatingSystem.IsWindows())
            {
                // ProgramData is readable by all local users by default — lock the token down.
                var security = new FileSecurity();
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    FileSystemRights.FullControl, AccessControlType.Allow));
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                    FileSystemRights.FullControl, AccessControlType.Allow));
                using var fs = new FileInfo(paths.SetupTokenFile).Create(FileMode.CreateNew, FileSystemRights.FullControl,
                    FileShare.None, 4096, FileOptions.None, security);
                var bytes = Encoding.UTF8.GetBytes(token + Environment.NewLine);
                fs.Write(bytes);
            }
            else
            {
                File.WriteAllText(paths.SetupTokenFile, token + Environment.NewLine);
                File.SetUnixFileMode(paths.SetupTokenFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not write the setup token file {File}. Use the token from this log instead.", paths.SetupTokenFile);
        }
    }
}
