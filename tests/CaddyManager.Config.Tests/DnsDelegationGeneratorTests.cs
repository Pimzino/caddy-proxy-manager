using System.Text.Json.Nodes;
using CaddyManager.Config.Generation;
using CaddyManager.Core.Models;

namespace CaddyManager.Config.Tests;

/// <summary>
/// Generator side of DNS challenge delegation (SPEC Round 3b): DNS-challenge ACME subjects grouped into one automation
/// policy per effective delegation name. Real issuance through the CNAMEs is covered by DnsDelegationE2ETests.
///
/// Ways the grouping could fail (each is asserted below):
/// - override_domain still emitted for every DNS policy (the old global setting) or on the HTTP policy;
/// - hosts with different effective names merged into one policy (someone's TXT written at the wrong name), or hosts
///   sharing a name split into several policies;
/// - Off hosts inheriting the settings' default, Default hosts ignoring it, Custom hosts ignoring their own name;
/// - a name kept with its trailing dot or upper case (certmagic appends the dot: "name..");
/// - the wildcard of an HTTP-challenge host (DNS because a provider is configured) not following its host's delegation;
/// - the provider, resolvers or legacy issuer JSON present on some groups' issuers only;
/// - policy order depending on host order (config churn between applies).
/// </summary>
public sealed class DnsDelegationGeneratorTests : IDisposable
{
    private readonly TempEnv _env = new();
    public void Dispose() => _env.Dispose();

    private static CaddySettings Settings(string? defaultName) => new()
    {
        DnsProvider = "cloudflare",
        DnsResolvers = ["1.1.1.1:53"],
        DnsOverrideDomain = defaultName,
    };

    private static SiteHost Host(string domain, HostDnsDelegation delegation = HostDnsDelegation.Default, string? name = null,
        HostAcmeChallenge challenge = HostAcmeChallenge.Dns, params string[] more)
    {
        var h = Build.Proxy(domain, tls: TlsMode.Acme);
        h.Domains.AddRange(more);
        h.AcmeChallenge = challenge;
        h.DnsDelegation = delegation;
        h.DnsOverrideDomain = name;
        return h;
    }

    private JsonArray Policies(CaddySettings s, IEnumerable<SiteHost> hosts, string? issuerJson = null)
    {
        var input = Build.Input(_env.Paths, s, hosts) with
        {
            DnsProviderSecrets = new Dictionary<string, string> { ["api_token"] = "tok-123" },
            AcmeIssuerJson = issuerJson,
        };
        return CaddyConfigGenerator.Generate(input).Config["apps"]!["tls"]!["automation"]!["policies"]!.AsArray();
    }

