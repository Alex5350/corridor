using Corridor.Portal.Models;

namespace Corridor.Portal.Services.TraceLink;

/// <summary>The legacy TraceLink service raised a SOAP fault. Subcode carries the cor:* detail.</summary>
public sealed class TraceLinkFaultException(string subcode, string message) : Exception(message)
{
    public string Subcode { get; } = subcode;
}

public static class TraceLinkFaults
{
    public const string InvalidIdentityMode = "cor:InvalidIdentityMode";
    public const string IllegalStatusTransition = "cor:IllegalStatusTransition";
    public const string CaseNotFound = "cor:CaseNotFound";
    public const string ValidationError = "cor:ValidationError";
    public const string Unavailable = "cor:ServiceUnavailable";
}

/// <summary>Client for the TraceLink SOAP 1.1 service. The real implementation posts envelopes over HTTP; tests substitute a fake.</summary>
public interface ITraceLinkClient
{
    Task<IReadOnlyList<TraceCase>> SearchCasesAsync(string requester, string? statusFilter, int maxRows, CancellationToken ct = default);

    Task<TraceCase?> GetCaseAsync(string caseNumber, CancellationToken ct = default);

    /// <summary>Returns the new case number (TRC-######).</summary>
    Task<string> CreateTraceRequestAsync(TraceRequestCreate request, CancellationToken ct = default);

    Task<bool> UpdateStatusAsync(string caseNumber, string newStatus, string actor, CancellationToken ct = default);
}

public sealed record SoapCredential(bool IsSamlAssertion, string Content);

/// <summary>Maps SOAP fault subcodes onto RFC 9457 problem status codes and titles.</summary>
public static class TraceLinkProblemMapper
{
    public static (int Status, string Title) Map(string subcode)
    {
        return subcode switch
        {
            TraceLinkFaults.InvalidIdentityMode => (502, "The legacy service rejected the portal service credential for the current trust mode."),
            TraceLinkFaults.IllegalStatusTransition => (409, "The requested status transition is not allowed."),
            TraceLinkFaults.CaseNotFound => (404, "Trace case not found."),
            TraceLinkFaults.ValidationError => (400, "The legacy service rejected the request payload."),
            _ => (502, "The legacy trace service returned an error.")
        };
    }
}
