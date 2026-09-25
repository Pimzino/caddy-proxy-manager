using CaddyManager.Config.Validation;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CaddyManager.Config.Endpoints;

/// <summary>Access list wire shape: passwords are write-only.</summary>
public sealed record AccessListDto
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public bool SatisfyAny { get; init; }
    public bool PassAuthToUpstream { get; init; }
    public List<IpRule> Rules { get; init; } = new();
    public List<AccessUserDto> Users { get; init; } = new();
    public int UsedBy { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public sealed record AccessUserDto(string Username, bool HasPassword);

public sealed record AccessListInput
{
    public string? Name { get; init; }
    public bool SatisfyAny { get; init; }
    public bool PassAuthToUpstream { get; init; }
    public List<IpRule>? Rules { get; init; }
    public List<AccessUserInput>? Users { get; init; }
}

public sealed record AccessUserInput
{
    public string? Username { get; init; }
    /// <summary>Plain-text password. Null/empty on PUT keeps the existing hash for that username.</summary>
    public string? Password { get; init; }
}

internal static class AccessListEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/access-lists").RequireAuthorization(Policies.Viewer);

        g.MapGet("/", (IStore store) =>
        {
            var hosts = store.Col<SiteHost>().FindAll().ToList();
            return Results.Ok(store.Col<AccessList>().FindAll()
                .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .Select(a => ToDto(a, hosts)).ToList());
        });

        g.MapGet("/{id}", (string id, IStore store) =>
            store.Col<AccessList>().FindById(id) is { } a
                ? Results.Ok(ToDto(a, store.Col<SiteHost>().FindAll().ToList()))
                : ApiResults.NotFound("Access list"));

        g.MapPost("/", async (AccessListInput? body, IStore store, HttpContext http) =>
        {
            if (body is null) return ApiResults.BadRequest("An access list object is required.");
            var list = new AccessList { Id = Entity.NewId() };
            if (Apply(body, list, previous: null) is { } problem) return problem;
            var col = store.Col<AccessList>();
            return await ConfigTransaction.RunAsync(http, $"Access list created: {list.Name}",
                persist: () => col.Insert(list),
                rollback: () => col.Delete(list.Id),
                onSuccess: apply =>
                {
                    ConfigTransaction.Audit(http, "created", "accessList", list.Id, list.Name);
                    return Results.Ok(new { item = ToDto(list, store.Col<SiteHost>().FindAll().ToList()), apply });
                });
        }).RequireAuthorization(Policies.Operator).RejectOnManagedNode();

        g.MapPut("/{id}", async (string id, AccessListInput? body, IStore store, HttpContext http) =>
        {
            if (body is null) return ApiResults.BadRequest("An access list object is required.");
            var col = store.Col<AccessList>();
            var existing = col.FindById(id);
            if (existing is null) return ApiResults.NotFound("Access list");
            var list = new AccessList { Id = id, CreatedAt = existing.CreatedAt };
            if (Apply(body, list, existing) is { } problem) return problem;
            return await ConfigTransaction.RunAsync(http, $"Access list updated: {list.Name}",
                persist: () => col.Update(list),
                rollback: () => col.Upsert(existing),
                onSuccess: apply =>
                {
                    ConfigTransaction.Audit(http, "updated", "accessList", id, list.Name);
                    return Results.Ok(new { item = ToDto(list, store.Col<SiteHost>().FindAll().ToList()), apply });
                });
        }).RequireAuthorization(Policies.Operator).RejectOnManagedNode();

        g.MapDelete("/{id}", async (string id, IStore store, HttpContext http) =>
        {
            var col = store.Col<AccessList>();
            var existing = col.FindById(id);
            if (existing is null) return ApiResults.NotFound("Access list");
            var users = store.Col<SiteHost>().FindAll().Where(h => h.AccessListId == id).ToList();
            if (users.Count > 0)
                return ApiResults.Conflict($"The access list '{existing.Name}' is used by {users.Count} host(s): {string.Join(", ", users.Select(h => h.Domains.FirstOrDefault() ?? h.Id))}. Remove it from those hosts first.");
            return await ConfigTransaction.RunAsync(http, $"Access list deleted: {existing.Name}",
                persist: () => col.Delete(id),
                rollback: () => col.Upsert(existing),
                onSuccess: apply =>
                {
                    ConfigTransaction.Audit(http, "deleted", "accessList", id, existing.Name);
                    return Results.Ok(new { apply });
                });
        }).RequireAuthorization(Policies.Operator).RejectOnManagedNode();
    }

    internal static AccessListDto ToDto(AccessList a, List<SiteHost> hosts) => new()
    {
        Id = a.Id,
        Name = a.Name,
        SatisfyAny = a.SatisfyAny,
        PassAuthToUpstream = a.PassAuthToUpstream,
        Rules = a.Rules,
        Users = a.Users.Select(u => new AccessUserDto(u.Username, !string.IsNullOrEmpty(u.PasswordHash))).ToList(),
        UsedBy = hosts.Count(h => h.AccessListId == a.Id),
        CreatedAt = a.CreatedAt,
        UpdatedAt = a.UpdatedAt,
    };

    /// <summary>Validates the input and fills <paramref name="target"/>. Returns a problem result when invalid.</summary>
    private static IResult? Apply(AccessListInput input, AccessList target, AccessList? previous)
    {
        var v = new Validator();
        var name = input.Name?.Trim() ?? "";
        if (name.Length == 0) v.Add("name", "Name is required.");
        else if (name.Length > 100) v.Add("name", "Name must be at most 100 characters.");

        var rules = new List<IpRule>();
        var inRules = input.Rules ?? [];
        for (var i = 0; i < inRules.Count; i++)
        {
            var cidr = inRules[i]?.Cidr?.Trim() ?? "";
            if (!NetUtil.IsValidCidr(cidr)) v.Add($"rules[{i}].cidr", $"'{cidr}' is not an IP address, CIDR range or 'all'.");
            else rules.Add(new IpRule { Action = inRules[i].Action, Cidr = cidr.Equals("all", StringComparison.OrdinalIgnoreCase) ? "all" : cidr });
        }

        var users = new List<AccessUser>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var inUsers = input.Users ?? [];
        for (var i = 0; i < inUsers.Count; i++)
        {
            var u = inUsers[i];
            var username = u?.Username?.Trim() ?? "";
            if (username.Length == 0) { v.Add($"users[{i}].username", "Username is required."); continue; }
            if (username.Contains(':') || username.Any(char.IsControl)) { v.Add($"users[{i}].username", "Username may not contain ':' or control characters."); continue; }
            if (!seen.Add(username)) { v.Add($"users[{i}].username", $"User '{username}' is listed twice."); continue; }
            if (!string.IsNullOrEmpty(u!.Password))
            {
                if (u.Password.Length > 72) { v.Add($"users[{i}].password", "Password must be at most 72 characters (bcrypt limit)."); continue; }
                users.Add(new AccessUser { Username = username, PasswordHash = Passwords.Hash(u.Password) });
            }
            else
            {
                var old = previous?.Users.FirstOrDefault(x => x.Username == username);
                if (old is null || string.IsNullOrEmpty(old.PasswordHash))
                    v.Add($"users[{i}].password", $"A password is required for new user '{username}'.");
                else users.Add(new AccessUser { Username = username, PasswordHash = old.PasswordHash });
            }
        }

        if (!v.IsValid) return v.ToResult();
        target.Name = name;
        target.SatisfyAny = input.SatisfyAny;
        target.PassAuthToUpstream = input.PassAuthToUpstream;
        target.Rules = rules;
        target.Users = users;
        target.UpdatedAt = DateTime.UtcNow;
        return null;
    }
}
