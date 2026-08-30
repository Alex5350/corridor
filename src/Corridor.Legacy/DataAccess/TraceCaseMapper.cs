using System.Data;
using Corridor.Legacy.Contracts;

namespace Corridor.Legacy.DataAccess;

/// <summary>
/// Maps an ADO.NET reader row onto the TraceCase data contract. Column names
/// are the contract with trace.usp_SearchCases / usp_GetCase, both of which
/// select the same column list.
/// </summary>
public static class TraceCaseMapper
{
    public static TraceCase Map(IDataRecord reader) => new()
    {
        CaseNumber = GetString(reader, "CaseNumber"),
        LicenseeName = GetString(reader, "LicenseeName"),
        ItemDescription = GetString(reader, "ItemDescription"),
        Serial = GetString(reader, "Serial"),
        Status = GetString(reader, "Status"),
        SubmittedAt = GetDateTime(reader, "SubmittedAt"),
        SubmittedBy = GetString(reader, "SubmittedBy"),
        Disposition = GetNullableString(reader, "Disposition")
    };

    private static string GetString(IDataRecord reader, string column)
    {
        int ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
    }

    private static string? GetNullableString(IDataRecord reader, string column)
    {
        int ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static DateTime GetDateTime(IDataRecord reader, string column) => reader.GetDateTime(reader.GetOrdinal(column));
}
