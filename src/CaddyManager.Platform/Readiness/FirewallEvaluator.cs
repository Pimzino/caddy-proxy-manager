using System.Text;
using CaddyManager.Core.Contracts;

namespace CaddyManager.Platform.Readiness;

/// <summary>
/// Decides whether the effective firewall policy (ActiveStore = local + GPO rules as currently enforced)
/// lets inbound traffic reach a port, for every active network profile.
/// </summary>
public static class FirewallEvaluator
{
    private static readonly string[] AllProfiles = ["Domain", "Private", "Public"];

    /// <summary>Firewall profiles in effect, derived from the network connection categories.</summary>
    public static IReadOnlyList<string> ActiveProfiles(IEnumerable<NetworkProfileFact> connections)
    {
        var list = connections.Select(c => c.Category switch
            {
                "DomainAuthenticated" or "Domain" or "2" => "Domain",
                "Private" or "1" => "Private",
                "Public" or "0" => "Public",
                _ => null,
            })
            .Where(p => p is not null).Select(p => p!).Distinct().ToList();
        return list.Count > 0 ? list : AllProfiles;
    }

    /// <summary>True when Group Policy says "Apply local firewall rules: No" for the profile.</summary>
    public static bool LocalRulesIgnored(FirewallFacts f, string profile) =>
        f.Profiles.Any(p => p.Name.Equals(profile, StringComparison.OrdinalIgnoreCase) &&
                            p.AllowLocalFirewallRules.Equals("False", StringComparison.OrdinalIgnoreCase))
        || (f.GpoLocalMerge.TryGetValue(profile, out var v) && v == 0);

    public static ReadinessCheck Evaluate(RequiredFirewallRule req, FirewallFacts f, IReadOnlyList<string> activeProfiles)
    {
        var details = new StringBuilder();
        var allowedProfiles = new List<string>();
        var deniedProfiles = new List<string>();
        var blocked = false;
        var anyLocalIgnored = false;

        foreach (var profile in activeProfiles)
        {
            var prof = f.Profiles.FirstOrDefault(p => p.Name.Equals(profile, StringComparison.OrdinalIgnoreCase));
            var localIgnored = LocalRulesIgnored(f, profile);
            anyLocalIgnored |= localIgnored;
            if (prof is not null && prof.Enabled.Equals("False", StringComparison.OrdinalIgnoreCase))
            {
                allowedProfiles.Add(profile);
                details.AppendLine($"{profile}: firewall profile is disabled, traffic is not filtered.");
                continue;
            }
            var matching = f.Rules.Where(r => Applies(r, req, profile, localIgnored)).ToList();
            var blocks = matching.Where(r => r.Action.Equals("Block", StringComparison.OrdinalIgnoreCase)).ToList();
            var allows = matching.Where(r => r.Action.Equals("Allow", StringComparison.OrdinalIgnoreCase)).ToList();
            if (blocks.Count > 0)
            {
                blocked = true;
                deniedProfiles.Add(profile);
                details.AppendLine($"{profile}: BLOCKED by {Describe(blocks)} (block rules override allow rules).");
            }
            else if (allows.Count > 0)
            {
                allowedProfiles.Add(profile);
                details.AppendLine($"{profile}: allowed by {Describe(allows)}.");
            }
            else if (prof is not null && prof.DefaultInboundAction.Equals("Allow", StringComparison.OrdinalIgnoreCase))
            {
                allowedProfiles.Add(profile);
                details.AppendLine($"{profile}: no specific rule, but the profile's default inbound action is Allow.");
            }
            else
            {
                deniedProfiles.Add(profile);
                details.AppendLine($"{profile}: no enabled inbound allow rule for {req.Protocol} {req.Port}" +
                                   (localIgnored ? " — local rules are ignored by Group Policy (AllowLocalFirewallRules = False), only GPO rules count." : "."));
            }
        }

        var what = $"{req.Protocol} {req.Port} ({req.Purpose})";
        var status = deniedProfiles.Count == 0 ? CheckStatus.Pass
            : allowedProfiles.Count == 0 ? CheckStatus.Fail
            : CheckStatus.Warn;
        var fixable = status != CheckStatus.Pass && !blocked && !anyLocalIgnored;
        string summary = status switch
        {
            CheckStatus.Pass => $"Inbound {what} is allowed for the active profile(s): {string.Join(", ", activeProfiles)}.",
            CheckStatus.Warn => $"Inbound {what} is allowed for {string.Join(", ", allowedProfiles)} but not for {string.Join(", ", deniedProfiles)}.",
            _ => blocked ? $"Inbound {what} is blocked by a firewall rule." : $"No firewall rule allows inbound {what}.",
        };
        string? remediation = status == CheckStatus.Pass ? null
            : blocked ? "Disable or narrow the blocking rule(s) listed in the details (if they come from Group Policy, change the GPO)."
            : anyLocalIgnored ? "Group Policy ignores local firewall rules on this server. Add the rule to a GPO — use the script from the Domain check (GPO script)."
            : $"Create the rule '{req.DisplayName}' (click Fix, or run the script).";
        string? script = status == CheckStatus.Pass || blocked ? null
            : anyLocalIgnored ? "# Local rules are ignored by Group Policy on this server; deploy the rule via GPO:\n# GET /api/readiness/gpo-script (Readiness page → GPO script)"
            : ReadinessScripts.CreateFirewallRuleCommand(req);

        return new ReadinessCheck
        {
            Id = req.CheckId,
            Category = "Firewall",
            Title = $"Inbound {req.Protocol} {req.Port} — {req.Purpose}",
            Status = status,
            Summary = summary,
            Details = details.ToString().Trim(),
            Remediation = remediation,
            Script = script,
            Fixable = fixable,
        };
    }

