using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaddyManager.Cluster;

/// <summary>A cluster operation that is not allowed in the current role (endpoints answer 409).</summary>
public sealed class ClusterConflictException(string message) : Exception(message);

/// <summary>
/// This server's cluster role and membership, the primary's node registry and the summaries shown on the Servers page.
/// Implements IClusterRole: Node when joined to a primary, Primary when it has at least one node, otherwise Standalone.
/// </summary>
public sealed class ClusterService : IClusterRole
{
    private readonly IStore _store;
    private readonly ISecretProtector _secrets;
    private readonly AppPaths _paths;
    private readonly IServiceProvider _services;
    private readonly ClusterOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ClusterService> _logger;
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<string, (string Protected, byte[] Key)> _keys = new();
    private ClusterSettings _settings;
    private int _nodeCount;
    private DateTime _contactPersistedAt;

    public ClusterService(IStore store, ISecretProtector secrets, AppPaths paths, IServiceProvider services,
        IOptions<ClusterOptions> options, TimeProvider time, ILogger<ClusterService> logger)
    {
        _store = store;
        _secrets = secrets;
        _paths = paths;
        _services = services;
        _options = options.Value;
        _time = time;
        _logger = logger;
        _settings = store.GetSettings<ClusterSettings>();
        _nodeCount = store.Col<ClusterNode>().Count();
        store.SettingsChanged += t =>
        {
            if (t == typeof(ClusterSettings)) lock (_gate) _settings = store.GetSettings<ClusterSettings>();
        };
    }

    // ------------------------------------------------------------------ IClusterRole

    public ClusterRole Role
    {
        get
        {
            lock (_gate) return _settings.Role == ClusterRole.Node ? ClusterRole.Node : _nodeCount > 0 ? ClusterRole.Primary : ClusterRole.Standalone;
        }
    }

    public bool IsManagedNode => Role == ClusterRole.Node;

    public string? PrimaryName
    {
        get { lock (_gate) return _settings.Role == ClusterRole.Node ? _settings.PrimaryName : null; }
    }

    /// <summary>Snapshot copy of the membership settings.</summary>
    public ClusterSettings Settings
    {
        get
        {
            lock (_gate) return JsonSerializer.Deserialize<ClusterSettings>(JsonSerializer.Serialize(_settings, JsonDefaults.Storage), JsonDefaults.Storage)!;
        }
    }

    public ClusterOptions Options => _options;

    /// <summary>This server's display name: UiSettings.DisplayName, else the machine name.</summary>
    public string ServerName =>
        _store.GetSettings<UiSettings>().DisplayName is { Length: > 0 } n && !string.IsNullOrWhiteSpace(n) ? n.Trim() : Environment.MachineName;

    internal void UpdateSettings(Action<ClusterSettings> change)
    {
        // Read-modify-write under the (re-entrant) lock so concurrent updates never lose each other's fields.
        lock (_gate)
        {
            var s = _store.GetSettings<ClusterSettings>();
            change(s);
            _settings = s;
            _store.SaveSettings(s);
        }
    }

