using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using CaddyManager.Core;
using CaddyManager.Core.Models;
using CaddyManager.Ops.Auth.Ldap;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaddyManager.Ops.Tests;

/// <summary>In-memory stand-in for an Active Directory domain controller.</summary>
internal sealed partial class FakeDirectory : ILdapConnector
{
    public const string BaseDn = "DC=corp,DC=example,DC=com";
    public const string AdminsDn = "CN=CPM Admins,OU=Groups,DC=corp,DC=example,DC=com";
    public const string OperatorsDn = "CN=CPM Operators,OU=Groups,DC=corp,DC=example,DC=com";
    public const string ViewersDn = "CN=CPM Viewers,OU=Groups,DC=corp,DC=example,DC=com";
    public const string ServiceDn = "CN=svc-cpm,OU=Service Accounts,DC=corp,DC=example,DC=com";
    public const string ServicePassword = "service-account-password";

    public sealed class Account
    {
        public required string Sam { get; init; }
        public string Dn => $"CN={Sam},OU=Staff,{BaseDn}";
        public Guid Guid { get; init; } = Guid.NewGuid();
        public string? Upn { get; init; }
        public string? Mail { get; set; }
        public string? DisplayName { get; init; }
        public string Password { get; set; } = "Directory-Pa55word!";
        public List<string> MemberOf { get; set; } = new();
        /// <summary>All groups including nested membership (what LDAP_MATCHING_RULE_IN_CHAIN evaluates).</summary>
        public List<string> TransitiveGroups { get; set; } = new();
        public int Uac { get; set; } = 512;
    }

    public List<Account> Accounts { get; } = new();
    public List<string> Log { get; } = new();
    public bool Unreachable { get; set; }
    public int Connections;

    public Account Add(string sam, params string[] memberOf)
    {
        var a = new Account
        {
            Sam = sam, Upn = $"{sam}@corp.example.com", Mail = $"{sam}@example.com", DisplayName = $"{sam} Display",
            MemberOf = memberOf.ToList(), TransitiveGroups = memberOf.ToList(),
        };
        Accounts.Add(a);
        return a;
    }

    public ILdapSession Connect(LdapSettings settings)
    {
        if (Unreachable)
            throw new LdapDirectoryException($"Cannot connect to the LDAP server {settings.Server}:{settings.Port} ({settings.Security}).");
        Interlocked.Increment(ref Connections);
        lock (Log) Log.Add("connect");
        return new Session(this);
    }

    private sealed partial class Session(FakeDirectory d) : ILdapSession
    {
        public void Bind(string name, string password)
        {
            lock (d.Log) d.Log.Add($"bind {name}");
            // Like a real server: an empty password is an anonymous/unauthenticated bind that "succeeds".
            if (password.Length == 0) return;
            if (name == ServiceDn)
            {
                if (password != ServicePassword) throw new LdapInvalidCredentialsException("wrong password");
                return;
            }
            var a = d.Accounts.FirstOrDefault(x => string.Equals(x.Dn, name, StringComparison.OrdinalIgnoreCase) ||
                                                   string.Equals(x.Upn, name, StringComparison.OrdinalIgnoreCase) ||
                                                   string.Equals("CORP\\" + x.Sam, name, StringComparison.OrdinalIgnoreCase));
            if (a is null) throw new LdapInvalidCredentialsException("user not found");
            if ((a.Uac & 2) != 0) throw new LdapInvalidCredentialsException("account disabled");
            if (a.Password != password) throw new LdapInvalidCredentialsException("wrong password");
        }

