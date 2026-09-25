using CaddyManager.Core.Models;

namespace CaddyManager.Ops.Auth.Ldap;

public enum LdapSecurity
{
    /// <summary>Plain LDAP (port 389). The password travels in clear text — only for lab use.</summary>
    None,
    /// <summary>LDAP on port 389 upgraded with the StartTLS extended operation (needs a DC certificate).</summary>
    StartTls,
    /// <summary>LDAP over TLS (port 636, or 3269 for the global catalog).</summary>
    Ldaps,
}

/// <summary>
/// LDAP / Active Directory sign-in. Owned by the Ops module (stored with IStore.GetSettings).
/// Wire shape follows the settings rule: <c>bindPasswordProtected</c> → output <c>hasBindPassword</c>, input <c>bindPassword</c>.
/// </summary>
public sealed class LdapSettings : ISettingsDocument
{
    public const string DefaultUserFilter = "(&(objectClass=user)(|(sAMAccountName={0})(userPrincipalName={0})))";

    public bool Enabled { get; set; }
    /// <summary>Domain controller / LDAP server host name (use the FQDN so the TLS certificate name matches), e.g. dc01.corp.example.com or corp.example.com.</summary>
    public string Server { get; set; } = "";
    public int Port { get; set; } = 389;
    public LdapSecurity Security { get; set; } = LdapSecurity.StartTls;
    /// <summary>Skip validation of the server's TLS certificate (self-signed DC certificates). Not recommended.</summary>
    public bool AllowInvalidCertificate { get; set; }
    /// <summary>Service account used to search for users: a DN, a UPN (svc-cpm@corp.example.com) or DOMAIN\user. Empty = search as the signing-in user.</summary>
    public string? BindDn { get; set; }
    public string? BindPasswordProtected { get; set; }
    /// <summary>Where users are searched (subtree), e.g. DC=corp,DC=example,DC=com.</summary>
    public string BaseDn { get; set; } = "";
    /// <summary>LDAP filter; {0} is replaced by the escaped user name typed at sign-in.</summary>
    public string UserFilter { get; set; } = DefaultUserFilter;
    public string? AdminGroupDn { get; set; }
    public string? OperatorGroupDn { get; set; }
    public string? ViewerGroupDn { get; set; }
    /// <summary>Follow nested group membership (Active Directory LDAP_MATCHING_RULE_IN_CHAIN).</summary>
    public bool NestedGroups { get; set; } = true;
    /// <summary>Upper bound for each directory operation (connect, bind, search).</summary>
    public int TimeoutSeconds { get; set; } = 10;
}
