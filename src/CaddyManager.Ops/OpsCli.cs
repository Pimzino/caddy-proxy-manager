namespace CaddyManager.Ops;

/// <summary>Command line verbs handled before the web host starts (reset-password, ...).</summary>
public static class OpsCli
{
    /// <summary>Returns an exit code if args were an Ops CLI verb, otherwise null.</summary>
    public static Task<int?> TryRunAsync(string[] args) => Task.FromResult<int?>(null);
}
