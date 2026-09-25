using System.Text.Json;
using CaddyManager.Core.Contracts;

namespace CaddyManager.Platform.Readiness;

public sealed record DomainFacts(bool PartOfDomain, string? Domain, int DomainRole, string? ComputerDn, string? DnError);

public sealed record PortListener(string Protocol, int Port, string Address, int ProcessId, string? ProcessName, string? ProcessPath)
{
    public bool IsHttpSys => ProcessId == 4;
}

public sealed record NetworkProfileFact(string InterfaceAlias, int InterfaceIndex, string Name, string Category);

public sealed record SystemFacts(
    List<NetworkProfileFact> Profiles,
    DomainFacts Domain,
    List<PortListener> Listeners,
    List<string> HttpSysUrls,
    string? W3svc,
    string? WinHttpProxy);

public sealed record FirewallProfileFact(string Name, string Enabled, string DefaultInboundAction, string AllowLocalFirewallRules);

public sealed record FirewallRuleFact(
    string Name,
    string DisplayName,
    string Action,
    string Profile,
    string Protocol,
    List<string> LocalPorts,
    string Program,
    string Service,
    List<string> RemoteAddresses,
    string Source,
    string? SourceName,
    string? Group);

public sealed record FirewallFacts(
    string Service,
    List<FirewallProfileFact> Profiles,
    Dictionary<string, int> GpoLocalMerge,
    List<FirewallRuleFact> Rules);

/// <summary>Maps the JSON produced by <see cref="ReadinessScripts"/> to typed facts (tolerant of missing fields).</summary>
public static class WindowsFactsParser
{
    public static SystemFacts ParseSystem(JsonElement e)
    {
        var d = Obj(e, "domain");
        return new SystemFacts(
            Arr(e, "profiles").Select(p => new NetworkProfileFact(S(p, "interfaceAlias") ?? "", I(p, "interfaceIndex"), S(p, "name") ?? "", S(p, "category") ?? "")).ToList(),
            new DomainFacts(B(d, "partOfDomain"), S(d, "domain"), I(d, "domainRole"), NullIfEmpty(S(d, "computerDn")), NullIfEmpty(S(d, "dnError"))),
            Arr(e, "listeners").Select(l => new PortListener(S(l, "protocol") ?? "", I(l, "port"), S(l, "address") ?? "", I(l, "processId"),
                NullIfEmpty(S(l, "processName")), NullIfEmpty(S(l, "processPath")))).ToList(),
            Strings(e, "httpSysUrls"),
            NullIfEmpty(S(e, "w3svc")),
            NullIfEmpty(S(e, "winHttpProxy")));
    }

    public static FirewallFacts ParseFirewall(JsonElement e) => new(
        S(e, "service") ?? "Unknown",
        Arr(e, "profiles").Select(p => new FirewallProfileFact(S(p, "name") ?? "", S(p, "enabled") ?? "", S(p, "defaultInboundAction") ?? "",
            S(p, "allowLocalFirewallRules") ?? "")).ToList(),
        Arr(e, "gpoLocalMerge").Where(g => S(g, "profile") is not null)
            .ToDictionary(g => S(g, "profile")!, g => I(g, "value", -1), StringComparer.OrdinalIgnoreCase),
        Arr(e, "rules").Select(r => new FirewallRuleFact(
            S(r, "name") ?? "", S(r, "displayName") ?? "", S(r, "action") ?? "", S(r, "profile") ?? "Any", S(r, "protocol") ?? "Any",
            Strings(r, "localPorts"), S(r, "program") ?? "Any", S(r, "service") ?? "Any", Strings(r, "remoteAddresses"),
            S(r, "source") ?? "", NullIfEmpty(S(r, "sourceName")), NullIfEmpty(S(r, "group")))).ToList());

    public static List<NetworkProfileInfo> ToProfileInfos(IEnumerable<NetworkProfileFact> facts) =>
        facts.Select(f => new NetworkProfileInfo { InterfaceAlias = f.InterfaceAlias, Name = f.Name, Category = f.Category }).ToList();

    // ---- tolerant JSON helpers (ConvertTo-Json in PS 5.1 may emit a scalar where an array was expected)

    private static JsonElement Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;

    /// <summary>Unwraps the PS 5.1 {"value":[...],"Count":n} array serialisation.</summary>
    private static JsonElement Unwrap(JsonElement v) =>
        v.ValueKind == JsonValueKind.Object && v.TryGetProperty("value", out var inner) && inner.ValueKind == JsonValueKind.Array
        && v.TryGetProperty("Count", out _) ? inner : v;

    private static IEnumerable<JsonElement> Arr(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return [];
        v = Unwrap(v);
        return v.ValueKind switch
        {
            JsonValueKind.Array => v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).ToList(),
            JsonValueKind.Object => [v],
            _ => [],
        };
    }

    private static List<string> Strings(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return [];
        v = Unwrap(v);
        return v.ValueKind switch
        {
            JsonValueKind.Array => v.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : x.ToString()).ToList(),
            JsonValueKind.String => [v.GetString()!],
            JsonValueKind.Number => [v.ToString()],
            _ => [],
        };
    }

    private static string? S(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToString(),
            _ => null,
        };
    }

    private static int I(JsonElement e, string name, int fallback = 0)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        return v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out n) ? n : fallback;
    }

    private static bool B(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) &&
        (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b) && b));

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
