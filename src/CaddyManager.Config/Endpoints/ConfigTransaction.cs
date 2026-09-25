using CaddyManager.Core;
using CaddyManager.Core.Contracts;
using CaddyManager.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CaddyManager.Config.Endpoints;

/// <summary>Serialises configuration mutations (persist → apply → rollback) across all Config endpoints.</summary>
public sealed class ConfigMutationGate
{
    public SemaphoreSlim Lock { get; } = new(1, 1);
}

internal static class ConfigTransaction
{
    public const string RejectedTitle = "Caddy rejected the configuration";

    /// <summary>
    /// Persists a change, applies the configuration and rolls the change back when Caddy rejects it (422).
    /// When Caddy is not running the change is kept (apply.writtenOnly = true).
    /// </summary>
    /// <param name="affectsCaddyfileMode">false for model changes that are irrelevant while Caddyfile mode is active.</param>
    public static async Task<IResult> RunAsync(
        HttpContext http,
        string reason,
        Action persist,
        Action rollback,
        Func<ApplyResult, IResult> onSuccess,
        bool affectsCaddyfileMode = false)
    {
        var sp = http.RequestServices;
        var gate = sp.GetRequiredService<ConfigMutationGate>();
        var config = sp.GetRequiredService<ICaddyConfigService>();
        var store = sp.GetRequiredService<IStore>();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("CaddyManager.Config.Transaction");

        await gate.Lock.WaitAsync(http.RequestAborted);
        try
        {
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
            return onSuccess(apply);
        }
        finally
        {
            gate.Lock.Release();
        }
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
