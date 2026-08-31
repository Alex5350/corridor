using System.Security.Claims;
using Corridor.Portal.Auth;
using Corridor.Portal.Auth.Pdp;
using Corridor.Portal.Data;
using Corridor.Portal.Services;
using Microsoft.AspNetCore.Mvc;

namespace Corridor.Portal.Api;

public sealed record AssignmentPatchRequest(int? ItemIndex, bool? Done);

public sealed record AssignmentResponse(
    int Id,
    string InspectorUpn,
    string LicenseeName,
    string Focus,
    DateTime DueAt,
    IReadOnlyList<ChecklistItem> Checklist);

/// <summary>Endpoints consumed by the FieldInsight SPA. Bearer only: the SPA exists after cutover.</summary>
public static class AssignmentsApi
{
    /// <summary>Stable error code carried on 403 problem details when the caller may not modify an assignment.</summary>
    public const string NotAssignmentOwnerCode = "cor:NotAssignmentOwner";

    public static IEndpointRouteBuilder MapAssignmentsApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/assignments").RequireAuthorization("SpaBearer").RequireCors("Spa");

        group.MapGet("/", async ([FromQuery] string? inspector, ClaimsPrincipal user,
            [FromServices] IAssignmentRepository assignments, [FromServices] ChecklistService checklists,
            CancellationToken ct) =>
        {
            var caller = PortalClaims.ReadUpn(user) ?? "unknown";
            var isAdmin = user.IsInRole("Admin");
            var scope = inspector is not null && isAdmin ? inspector : caller;
            var list = await assignments.ListAsync(scope, ct);
            return Results.Ok(list.Select(a => ToResponse(a, checklists)));
        });

        group.MapPatch("/{id:int}", async (int id, [FromBody] AssignmentPatchRequest request,
            ClaimsPrincipal user, [FromServices] IAssignmentRepository assignments,
            [FromServices] ChecklistService checklists, [FromServices] IPdpClient pdp, CancellationToken ct) =>
        {
            if (request.ItemIndex is null || request.Done is null)
            {
                return Results.Problem(title: "Invalid checklist patch.",
                    statusCode: 400,
                    detail: "Both itemIndex and done are required.");
            }
            var assignment = await assignments.GetAsync(id, ct);
            if (assignment is null)
            {
                return Results.Problem(title: "Assignment not found.",
                    statusCode: 404,
                    detail: $"No assignment {id}.");
            }
            // Writes follow policy 20: only Inspectors write assignments, and only on their
            // own work; Admin overrides for reassignment support. GET above stays scoped to
            // the caller, this closes the same gap for the mutating verb.
            var caller = PortalClaims.ReadUpn(user);
            var isOwner = user.IsInRole("Inspector")
                && caller is not null
                && string.Equals(assignment.InspectorUpn, caller, StringComparison.OrdinalIgnoreCase);
            if (!isOwner && !user.IsInRole("Admin"))
            {
                return Results.Problem(
                    title: "Only the assigned inspector or an administrator may update this checklist.",
                    statusCode: 403,
                    detail: $"Assignment {id} is assigned to {assignment.InspectorUpn}.",
                    extensions: new Dictionary<string, object?> { ["errorCode"] = NotAssignmentOwnerCode });
            }
            // Defense in depth (ADR 0007): ownership passed, now the central PDP gets the final
            // word on assignments:write (policy 20: Inspectors). A Deny is 403 cor:PdpDenied.
            var role = PdpEnforcement.ReadRole(user);
            var decision = role is null
                ? new PdpDecision(false, "The authenticated identity carries no role claim for the policy decision point.")
                : await pdp.DecideAsync(role, PdpAuthorization.AssignmentsResource, PdpAuthorization.WriteAction, ct);
            if (!decision.Permit)
            {
                return PdpEnforcement.Denied(decision.StatusMessage);
            }
            if (!checklists.TryToggle(assignment.ChecklistJson, request.ItemIndex.Value, request.Done.Value, out var updatedJson))
            {
                return Results.Problem(title: "Checklist index out of range.",
                    statusCode: 400,
                    detail: $"Assignment {id} has no checklist item at index {request.ItemIndex}.");
            }
            var stored = await assignments.SaveChecklistAsync(id, updatedJson, ct);
            return Results.Ok(ToResponse(stored, checklists));
        });

        return app;
    }

    private static AssignmentResponse ToResponse(Models.Assignment assignment, ChecklistService checklists)
    {
        return new AssignmentResponse(
            assignment.Id,
            assignment.InspectorUpn,
            assignment.LicenseeName,
            assignment.Focus,
            assignment.DueAt,
            checklists.Parse(assignment.ChecklistJson));
    }
}
