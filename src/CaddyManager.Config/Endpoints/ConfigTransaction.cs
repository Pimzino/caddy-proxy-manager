using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Config.Endpoints;

/// <summary>
/// Serialises configuration mutations (persist → apply → rollback) across all Config endpoints, and — as
/// IConfigMutationLock — with other modules (cluster replication on a node).
/// </summary>
public sealed class ConfigMutationGate : IConfigMutationLock
{
    public SemaphoreSlim Lock { get; } = new(1, 1);

    public async Task<IDisposable> AcquireAsync(CancellationToken ct = default)
    {
        await Lock.WaitAsync(ct);
        return new Releaser(Lock);
    }

    public async Task<IDisposable?> TryAcquireAsync(TimeSpan timeout, CancellationToken ct = default) =>
        await Lock.WaitAsync(timeout, ct) ? new Releaser(Lock) : null;

    /// <summary>Releases the lock once, however often it is disposed.</summary>
    private sealed class Releaser(SemaphoreSlim sem) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) sem.Release();
        }
    }
}

/// <summary>
/// A change prepared under the gate by <see cref="ConfigTransaction.RunPreparedAsync"/>: either a result to return right
/// away (validation failure) or the persist / rollback / success steps.
/// </summary>
internal sealed record PreparedChange(
    IResult? Early,
    string Reason = "",
    Action? Persist = null,
    Action? Rollback = null,
    Func<ApplyResult, IResult>? OnSuccess = null)
{
    public static PreparedChange Stop(IResult result) => new(result);
    public static PreparedChange Run(string reason, Action persist, Action rollback, Func<ApplyResult, IResult> onSuccess) =>
        new(null, reason, persist, rollback, onSuccess);
}

internal static class ConfigTransaction
{
    public const string RejectedTitle = "Caddy rejected the configuration";

    /// <summary>
    /// Persists a change, applies the configuration and rolls the change back when Caddy rejects it (422).
    /// When Caddy is not running the change is kept (apply.writtenOnly = true).
    /// </summary>
    /// <param name="affectsCaddyfileMode">false for model changes that are irrelevant while Caddyfile mode is active.</param>
    /// <param name="concernsStreams">
    /// true for stream mutations. Other mutations drop the standing "streams skipped (no layer4 module)" warning, which the
    /// Streams page and the config preview already show; repeating it on every host or certificate save is noise.
    /// </param>
    public static async Task<IResult> RunAsync(
        HttpContext http,
        string reason,
        Action persist,
        Action rollback,
        Func<ApplyResult, IResult> onSuccess,
        bool affectsCaddyfileMode = false,
        bool concernsStreams = false)
    {
        var gate = http.RequestServices.GetRequiredService<ConfigMutationGate>();
        await gate.Lock.WaitAsync(http.RequestAborted);
        try
        {
            return await RunLockedAsync(http, reason, persist, rollback, onSuccess, affectsCaddyfileMode, concernsStreams);
        }
        finally
        {
            gate.Lock.Release();
        }
    }

    /// <summary>
    /// Like <see cref="RunAsync"/>, but <paramref name="prepare"/> runs under the gate too, so it reads the state that is
    /// current when the change is persisted: a change that rewrites a whole document (the Caddy settings) must not be built
    /// from a copy read before another mutation (e.g. cluster replication) finished, or it silently reverts that mutation.
    /// </summary>
    public static async Task<IResult> RunPreparedAsync(HttpContext http, Func<Task<PreparedChange>> prepare,
        bool affectsCaddyfileMode = false, bool concernsStreams = false)
    {
        var gate = http.RequestServices.GetRequiredService<ConfigMutationGate>();
        await gate.Lock.WaitAsync(http.RequestAborted);
        try
        {
            var change = await prepare();
            if (change.Early is not null) return change.Early;
            return await RunLockedAsync(http, change.Reason, change.Persist!, change.Rollback!, change.OnSuccess!, affectsCaddyfileMode, concernsStreams);
        }
        finally
        {
            gate.Lock.Release();
        }
    }

    /// <summary>Persist → apply → rollback on rejection; the caller holds the gate.</summary>
    private static async Task<IResult> RunLockedAsync(HttpContext http, string reason, Action persist, Action rollback,
        Func<ApplyResult, IResult> onSuccess, bool affectsCaddyfileMode, bool concernsStreams)
    {
        var sp = http.RequestServices;
        var config = sp.GetRequiredService<ICaddyConfigService>();
        var store = sp.GetRequiredService<IStore>();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("CaddyManager.Config.Transaction");

        Services.CaddyConfigService.AmbientUser.Value = UserName(http);
        persist();
        ApplyResult apply;
        try
        {
            if (!affectsCaddyfileMode && store.GetSettings<CaddySettings>().Mode == ConfigMode.Caddyfile)
            {
                apply = new ApplyResult
                {
                    Success = true,
                    Warnings = ["Caddyfile mode is active: the change was saved but is not used until you switch back to Managed mode."],
                };
            }
            else
            {
                // Never cancel half-way: the DB and Caddy must end up consistent even if the client disconnects.
                apply = await config.ApplyAsync(reason, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Apply failed unexpectedly ({Reason}); rolling back", reason);
            SafeRollback(rollback, logger, reason);
            throw;
        }

        if (!apply.Success)
        {
            SafeRollback(rollback, logger, reason);
            return ApiResults.Failed(RejectedTitle, apply.Error ?? "Caddy reported an error without details.");
        }
        if (!concernsStreams) apply = apply.WithoutStreamsSkippedWarning();
        return onSuccess(apply);
    }

    private static void SafeRollback(Action rollback, ILogger logger, string reason)
    {
        try
        {
            rollback();
            logger.LogInformation("Rolled back change after failed apply ({Reason})", reason);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Rollback failed after failed apply ({Reason})", reason);
        }
    }

    /// <summary>The signed-in user (Ops ICurrentUser, falling back to the authenticated principal).</summary>
    public static string? UserName(HttpContext http)
    {
        try
        {
            var name = http.RequestServices.GetService<ICurrentUser>()?.UserName;
            if (!string.IsNullOrWhiteSpace(name) && name != "system") return name;
        }
        catch (Exception)
        {
            // fall through to the principal
        }
        return http.User.Identity?.IsAuthenticated == true ? http.User.Identity.Name : null;
    }

    public static ApplyResult WithoutStreamsSkippedWarning(this ApplyResult apply) =>
        apply.Warnings.Any(Generation.CaddyConfigGenerator.IsStreamsSkippedWarning)
            ? apply with { Warnings = apply.Warnings.Where(w => !Generation.CaddyConfigGenerator.IsStreamsSkippedWarning(w)).ToList() }
            : apply;

    public static ApplyResult WithWarnings(this ApplyResult apply, IEnumerable<string> extra)
    {
        var list = extra.ToList();
        return list.Count == 0 ? apply : apply with { Warnings = [.. list, .. apply.Warnings] };
    }

    /// <summary>Records an audit entry when the Ops audit log is available.</summary>
    public static void Audit(HttpContext http, string action, string objectType, string? objectId = null, string? objectName = null, string? details = null)
    {
        try
        {
            http.RequestServices.GetService<IAuditLog>()?.Record(action, objectType, objectId, objectName, details);
        }
        catch (Exception ex)
        {
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("CaddyManager.Config.Audit")
                .LogWarning(ex, "Could not record audit entry {Action} {Type}", action, objectType);
        }
    }
}
