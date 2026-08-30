namespace Corridor.Portal.Models;

/// <summary>Trust state for one application in the migration program.</summary>
public enum TrustMode
{
    Adfs = 0,
    Dual = 1,
    Okta = 2
}

public static class TrustModes
{
    public static TrustMode Parse(string? value)
    {
        return value switch
        {
            "Dual" => TrustMode.Dual,
            "Okta" => TrustMode.Okta,
            _ => TrustMode.Adfs
        };
    }

    public static string Label(TrustMode mode)
    {
        return mode switch
        {
            TrustMode.Dual => "Dual",
            TrustMode.Okta => "Okta",
            _ => "Adfs"
        };
    }
}

public sealed record Permit(
    int Id,
    string PermitNumber,
    string LicenseeName,
    string ItemDescription,
    int Quantity,
    string Purpose,
    string Status,
    DateTime SubmittedAt,
    string SubmittedBy);

public sealed record NewPermit(
    string LicenseeName,
    string ItemDescription,
    int Quantity,
    string Purpose,
    string SubmittedBy);

public sealed record TraceCase(
    string CaseNumber,
    string LicenseeName,
    string ItemDescription,
    string Serial,
    string Status,
    DateTime SubmittedAt,
    string SubmittedBy,
    string? Disposition);

public sealed record TraceRequestCreate(
    string LicenseeName,
    string ItemDescription,
    string Serial,
    string RequesterUpn);

public sealed record MigrationApp(
    string AppKey,
    string AppName,
    TrustMode TrustMode,
    DateTime? LastFlippedAt,
    string? FlippedBy);

public sealed record AuditEvent(
    long Id,
    DateTime At,
    string Actor,
    string AppKey,
    string Event,
    string? Detail);

public sealed record Assignment(
    int Id,
    string InspectorUpn,
    string LicenseeName,
    string Focus,
    DateTime DueAt,
    string ChecklistJson);
