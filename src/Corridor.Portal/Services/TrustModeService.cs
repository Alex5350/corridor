using Corridor.Portal.Data;
using Corridor.Portal.Models;

namespace Corridor.Portal.Services;

/// <summary>
/// Flips an application's TrustMode through the migration cycle Adfs -> Dual -> Okta -> Adfs,
/// writing the idn.MigrationApps row and a TrustModeChanged audit event together.
/// </summary>
public sealed class TrustModeService(IMigrationAppRepository apps, IAuditEventRepository audit)
{
    public const string AuditEventName = "TrustModeChanged";

    public static TrustMode NextMode(TrustMode current)
    {
        return current switch
        {
            TrustMode.Adfs => TrustMode.Dual,
            TrustMode.Dual => TrustMode.Okta,
            _ => TrustMode.Adfs
        };
    }

    public async Task<TrustMode> FlipAsync(string appKey, string actor, CancellationToken ct = default)
    {
        var app = await apps.GetAsync(appKey, ct)
            ?? throw new InvalidOperationException($"Unknown application key {appKey}.");
        var next = NextMode(app.TrustMode);
        await apps.UpdateTrustModeAsync(appKey, next, actor, DateTime.UtcNow, ct);
        await audit.RecordAsync(new AuditEvent(
            0,
            DateTime.UtcNow,
            actor,
            appKey,
            AuditEventName,
            $"{TrustModes.Label(app.TrustMode)} -> {TrustModes.Label(next)}"), ct);
        return next;
    }
}
