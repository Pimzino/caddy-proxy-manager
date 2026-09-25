using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using CaddyManager.Config.Certificates;

namespace CaddyManager.Config.Tests;

/// <summary>A fact that only runs on Windows in an elevated process (the windows-latest CI runner); skipped elsewhere.</summary>
public sealed class ElevatedWindowsFactAttribute : FactAttribute
{
    public ElevatedWindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows only.";
        else if (!IsElevated()) Skip = "Needs an elevated process (the Windows CI runner is).";
    }

    private static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
}

/// <summary>
/// Windows certificate store export against REAL store keys (research windows #27): certificates are created with
/// New-SelfSignedCertificate in Cert:\LocalMachine\My — as AD CS autoenrollment or an admin would — and exported by the
/// same code the "Windows certificate store" certificate source uses. Runs on the elevated windows-latest CI runner.
/// </summary>
public sealed class WindowsCertificateStoreE2ETests
{
    /// <summary>
    /// Ways it could fail: (1) an exportable CNG key (Microsoft Software Key Storage Provider, the default) is reported
    /// as not exportable or cannot be exported because CNG refuses PLAINTEXT export (only encrypted export is allowed);
    /// (2) a legacy CAPI key (Microsoft Enhanced RSA and AES Cryptographic Provider) is not handled; (3) a
    /// non-exportable key is reported exportable, or its export fails with an unhelpful CryptographicException instead
    /// of the message that tells the admin how to re-enroll; (4) the exported PEM does not round-trip (key does not
    /// match the certificate, wrong certificate, missing DNS names); (5) the listing misses the certificate or its DNS
    /// names; (6) the test leaves certificates or private keys behind on the machine.
    /// </summary>
    [ElevatedWindowsFact]
    public void Store_certificates_export_as_pem_or_explain_why_they_cannot()
    {
        var report = E2EArtifacts.Report(nameof(Store_certificates_export_as_pem_or_explain_why_they_cannot));
        var source = new WindowsCertificateStoreSource();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var cases = new (string Name, string Args, bool Exportable)[]
        {
            ("cng-exportable", "-KeyExportPolicy Exportable", true),
            ("cng-non-exportable", "-KeyExportPolicy NonExportable", false),
            ("capi-exportable", "-KeyExportPolicy Exportable -Provider 'Microsoft Enhanced RSA and AES Cryptographic Provider' -KeySpec KeyExchange", true),
        };
        var created = new List<string>();
        var results = new JsonArray();
        try
        {
            foreach (var (name, args, exportable) in cases)
            {
                var dns = $"{name}-{tag}.cpm-e2e.test";
                var thumb = PowerShell($"(New-SelfSignedCertificate -DnsName '{dns}' -CertStoreLocation 'Cert:\\LocalMachine\\My' -KeyAlgorithm RSA -KeyLength 2048 {args}).Thumbprint").Trim();
                Assert.Matches("^[0-9A-F]{40}$", thumb);
                created.Add(thumb);
                var row = new JsonObject { ["case"] = name, ["thumbprint"] = thumb, ["expectedExportable"] = exportable };

                var listed = source.List(WindowsStoreNames.LocalMachine, WindowsStoreNames.DefaultStore).SingleOrDefault(c => c.Thumbprint == thumb);
                Assert.NotNull(listed);                                              // (5)
                Assert.Contains(dns, listed!.DnsNames);
                Assert.True(listed.HasPrivateKey);
                row["listedExportable"] = listed.Exportable;
                Assert.Equal(exportable, listed.Exportable);                         // (1)(2)(3)

                if (exportable)
                {
                    var parsed = source.Export(WindowsStoreNames.LocalMachine, WindowsStoreNames.DefaultStore, thumb);
                    using var roundTrip = X509Certificate2.CreateFromPem(parsed.FullChainPem, parsed.PrivateKeyPem);
                    Assert.Equal(thumb, roundTrip.Thumbprint);                        // (4)
                    Assert.Contains(dns, WindowsCertificateStoreSource.DnsNames(roundTrip));
                    using var key = roundTrip.GetRSAPrivateKey()!;
                    using var pub = roundTrip.GetRSAPublicKey()!;
                    var data = RandomNumberGenerator.GetBytes(64);
                    var sig = key.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    Assert.True(pub.VerifyData(data, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
                    row["exported"] = true;
                    row["pemBytes"] = parsed.FullChainPem.Length + parsed.PrivateKeyPem.Length;
                }
                else
                {
                    var ex = Assert.Throws<CertificateImportException>(() => source.Export(WindowsStoreNames.LocalMachine, WindowsStoreNames.DefaultStore, thumb));
                    Assert.Equal(WindowsCertificateStoreSource.NotExportableMessage, ex.Message); // (3)
                    row["exportError"] = ex.Message;
                }
                results.Add(row);
            }
        }
        finally
        {
            // (6) remove the certificates together with their private keys
            foreach (var thumb in created)
                PowerShell($"Remove-Item -Path 'Cert:\\LocalMachine\\My\\{thumb}' -DeleteKey -ErrorAction SilentlyContinue");
        }
        report["cases"] = results;
        using (var store = new X509Store(StoreName.My, StoreLocation.LocalMachine))
        {
            store.Open(OpenFlags.ReadOnly);
            var left = created.Where(t => store.Certificates.Find(X509FindType.FindByThumbprint, t, false).Count > 0).ToList();
            report["leftBehind"] = new JsonArray(left.Select(t => (JsonNode)t).ToArray());
            Assert.Empty(left);
        }
        E2EArtifacts.Write("windows-certificate-store-export.json", report);
    }

    private static string PowerShell(string command)
    {
        var psi = new ProcessStartInfo("powershell.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", "$ErrorActionPreference='Stop'; " + command }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(120_000);
        Assert.True(p.ExitCode == 0, $"PowerShell failed ({p.ExitCode}): {command}\n{stderr}");
        return stdout;
    }
}