    public ClusterStatus GetStatus()
    {
        var s = Settings;
        var role = Role;
        var caddy = _store.GetSettings<CaddySettings>();
        // Local storage in a cluster is explained next to the storage settings (Settings › Cluster), from StorageBackend.
        var warnings = new List<string>();
        if (role == ClusterRole.Node)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var quiet = TimeSpan.FromTicks(Math.Max(_options.HeartbeatInterval.Ticks * 8, TimeSpan.FromMinutes(2).Ticks));
            if (s.LastPrimaryContactAt is null)
                warnings.Add($"The primary '{s.PrimaryName}' has not contacted this server yet. Check that it can reach this server's management URL.");
            else if (now - s.LastPrimaryContactAt > quiet)
                warnings.Add($"The primary '{s.PrimaryName}' has not contacted this server since {s.LastPrimaryContactAt:u}. This server keeps serving the last applied configuration.");
            if (s.PendingRevision is not null)
                warnings.Add("Caddy is being rebuilt with the plugins the primary requires; the new configuration is applied when that finishes.");
            if (s.LastSyncError is { Length: > 0 } err) warnings.Add("The last configuration sync failed: " + err);
        }
        return new ClusterStatus
        {
            Role = role,
            ServerName = ServerName,
            PrimaryName = role == ClusterRole.Node ? s.PrimaryName : null,
            LastPrimaryContactAt = role == ClusterRole.Node ? s.LastPrimaryContactAt : null,
            AppliedRevision = role == ClusterRole.Node ? s.AppliedRevision : null,
            NodeCount = _nodeCount,
            StorageBackend = caddy.StorageBackend,
            Warnings = warnings,
        };
    }

    // ------------------------------------------------------------------ membership (node side)

    /// <summary>
    /// Joins the cluster of the primary that issued <paramref name="token"/>. A standalone server without nodes can join;
    /// a node can join again with a new token of its own primary (same primary name, or a token for its node id), e.g.
    /// after the primary regenerated its token or was restored. A node of another primary must leave first.
    /// </summary>
    public ClusterStatus Join(string? token) => Join(token, out _);

    public ClusterStatus Join(string? token, out bool rejoined)
    {
        var t = JoinToken.Parse(token);
        lock (_gate)
        {
            rejoined = _settings.Role == ClusterRole.Node;
            if (rejoined && !string.Equals(t.Primary, _settings.PrimaryName, StringComparison.Ordinal) &&
                !string.Equals(t.NodeId, _settings.NodeId, StringComparison.Ordinal))
                throw new ClusterConflictException($"This server is a node of '{_settings.PrimaryName}', and the token was issued by '{t.Primary}'. " +
                                                   "Leave that cluster first (Settings › Cluster › Leave, or \"cluster leave\"), then join with the new token.");
            if (_nodeCount > 0)
                throw new ClusterConflictException("This server is a cluster primary (it manages other servers) and cannot join another cluster. Remove its servers first.");
        }
        UpdateSettings(s =>
        {
            s.Role = ClusterRole.Node;
            s.NodeId = t.NodeId;
            s.PrimaryName = t.Primary;
            s.SecretProtected = _secrets.Protect(Convert.ToBase64String(t.Secret));
            s.JoinedAt = _time.GetUtcNow().UtcDateTime;
            s.LastPrimaryContactAt = null;
            s.AppliedRevision = null;
            s.AppliedAt = null;
            s.PendingRevision = null;
            s.PendingJobId = null;
            s.LastSyncError = null;
            s.LastSyncErrorRevision = null;
            // The token is the authority: the primary instance that uses it next is pinned again.
            s.PinnedPrimaryId = null;
            s.PrimaryClockOffsetSeconds = null;
        });
        _keys.Clear();
        _logger.LogInformation("{Action} the cluster of {Primary} as node {NodeId}", rejoined ? "Joined again" : "Joined", t.Primary, t.NodeId);
        return GetStatus();
    }

    /// <summary>Node → Standalone. The last applied data stays and becomes editable.</summary>
    public ClusterStatus Leave()
    {
        lock (_gate)
            if (_settings.Role != ClusterRole.Node) throw new ClusterConflictException("This server is not a cluster node.");
        var primary = PrimaryName;
        UpdateSettings(s =>
        {
            s.Role = ClusterRole.Standalone;
            s.NodeId = null;
            s.SecretProtected = null;
            s.PrimaryName = null;
            s.PendingRevision = null;
            s.PendingJobId = null;
            s.LastSyncError = null;
            s.LastSyncErrorRevision = null;
            s.PinnedPrimaryId = null;
            s.PrimaryClockOffsetSeconds = null;
        });
        _keys.Clear();
        _logger.LogInformation("Left the cluster of {Primary}", primary);
        return GetStatus();
    }

    /// <summary>Node only: replaces the shared secret (the `rekey` RPC of a key rotation). The previous key stops working.</summary>
    internal void Rekey(byte[] secret)
    {
        if (secret.Length != ClusterCrypto.SecretLength) throw new ArgumentException("The new cluster key has the wrong length.");
        UpdateSettings(s =>
        {
            if (s.Role != ClusterRole.Node) throw new ClusterConflictException("This server is not a cluster node.");
            s.SecretProtected = _secrets.Protect(Convert.ToBase64String(secret));
        });
        _keys.TryRemove("self", out _);
    }

    /// <summary>
    /// Node only: remembers the primary's clock offset (primary − node, seconds) measured from an accepted RPC. Persisted
    /// only when it moved by more than 1 s (timestamps are whole seconds), so steady heartbeats never write the database.
    /// </summary>
    internal void ObservePrimaryClock(long offsetSeconds)
    {
        lock (_gate)
            if (_settings.PrimaryClockOffsetSeconds is { } known && Math.Abs(known - offsetSeconds) <= 1) return;
        UpdateSettings(s => s.PrimaryClockOffsetSeconds = offsetSeconds);
    }

    /// <summary>Node only: pins the primary instance at its first RPC; false when another instance is pinned.</summary>
    internal bool AcceptPrimaryInstance(string primaryId)
    {
        lock (_gate)
        {
            if (_settings.PinnedPrimaryId == primaryId) return true;
            if (_settings.PinnedPrimaryId is not null) return false;
        }
        var accepted = false;
        UpdateSettings(s =>
        {
            s.PinnedPrimaryId ??= primaryId;
            accepted = s.PinnedPrimaryId == primaryId;
        });
        return accepted;
    }

    /// <summary>
    /// Primary: this instance's id, sent with every RPC. Created on first use and created again when the database now runs
    /// on a machine with another name (restored elsewhere or cloned and renamed), so nodes never obey two live primaries.
    /// </summary>
    public string PrimaryInstanceId
    {
        get
        {
            var machine = Environment.MachineName;
            lock (_gate)
                if (_settings.PrimaryInstanceId is { Length: > 0 } id && _settings.PrimaryInstanceMachine == machine) return id;
            string? previous = null;
            UpdateSettings(s =>
            {
                if (s.PrimaryInstanceId is { Length: > 0 } && s.PrimaryInstanceMachine == machine) return;
                previous = s.PrimaryInstanceMachine;
                s.PrimaryInstanceId = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
                s.PrimaryInstanceMachine = machine;
            });
            if (previous is not null)
                _logger.LogWarning("This database was last used as a cluster primary on '{Previous}': this server is a new primary instance. " +
                                   "Its nodes accept it after they join again with a regenerated token.", previous);
            lock (_gate) return _settings.PrimaryInstanceId!;
        }
    }

    /// <summary>Node only: AES key derived from the stored secret (cached).</summary>
    internal byte[]? NodeKey()
    {
        var s = Settings;
        if (s.Role != ClusterRole.Node || string.IsNullOrEmpty(s.SecretProtected)) return null;
        return Key("self", s.SecretProtected);
    }

    internal void TouchPrimaryContact()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        lock (_gate) _settings.LastPrimaryContactAt = now;
        // Persist at most once a minute: every heartbeat would otherwise write the database.
        if (now - _contactPersistedAt < TimeSpan.FromMinutes(1)) return;
        _contactPersistedAt = now;
        UpdateSettings(s => s.LastPrimaryContactAt = now);
    }

    // ------------------------------------------------------------------ node registry (primary side)

    public List<ClusterNode> Nodes() => _store.Col<ClusterNode>().FindAll().OrderBy(n => n.CreatedAt).ThenBy(n => n.Name).ToList();

    public ClusterNode? FindNode(string id) => _store.Col<ClusterNode>().FindById(id);

    /// <summary>Registers a node (this server becomes Primary) and returns it with its join token.</summary>
    public (ClusterNode Node, string Token) AddNode(string name, string url, string? fingerprint)
    {
        lock (_gate)
            if (_settings.Role == ClusterRole.Node)
                throw new ClusterConflictException($"This server is a node managed by '{_settings.PrimaryName}' and cannot manage other servers.");
        var secret = ClusterCrypto.NewSecret();
        var node = new ClusterNode
        {
            Name = name,
            Url = url,
            SecretProtected = _secrets.Protect(Convert.ToBase64String(secret)),
            PinnedFingerprint = fingerprint,
            TokenIssuedAt = _time.GetUtcNow().UtcDateTime,
        };
        _store.Col<ClusterNode>().Insert(node);
        lock (_gate) _nodeCount = _store.Col<ClusterNode>().Count();
        return (node, new JoinToken(ServerName, node.Id, secret).Encode());
    }

    /// <summary>
    /// Starts a key rotation: a new secret is kept as the node's pending key and a join token for it is returned. The node
    /// still trusts the current key until it confirms the new one (`rekey` RPC, see ClusterWorker.RotateKeyAsync) or joins
    /// again with the returned token.
    /// </summary>
    public string BeginKeyRotation(string id)
    {
        var secret = ClusterCrypto.NewSecret();
        var node = UpdateNode(id, n =>
        {
            n.PendingSecretProtected = _secrets.Protect(Convert.ToBase64String(secret));
            n.TokenIssuedAt = _time.GetUtcNow().UtcDateTime;
        }) ?? throw new KeyNotFoundException();
        return new JoinToken(ServerName, node.Id, secret).Encode();
    }

    /// <summary>The node confirmed <paramref name="pendingProtected"/>: it becomes the node's key (no-op when superseded).</summary>
    internal ClusterNode? CompleteKeyRotation(string id, string pendingProtected)
    {
        var node = UpdateNode(id, n =>
        {
            if (n.PendingSecretProtected != pendingProtected) return;
            n.SecretProtected = pendingProtected;
            n.PendingSecretProtected = null;
            n.LastSeenAt = _time.GetUtcNow().UtcDateTime;
        });
        _keys.TryRemove(id, out _);
        return node;
    }

    /// <summary>Plain pending secret of a rotation (sent to the node inside the encrypted `rekey` RPC).</summary>
    internal byte[] PendingSecret(ClusterNode node) =>
        Convert.FromBase64String(_secrets.Unprotect(node.PendingSecretProtected ?? throw new InvalidOperationException("No key rotation is pending.")));

    internal byte[] PendingKey(ClusterNode node) =>
        Key(node.Id + "#pending", node.PendingSecretProtected ?? throw new InvalidOperationException("No key rotation is pending."));

    /// <summary>Re-reads the node, applies the change and saves it (callers on different threads never overwrite each other's fields).</summary>
    public ClusterNode? UpdateNode(string id, Action<ClusterNode> change)
    {
        lock (_gate)
        {
            var n = _store.Col<ClusterNode>().FindById(id);
            if (n is null) return null;
            change(n);
            n.UpdatedAt = _time.GetUtcNow().UtcDateTime;
            _store.Col<ClusterNode>().Update(n);
            return n;
        }
    }

    public bool RemoveNode(string id)
    {
        bool removed;
        lock (_gate)
        {
            removed = _store.Col<ClusterNode>().Delete(id);
            _nodeCount = _store.Col<ClusterNode>().Count();
        }
        _keys.TryRemove(id, out _);
        _keys.TryRemove(id + "#pending", out _);
        return removed;
    }

    internal byte[] NodeKey(ClusterNode node) => Key(node.Id, node.SecretProtected);

    private byte[] Key(string cacheKey, string protectedSecret)
    {
        if (_keys.TryGetValue(cacheKey, out var k) && k.Protected == protectedSecret) return k.Key;
        var key = ClusterCrypto.DeriveKey(Convert.FromBase64String(_secrets.Unprotect(protectedSecret)));
        _keys[cacheKey] = (protectedSecret, key);
        return key;
    }

    // ------------------------------------------------------------------ server summaries

    /// <summary>Facts about this server: from the Telemetry module when it is registered, otherwise gathered here.</summary>
    public async Task<ServerInfo> GetLocalInfoAsync(CancellationToken ct = default)
    {
        if (_services.GetService<IServerTelemetry>() is { } telemetry)
        {
            try { return await telemetry.GetInfoAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogDebug(ex, "Telemetry info failed; using basic facts"); }
        }
        CaddyStatus? status = null;
        InstalledBinary? installed = null;
        try
        {
            if (_services.GetService<ICaddyHost>() is { } host) status = await host.GetStatusAsync(ct);
            if (_services.GetService<ICaddyBinaryManager>() is { } bin) installed = await bin.GetInstalledAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogDebug(ex, "Caddy facts unavailable"); }
        return new ServerInfo
        {
            Hostname = Environment.MachineName,
            Fqdn = SafeFqdn(),
            Os = RuntimeInformation.OSDescription,
            IsWindows = OperatingSystem.IsWindows(),
            Architecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            ManagerVersion = ManagerVersion,
            CaddyVersion = status?.Version ?? installed?.Version,
            CaddyState = status?.State ?? CaddyRunState.Unknown,
            CaddyStartedAt = status?.StartedAt,
            CaddyPlugins = installed?.Plugins ?? new(),
            ProcessorCount = Environment.ProcessorCount,
            SystemUptimeSeconds = Environment.TickCount64 / 1000,
            ManagerUptimeSeconds = (long)(DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds,
            DataDir = _paths.DataDir,
            IpAddresses = LocalAddresses(),
            CollectedAt = _time.GetUtcNow().UtcDateTime,
        };
    }

    public IReadOnlyList<ResourceSample> GetLocalSamples(DateTime? since) =>
        _services.GetService<IServerTelemetry>()?.GetSamples(since) ?? [];

    public async Task<TrafficReport> GetLocalTrafficAsync(TrafficQuery query, CancellationToken ct)
    {
        if (_services.GetService<IServerTelemetry>() is { } telemetry) return await telemetry.GetTrafficAsync(query, ct);
        var now = _time.GetUtcNow().UtcDateTime;
        return new TrafficReport
        {
            Range = query.Range, Host = query.Host, From = now, To = now, Enabled = false,
            Notes = ["Traffic statistics are not available on this server (the Telemetry module is not installed)."],
        };
    }

    public async Task<ServerSummary> LocalSummaryAsync(CancellationToken ct)
    {
        var info = await GetLocalInfoAsync(ct);
        var samples = GetLocalSamples(null);
        return new ServerSummary
        {
            Id = "local",
            Name = ServerName,
            IsLocal = true,
            Status = ServerStatus.Online,
            LastSeenAt = _time.GetUtcNow().UtcDateTime,
            Info = info,
            Latest = samples.Count > 0 ? samples[^1] : null,
        };
    }

    public static ServerSummary ToSummary(ClusterNode n) => new()
    {
        Id = n.Id,
        Name = n.Name,
        IsLocal = false,
        Url = n.Url,
        Status = n.Status,
        LastSeenAt = n.LastSeenAt,
        LastError = n.LastError,
        Info = Parse<ServerInfo>(n.InfoJson),
        Latest = Parse<ResourceSample>(n.LatestJson),
        Sync = new ServerSyncState
        {
            DesiredRevision = n.DesiredRevision,
            AppliedRevision = n.AppliedRevision,
            InSync = n.DesiredRevision is not null && n.DesiredRevision == n.AppliedRevision,
            LastSyncAt = n.LastSyncAt,
            LastError = n.LastSyncError,
            Warnings = n.PendingRevision is not null && n.PendingRevision != n.AppliedRevision
                ? [.. n.SyncWarnings, "The node is rebuilding Caddy with the required plugins; the configuration is applied when that finishes."]
                : n.SyncWarnings,
        },
        AddedAt = n.CreatedAt,
        Fingerprint = n.PinnedFingerprint,
        TokenIssuedAt = n.TokenIssuedAt,
        KeyRotationPending = n.PendingSecretProtected is not null,
    };

    private static T? Parse<T>(string? json) where T : class
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, JsonDefaults.Api); }
        catch (JsonException) { return null; }
    }

    public static string ManagerVersion =>
        (Assembly.GetEntryAssembly() ?? typeof(ClusterService).Assembly).GetName().Version?.ToString(3) ?? "";

    private static string? SafeFqdn()
    {
        try
        {
            var p = IPGlobalProperties.GetIPGlobalProperties();
            return string.IsNullOrEmpty(p.DomainName) ? null : $"{p.HostName}.{p.DomainName}";
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException) { return null; }
    }

    private static List<string> LocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(i => i.OperationalStatus == OperationalStatus.Up && i.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(i => i.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => !IPAddress.IsLoopback(a) && !a.IsIPv6LinkLocal)
                .Select(a => a.ToString())
                .Distinct()
                .ToList();
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException) { return new(); }
    }
}
