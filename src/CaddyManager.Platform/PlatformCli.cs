namespace CaddyManager.Platform;

/// <summary>Command line verbs handled before the web host starts (install, uninstall, ...).</summary>
public static class PlatformCli
{
    /// <summary>Returns an exit code if args were a Platform CLI verb, otherwise null.</summary>
    public static Task<int?> TryRunAsync(string[] args) => Task.FromResult<int?>(null);
}
