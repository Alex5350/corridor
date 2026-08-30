using CoreWCF;
using Corridor.Legacy.Contracts;
using Corridor.Legacy.DataAccess;
using Corridor.Legacy.Security;
using Microsoft.Data.SqlClient;

namespace Corridor.Legacy.Services;

/// <summary>
/// TraceLink SOAP service implementation. All data access flows through
/// TraceCaseRepository (raw ADO.NET stored procedures); SQL errors raised by
/// the procs are mapped to SOAP faults with cor: subcodes. Legal status
/// transitions live in the procs themselves, they are the source of truth.
/// </summary>
public sealed class TraceLinkService : ITraceLinkService
{
    private const int DefaultMaxRows = 50;
    private const int MaxMaxRows = 500;

    private readonly TraceCaseRepository _repository;
    private readonly ILogger<TraceLinkService> _logger;

    public TraceLinkService(TraceCaseRepository repository, ILogger<TraceLinkService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public TraceCase[] SearchCases(string requester, string statusFilter, int maxRows)
    {
        string? filter = string.IsNullOrWhiteSpace(statusFilter) ? null : statusFilter.Trim();
        int rowLimit = maxRows <= 0 ? DefaultMaxRows : Math.Min(maxRows, MaxMaxRows);
        return Wrap("SearchCases", () => _repository.SearchCases(string.IsNullOrWhiteSpace(requester) ? "unknown" : requester.Trim(), filter, rowLimit).ToArray());
    }

    public TraceCase GetCase(string caseNumber)
    {
        if (string.IsNullOrWhiteSpace(caseNumber))
        {
            throw CorridorFault.Sender(CorridorFaultSubcodes.InvalidRequest, "caseNumber must not be empty.");
        }

        string number = caseNumber.Trim();
        return Wrap("GetCase", () =>
        {
            TraceCase? traceCase = _repository.GetCase(number);
            return traceCase ?? throw CorridorFault.Sender(CorridorFaultSubcodes.CaseNotFound, $"Case {number} not found.");
        });
    }

    public string CreateTraceRequest(TraceRequestCreate request)
    {
        if (request is null
            || string.IsNullOrWhiteSpace(request.LicenseeName)
            || string.IsNullOrWhiteSpace(request.ItemDescription)
            || string.IsNullOrWhiteSpace(request.Serial)
            || string.IsNullOrWhiteSpace(request.RequesterUpn))
        {
            throw CorridorFault.Sender(CorridorFaultSubcodes.InvalidRequest,
                "TraceRequestCreate requires LicenseeName, ItemDescription, Serial, and RequesterUpn.");
        }

        return Wrap("CreateTraceRequest", () => _repository.CreateTraceRequest(request));
    }

    public bool UpdateStatus(string caseNumber, string newStatus, string actor)
    {
        if (string.IsNullOrWhiteSpace(caseNumber) || string.IsNullOrWhiteSpace(newStatus) || string.IsNullOrWhiteSpace(actor))
        {
            throw CorridorFault.Sender(CorridorFaultSubcodes.InvalidRequest, "caseNumber, newStatus, and actor must not be empty.");
        }

        return Wrap("UpdateStatus", () => _repository.UpdateStatus(caseNumber.Trim(), newStatus.Trim(), actor.Trim()) > 0);
    }

    private T Wrap<T>(string operation, Func<T> action)
    {
        try
        {
            T result = action();
            _logger.LogInformation("TraceLink {Operation} ok for {Caller} (token kind {TokenKind})", operation, CallerUpn(), CallerTokenKind());
            return result;
        }
        catch (SqlException exception)
        {
            // The trace procs own the transition rules; their RAISERROR messages
            // decide which cor: subcode the caller sees.
            _logger.LogWarning("TraceLink {Operation} rejected by SQL error {ErrorNumber}: {Message}", operation, exception.Number, exception.Message);
            throw SqlFaultMapper.Map(exception);
        }
    }

    private string CallerUpn()
    {
        return (OperationContext.Current?.IncomingMessageProperties[CorridorSecurityMessageInspector.IdentityPropertyName] as ValidatedIdentity)?.Upn
            ?? "unknown";
    }

    private string CallerTokenKind()
    {
        return (OperationContext.Current?.IncomingMessageProperties[CorridorSecurityMessageInspector.IdentityPropertyName] as ValidatedIdentity)?.Kind.ToString()
            ?? "unknown";
    }
}
