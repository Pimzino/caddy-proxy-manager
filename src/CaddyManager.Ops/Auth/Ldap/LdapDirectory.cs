using System.DirectoryServices.Protocols;
using System.Net;
using System.Text.RegularExpressions;

namespace CaddyManager.Ops.Auth.Ldap;

internal enum LdapScope { Base, Subtree }

/// <summary>A directory entry with the attributes that were requested (names are case-insensitive).</summary>
internal sealed class LdapEntry(string dn)
{
    public string Dn { get; } = dn;
    public Dictionary<string, List<string>> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Active Directory objectGUID (16 bytes) when present.</summary>
    public byte[]? ObjectGuid { get; set; }

    public string? First(string attribute) =>
        Values.TryGetValue(attribute, out var v) ? v.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) : null;

    public IReadOnlyList<string> All(string attribute) => Values.TryGetValue(attribute, out var v) ? v : [];
}

/// <summary>One connection to the directory. Implementations are synchronous and honour the configured timeout.</summary>
internal interface ILdapSession : IDisposable
{
    /// <summary>Simple bind. Throws <see cref="LdapInvalidCredentialsException"/> or <see cref="LdapDirectoryException"/>.</summary>
    void Bind(string name, string password);
    /// <summary>Throws <see cref="LdapDirectoryException"/>; <see cref="LdapTooManyResultsException"/> when more than sizeLimit entries match.</summary>
    List<LdapEntry> Search(string baseDn, string filter, LdapScope scope, IReadOnlyList<string> attributes, int sizeLimit);
}

/// <summary>Opens directory connections (the real one uses System.DirectoryServices.Protocols; tests use a fake).</summary>
internal interface ILdapConnector
{
    /// <summary>Connects (and negotiates TLS) according to the settings. Throws <see cref="LdapDirectoryException"/>.</summary>
    ILdapSession Connect(LdapSettings settings);
}

/// <summary>A directory problem an administrator must fix (unreachable server, TLS, bind account, filter...).</summary>
internal sealed class LdapDirectoryException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Result code 49. <see cref="Reason"/> is a readable Active Directory sub-code description when available.</summary>
internal sealed class LdapInvalidCredentialsException(string reason) : Exception("Invalid credentials")
{
    public string Reason { get; } = reason;
}

internal sealed class LdapTooManyResultsException() : Exception("More than one entry matched");