    private static string[] Subjects(JsonNode p) => p["subjects"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
    private static string? OverrideOf(JsonNode p) =>
        p["issuers"]!.AsArray().Select(i => i!["challenges"]?["dns"]?["override_domain"]?.GetValue<string>()).Distinct().Single();

    [Fact]
    public void Dns_policies_are_grouped_by_effective_delegation_name()
    {
        var hosts = new[]
        {
            Host("off.example.com", HostDnsDelegation.Off),
            Host("custom1.example.com", HostDnsDelegation.Custom, "_ACME-challenge.one.validation.example.net."),
            Host("default1.example.com"),
            Host("custom2.example.com", HostDnsDelegation.Custom, "_acme-challenge.one.validation.example.net"),
            Host("default2.example.com", more: "*.default2.example.com"),
            Host("http.example.com", HostDnsDelegation.Custom, "_acme-challenge.wild.validation.example.net", HostAcmeChallenge.Http, "*.http.example.com"),
            Host("plain-http.example.com", challenge: HostAcmeChallenge.Http),
        };
        var policies = Policies(Settings("_acme-challenge.validation.example.net"), hosts);

        // HTTP policy first (unchanged), then one DNS policy per name: none, then names in ordinal order.
        Assert.Equal(5, policies.Count);
        Assert.Equal(["http.example.com", "plain-http.example.com"], Subjects(policies[0]!));
        Assert.Null(policies[0]!["issuers"]![0]!["challenges"]?["dns"]);
        Assert.Equal(["off.example.com"], Subjects(policies[1]!));
        Assert.Null(OverrideOf(policies[1]!));
        Assert.Equal(["custom1.example.com", "custom2.example.com"], Subjects(policies[2]!));
        Assert.Equal("_acme-challenge.one.validation.example.net", OverrideOf(policies[2]!));
        Assert.Equal(["*.default2.example.com", "default1.example.com", "default2.example.com"], Subjects(policies[3]!));
        Assert.Equal("_acme-challenge.validation.example.net", OverrideOf(policies[3]!));
        // the wildcard of an HTTP-challenge host follows its host's delegation
        Assert.Equal(["*.http.example.com"], Subjects(policies[4]!));
        Assert.Equal("_acme-challenge.wild.validation.example.net", OverrideOf(policies[4]!));

        foreach (var p in policies.Skip(1))
            foreach (var iss in p!["issuers"]!.AsArray())
            {
                var dns = iss!["challenges"]!["dns"]!;
                Assert.Equal("cloudflare", dns["provider"]!["name"]!.GetValue<string>());
                Assert.Equal("tok-123", dns["provider"]!["api_token"]!.GetValue<string>());
                Assert.Equal(["1.1.1.1:53"], dns["resolvers"]!.AsArray().Select(x => x!.GetValue<string>()));
            }

        // host order does not change the output
        var reversed = Policies(Settings("_acme-challenge.validation.example.net"), hosts.Reverse());
        Assert.Equal(policies.ToJsonString(), reversed.ToJsonString());
    }

    [Fact]
    public void Without_a_default_name_default_hosts_share_the_policy_without_delegation()
    {
        var policies = Policies(Settings(null), [Host("a.example.com"), Host("b.example.com", HostDnsDelegation.Off), Host("c.example.com", HostDnsDelegation.Custom, "deleg.acme-dns.example.org")]);
        Assert.Equal(2, policies.Count);
        Assert.Equal(["a.example.com", "b.example.com"], Subjects(policies[0]!));
        Assert.Null(OverrideOf(policies[0]!));
        Assert.Equal(["c.example.com"], Subjects(policies[1]!));
        Assert.Equal("deleg.acme-dns.example.org", OverrideOf(policies[1]!));
    }

    [Fact]
    public void Legacy_issuer_json_is_merged_into_every_delegation_group()
    {
        var policies = Policies(Settings("_acme-challenge.validation.example.net"),
            [Host("a.example.com"), Host("b.example.com", HostDnsDelegation.Off)], issuerJson: """{"preferred_chains":{"smallest":true}}""");
        Assert.Equal(2, policies.Count);
        Assert.All(policies, p => Assert.True(p!["issuers"]![0]!["preferred_chains"]!["smallest"]!.GetValue<bool>()));
        Assert.Null(OverrideOf(policies[0]!));
        Assert.Equal("_acme-challenge.validation.example.net", OverrideOf(policies[1]!));
    }

    [Fact]
    public void Effective_name_and_dns_use_follow_the_host_and_settings()
    {
        var s = Settings("_acme-challenge.validation.example.net.");
        Assert.Equal("_acme-challenge.validation.example.net", CaddyConfigGenerator.EffectiveDnsOverrideDomain(Host("a.example.com"), s));
        Assert.Null(CaddyConfigGenerator.EffectiveDnsOverrideDomain(Host("a.example.com", HostDnsDelegation.Off), s));
        Assert.Equal("x.example.net", CaddyConfigGenerator.EffectiveDnsOverrideDomain(Host("a.example.com", HostDnsDelegation.Custom, "X.example.net"), s));
        Assert.Null(CaddyConfigGenerator.EffectiveDnsOverrideDomain(Host("a.example.com"), Settings(" ")));

        Assert.True(CaddyConfigGenerator.UsesDnsChallenge(Host("a.example.com"), s));
        Assert.False(CaddyConfigGenerator.UsesDnsChallenge(Host("a.example.com", challenge: HostAcmeChallenge.Http), s));
        Assert.True(CaddyConfigGenerator.UsesDnsChallenge(Host("a.example.com", challenge: HostAcmeChallenge.Http, more: "*.a.example.com"), s));
        Assert.False(CaddyConfigGenerator.UsesDnsChallenge(Host("a.example.com", challenge: HostAcmeChallenge.Http, more: "*.a.example.com"), new CaddySettings()));
        Assert.True(CaddyConfigGenerator.UsesDnsChallenge(Host("a.example.com", challenge: HostAcmeChallenge.Default), new CaddySettings { DefaultAcmeChallenge = AcmeChallengeType.Dns }));
        var internalHost = Host("a.example.com");
        internalHost.Tls = TlsMode.Internal;
        Assert.False(CaddyConfigGenerator.UsesDnsChallenge(internalHost, s));
    }
}