        public List<LdapEntry> Search(string baseDn, string filter, LdapScope scope, IReadOnlyList<string> attributes, int sizeLimit)
        {
            lock (d.Log) d.Log.Add($"search {scope} {baseDn} {filter}");
            if (scope == LdapScope.Base)
            {
                var user = d.Accounts.FirstOrDefault(a => a.Dn == baseDn);
                var m = InChain().Match(filter);
                if (user is null || !m.Success) return [];
                var group = Unescape(m.Groups[1].Value);
                return user.TransitiveGroups.Any(g => LdapAuthenticator.SameDn(g, group)) ? [Entry(user)] : [];
            }
            var wanted = Pairs().Matches(filter).Select(x => (Attr: x.Groups[1].Value, Value: Unescape(x.Groups[2].Value))).ToList();
            var found = d.Accounts.Where(a => wanted.Any(w =>
                (w.Attr == "sAMAccountName" && string.Equals(a.Sam, w.Value, StringComparison.OrdinalIgnoreCase)) ||
                (w.Attr == "userPrincipalName" && string.Equals(a.Upn, w.Value, StringComparison.OrdinalIgnoreCase)) ||
                (w.Attr == "mail" && string.Equals(a.Mail, w.Value, StringComparison.OrdinalIgnoreCase)))).ToList();
            if (found.Count > sizeLimit) throw new LdapTooManyResultsException();
            return found.Select(Entry).ToList();
        }

        private static LdapEntry Entry(Account a)
        {
            var e = new LdapEntry(a.Dn) { ObjectGuid = a.Guid.ToByteArray() };
            e.Values["sAMAccountName"] = [a.Sam];
            if (a.Upn is not null) e.Values["userPrincipalName"] = [a.Upn];
            if (a.Mail is not null) e.Values["mail"] = [a.Mail];
            if (a.DisplayName is not null) e.Values["displayName"] = [a.DisplayName];
            e.Values["memberOf"] = a.MemberOf.ToList();
            e.Values["userAccountControl"] = [a.Uac.ToString()];
            return e;
        }

        private static string Unescape(string v) => Regex.Replace(v, @"\\([0-9a-fA-F]{2})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());

        [GeneratedRegex(@"\((sAMAccountName|userPrincipalName|mail)=([^)]*)\)")]
        private static partial Regex Pairs();

        [GeneratedRegex(@"^\(memberOf:1\.2\.840\.113556\.1\.4\.1941:=(.*)\)$")]
        private static partial Regex InChain();

        public void Dispose() { }
    }

    public static LdapSettings Settings(bool nested = true, bool serviceAccount = true) => new()
    {
        Enabled = true,
        Server = "dc01.corp.example.com",
        Port = 389,
        Security = LdapSecurity.StartTls,
        BindDn = serviceAccount ? ServiceDn : null,
        BaseDn = BaseDn,
        AdminGroupDn = AdminsDn,
        OperatorGroupDn = OperatorsDn,
        ViewerGroupDn = ViewersDn,
        NestedGroups = nested,
    };
}

public class LdapAuthenticatorTests
{
    private readonly FakeDirectory _dir = new();
    private LdapAuthResult Auth(string login, string password, LdapSettings? s = null, string? bindPassword = FakeDirectory.ServicePassword) =>
        new LdapAuthenticator(_dir, NullLogger<LdapAuthenticator>.Instance).Authenticate(s ?? FakeDirectory.Settings(), bindPassword, login, password);

    [Fact]
    public void Direct_membership_maps_roles_with_admin_precedence()
    {
        var alice = _dir.Add("alice", FakeDirectory.OperatorsDn, FakeDirectory.AdminsDn);
        _dir.Add("bob", FakeDirectory.ViewersDn);

        var r = Auth("alice@corp.example.com", alice.Password);
        Assert.Equal(LdapOutcome.Success, r.Outcome);
        Assert.Equal(UserRole.Admin, r.Role);
        Assert.Equal(alice.Guid.ToString(), r.ExternalId);
        Assert.Equal("alice@example.com", r.Email);
        Assert.Equal("alice Display", r.DisplayName);
        Assert.Contains(FakeDirectory.AdminsDn, r.Groups);

        var b = Auth("CORP\\bob", "Directory-Pa55word!");
        Assert.Equal(UserRole.Viewer, b.Role);
        // DOMAIN\user searches by sAMAccountName and verifies with a bind as the user's DN on a second connection.
        Assert.Contains(_dir.Log, l => l.Contains("(sAMAccountName=bob)"));
        Assert.Contains(_dir.Log, l => l == $"bind CN=bob,OU=Staff,{FakeDirectory.BaseDn}");
        Assert.Contains(_dir.Log, l => l == $"bind {FakeDirectory.ServiceDn}");
    }