    internal static bool Applies(FirewallRuleFact r, RequiredFirewallRule req, string profile, bool localIgnored)
    {
        if (localIgnored && r.Source.Equals("Local", StringComparison.OrdinalIgnoreCase)) return false;
        if (!ProfileMatches(r.Profile, profile)) return false;
        if (!ProtocolMatches(r.Protocol, req.Protocol)) return false;
        if (!r.LocalPorts.Any(p => PortMatches(p, req.Port))) return false;
        if (!IsAny(r.Program) && (req.Program is null || !SamePath(r.Program, req.Program))) return false;
        if (!IsAny(r.Service) && (req.Service is null || !r.Service.Equals(req.Service, StringComparison.OrdinalIgnoreCase))) return false;
        return true;
    }

    internal static bool ProfileMatches(string ruleProfile, string profile)
    {
        if (string.IsNullOrWhiteSpace(ruleProfile) || ruleProfile.Equals("Any", StringComparison.OrdinalIgnoreCase)) return true;
        return ruleProfile.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(p => p.Equals(profile, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool ProtocolMatches(string ruleProtocol, string wanted) => ruleProtocol.ToUpperInvariant() switch
    {
        "ANY" or "" => true,
        "TCP" or "6" => wanted.Equals("TCP", StringComparison.OrdinalIgnoreCase),
        "UDP" or "17" => wanted.Equals("UDP", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    internal static bool PortMatches(string spec, int port)
    {
        spec = spec.Trim();
        if (spec.Equals("Any", StringComparison.OrdinalIgnoreCase)) return true;
        var dash = spec.IndexOf('-');
        if (dash > 0 && int.TryParse(spec[..dash], out var lo) && int.TryParse(spec[(dash + 1)..], out var hi))
            return port >= lo && port <= hi;
        return int.TryParse(spec, out var single) && single == port;
    }

    private static bool IsAny(string? s) => string.IsNullOrWhiteSpace(s) || s.Equals("Any", StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string a, string b)
    {
        static string Norm(string p) => Environment.ExpandEnvironmentVariables(p.Trim().Trim('"')).Replace('/', '\\').TrimEnd('\\');
        return string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string Describe(List<FirewallRuleFact> rules) => string.Join(", ", rules.Take(5).Select(r =>
    {
        var src = r.Source.Equals("GroupPolicy", StringComparison.OrdinalIgnoreCase)
            ? $"GPO{(r.SourceName is { Length: > 0 } n ? " " + n : "")}"
            : r.Source.Length > 0 ? r.Source : "local";
        var remote = r.RemoteAddresses.Count > 0 && !r.RemoteAddresses.All(IsAny)
            ? $", remote addresses {string.Join(" ", r.RemoteAddresses.Take(5))}"
            : "";
        return $"'{r.DisplayName}' [{src}{remote}]";
    })) + (rules.Count > 5 ? $" and {rules.Count - 5} more" : "");
}
