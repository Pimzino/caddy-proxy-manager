using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CaddyManager.Cluster;

public sealed record JoinRequest(string? Token);
public sealed record AddServerRequest(string? Name, string? Url);
public sealed record UpdateServerRequest(string? Name, string? Url, bool? Repin);
public sealed record CaddyUpdateRequest(string? Version);

/// <summary>/api/cluster and /api/servers (SPEC "Cluster module / Endpoints"). {id} is "local" or a node id.</summary>
internal static class ClusterEndpoints
{
    private const string Local = "local";

    public static void Map(IEndpointRouteBuilder app)
    {
        MapCluster(app);
        MapServers(app);
    }

    // ------------------------------------------------------------------ /api/cluster

    private static void MapCluster(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/cluster").RequireAuthorization(Policies.Viewer);

        g.MapGet("/", (ClusterService cluster) => Results.Ok(cluster.GetStatus()));

        g.MapPost("/join", (JoinRequest? body, ClusterService cluster, IAuditLog audit) =>
        {
            try
            {
                var status = cluster.Join(body?.Token, out var rejoined);
                audit.Record("joined", "cluster", cluster.Settings.NodeId, status.PrimaryName, rejoined
                    ? $"Joined the cluster of '{status.PrimaryName}' again with a new token"
                    : $"Joined the cluster of '{status.PrimaryName}' as a managed node");
                return Results.Ok(status);
            }
            catch (FormatException ex) { return ApiResults.BadRequest(ex.Message, new Dictionary<string, string[]> { ["token"] = [ex.Message] }); }
            catch (ClusterConflictException ex) { return ApiResults.Conflict(ex.Message); }
        }).RequireAuthorization(Policies.Admin);

        g.MapPost("/leave", (ClusterService cluster, IAuditLog audit) =>
        {
            var primary = cluster.PrimaryName;
            try
            {
                var status = cluster.Leave();
                audit.Record("left", "cluster", null, primary, "Left the cluster; the last applied configuration stays and is editable here");
                return Results.Ok(status);
            }
            catch (ClusterConflictException ex) { return ApiResults.Conflict(ex.Message); }
        }).RequireAuthorization(Policies.Admin);

        // Anonymous at cookie level: authenticated by the envelope (key known only to the primary and this node).
        app.MapPost("/api/cluster/rpc", (HttpContext http, NodeRpcHandler handler) => handler.HandleAsync(http)).AllowAnonymous();
    }

    // ------------------------------------------------------------------ /api/servers

