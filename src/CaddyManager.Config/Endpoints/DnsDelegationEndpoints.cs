using CaddyManager.Config.DnsProviders;
using CaddyManager.Config.Generation;
using CaddyManager.Config.Validation;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CaddyManager.Config.Endpoints;

/// <param name="PublicResolvers">true: ask the public resolvers (what the CA sees), even when resolvers are configured.</param>
/// <param name="SystemResolvers">true: ask this server's own (operating system) resolvers, e.g. Active Directory DNS.</param>
public sealed record DelegationCheckInput(string? HostId, List<string>? Domains, string? Target, bool? PublicResolvers, bool? SystemResolvers = null);

/// <summary>POST /api/dns/delegation-check (viewer): are the _acme-challenge CNAME records of DNS challenge delegation in place?</summary>
internal static class DnsDelegationEndpoints
{
    public const int MaxDomains = 100;

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/dns/delegation-check", async (DelegationCheckInput? body, IStore store, CancellationToken ct) =>
        {
            body ??= new DelegationCheckInput(null, null, null, null);
            if (body.PublicResolvers == true && body.SystemResolvers == true)
                return ApiResults.BadRequest("Choose either the public resolvers or the system resolvers, not both.");
            var settings = store.GetSettings<CaddySettings>();
            var v = new Validator();
            List<string> domains;
            string? target;
            if (!string.IsNullOrWhiteSpace(body.HostId))
            {
                // The host's DNS-challenge names and its effective delegation name (as the generator uses them).
                if (store.Col<SiteHost>().FindById(body.HostId.Trim()) is not { } host) return ApiResults.NotFound("Host");
                if (!CaddyConfigGenerator.UsesDnsChallenge(host, settings))
                    return ApiResults.BadRequest("This host does not use the DNS challenge, so it needs no delegation records.",
                        new Dictionary<string, string[]> { ["hostId"] = ["The host does not use the DNS challenge."] });
                var allDns = CaddyConfigGenerator.EffectiveChallenge(host, settings) == AcmeChallengeType.Dns;
                domains = host.Domains.Where(d => allDns || CaddyConfigGenerator.IsWildcard(d)).Where(d => !NetUtil.TryParseIp(d, out _)).ToList();
                target = CaddyConfigGenerator.EffectiveDnsOverrideDomain(host, settings);
                if (target is null)
                    return ApiResults.BadRequest("This host does not delegate the DNS challenge.",
                        new Dictionary<string, string[]> { ["target"] = [host.DnsDelegation == HostDnsDelegation.Off
                            ? "Delegation is turned off for this host."
                            : "No default delegation name is set under Settings > Caddy, and the host has no custom one."] });
            }
            else
            {
                domains = [];
                foreach (var raw in body.Domains ?? [])
                {
                    var d = NetUtil.NormalizeDomain(raw);
                    if (d is null) continue;
                    if (!NetUtil.IsValidDomain(d) || NetUtil.TryParseIp(d, out _)) v.Add("domains", $"'{raw}' is not a domain name.");
                    else if (!domains.Contains(d)) domains.Add(d);
                }
                if (domains.Count == 0 && v.IsValid) v.Add("domains", "Enter at least one domain.");
                target = string.IsNullOrWhiteSpace(body.Target) ? settings.DnsOverrideDomain : body.Target;
                target = NetUtil.NormalizeDomain(target);
                if (target is null) v.Add("target", "Enter the delegation name, or set a default one under Settings > Caddy.");
                else if (!NetUtil.IsValidDnsName(target)) v.Add("target", $"'{target}' is not a valid DNS name.");
            }
            if (domains.Count > MaxDomains) v.Add("domains", $"At most {MaxDomains} domains can be checked at once.");
            if (!v.IsValid) return v.ToResult();

            // An explicit choice wins: the public resolvers (what the CA sees) or the operating system's. Otherwise the
            // resolvers Caddy itself uses: those of Settings > Caddy when set, else the public resolvers — with no
            // resolvers configured, certmagic (v0.25.3 RecursiveNameservers) reads /etc/resolv.conf, which does not exist
            // on Windows, and falls back to public DNS (8.8.8.8, 8.8.4.4, 1.1.1.1, 1.0.0.1). The OS resolvers can differ
            // from both on split-horizon (e.g. Active Directory) networks.
            // https://github.com/caddyserver/certmagic/blob/v0.25.3/dnsutil.go
            IReadOnlyList<string>? resolvers = body.PublicResolvers == true ? DnsDelegationChecker.PublicResolvers
                : body.SystemResolvers == true ? null
                : settings.DnsResolvers.Count > 0 ? settings.DnsResolvers
                : DnsDelegationChecker.PublicResolvers;
            return Results.Ok(await DnsDelegationChecker.CheckAsync(domains, target!, resolvers, ct));
        }).RequireAuthorization(Policies.Viewer);
    }
}
