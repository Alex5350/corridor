using System.Runtime.Serialization;
using CoreWCF;

namespace Corridor.Legacy.Contracts;

/// <summary>
/// TraceLink SOAP 1.1 service contract. Operation names, parameter names, and
/// data member names here are the WSDL shapes frozen by the architecture
/// contract; the contract-shape unit tests fail if any of them drift.
/// </summary>
[ServiceContract(Namespace = TraceLinkNamespaces.Contract, Name = "TraceLinkService")]
public interface ITraceLinkService
{
    /// <summary>Returns trace cases newest first, optionally filtered by status.</summary>
    [OperationContract]
    TraceCase[] SearchCases(string requester, string statusFilter, int maxRows);

    /// <summary>Returns a single trace case by case number (TRC-######).</summary>
    [OperationContract]
    TraceCase GetCase(string caseNumber);

    /// <summary>Creates a case in Received status; returns the new case number TRC-######.</summary>
    [OperationContract]
    string CreateTraceRequest(TraceRequestCreate request);

    /// <summary>Moves a case to a new status; returns true when a row was updated.</summary>
    [OperationContract]
    bool UpdateStatus(string caseNumber, string newStatus, string actor);
}

/// <summary>A trace case exactly as stored in trace.TraceCases.</summary>
[DataContract(Name = "TraceCase", Namespace = TraceLinkNamespaces.Contract)]
public sealed class TraceCase
{
    [DataMember(Name = "CaseNumber")]
    public string CaseNumber { get; set; } = string.Empty;

    [DataMember(Name = "LicenseeName")]
    public string LicenseeName { get; set; } = string.Empty;

    [DataMember(Name = "ItemDescription")]
    public string ItemDescription { get; set; } = string.Empty;

    [DataMember(Name = "Serial")]
    public string Serial { get; set; } = string.Empty;

    [DataMember(Name = "Status")]
    public string Status { get; set; } = string.Empty;

    [DataMember(Name = "SubmittedAt")]
    public DateTime SubmittedAt { get; set; }

    [DataMember(Name = "SubmittedBy")]
    public string SubmittedBy { get; set; } = string.Empty;

    [DataMember(Name = "Disposition")]
    public string? Disposition { get; set; }
}

/// <summary>Input for CreateTraceRequest.</summary>
[DataContract(Name = "TraceRequestCreate", Namespace = TraceLinkNamespaces.Contract)]
public sealed class TraceRequestCreate
{
    [DataMember(Name = "LicenseeName")]
    public string LicenseeName { get; set; } = string.Empty;

    [DataMember(Name = "ItemDescription")]
    public string ItemDescription { get; set; } = string.Empty;

    [DataMember(Name = "Serial")]
    public string Serial { get; set; } = string.Empty;

    [DataMember(Name = "RequesterUpn")]
    public string RequesterUpn { get; set; } = string.Empty;
}