    private static void MapServers(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/servers").RequireAuthorization(Policies.Viewer);

        g.MapGet("/", async (ClusterService cluster, CancellationToken ct) =>
        {
            var list = new List<ServerSummary> { await cluster.LocalSummaryAsync(ct) };
            list.AddRange(cluster.Nodes().Select(ClusterService.ToSummary));
            return Results.Ok(list);
        });

        g.MapGet("/{id}", async (string id, ClusterService cluster, ClusterWorker worker, CancellationToken ct) =>
        {
            if (id == Local) return Results.Ok(await cluster.LocalSummaryAsync(ct));
            if (cluster.FindNode(id) is null) return ApiResults.NotFound("Server");
            // Fresh: `hello` now (QueryTimeout); the summary falls back to the cached data when the node does not answer. A node
            // that is behind gets a background push — the request never waits for a sync.
            var node = await worker.HeartbeatAsync(id, ct);
            return node is null ? ApiResults.NotFound("Server") : Results.Ok(ClusterService.ToSummary(node));
        });

        g.MapGet("/{id}/samples", async (string id, DateTime? since, ClusterService cluster, NodeClient client, CancellationToken ct) =>
        {
            var sinceUtc = since?.ToUniversalTime();
            if (id == Local) return Results.Ok(cluster.GetLocalSamples(sinceUtc));
            return await OnNode(cluster, id, async node =>
                Results.Ok(await client.CallAsync<List<ResourceSample>>(node, "samples", new { since = sinceUtc }, cluster.Options.QueryTimeout, ct) ?? []));
        });

        g.MapGet("/{id}/traffic", async (string id, string? range, string? host, ClusterService cluster, NodeClient client, CancellationToken ct) =>
        {
            var r = TrafficRange.Day;
            if ((!string.IsNullOrEmpty(range) && !Enum.TryParse(range, ignoreCase: true, out r)) || !Enum.IsDefined(r))
                return ApiResults.BadRequest("range must be hour, day, week or month.", new Dictionary<string, string[]> { ["range"] = ["Use hour, day, week or month."] });
            var query = new TrafficQuery { Range = r, Host = string.IsNullOrWhiteSpace(host) ? null : host.Trim().ToLowerInvariant() };
            if (id == Local) return Results.Ok(await cluster.GetLocalTrafficAsync(query, ct));
            return await OnNode(cluster, id, async node =>
                Results.Ok(await client.CallAsync<TrafficReport>(node, "traffic", new { query }, cluster.Options.QueryTimeout, ct)));
        });

        g.MapPost("/", async (AddServerRequest? body, ClusterService cluster, ClusterWorker worker, IAuditLog audit, CancellationToken ct) =>
        {
            if (cluster.IsManagedNode)
                return ApiResults.Conflict($"This server is a node managed by '{cluster.PrimaryName}' and cannot manage other servers.");
            var v = Validate(body?.Name, body?.Url, out var name, out var url);
            if (!v.IsValid) return v.ToResult();
            if (cluster.Nodes().Any(n => string.Equals(n.Url, url, StringComparison.OrdinalIgnoreCase)))
                return ApiResults.Conflict($"A server with the URL {url} already exists.");
            var fingerprint = await NodeClient.ProbeFingerprintAsync(url, TimeSpan.FromSeconds(5), ct);
            ClusterNode node;
            string token;
            try { (node, token) = cluster.AddNode(name, url, fingerprint); }
            catch (ClusterConflictException ex) { return ApiResults.Conflict(ex.Message); }
            audit.Record("created", "server", node.Id, node.Name, $"{url}" + (fingerprint is null ? "" : $"; pinned certificate {fingerprint}"));
            worker.Kick(node.Id);
            return Results.Ok(new { server = ClusterService.ToSummary(node), joinToken = token, fingerprint });
        }).RequireAuthorization(Policies.Admin);

        g.MapPut("/{id}", async (string id, UpdateServerRequest? body, ClusterService cluster, NodeClient client, ClusterWorker worker,
            IAuditLog audit, CancellationToken ct) =>
        {
            if (id == Local) return ApiResults.BadRequest("Rename this server under Settings → UI (display name).");
            if (cluster.FindNode(id) is not { } existing) return ApiResults.NotFound("Server");
            var v = Validate(body?.Name ?? existing.Name, body?.Url ?? existing.Url, out var name, out var url);
            if (!v.IsValid) return v.ToResult();
            if (cluster.Nodes().Any(n => n.Id != id && string.Equals(n.Url, url, StringComparison.OrdinalIgnoreCase)))
                return ApiResults.Conflict($"A server with the URL {url} already exists.");
            var urlChanged = !string.Equals(url, existing.Url, StringComparison.Ordinal);
            var repin = body?.Repin == true || urlChanged;
            var fingerprint = repin ? await NodeClient.ProbeFingerprintAsync(url, TimeSpan.FromSeconds(5), ct) : existing.PinnedFingerprint;
            var node = cluster.UpdateNode(id, n =>
            {
                n.Name = name;
                n.Url = url;
                if (repin) n.PinnedFingerprint = fingerprint;
            });
            if (node is null) return ApiResults.NotFound("Server");
            client.Reset(id);
            var changes = new List<string>();
            if (existing.Name != name) changes.Add($"name '{existing.Name}' → '{name}'");
            if (urlChanged) changes.Add($"URL {existing.Url} → {url}");
            if (repin) changes.Add(fingerprint is null ? "certificate pin cleared (captured on next contact)" : $"pinned certificate {fingerprint}");
            audit.Record("updated", "server", id, name, changes.Count == 0 ? null : string.Join("; ", changes));
            worker.Kick(id);
            return Results.Ok(ClusterService.ToSummary(node));
        }).RequireAuthorization(Policies.Admin);

        g.MapPost("/{id}/token", async (string id, ClusterService cluster, ClusterWorker worker, IAuditLog audit, CancellationToken ct) =>
        {
            if (id == Local || cluster.FindNode(id) is not { } node) return ApiResults.NotFound("Server");
            // Key rotation: the node gets the new key over the encrypted channel (`rekey`); until it confirmed, it still
            // trusts its current key (KeyRotationPending) and the rotation is retried at every contact.
            if (await worker.RotateKeyAsync(id, ct) is not { } rotation) return ApiResults.NotFound("Server");
            audit.Record("tokenRegenerated", "server", id, node.Name, rotation.Rotated
                ? "New key and join token: the node switched to the new key; the previous token and key no longer work"
                : "New key and join token issued, but the node could not be reached: it still accepts the previous key until the rotation " +
                  "completes (retried at every contact) or the node leaves the cluster");
            return Results.Ok(new RegenerateTokenResult { JoinToken = rotation.Token, Rotated = rotation.Rotated });
        }).RequireAuthorization(Policies.Admin);

        g.MapDelete("/{id}", async (string id, ClusterService cluster, NodeClient client, IAuditLog audit, IServiceProvider sp) =>
        {
            if (id == Local || cluster.FindNode(id) is not { } node) return ApiResults.NotFound("Server");
            string outcome;
            try
            {
                // Best effort: the node becomes standalone again (keeping its configuration). Not bound to the request.
                await client.CallAsync(node, "leave", null, cluster.Options.QueryTimeout);
                outcome = "the node left the cluster and is standalone again";
            }
            catch (NodeRpcException ex)
            {
                outcome = "the node could not be told to leave (" + ex.Message + "); it still trusts its cluster key until " +
                          "\"CaddyManager.exe cluster leave\" (or Settings › Cluster › Leave) is run on it";
                // Visible on the Events page: whoever holds the node's key (or token) can still manage it.
                sp.GetService<IEventSink>()?.Raise(EventSeverity.Warning, "cluster", $"Server '{node.Name}' was removed but could not be told to leave",
                    $"{node.Url}: {ex.Message}. The server still accepts requests signed with its cluster key and serves its last configuration. " +
                    "Run \"CaddyManager.exe cluster leave\" on it (service stopped) or Settings › Cluster › Leave there.",
                    key: ClusterWorker.RemovedKeyPrefix + node.Id);
            }
            cluster.RemoveNode(id);
            client.Reset(id);
            audit.Record("deleted", "server", id, node.Name, $"{node.Url}: {outcome}");
            return Results.NoContent();
        }).RequireAuthorization(Policies.Admin);

        g.MapPost("/{id}/sync", async (string id, ClusterService cluster, ClusterWorker worker, IAuditLog audit, CancellationToken ct) =>
        {
            if (id == Local) return ApiResults.BadRequest("Only cluster nodes are synchronised; this server is the source of the configuration.");
            if (cluster.FindNode(id) is null) return ApiResults.NotFound("Server");
            var node = await worker.SyncNowAsync(id, ct);
            if (node is null) return ApiResults.NotFound("Server");
            audit.Record("synced", "server", id, node.Name, node.LastSyncError is null ? $"Revision {node.AppliedRevision}" : "Failed: " + node.LastSyncError);
            return Results.Ok(ClusterService.ToSummary(node));
        }).RequireAuthorization(Policies.Operator);

        g.MapPost("/{id}/caddy/restart", async (string id, ClusterService cluster, NodeClient client, IServiceProvider sp, IAuditLog audit) =>
        {
            if (id == Local)
            {
                var host = sp.GetRequiredService<ICaddyHost>();
                try { await host.RestartAsync(); }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException or UnauthorizedAccessException)
                {
                    audit.Record("restarted", "caddy", AppPaths.CaddyServiceName, "Caddy", "Failed: " + ex.Message);
                    return ApiResults.Failed("Could not restart Caddy", ex.Message);
                }
                audit.Record("restarted", "caddy", AppPaths.CaddyServiceName, "Caddy");
                return Results.Ok(await host.GetStatusAsync());
            }
            return await OnNode(cluster, id, async node =>
            {
                var status = await client.CallAsync<CaddyStatus>(node, "caddy.restart", null, cluster.Options.SyncTimeout);
                audit.Record("restarted", "server", id, node.Name, "Caddy restarted on the node");
                return Results.Ok(status);
            });
        }).RequireAuthorization(Policies.Operator);

