using Corridor.Portal.Data;
using Corridor.Portal.Models;
using Corridor.Portal.Services.Scim;

namespace Corridor.Portal.Services;

/// <summary>Which SCIM operation the next provisioning run performs for one account.</summary>
public enum ProvisionAction
{
    Create,
    Replace,
    Deactivate,
    Skip
}

/// <summary>
/// The create-versus-update decision, kept pure so it can be pinned by tests: an
/// active account with no recorded SCIM id is created, an active account that
/// already has one is replaced, and an inactive account is deactivated only when
/// it was ever provisioned (there is nothing to switch off otherwise).
/// </summary>
public static class ProvisionPlan
{
    public static ProvisionAction Decide(DirectoryUserAccount account)
    {
        if (account.Active)
        {
            return account.ScimExternalId is null ? ProvisionAction.Create : ProvisionAction.Replace;
        }
        return account.ScimExternalId is null ? ProvisionAction.Skip : ProvisionAction.Deactivate;
    }
}

/// <summary>Counts for one provisioning run; Describe() is the audit detail and the dashboard summary.</summary>
public sealed record ProvisioningSummary(int Created, int Updated, int Deactivated)
{
    public string Describe() => $"created {Created}, updated {Updated}, deactivated {Deactivated}";
}

/// <summary>
/// Synchronizes idn.Users into the target directory through the SCIM bridge: creates
/// active accounts that were never provisioned (recording the returned SCIM id),
/// replaces the rest with userName, displayName, active, and the role extension, and
/// patches inactive accounts to active=false. One DirectoryProvisioned audit event is
/// written per run, after every account succeeded; a failure propagates and writes
/// nothing, so the audit trail never claims a provisioning run that did not complete.
/// </summary>
public sealed class DirectoryProvisioner(
    IDirectoryUserRepository users,
    IScimProvisioner scim,
    IAuditEventRepository audit)
{
    public const string AuditAppKey = "oktasim";
    public const string AuditEventName = "DirectoryProvisioned";

    public async Task<ProvisioningSummary> ProvisionAsync(string actor, CancellationToken ct = default)
    {
        var accounts = await users.ListAsync(ct);
        int created = 0, updated = 0, deactivated = 0;
        foreach (var account in accounts)
        {
            switch (ProvisionPlan.Decide(account))
            {
                case ProvisionAction.Create:
                    var externalId = await scim.CreateAsync(
                        new ScimUser(account.Upn, account.DisplayName, account.Role), ct);
                    await users.UpdateScimExternalIdAsync(account.Upn, externalId, ct);
                    created++;
                    break;
                case ProvisionAction.Replace:
                    await scim.ReplaceAsync(
                        account.ScimExternalId!,
                        new ScimUser(account.Upn, account.DisplayName, account.Role),
                        ct);
                    updated++;
                    break;
                case ProvisionAction.Deactivate:
                    await scim.DeactivateAsync(account.ScimExternalId!, ct);
                    deactivated++;
                    break;
            }
        }

        var summary = new ProvisioningSummary(created, updated, deactivated);
        await audit.RecordAsync(new AuditEvent(
            0,
            DateTime.UtcNow,
            actor,
            AuditAppKey,
            AuditEventName,
            summary.Describe()), ct);
        return summary;
    }
}