    [Fact]
    public void Nested_groups_use_the_in_chain_matching_rule_only_when_enabled()
    {
        var carol = _dir.Add("carol", "CN=Web Team,OU=Groups,DC=corp,DC=example,DC=com");
        carol.TransitiveGroups.Add(FakeDirectory.OperatorsDn);

        var nested = Auth("carol", carol.Password);
        Assert.Equal(LdapOutcome.Success, nested.Outcome);
        Assert.Equal(UserRole.Operator, nested.Role);
        Assert.Contains(_dir.Log, l => l.StartsWith("search Base CN=carol", StringComparison.Ordinal) &&
                                       l.Contains($"(memberOf:{LdapAuthenticator.InChainRule}:={FakeDirectory.OperatorsDn})"));
        Assert.Contains(FakeDirectory.OperatorsDn, nested.Groups);

        var flat = Auth("carol", carol.Password, FakeDirectory.Settings(nested: false));
        Assert.Equal(LdapOutcome.NotAuthorized, flat.Outcome);
        Assert.Null(flat.Role);
        Assert.Contains("not a member", flat.Message);
    }

    [Fact]
    public void Filter_values_are_escaped_against_ldap_injection()
    {
        _dir.Add("admin", FakeDirectory.AdminsDn);
        var r = Auth("*)(sAMAccountName=admin", "Directory-Pa55word!");
        Assert.Equal(LdapOutcome.InvalidCredentials, r.Outcome);
        Assert.Contains(_dir.Log, l => l.Contains(@"(sAMAccountName=\2a\29\28sAMAccountName=admin)"));
        Assert.Equal(@"a\5cb\2a\28\29\00", LdapAuthenticator.EscapeFilterValue("a\\b*()\0"));
    }

    [Fact]
    public void Empty_password_is_rejected_before_any_bind()
    {
        _dir.Add("dave", FakeDirectory.AdminsDn);
        var r = Auth("dave", "");
        Assert.Equal(LdapOutcome.InvalidCredentials, r.Outcome);
        Assert.Empty(_dir.Log); // the fake (like real servers) would have accepted an unauthenticated bind
    }

    [Fact]
    public void Wrong_password_disabled_unknown_and_ambiguous_accounts()
    {
        _dir.Add("erin", FakeDirectory.AdminsDn);
        Assert.Equal(LdapOutcome.InvalidCredentials, Auth("erin", "wrong-password-123").Outcome);
        Assert.Equal("wrong password", Auth("erin", "wrong-password-123").Message);
        Assert.Equal(LdapOutcome.InvalidCredentials, Auth("nobody", "whatever-password").Outcome);

        _dir.Add("frank", FakeDirectory.AdminsDn).Uac = 514;
        var disabled = Auth("frank", "Directory-Pa55word!");
        Assert.Equal(LdapOutcome.InvalidCredentials, disabled.Outcome);
        Assert.Equal("account disabled", disabled.Message);

        _dir.Accounts.Add(new FakeDirectory.Account { Sam = "twin", Upn = "twin1@corp.example.com" });
        _dir.Accounts.Add(new FakeDirectory.Account { Sam = "twin", Upn = "twin2@corp.example.com" });
        var ambiguous = Auth("twin", "Directory-Pa55word!");
        Assert.Equal(LdapOutcome.Error, ambiguous.Outcome);
        Assert.Contains("More than one", ambiguous.Message);
    }

    [Fact]
    public void Bind_account_and_connectivity_problems_are_errors_not_invalid_credentials()
    {
        _dir.Add("gina", FakeDirectory.AdminsDn);
        var badService = Auth("gina", "Directory-Pa55word!", bindPassword: "stale");
        Assert.Equal(LdapOutcome.Error, badService.Outcome);
        Assert.Contains("bind account", badService.Message);

        _dir.Unreachable = true;
        var down = Auth("gina", "Directory-Pa55word!");
        Assert.Equal(LdapOutcome.Error, down.Outcome);
        Assert.Contains("Cannot connect", down.Message);
    }

