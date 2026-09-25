using System.Globalization;
using System.Net;
using System.Net.Sockets;
using CaddyManager.Config.Validation;
using CaddyManager.Core.Contracts;
using DnsClient;
using DnsClient.Protocol;

namespace CaddyManager.Config.DnsProviders;

/// <summary>
/// Checks the one-time CNAME records of DNS challenge delegation: _acme-challenge.&lt;domain&gt; (a wildcard's "*." removed)
/// must be a CNAME that leads, directly or through a chain of at most <see cref="MaxHops"/> CNAMEs, to the delegation name
/// Caddy writes the TXT record at (challenges.dns.override_domain).
///
/// .NET has no CNAME lookup API, so queries go through DnsClient.NET 1.8.0 (Apache-2.0,
/// https://dnsclient.michaco.net/ ; https://github.com/MichaCo/DnsClient.NET): recursive CNAME queries (a resolver does
/// not chase CNAMEs when the CNAME type itself is asked for, so each hop is one query), TCP fallback on truncation, OS
/// resolver discovery (network interfaces and NRPT on Windows) when no resolvers are given. Its response cache is
/// disabled: every check reflects the DNS as it is now. Names are compared case-insensitively without trailing dots.
/// </summary>
public static class DnsDelegationChecker
{
    public const int MaxHops = 8;
    public static readonly TimeSpan DomainTimeout = TimeSpan.FromSeconds(5);
    /// <summary>What a public ACME CA sees (Cloudflare and Google public DNS).</summary>
    public static readonly IReadOnlyList<string> PublicResolvers = ["1.1.1.1:53", "8.8.8.8:53"];
    public const string SystemResolvers = "system";

    /// <param name="resolvers">host:port resolvers, or null for the operating system's resolvers.</param>
    public static async Task<DelegationCheckResult> CheckAsync(IReadOnlyList<string> domains, string target, IReadOnlyList<string>? resolvers,
        CancellationToken ct = default)
    {
        var expected = Normalize(target);
        var checkedAt = DateTime.UtcNow;
        var records = domains.Select(d => (Domain: d, Record: NetUtil.AcmeChallengeRecord(d))).ToList();

        ILookupClient? client = null;
        string? setupError = null;
        List<string> resolverNames;
        if (resolvers is null)
        {
            resolverNames = [SystemResolvers];
            try { client = CreateClient(null); }
            catch (Exception ex)
            {
                // Name server discovery reads the network configuration; any failure there is reported per domain.
                setupError = "The operating system's DNS resolvers could not be determined: " + ex.Message;
            }
        }
        else
        {
            resolverNames = resolvers.ToList();
            var servers = new List<NameServer>();
            var unresolved = new List<string>();
            foreach (var r in resolvers)
            {
                var endpoints = await ResolveEndpointAsync(r, ct);
                if (endpoints.Count == 0) unresolved.Add(r);
                servers.AddRange(endpoints.Select(e => new NameServer(e)));
            }
            if (servers.Count == 0) setupError = $"None of the DNS resolvers could be used ({string.Join(", ", unresolved)}): their host names do not resolve.";
            else client = CreateClient(servers);
        }

        // One lookup per record name (a wildcard and its base domain share _acme-challenge.<base>), all in parallel.
        var lookups = records.Select(r => r.Record).Distinct(StringComparer.Ordinal).ToDictionary(r => r, r => client is null
            ? Task.FromResult(new Outcome(DelegationStatus.Error, [], setupError))
            : CheckRecordWithTimeoutAsync(client, r, expected, ct), StringComparer.Ordinal);
        await Task.WhenAll(lookups.Values);

        return new DelegationCheckResult
        {
            Resolvers = resolverNames,
            CheckedAt = checkedAt,
            Checks = records.Select(r =>
            {
                var o = lookups[r.Record].Result;
                return new DelegationCheck
                {
                    Domain = r.Domain,
                    RecordName = r.Record,
                    ExpectedTarget = expected,
                    Status = o.Status,
                    Found = o.Found,
                    Detail = o.Detail,
                };
            }).ToList(),
        };
    }

    private sealed record Outcome(DelegationStatus Status, List<string> Found, string? Detail);

    private static LookupClient CreateClient(List<NameServer>? servers)
    {
        var options = servers is null ? new LookupClientOptions() : new LookupClientOptions(servers.ToArray());
        options.UseCache = false;
        options.Recursion = true;
        options.Timeout = TimeSpan.FromSeconds(2);
        options.Retries = 1;
        options.UseTcpFallback = true;
        options.ThrowDnsErrors = false;
        // An empty answer (no CNAME) is a real answer, not a reason to ask the next resolver.
        options.ContinueOnEmptyResponse = false;
        return new LookupClient(options);
    }

