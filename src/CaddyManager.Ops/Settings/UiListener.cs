using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CaddyManager.Core;
using CaddyManager.Core.Models;

namespace CaddyManager.Ops.Settings;

/// <summary>What the host should listen on for the management UI, after validating UiSettings.</summary>
public sealed class UiListenerPlan
{
    public required IPEndPoint Http { get; init; }
    public IPEndPoint? Https { get; init; }
    public X509Certificate2? Certificate { get; init; }
    /// <summary>Problems found in the settings and the fallbacks applied (logged and raised as an event by the host).</summary>
    public List<string> Warnings { get; init; } = new();
}

/// <summary>
/// Turns UiSettings into listener endpoints without ever throwing: a bad bind address or port, a port already
/// in use, or a broken/missing HTTPS PFX must not crash-loop the service (which would lock the administrator
/// out of the UI that could fix the setting). Instead it falls back to the defaults and reports warnings.
/// </summary>
public static class UiListener
{
    public const int DefaultPort = 81;

    /// <param name="portOverride">CM_UI_PORT (development).</param>
    /// <param name="canBind">Probe used to test that an endpoint can be bound (tests replace it).</param>
    public static UiListenerPlan Plan(AppPaths paths, UiSettings ui, string? portOverride, ISecretProtector secrets,
        Func<IPEndPoint, bool>? canBind = null)
    {
        canBind ??= CanBind;
        var warnings = new List<string>();

        // ---- HTTP
        int port;
        if (!string.IsNullOrWhiteSpace(portOverride))
        {
            if (int.TryParse(portOverride, out var p) && p is >= 1 and <= 65535) port = p;
            else
            {
                warnings.Add($"CM_UI_PORT '{portOverride}' is not a valid TCP port; using the configured port.");
                port = ValidPort(ui.Port, DefaultPort, "UI port", warnings);
            }
        }
        else port = ValidPort(ui.Port, DefaultPort, "UI port", warnings);

        IPAddress bind;
        if (IPAddress.TryParse(ui.BindAddress?.Trim(), out var ip)) bind = ip;
        else
        {
            bind = IPAddress.Any;
            if (!string.IsNullOrWhiteSpace(ui.BindAddress))
                warnings.Add($"UI bind address '{ui.BindAddress}' is not an IP address; listening on all interfaces (0.0.0.0) instead.");
        }

        var http = new IPEndPoint(bind, port);
        if (!canBind(http))
        {
            var fallback = new IPEndPoint(IPAddress.Any, DefaultPort);
            if (!fallback.Equals(http) && canBind(fallback))
            {
                warnings.Add($"The UI cannot listen on {Format(http)} (address not available on this server or port in use); " +
                             $"falling back to {Format(fallback)}. Fix the UI settings and restart the service.");
                http = fallback;
            }
            else
            {
                warnings.Add($"The UI cannot listen on {Format(http)} (address not available on this server or port in use).");
            }
        }

        // ---- HTTPS (optional; never prevents the HTTP listener from starting)
        IPEndPoint? https = null;
        X509Certificate2? cert = null;
        if (ui.HttpsEnabled)
        {
            if (ui.HttpsPort is < 1 or > 65535)
                warnings.Add($"UI HTTPS port {ui.HttpsPort} is not a valid TCP port; HTTPS is disabled until it is fixed.");
            else if (ui.HttpsPort == http.Port)
                warnings.Add($"UI HTTPS port {ui.HttpsPort} is the same as the HTTP port; HTTPS is disabled until it is fixed.");
            else
            {
                var candidate = new IPEndPoint(http.Address, ui.HttpsPort);
                if (!canBind(candidate))
                    warnings.Add($"The UI cannot listen for HTTPS on {Format(candidate)} (port in use?); HTTPS is disabled for this run.");
                else
                {
                    cert = LoadCertificate(paths, ui, secrets, warnings);
                    if (cert is not null) https = candidate;
                }
            }
        }

        return new UiListenerPlan { Http = http, Https = https, Certificate = cert, Warnings = warnings };
    }