    [Fact]
    public void Without_a_service_account_users_bind_directly_with_upn_or_domain_name()
    {
        var hank = _dir.Add("hank", FakeDirectory.ViewersDn);
        var s = FakeDirectory.Settings(serviceAccount: false);
        Assert.Equal(LdapOutcome.InvalidCredentials, Auth("hank", hank.Password, s, bindPassword: null).Outcome);
        _dir.Log.Clear();
        var r = Auth("hank@corp.example.com", hank.Password, s, bindPassword: null);
        Assert.Equal(LdapOutcome.Success, r.Outcome);
        Assert.Equal(UserRole.Viewer, r.Role);
        // The user's own bind proves the password: one connection, no service bind, no second bind.
        Assert.Equal(1, _dir.Log.Count(l => l == "connect"));
        Assert.Equal(["bind hank@corp.example.com"], _dir.Log.Where(l => l.StartsWith("bind", StringComparison.Ordinal)).ToArray());
        Assert.Equal(LdapOutcome.InvalidCredentials, Auth("CORP\\hank", "nope-nope-nope", s, bindPassword: null).Outcome);
    }

    [Fact]
    public void Email_falls_back_to_upn_then_account_at_domain()
    {
        var ivy = _dir.Add("ivy", FakeDirectory.ViewersDn);
        ivy.Mail = null;
        Assert.Equal("ivy@corp.example.com", Auth("ivy", ivy.Password).Email);
        _dir.Accounts.Add(new FakeDirectory.Account { Sam = "jack", MemberOf = [FakeDirectory.ViewersDn], TransitiveGroups = [FakeDirectory.ViewersDn] });
        Assert.Equal("jack@corp.example.com", Auth("jack", "Directory-Pa55word!").Email);
    }

    [Theory]
    [InlineData("80090308: LdapErr: DSID-0C09044E, comment: AcceptSecurityContext error, data 52e, v4563", "wrong password")]
    [InlineData("... data 775, v4563", "account locked out")]
    [InlineData("... data 532, v4563", "password expired")]
    [InlineData(null, "wrong user name or password")]
    public void Active_directory_sub_codes_are_described(string? server, string expected) =>
        Assert.Equal(expected, LdapConnector.DescribeInvalidCredentials(server));

    [Fact]
    public void Library_errors_are_translated_into_actionable_messages()
    {
        var s = FakeDirectory.Settings();
        var signing = LdapConnector.Translate(new System.DirectoryServices.Protocols.LdapException(8, "Strong authentication required"), s, "bind");
        Assert.Contains("startTls", signing.Message);
        Assert.Contains("ldaps", signing.Message);
        var down = LdapConnector.Translate(new System.DirectoryServices.Protocols.LdapException(81, "The LDAP server is unavailable."), s, "bind");
        Assert.Contains("Cannot connect to the LDAP server dc01.corp.example.com:389 (LDAP with StartTLS)", down.Message);
        Assert.Contains("TLS handshake", down.Message);
        var slow = LdapConnector.Translate(new System.DirectoryServices.Protocols.LdapException(85, "Timeout"), s, "search");
        Assert.Contains("did not answer the search within 10 seconds", slow.Message);
        var plain = FakeDirectory.Settings();
        plain.Security = LdapSecurity.None;
        Assert.DoesNotContain("TLS", LdapConnector.Translate(new System.DirectoryServices.Protocols.LdapException(81, "x"), plain, "bind").Message);
    }

