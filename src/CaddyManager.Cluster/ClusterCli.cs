namespace CaddyManager.Cluster;

/// <summary>Command line verbs (cluster join/leave/status) handled before the web host starts.</summary>
public static class ClusterCli
{
    /// <summary>Returns an exit code if args were a cluster CLI verb, otherwise null.</summary>
    public static Task<int?> TryRunAsync(string[] args) => Task.FromResult<int?>(null); // TODO(cluster)
}