        g.MapPost("/{id}/caddy/update", async (string id, CaddyUpdateRequest? body, ClusterService cluster, NodeClient client, IServiceProvider sp,
            IAuditLog audit) =>
        {
            var version = string.IsNullOrWhiteSpace(body?.Version) ? null : body!.Version!.Trim();
            if (id == Local)
            {
                JobInfo job;
                try { job = sp.GetRequiredService<ICaddyBinaryManager>().StartInstallOrUpdate(version); }
                catch (InvalidOperationException ex) { return ApiResults.Conflict(ex.Message); }
                audit.Record("install", "caddyBinary", null, version ?? "latest", "Caddy install/update started");
                return Results.Ok(job);
            }
            return await OnNode(cluster, id, async node =>
            {
                var job = await client.CallAsync<JobInfo>(node, "caddy.update", new { version }, cluster.Options.QueryTimeout);
                audit.Record("caddyUpdate", "server", id, node.Name, $"Caddy {version ?? "latest"} install started on the node (job {job?.Id})");
                return Results.Ok(job);
            });
        }).RequireAuthorization(Policies.Admin);

        g.MapGet("/{id}/jobs/{jobId}", async (string id, string jobId, ClusterService cluster, NodeClient client, IJobRunner jobs) =>
        {
            if (id == Local) return jobs.Get(jobId) is { } j ? Results.Ok(j) : ApiResults.NotFound("Job");
            return await OnNode(cluster, id, async node =>
                Results.Ok(await client.CallAsync<JobInfo>(node, "job", new { id = jobId }, cluster.Options.QueryTimeout)));
        });
    }

    /// <summary>Runs a node call; unknown node → 404, node errors → 502 with the reason.</summary>
    private static async Task<IResult> OnNode(ClusterService cluster, string id, Func<ClusterNode, Task<IResult>> call)
    {
        if (cluster.FindNode(id) is not { } node) return ApiResults.NotFound("Server");
        try
        {
            return await call(node);
        }
        catch (NodeRpcException ex)
        {
            return Results.Problem(title: $"Server '{node.Name}' did not answer", detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static Validator Validate(string? nameIn, string? urlIn, out string name, out string url)
    {
        var v = new Validator();
        name = (nameIn ?? "").Trim();
        url = (urlIn ?? "").Trim().TrimEnd('/');
        v.Require(name.Length is > 0 and <= 100, "name", "Enter a name (at most 100 characters).");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host))
            v.Add("url", "Enter the node's management URL, e.g. https://proxy2.corp.local:81.");
        else if (uri.PathAndQuery is not ("/" or "") || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
            v.Add("url", "Use the base URL of the node's management UI (scheme, host and port only).");
        else
            url = $"{uri.Scheme}://{uri.Authority}";
        return v;
    }
}