    [Fact]
    public void Settings_validation_catches_common_mistakes()
    {
        var s = FakeDirectory.Settings();
        s.BindPasswordProtected = "x";
        Assert.True(LdapEndpoints.Validate(s).IsValid);
        s.Server = "ldaps://dc01";
        s.Security = LdapSecurity.Ldaps;
        s.UserFilter = "(sAMAccountName=user)";
        s.AdminGroupDn = s.OperatorGroupDn = s.ViewerGroupDn = null;
        var v = LdapEndpoints.Validate(s);
        Assert.False(v.IsValid);
    }
}

public class LdapSignInTests
{
    private static async Task<(TestApp App, FakeDirectory Dir, HttpClient Admin)> StartAsync(bool enable = true)
    {
        var dir = new FakeDirectory();
        var app = await TestApp.StartAsync(services: s => s.Replace(ServiceDescriptor.Singleton<ILdapConnector>(dir)));
        var admin = await app.SetupAdminAsync();
        var put = await admin.PutJsonAsync("api/settings/ldap", new
        {
            enabled = enable, server = "dc01.corp.example.com", port = 389, security = "startTls", bindDn = FakeDirectory.ServiceDn,
            bindPassword = FakeDirectory.ServicePassword, baseDn = FakeDirectory.BaseDn, adminGroupDn = FakeDirectory.AdminsDn,
            operatorGroupDn = FakeDirectory.OperatorsDn, viewerGroupDn = FakeDirectory.ViewersDn, nestedGroups = true,
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        return (app, dir, admin);
    }

    [Fact]
    public async Task Settings_roundtrip_redacts_the_bind_password_and_validates()
    {
        var (app, _, admin) = await StartAsync();
        await using var _app = app;
        var text = await (await admin.GetAsync("api/settings/ldap")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(FakeDirectory.ServicePassword, text);
        Assert.DoesNotContain("bindPasswordProtected", text);
        var json = System.Text.Json.JsonDocument.Parse(text).RootElement;
        Assert.True(json.GetProperty("hasBindPassword").GetBoolean());
        Assert.Equal("startTls", json.GetProperty("security").GetString());
        Assert.Equal(LdapSettings.DefaultUserFilter, json.GetProperty("userFilter").GetString());

        var bad = await admin.PutJsonAsync("api/settings/ldap", new { server = "ldap://dc01", security = "ldaps", port = 389, userFilter = "(uid=x)" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var errors = (await bad.JsonAsync()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("server", out _));
        Assert.True(errors.TryGetProperty("port", out _));
        Assert.True(errors.TryGetProperty("userFilter", out _));

        Assert.Contains(app.Store.Col<AuditEntry>().FindAll(), a => a.ObjectId == "ldap" && (a.Details ?? "").Contains("bind password changed"));
        Assert.DoesNotContain(app.Store.Col<AuditEntry>().FindAll(), a => (a.Details ?? "").Contains(FakeDirectory.ServicePassword));
    }

    [Fact]
    public async Task Directory_user_is_provisioned_updated_and_marked_external()
    {
        var (app, dir, admin) = await StartAsync();
        await using var _app = app;
        var kim = dir.Add("kim", FakeDirectory.OperatorsDn);

        var c = app.Client();
        var login = await c.PostAsJsonAsync("api/auth/login", new { email = "CORP\\kim", password = kim.Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var dto = await login.JsonAsync();
        Assert.Equal("ldap", dto.GetProperty("externalSource").GetString());
        Assert.Equal("operator", dto.GetProperty("role").GetString());
        Assert.Equal("kim@example.com", dto.GetProperty("email").GetString());

        var user = app.Store.Col<User>().FindOne(u => u.Email == "kim@example.com");
        Assert.Equal("ldap", user.ExternalSource);
        Assert.Equal(kim.Guid.ToString(), user.ExternalId);
        Assert.True(string.IsNullOrEmpty(user.PasswordHash));
        Assert.NotNull(user.LastLoginAt);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("api/auth/me")).StatusCode);

        // Group change in AD is applied at the next sign-in; the same record is reused (matched by objectGUID).
        kim.MemberOf = [FakeDirectory.AdminsDn];
        kim.TransitiveGroups = [FakeDirectory.AdminsDn];
        kim.Mail = "kim.new@example.com";
        var again = await app.Client().PostAsJsonAsync("api/auth/login", new { email = "kim@corp.example.com", password = kim.Password });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var users = app.Store.Col<User>().Find(u => u.ExternalSource == "ldap").ToList();
        Assert.Single(users);
        Assert.Equal(UserRole.Admin, users[0].Role);
        Assert.Equal("kim.new@example.com", users[0].Email);

        var list = await (await admin.GetAsync("api/users")).JsonAsync();
        Assert.Contains(list.EnumerateArray(), u => u.TryGetProperty("externalSource", out var src) && src.GetString() == "ldap");
        Assert.Contains(list.EnumerateArray(), u => !u.TryGetProperty("externalSource", out _)); // local admin

        var audit = app.Store.Col<AuditEntry>().FindAll().ToList();
        Assert.Contains(audit, a => a.Action == "login" && (a.Details ?? "").StartsWith("LDAP CN=kim", StringComparison.Ordinal));
        Assert.DoesNotContain(audit, a => (a.Details ?? "").Contains(kim.Password) || (a.ObjectName ?? "").Contains(kim.Password));
    }

    [Fact]
    public async Task Directory_accounts_have_no_local_password_and_role_is_read_only()
    {
        var (app, dir, admin) = await StartAsync();
        await using var _app = app;
        var lee = dir.Add("lee", FakeDirectory.ViewersDn);
        var c = app.Client();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("api/auth/login", new { email = "lee", password = lee.Password })).StatusCode);

        var change = await c.PostAsJsonAsync("api/auth/change-password", new { currentPassword = lee.Password, newPassword = "something-new-and-long" });
        Assert.Equal(HttpStatusCode.BadRequest, change.StatusCode);
        Assert.Contains("directory", (await change.JsonAsync()).GetProperty("detail").GetString());

        var id = app.Store.Col<User>().FindOne(u => u.ExternalSource == "ldap").Id;
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutJsonAsync($"api/users/{id}", new { password = "a-brand-new-password" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutJsonAsync($"api/users/{id}", new { role = "admin" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutJsonAsync($"api/users/{id}", new { email = "other@example.com" })).StatusCode);
        // Blocking a directory user locally is allowed and wins over the directory.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutJsonAsync($"api/users/{id}", new { disabled = true, role = "viewer" })).StatusCode);
        var blocked = await app.Client().PostAsJsonAsync("api/auth/login", new { email = "lee", password = lee.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, blocked.StatusCode);

        // The local password check never accepts anything for a directory account.
        dir.Unreachable = true;
        var noDir = await app.Client().PostAsJsonAsync("api/auth/login", new { email = "lee@example.com", password = "" });
        Assert.Equal(HttpStatusCode.Unauthorized, noDir.StatusCode);
    }

    [Fact]
    public async Task Cli_refuses_to_reset_the_password_of_a_directory_account()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "cpm-ops-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(dataDir);
            paths.EnsureCreated();
            using (var store = new Core.Infrastructure.LiteStore(paths))
                store.Col<User>().Insert(new User { Email = "quinn@example.com", Name = "Quinn", ExternalSource = "ldap", ExternalId = "g-1" });
            var output = new StringWriter();
            var exit = await OpsCli.TryRunAsync(["reset-password", "--email", "quinn@example.com", "--data-dir", dataDir], output, output);
            Assert.Equal(1, exit);
            Assert.Contains("directory (ldap) account", output.ToString());
            var list = new StringWriter();
            Assert.Equal(0, await OpsCli.TryRunAsync(["list-users", "--data-dir", dataDir], list, list));
            Assert.Contains("ldap", list.ToString());
        }
        finally
        {
            try { Directory.Delete(dataDir, true); } catch { /* temp */ }
        }
    }

    [Fact]
    public async Task Local_accounts_are_tried_first_and_directory_outage_is_reported()
    {
        var (app, dir, _) = await StartAsync();
        await using var _app = app;
        dir.Unreachable = true;
        // Break-glass: the local admin signs in while the directory is down.
        Assert.Equal(HttpStatusCode.OK, (await app.Client().PostAsJsonAsync("api/auth/login",
            new { email = TestApp.AdminEmail, password = TestApp.AdminPassword })).StatusCode);
        Assert.Equal(0, dir.Connections);

        // Wrong local password with the directory down is still "invalid credentials", not an outage.
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Client().PostAsJsonAsync("api/auth/login",
            new { email = TestApp.AdminEmail, password = "not the password" })).StatusCode);

        var outage = await app.Client().PostAsJsonAsync("api/auth/login", new { email = "someone", password = "a-password-here" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, outage.StatusCode);
        Assert.Contains(app.Store.Col<AuditEntry>().FindAll(), a => a.Action == "loginFailed" && (a.Details ?? "").StartsWith("LDAP error", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Users_without_a_mapped_group_are_refused_and_their_sessions_revoked()
    {
        var (app, dir, _) = await StartAsync();
        await using var _app = app;
        var mia = dir.Add("mia", FakeDirectory.ViewersDn);
        var session = app.Client();
        Assert.Equal(HttpStatusCode.OK, (await session.PostAsJsonAsync("api/auth/login", new { email = "mia", password = mia.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("api/auth/me")).StatusCode);

        mia.MemberOf = [];
        mia.TransitiveGroups = [];
        var refused = await app.Client().PostAsJsonAsync("api/auth/login", new { email = "mia", password = mia.Password });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("Not authorised", (await refused.JsonAsync()).GetProperty("title").GetString());

        // The previously issued session no longer works (security stamp bumped); allow the principal cache to expire.
        var cache = app.Services.GetRequiredService<CaddyManager.Ops.Auth.UserSnapshotCache>();
        cache.Invalidate(app.Store.Col<User>().FindOne(u => u.ExternalSource == "ldap").Id);
        Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync("api/auth/me")).StatusCode);

        var wrong = await app.Client().PostAsJsonAsync("api/auth/login", new { email = "mia", password = "bad-password-x" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
    }

    [Fact]
    public async Task Directory_account_is_never_linked_to_a_local_account_with_the_same_email()
    {
        var (app, dir, _) = await StartAsync();
        await using var _app = app;
        app.CreateUser("nora@example.com", UserRole.Viewer);
        var nora = dir.Add("nora", FakeDirectory.AdminsDn); // mail nora@example.com collides
        var r = await app.Client().PostAsJsonAsync("api/auth/login", new { email = "nora", password = nora.Password });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        var local = app.Store.Col<User>().FindOne(u => u.Email == "nora@example.com");
        Assert.Null(local.ExternalSource);
        Assert.Equal(UserRole.Viewer, local.Role);
    }

    [Fact]
    public async Task Disabled_ldap_is_ignored_and_test_endpoint_reports_the_mapping()
    {
        var (app, dir, admin) = await StartAsync(enable: false);
        await using var _app = app;
        var otto = dir.Add("otto", "CN=Nested,OU=Groups,DC=corp,DC=example,DC=com");
        otto.TransitiveGroups.Add(FakeDirectory.AdminsDn);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Client().PostAsJsonAsync("api/auth/login", new { email = "otto", password = otto.Password })).StatusCode);
        Assert.Equal(0, dir.Connections);

        var test = await (await admin.PostAsJsonAsync("api/settings/ldap/test", new { username = "otto", password = otto.Password })).JsonAsync();
        Assert.True(test.GetProperty("ok").GetBoolean(), test.ToString());
        Assert.Equal("admin", test.GetProperty("role").GetString());
        Assert.Equal("otto@example.com", test.GetProperty("email").GetString());
        Assert.Contains(test.GetProperty("groups").EnumerateArray(), g => g.GetString() == FakeDirectory.AdminsDn);
        Assert.Contains(test.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("disabled"));

        var fail = await (await admin.PostAsJsonAsync("api/settings/ldap/test", new { username = "otto", password = "wrong-one-here" })).JsonAsync();
        Assert.False(fail.GetProperty("ok").GetBoolean());
        Assert.StartsWith("Invalid credentials", fail.GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("api/settings/ldap/test", new { username = "otto" })).StatusCode);
        Assert.DoesNotContain(app.Store.Col<AuditEntry>().FindAll(), a => (a.Details ?? "").Contains(otto.Password));
    }

    [Fact]
    public async Task Directory_admins_do_not_count_as_the_last_local_admin()
    {
        var (app, dir, admin) = await StartAsync();
        await using var _app = app;
        var pat = dir.Add("pat", FakeDirectory.AdminsDn);
        var patClient = app.Client();
        Assert.Equal(HttpStatusCode.OK, (await patClient.PostAsJsonAsync("api/auth/login", new { email = "pat", password = pat.Password })).StatusCode);
        var localAdmin = app.Store.Col<User>().FindOne(u => u.Email == TestApp.AdminEmail);
        var del = await patClient.DeleteAsync($"api/users/{localAdmin.Id}");
        Assert.Equal(HttpStatusCode.Conflict, del.StatusCode);
        Assert.Contains("local administrator", (await del.JsonAsync()).GetProperty("detail").GetString());
        // Directory admins can be deleted freely (they are re-created at their next sign-in).
        var patId = app.Store.Col<User>().FindOne(u => u.ExternalSource == "ldap").Id;
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"api/users/{patId}")).StatusCode);
    }
}
