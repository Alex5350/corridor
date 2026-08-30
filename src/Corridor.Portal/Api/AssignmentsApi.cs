using System.Security.Claims;
using Corridor.Portal.Auth;
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
            [FromServices] IAssignmentRepository assignments, [FromServices] ChecklistService checklists,
            CancellationToken ct) =>
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
