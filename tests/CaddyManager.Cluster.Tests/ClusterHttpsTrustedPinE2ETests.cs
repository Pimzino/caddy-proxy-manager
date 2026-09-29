using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit.Abstractions;
using static CaddyManager.Cluster.Tests.ClusterE2ETests;

namespace CaddyManager.Cluster.Tests;

/// <summary>
/// End to end over HTTPS with node certificates the primary TRUSTS (issued by a test CA the primary's chain policy trusts,
/// with the right IP address): once a fingerprint is pinned, only that certificate is accepted. ClusterHttpsPinE2ETests
/// covers self-signed (untrusted) certificates. Ways this could fail, and the step that checks each:
/// <list type="number">
/// <item>A different certificate that the primary trusts is accepted because it is trusted (the bug) — step 3.</item>
/// <item>The pinned certificate is refused because it is also trusted, or its chain is now ignored wrongly — step 2.</item>
/// <item>The primary silently replaces the pin with the certificate it now sees (TOFU over a pinned node) — step 3.</item>
/// <item>The error does not say what the node presents, so an admin cannot check it before re-pinning — step 3.</item>
/// <item>A legitimately renewed certificate cannot be put back into service (re-pin does not work) — step 4.</item>
/// <item>Re-pinning adds the new certificate instead of replacing the old one, so the old one still works — step 5.</item>
/// <item>The test proves nothing because its certificates are not actually trusted (controls in steps 1 and 3).</item>
/// </list>
/// Writes e2e-artifacts/cluster-https-trusted-pin-e2e.json.
/// </summary>
public sealed class ClusterHttpsTrustedPinE2ETests(ITestOutputHelper output)
{
    private readonly JsonObject _report = E2EArtifacts.Report(nameof(ClusterHttpsTrustedPinE2ETests) + "." + nameof(TrustedCertificateWithAnotherFingerprintIsRefused));
    private readonly JsonArray _steps = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    [Fact]
    public async Task TrustedCertificateWithAnotherFingerprintIsRefused()
    {
        Assert.True(DevCaddy.Path is not null, "The development Caddy binary .dev/bin/caddy is required (copy .dev from the main checkout).");
        _report["steps"] = _steps;
        using var ca = TestCa();
        using var certA = Issue(ca);
        using var certB = Issue(ca);
        using var caPublic = X509CertificateLoader.LoadCertificate(ca.RawData);
        var trust = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
        trust.CustomTrustStore.Add(caPublic);
        var fpA = NodeClient.FingerprintOf(certA);
        var fpB = NodeClient.FingerprintOf(certB);
        Manager? primary = null, node = null;
        try
        {
            await Step("1. start a primary that trusts the test CA, and a node whose UI uses certificate A from that CA", async () =>
            {
                var p = Manager.CreateAsync("primary-trust", clusterOptions: o => o.NodeCertificateChainPolicy = trust);
                var n = Manager.CreateAsync("node-t", certA);
                primary = await p;
                node = await n;
                // Controls: with the primary's chain policy the node's certificate validates without any error (chain and
                // name); with the machine's own store it does not, so the policy is what makes it trusted.
                Assert.Equal(SslPolicyErrors.None, await PolicyErrors(node.Url, trust));
                Assert.NotEqual(SslPolicyErrors.None, await PolicyErrors(node.Url, null));
                return new JsonObject
                {
                    ["nodeUrl"] = node.Url, ["certificateA"] = fpA, ["certificateB"] = fpB,
                    ["policyErrorsA"] = SslPolicyErrors.None.ToString(), ["machineStoreErrorsA"] = (await PolicyErrors(node.Url, null)).ToString(),
                };
            });
            var P = primary!;
            var N = node!;

            string nodeId = "";
            await Step("2. adding the node pins trusted certificate A; sync works over it", async () =>
            {
                var added = await P.Api.PostAsJsonAsync("api/servers", new { name = "node-t", url = N.Url }).OkJsonAsync("POST /api/servers");
                nodeId = added.GetProperty("server").GetProperty("id").GetString()!;
                var token = added.GetProperty("joinToken").GetString()!;
                Assert.True(ClusterCrypto.SameFingerprint(fpA, added.GetProperty("fingerprint").GetString()));
                await N.Api.PostAsJsonAsync("api/cluster/join", new { token }).OkJsonAsync("POST /api/cluster/join");
                var synced = await WaitInSync(P, nodeId, null, "sync over trusted, pinned https");
                return new JsonObject { ["pinned"] = fpA, ["status"] = synced.GetProperty("status").GetString(), ["revision"] = Rev(synced) };
            });

            await Step("3. the node presents certificate B (trusted, right address, other fingerprint): refused, the pin stays A", async () =>
            {
                await N.StopAsync();
                N.UiCertificate = certB;
                await N.StartAsync();
                Assert.Equal(SslPolicyErrors.None, await PolicyErrors(N.Url, trust)); // control: B is trusted too
                var refused = await Wait.ForValueAsync(async () =>
                {
                    var s = await Server(P, nodeId);
                    var error = s.TryGetProperty("lastError", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString()! : "";
                    return (error.Contains("pinned fingerprint") && error.Contains(fpB), s);
                }, TimeSpan.FromSeconds(30), () => "pin mismatch reported with the presented fingerprint\n" + P.LogTail());
                var offline = await Wait.ForValueAsync(async () =>
                {
                    var s = await Server(P, nodeId);
                    return (s.GetProperty("status").GetString() == "offline", s);
                }, TimeSpan.FromSeconds(30), () => "node reported offline\n" + P.LogTail());
                Assert.True(ClusterCrypto.SameFingerprint(fpA, P.Store.Col<ClusterNode>().FindById(nodeId).PinnedFingerprint), "the pin was not replaced");
                Assert.True(ClusterCrypto.SameFingerprint(fpA, offline.GetProperty("fingerprint").GetString()));
                return new JsonObject
                {
                    ["policyErrorsB"] = SslPolicyErrors.None.ToString(), ["lastError"] = refused.GetProperty("lastError").GetString(),
                    ["status"] = offline.GetProperty("status").GetString(), ["pinStillA"] = true,
                };
            });

            await Step("4. renewal: an admin re-pins, certificate B is pinned and the node is back in sync", async () =>
            {
                await P.Api.PutAsJsonAsync($"api/servers/{nodeId}", new { repin = true }).OkJsonAsync("PUT /api/servers/{id} repin");
                var pin = P.Store.Col<ClusterNode>().FindById(nodeId).PinnedFingerprint;
                Assert.True(ClusterCrypto.SameFingerprint(fpB, pin));
                var back = await WaitInSync(P, nodeId, null, "online after re-pin");
                var audit = P.Store.Col<Core.Models.AuditEntry>().FindAll().Last(a => a.ObjectType == "server" && a.Action == "updated");
                Assert.Contains(fpB, audit.Details);
                return new JsonObject { ["newPin"] = pin, ["status"] = back.GetProperty("status").GetString(), ["repinAudit"] = audit.Details };
            });

            await Step("5. the node goes back to certificate A (trusted, pinned before): refused, the re-pin replaced the pin", async () =>
            {
                await N.StopAsync();
                N.UiCertificate = certA;
                await N.StartAsync();
                var refused = await Wait.ForValueAsync(async () =>
                {
                    var s = await Server(P, nodeId);
                    var error = s.TryGetProperty("lastError", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString()! : "";
                    return (error.Contains("pinned fingerprint") && error.Contains(fpA), s);
                }, TimeSpan.FromSeconds(30), () => "old certificate refused after re-pin\n" + P.LogTail());
                Assert.True(ClusterCrypto.SameFingerprint(fpB, P.Store.Col<ClusterNode>().FindById(nodeId).PinnedFingerprint));
                return new JsonObject { ["lastError"] = refused.GetProperty("lastError").GetString(), ["pinStillB"] = true };
            });
            _report["result"] = "passed";
        }
        catch (Exception ex)
        {
            _report["result"] = "failed";
            _report["error"] = ex.ToString();
            if (primary is not null) _report["primaryLog"] = primary.LogTail(150);
            if (node is not null) _report["nodeLog"] = node.LogTail(150);
            throw;
        }
        finally
        {
            _report["totalMs"] = _clock.ElapsedMilliseconds;
            output.WriteLine("Artifact: " + E2EArtifacts.Write("cluster-https-trusted-pin-e2e.json", _report));
            if (node is not null) await node.DisposeAsync();
            if (primary is not null) await primary.DisposeAsync();
        }
    }

    /// <summary>
    /// The SslPolicyErrors a client sees for <paramref name="url"/> with <paramref name="policy"/> (null: the machine's
    /// trust store), with the same target host as the primary's node client.
    /// </summary>
    private static async Task<SslPolicyErrors> PolicyErrors(string url, X509ChainPolicy? policy)
    {
        var uri = new Uri(url);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(uri.Host, uri.Port);
        SslPolicyErrors? seen = null;
        await using var ssl = new SslStream(tcp.GetStream(), false);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = uri.IdnHost,
            CertificateChainPolicy = policy?.Clone(),
            RemoteCertificateValidationCallback = (_, _, _, errors) =>
            {
                seen = errors;
                return true;
            },
        });
        return seen ?? throw new InvalidOperationException("no certificate validation happened");
    }

    private static X509Certificate2 TestCa()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=CPM cluster e2e test CA", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    /// <summary>A server certificate for 127.0.0.1 (the node URL's host) issued by <paramref name="ca"/>, with its key.</summary>
    private static X509Certificate2 Issue(X509Certificate2 ca)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=127.0.0.1", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        san.AddDnsName("localhost");
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));
        using var issued = req.Create(ca, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(29), RandomNumberGenerator.GetBytes(16));
        using var withKey = issued.CopyWithPrivateKey(rsa);
        // Kestrel on macOS needs a certificate whose key is not ephemeral: round-trip through PKCS#12.
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
    }

    private async Task Step(string name, Func<Task<JsonObject>> body)
    {
        var sw = Stopwatch.StartNew();
        var entry = new JsonObject { ["step"] = name };
        _steps.Add(entry);
        try
        {
            entry["verified"] = await body();
            entry["ok"] = true;
            output.WriteLine($"[{sw.ElapsedMilliseconds,6} ms] {name}");
        }
        catch
        {
            entry["ok"] = false;
            throw;
        }
        finally
        {
            entry["ms"] = sw.ElapsedMilliseconds;
        }
    }
}
