using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Infrastructure;
using CaddyManager.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CaddyManager.Cluster;

/// <summary>
/// Command line verbs handled before the web host starts: <c>cluster join &lt;token&gt;</c>, <c>cluster leave</c>,
/// <c>cluster status</c> (each with optional <c>--data-dir</c>). Like reset-password they open the database directly, so the
/// CaddyProxyManager service must be stopped first.
/// </summary>
public static class ClusterCli
{
    private const string LockedMessage = "Stop the CaddyProxyManager service first";

    /// <summary>Returns an exit code if args were a cluster CLI verb, otherwise null.</summary>
    public static Task<int?> TryRunAsync(string[] args) => TryRunAsync(args, Console.Out, Console.Error);

    internal static Task<int?> TryRunAsync(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0 || !args[0].TrimStart('-', '/').Equals("cluster", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<int?>(null);
        return Task.FromResult<int?>(Run(args[1..], stdout, stderr));
    }

    private static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0)
        {
            PrintUsage(stderr);
            return 2;
        }
        var verb = args[0].ToLowerInvariant();
        string? token = null;
        string? dataDir = null;
        for (var i = 1; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--data-dir=", StringComparison.OrdinalIgnoreCase)) dataDir = a["--data-dir=".Length..];
            else if (a.Equals("--data-dir", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length) return Usage(stderr, "--data-dir needs a value.");
                dataDir = args[++i];
            }
            else if (verb == "join" && token is null && !a.StartsWith("--", StringComparison.Ordinal)) token = a;
            else return Usage(stderr, $"Unexpected argument '{a}'.");
        }
        if (verb is not ("join" or "leave" or "status")) return Usage(stderr, $"Unknown cluster command '{args[0]}'.");
        if (verb == "join" && string.IsNullOrWhiteSpace(token)) return Usage(stderr, "cluster join needs the join token from the primary.");

        var paths = new AppPaths(dataDir);
        if (!File.Exists(paths.DbFile))
        {
            stderr.WriteLine($"No database found at {paths.DbFile}. Has {AppPaths.ProductName} been started at least once?");
            return 1;
        }
        LiteStore store;
        try
        {
            store = new LiteStore(paths);
        }
        catch (Exception ex) when (IsLocked(ex))
        {
            stderr.WriteLine($"{LockedMessage} (the database {paths.DbFile} is in use): net stop {AppPaths.ManagerServiceName}");
            return 1;
        }
        using (store)
        {
            try
            {
                using var services = new ServiceCollection().BuildServiceProvider();
                var cluster = new ClusterService(store, new SecretProtector(paths), paths, services, Options.Create(new ClusterOptions()),
                    TimeProvider.System, NullLogger<ClusterService>.Instance);
                return verb switch
                {
                    "join" => Join(cluster, store, token!, stdout, stderr),
                    "leave" => Leave(cluster, store, stdout, stderr),
                    _ => Status(cluster, stdout),
                };
            }
            catch (Exception ex) when (IsLocked(ex))
            {
                stderr.WriteLine($"{LockedMessage} (the database {paths.DbFile} is in use): net stop {AppPaths.ManagerServiceName}");
                return 1;
            }
        }
    }

    private static int Join(ClusterService cluster, IStore store, string token, TextWriter stdout, TextWriter stderr)
    {
        ClusterStatus status;
        try { status = cluster.Join(token); }
        catch (FormatException ex)
        {
            stderr.WriteLine(ex.Message);
            return 2;
        }
        catch (ClusterConflictException ex)
        {
            stderr.WriteLine(ex.Message);
            return 1;
        }
        Audit(store, "joined", status.PrimaryName, $"Joined the cluster of '{status.PrimaryName}' from the command line");
        stdout.WriteLine($"This server is now a node of '{status.PrimaryName}' (node id {cluster.Settings.NodeId}).");
        stdout.WriteLine("Start the CaddyProxyManager service: the primary pushes its configuration at its next heartbeat. Hosts, certificates,");
        stdout.WriteLine("access lists, streams, Caddy settings (except listeners and local paths) and plugins are then managed on the primary.");
        return 0;
    }

    private static int Leave(ClusterService cluster, IStore store, TextWriter stdout, TextWriter stderr)
    {
        var primary = cluster.PrimaryName;
        try { cluster.Leave(); }
        catch (ClusterConflictException ex)
        {
            stderr.WriteLine(ex.Message);
            return 1;
        }
        Audit(store, "left", primary, "Left the cluster from the command line; the last applied configuration stays and is editable");
        stdout.WriteLine($"This server left the cluster of '{primary}' and is standalone again. Its last applied configuration is kept and editable.");
        stdout.WriteLine("Remove it on the primary too (Servers → Remove), or the primary keeps reporting it offline.");
        return 0;
    }

    private static int Status(ClusterService cluster, TextWriter stdout)
    {
        var status = cluster.GetStatus();
        var s = cluster.Settings;
        stdout.WriteLine($"Server:   {status.ServerName}");
        stdout.WriteLine($"Role:     {status.Role}");
        stdout.WriteLine($"Storage:  {status.StorageBackend}");
        if (status.Role == ClusterRole.Node)
        {
            stdout.WriteLine($"Primary:  {s.PrimaryName}");
            stdout.WriteLine($"Node id:  {s.NodeId}");
            stdout.WriteLine($"Joined:   {s.JoinedAt:u}");
            stdout.WriteLine($"Contact:  {(s.LastPrimaryContactAt is { } c ? c.ToString("u") : "never")}");
            stdout.WriteLine($"Applied:  {s.AppliedRevision ?? "nothing yet"}{(s.AppliedAt is { } at ? $" at {at:u}" : "")}");
            if (s.PendingRevision is not null) stdout.WriteLine($"Pending:  {s.PendingRevision} (Caddy rebuild job {s.PendingJobId})");
        }
        else if (status.Role == ClusterRole.Primary)
        {
            stdout.WriteLine($"Nodes:    {status.NodeCount}");
            foreach (var n in cluster.Nodes())
                stdout.WriteLine($"  {n.Name,-24} {n.Url,-40} {n.Status,-8} applied {(n.AppliedRevision is { } r ? NodeSync.Short(r) : "-")}" +
                                 $" last seen {(n.LastSeenAt is { } seen ? seen.ToString("u") : "never")}");
        }
        foreach (var w in status.Warnings) stdout.WriteLine("Warning:  " + w);
        return 0;
    }

    private static void Audit(IStore store, string action, string? name, string details) =>
        store.Col<AuditEntry>().Insert(new AuditEntry
        {
            UserName = "cli:" + Environment.UserName, Action = action, ObjectType = "cluster", ObjectName = name, Details = details,
        });

    private static bool IsLocked(Exception ex) =>
        ex is IOException || ex.InnerException is IOException ||
        (ex is LiteDB.LiteException le && le.Message.Contains("locked", StringComparison.OrdinalIgnoreCase));

    private static int Usage(TextWriter w, string error)
    {
        w.WriteLine(error);
        PrintUsage(w);
        return 2;
    }

    private static void PrintUsage(TextWriter w)
    {
        w.WriteLine("Usage:");
        w.WriteLine("  CaddyManager.exe cluster join <token> [--data-dir <dir>]   Make this server a node of the primary that issued the token.");
        w.WriteLine("  CaddyManager.exe cluster leave [--data-dir <dir>]          Leave the cluster (keeps the last applied configuration).");
        w.WriteLine("  CaddyManager.exe cluster status [--data-dir <dir>]         Show this server's cluster role and state.");
        w.WriteLine($"Stop the {AppPaths.ManagerServiceName} service before running these commands.");
    }
}