    private static async Task<Outcome> CheckRecordWithTimeoutAsync(ILookupClient client, string record, string expected, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(DomainTimeout);
        try
        {
            // WaitAsync: the 5 s limit holds even if a transport ignores the token.
            return await CheckRecordAsync(client, record, expected, cts.Token).WaitAsync(DomainTimeout, ct);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return new Outcome(DelegationStatus.Error, [], $"No answer for {record} within {DomainTimeout.TotalSeconds:0} seconds.");
        }
        catch (DnsResponseException ex)
        {
            return new Outcome(DelegationStatus.Error, [], $"The lookup of {record} failed: {ex.DnsError ?? ex.Message}.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Socket errors, unusable name servers...: reported as the domain's result, never as a failed request.
            return new Outcome(DelegationStatus.Error, [], $"The lookup of {record} failed: {ex.Message.TrimEnd('.')}.");
        }
    }

    private static async Task<Outcome> CheckRecordAsync(ILookupClient client, string record, string expected, CancellationToken ct)
    {
        var found = new List<string>();
        var name = record;
        var seen = new HashSet<string>(StringComparer.Ordinal) { record };
        var nxdomain = false;
        for (var hop = 0; hop < MaxHops; hop++)
        {
            var response = await client.QueryAsync(name, QueryType.CNAME, QueryClass.IN, ct);
            if (response.Header.ResponseCode == DnsHeaderResponseCode.NotExistentDomain)
            {
                nxdomain = hop == 0;
                break;
            }
            if (response.HasError)
            {
                if (hop == 0)
                    return new Outcome(DelegationStatus.Error, found, $"The resolver answered {response.ErrorMessage} for {record}.");
                break; // the chain goes on somewhere the resolver cannot answer for; judged by what was found
            }
            var cnames = response.Answers.CnameRecords().ToList();
            var cname = cnames.FirstOrDefault(c => Normalize(c.DomainName.Value) == name) ?? cnames.FirstOrDefault();
            if (cname is null) break;
            var next = Normalize(cname.CanonicalName.Value);
            found.Add(next);
            if (next == expected)
                return new Outcome(DelegationStatus.Ok, found, found.Count == 1
                    ? $"{record} is a CNAME to {expected}."
                    : $"{record} reaches {expected} through {found.Count} CNAMEs.");
            if (!seen.Add(next))
                return new Outcome(DelegationStatus.Wrong, found, $"The CNAME chain of {record} loops back to {next}; point {record} directly to {expected}.");
            name = next;
        }

        if (found.Count == 0)
        {
            if (nxdomain)
                return new Outcome(DelegationStatus.Missing, found, $"{record} does not exist. Create the record: {record} CNAME {expected}.");
            // The name exists without a CNAME: a leftover TXT record (e.g. from a manual challenge) blocks the CNAME.
            var txt = await client.QueryAsync(record, QueryType.TXT, QueryClass.IN, ct);
            if (!txt.HasError && txt.Answers.TxtRecords().Any())
                return new Outcome(DelegationStatus.Wrong, found, $"{record} has a TXT record instead of a CNAME. Delete it and create: {record} CNAME {expected}.");
            return new Outcome(DelegationStatus.Missing, found, $"{record} has no CNAME record. Create the record: {record} CNAME {expected}.");
        }
        if (found.Count >= MaxHops)
            return new Outcome(DelegationStatus.Wrong, found, $"{record} starts a chain of more than {MaxHops} CNAMEs without reaching {expected}.");
        return new Outcome(DelegationStatus.Wrong, found, $"{record} points to {found[0]}{(found.Count > 1 ? $" (and on to {found[^1]})" : "")}, not to {expected}. Change the CNAME target to {expected}.");
    }

    /// <summary>Lower case, no trailing dot.</summary>
    public static string Normalize(string name) => name.Trim().TrimEnd('.').ToLowerInvariant();

    /// <summary>"host:port" (IPv6 bracketed) → endpoints; host names are resolved through the OS.</summary>
    private static async Task<List<IPEndPoint>> ResolveEndpointAsync(string hostPort, CancellationToken ct)
    {
        var t = hostPort.Trim();
        var colon = t.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(t[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port)) return [];
        var host = t[..colon].Trim('[', ']');
        if (IPAddress.TryParse(host, out var ip)) return [new IPEndPoint(ip, port)];
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct);
            return addresses.Select(a => new IPEndPoint(a, port)).ToList();
        }
        catch (SocketException)
        {
            return [];
        }
    }
}
