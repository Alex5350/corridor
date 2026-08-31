using System.Security.Claims;
using Corridor.Portal.Auth;
using Corridor.Portal.Models;
using Corridor.Portal.Services.TraceLink;
using Microsoft.AspNetCore.Mvc;

namespace Corridor.Portal.Api;

public sealed record CreateCaseRequest(string? LicenseeName, string? ItemDescription, string? Serial);

/// <summary>REST to SOAP bridge: JSON endpoints over the legacy TraceLink client, RFC 9457 errors.</summary>
public static class CasesApi
{
    public static IEndpointRouteBuilder MapCasesApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/cases").RequireAuthorization("AnyRole");

        group.MapGet("/", async ([FromQuery] string? statusFilter, ClaimsPrincipal user,
            [FromServices] ITraceLinkClient traceLink, CancellationToken ct) =>
        {
            try
            {
                var requester = PortalClaims.ReadUpn(user) ?? "unknown";
                var cases = await traceLink.SearchCasesAsync(requester, statusFilter, 50, ct);
                return Results.Ok(cases);
            }
            catch (TraceLinkFaultException fault)
            {
                return FaultToProblem(fault);
            }
            catch (HttpRequestException)
            {
                return UpstreamUnreachable();
            }
            // HttpClient timeouts surface as TaskCanceledException (an OperationCanceledException):
            // the filter keeps a genuine caller cancellation propagating instead of becoming a 502.
            catch (OperationCanceledException) when (ct.IsCancellationRequested is false)
            {
                return UpstreamTimeout();
            }
        });

        group.MapGet("/{caseNumber}", async (string caseNumber,
            [FromServices] ITraceLinkClient traceLink, CancellationToken ct) =>
        {
            try
            {
                var found = await traceLink.GetCaseAsync(caseNumber, ct);
                return found is null
                ? Results.Problem(title: "Trace case not found.", statusCode: 404, detail: $"No case {caseNumber}.")
                : Results.Ok(found);
            }
            catch (TraceLinkFaultException fault)
            {
                return FaultToProblem(fault);
            }
            catch (HttpRequestException)
            {
                return UpstreamUnreachable();
            }
            // HttpClient timeouts surface as TaskCanceledException (an OperationCanceledException):
            // the filter keeps a genuine caller cancellation propagating instead of becoming a 502.
            catch (OperationCanceledException) when (ct.IsCancellationRequested is false)
            {
                return UpstreamTimeout();
            }
        });

        group.MapPost("/", async ([FromBody] CreateCaseRequest request, ClaimsPrincipal user,
            [FromServices] ITraceLinkClient traceLink, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.LicenseeName)
                || string.IsNullOrWhiteSpace(request.ItemDescription)
                || string.IsNullOrWhiteSpace(request.Serial))
            {
                return Results.Problem(title: "Invalid trace request.",
                    statusCode: 400,
                    detail: "licenseeName, itemDescription, and serial are required.");
            }
            try
            {
                var create = new TraceRequestCreate(
                    request.LicenseeName.Trim(),
                    request.ItemDescription.Trim(),
                    request.Serial.Trim(),
                    PortalClaims.ReadUpn(user) ?? "unknown");
                var caseNumber = await traceLink.CreateTraceRequestAsync(create, ct);
                return Results.Created($"/api/cases/{caseNumber}", new { caseNumber });
            }
            catch (TraceLinkFaultException fault)
            {
                return FaultToProblem(fault);
            }
            catch (HttpRequestException)
            {
                return UpstreamUnreachable();
            }
            // HttpClient timeouts surface as TaskCanceledException (an OperationCanceledException):
            // the filter keeps a genuine caller cancellation propagating instead of becoming a 502.
            catch (OperationCanceledException) when (ct.IsCancellationRequested is false)
            {
                return UpstreamTimeout();
            }
        });

        return app;
    }

    internal static IResult FaultToProblem(TraceLinkFaultException fault)
    {
        var (status, title) = TraceLinkProblemMapper.Map(fault.Subcode);
        return Results.Problem(
            title: title,
            statusCode: status,
            detail: fault.Message,
            extensions: new Dictionary<string, object?> { ["faultSubcode"] = fault.Subcode });
    }

    private static IResult UpstreamUnreachable()
    {
        return Results.Problem(
            title: "The legacy trace service is unreachable.",
            statusCode: 502,
            detail: "The SOAP endpoint did not answer.",
            extensions: new Dictionary<string, object?> { ["faultSubcode"] = TraceLinkFaults.ServiceUnreachable });
    }

    private static IResult UpstreamTimeout()
    {
        return Results.Problem(
            title: "The legacy trace service timed out.",
            statusCode: 502,
            detail: "The SOAP endpoint did not answer within the configured timeout.",
            extensions: new Dictionary<string, object?> { ["faultSubcode"] = TraceLinkFaults.ServiceTimeout });
    }
}