/// <summary>System.DirectoryServices.Protocols implementation (wldap32 on Windows).</summary>
internal sealed partial class LdapConnector : ILdapConnector
{
    public ILdapSession Connect(LdapSettings s)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(s.TimeoutSeconds, 1, 120));
        LdapConnection? conn = null;
        try
        {
            var id = new LdapDirectoryIdentifier(s.Server.Trim(), s.Port, fullyQualifiedDnsHostName: true, connectionless: false);
            conn = new LdapConnection(id) { AuthType = AuthType.Basic, Timeout = timeout, AutoBind = false };
            var o = conn.SessionOptions;
            o.ProtocolVersion = 3;
            // AD answers subtree searches at the domain root with referrals to the DNS/configuration partitions;
            // chasing them costs a timeout per referral and adds nothing for user lookups.
            o.ReferralChasing = ReferralChasingOptions.None;
            if (s.AllowInvalidCertificate && s.Security != LdapSecurity.None && OperatingSystem.IsWindows())
                o.VerifyServerCertificate = (_, _) => true;
            if (s.Security == LdapSecurity.Ldaps) o.SecureSocketLayer = true;
            else if (s.Security == LdapSecurity.StartTls) o.StartTransportLayerSecurity(null);
            return new Session(conn, s, timeout);
        }
        catch (Exception ex)
        {
            conn?.Dispose();
            throw Translate(ex, s, "connection");
        }
    }

    private sealed class Session(LdapConnection conn, LdapSettings s, TimeSpan timeout) : ILdapSession
    {
        public void Bind(string name, string password)
        {
            try
            {
                conn.Bind(new NetworkCredential(name, password));
            }
            catch (LdapException ex) when (ex.ErrorCode == 49)
            {
                throw new LdapInvalidCredentialsException(DescribeInvalidCredentials(ex.ServerErrorMessage));
            }
            catch (DirectoryOperationException ex) when (ex.Response is { ResultCode: (ResultCode)49 })
            {
                throw new LdapInvalidCredentialsException(DescribeInvalidCredentials(ex.Response.ErrorMessage));
            }
            catch (Exception ex)
            {
                throw Translate(ex, s, "bind");
            }
        }

        public List<LdapEntry> Search(string baseDn, string filter, LdapScope scope, IReadOnlyList<string> attributes, int sizeLimit)
        {
            var request = new SearchRequest(baseDn, filter, scope == LdapScope.Base ? SearchScope.Base : SearchScope.Subtree, attributes.ToArray())
            {
                SizeLimit = sizeLimit,
                TimeLimit = timeout,
            };
            SearchResponse response;
            try
            {
                response = (SearchResponse)conn.SendRequest(request, timeout);
            }
            catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.SizeLimitExceeded)
            {
                throw new LdapTooManyResultsException();
            }
            catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.NoSuchObject)
            {
                // Base object (e.g. a moved user or a mistyped base DN) does not exist.
                if (scope == LdapScope.Base) return [];
                throw new LdapDirectoryException($"The base DN '{baseDn}' does not exist in the directory. Check the Base DN setting.", ex);
            }
            catch (Exception ex)
            {
                throw Translate(ex, s, "search");
            }

            var result = new List<LdapEntry>();
            foreach (SearchResultEntry e in response.Entries)
            {
                var entry = new LdapEntry(e.DistinguishedName);
                foreach (DirectoryAttribute a in e.Attributes.Values)
                {
                    if (string.Equals(a.Name, "objectGUID", StringComparison.OrdinalIgnoreCase))
                    {
                        if (a.GetValues(typeof(byte[])).FirstOrDefault() is byte[] { Length: 16 } guid) entry.ObjectGuid = guid;
                        continue;
                    }
                    entry.Values[a.Name] = a.GetValues(typeof(string)).OfType<string>().ToList();
                }
                result.Add(entry);
            }
            return result;
        }

        public void Dispose() => conn.Dispose();
    }

    /// <summary>Maps AD's "data 52e"-style sub-codes of result 49 to a readable reason (for the audit log only).</summary>
    internal static string DescribeInvalidCredentials(string? serverMessage)
    {
        var code = serverMessage is null ? null : AdSubCode().Match(serverMessage) is { Success: true } m ? m.Groups[1].Value.ToLowerInvariant() : null;
        return code switch
        {
            "525" => "user not found",
            "52e" => "wrong password",
            "530" => "sign-in not permitted at this time",
            "531" => "sign-in not permitted from this workstation",
            "532" => "password expired",
            "533" => "account disabled",
            "701" => "account expired",
            "773" => "user must change the password at next sign-in",
            "775" => "account locked out",
            _ => "wrong user name or password",
        };
    }

    [GeneratedRegex(@"data ([0-9a-fA-F]{3,4})")]
    private static partial Regex AdSubCode();

    internal static string SecurityLabel(LdapSecurity security) => security switch
    {
        LdapSecurity.Ldaps => "LDAPS",
        LdapSecurity.StartTls => "LDAP with StartTLS",
        _ => "plain LDAP",
    };

    /// <summary>Turns library exceptions into messages a Windows administrator can act on.</summary>
    internal static LdapDirectoryException Translate(Exception ex, LdapSettings s, string operation)
    {
        if (ex is LdapDirectoryException lde) return lde;
        var target = $"{s.Server}:{s.Port}";
        var tlsHint = s.Security == LdapSecurity.None ? "" :
            " If the server is reachable, the TLS handshake probably failed: the domain controller needs a certificate whose name matches " +
            $"'{s.Server}' and that this server trusts (or enable 'Allow invalid certificate' for testing).";
        switch (ex)
        {
            case DllNotFoundException or TypeInitializationException or PlatformNotSupportedException:
                return new($"The LDAP client library is not available on this platform ({ex.Message}).", ex);
            case LdapException { ErrorCode: 8 or 13 }:
            case DirectoryOperationException { Response.ResultCode: ResultCode.StrongAuthRequired or ResultCode.ConfidentialityRequired }:
                return new("The directory requires an encrypted or signed connection for simple binds (LDAP signing policy — enforced by default on " +
                           "Windows Server 2025 domain controllers). Set security to 'startTls' (port 389) or 'ldaps' (port 636).", ex);
            case LdapException le when le.ErrorCode == 85:
                return new($"The LDAP server {target} did not answer the {operation} within {Math.Clamp(s.TimeoutSeconds, 1, 120)} seconds.", ex);
            case LdapException le when le.ErrorCode is 81 or 91 or 52:
                return new($"Cannot connect to the LDAP server {target} ({SecurityLabel(s.Security)}). Check the host name, the port and that TCP {s.Port} is open " +
                           $"from this server to the domain controller.{tlsHint}", ex);
            case TlsOperationException:
                return new($"StartTLS with {target} failed: {ex.Message}.{tlsHint}", ex);
            case LdapException le:
                return new($"LDAP {operation} with {target} failed: {le.Message}" +
                           (string.IsNullOrWhiteSpace(le.ServerErrorMessage) ? "" : $" ({le.ServerErrorMessage.Trim('\0', ' ')})") + ".", ex);
            case DirectoryOperationException de:
                return new($"LDAP {operation} with {target} failed: {de.Response?.ResultCode.ToString() ?? de.Message}" +
                           (string.IsNullOrWhiteSpace(de.Response?.ErrorMessage) ? "" : $" ({de.Response!.ErrorMessage.Trim('\0', ' ')})") + ".", ex);
            default:
                return new($"LDAP {operation} with {target} failed: {ex.Message}", ex);
        }
    }
}