    private static int ValidPort(int port, int fallback, string what, List<string> warnings)
    {
        if (port is >= 1 and <= 65535) return port;
        warnings.Add($"{what} {port} is not a valid TCP port; using {fallback}.");
        return fallback;
    }

    private static string Format(IPEndPoint ep) => ep.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{ep.Address}]:{ep.Port}" : $"{ep.Address}:{ep.Port}";

    /// <summary>True if a TCP listener could be bound to the endpoint right now.</summary>
    public static bool CanBind(IPEndPoint ep)
    {
        try
        {
            using var s = new Socket(ep.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            s.Bind(ep);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------ certificate

    /// <summary>The configured PFX, else a generated self-signed certificate; null (with a warning) when neither works.</summary>
    internal static X509Certificate2? LoadCertificate(AppPaths paths, UiSettings ui, ISecretProtector secrets, List<string> warnings)
    {
        if (!string.IsNullOrWhiteSpace(ui.HttpsPfxPath))
        {
            var pfx = ui.HttpsPfxPath.Trim();
            if (!File.Exists(pfx))
                warnings.Add($"UI HTTPS certificate '{pfx}' was not found; using a self-signed certificate instead.");
            else
            {
                try
                {
                    var pwd = string.IsNullOrEmpty(ui.HttpsPfxPasswordProtected) ? null : secrets.Unprotect(ui.HttpsPfxPasswordProtected);
                    var cert = X509CertificateLoader.LoadPkcs12FromFile(pfx, pwd, KeyFlags);
                    if (cert.HasPrivateKey) return cert;
                    cert.Dispose();
                    warnings.Add($"UI HTTPS certificate '{pfx}' has no private key; using a self-signed certificate instead.");
                }
                catch (Exception ex)
                {
                    // Never include the password; the exception message does not contain it.
                    warnings.Add($"UI HTTPS certificate '{pfx}' could not be loaded ({ex.GetType().Name}: {ex.Message}); using a self-signed certificate instead.");
                }
            }
        }

        try
        {
            return SelfSigned(paths);
        }
        catch (Exception ex)
        {
            warnings.Add($"Could not create a self-signed certificate for the UI ({ex.Message}); HTTPS is disabled for this run.");
            return null;
        }
    }

    private static X509KeyStorageFlags KeyFlags =>
        OperatingSystem.IsWindows() ? X509KeyStorageFlags.MachineKeySet : X509KeyStorageFlags.DefaultKeySet;

    internal static string SelfSignedFile(AppPaths paths) => Path.Combine(paths.DataDir, "ui-selfsigned.pfx");

    private static X509Certificate2 SelfSigned(AppPaths paths)
    {
        var file = SelfSignedFile(paths);
        if (File.Exists(file))
        {
            try
            {
                var existing = X509CertificateLoader.LoadPkcs12FromFile(file, null, KeyFlags);
                if (existing.HasPrivateKey && existing.NotAfter > DateTime.Now.AddDays(30)) return existing;
                existing.Dispose();
            }
            catch (CryptographicException)
            {
                // Corrupt file: regenerate below.
            }
        }

        using var key = RSA.Create(2048);
        var host = Environment.MachineName;
        var req = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        san.AddDnsName("localhost");
        try
        {
            var fqdn = Dns.GetHostEntry(host).HostName;
            if (!string.Equals(fqdn, host, StringComparison.OrdinalIgnoreCase)) san.AddDnsName(fqdn);
        }
        catch { /* no DNS: machine name only */ }
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var created = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(2));
        var tmp = file + ".tmp";
        File.WriteAllBytes(tmp, created.Export(X509ContentType.Pfx));
        File.Move(tmp, file, overwrite: true);
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { /* best effort */ }
        }
        return X509CertificateLoader.LoadPkcs12FromFile(file, null, KeyFlags);
    }
}
