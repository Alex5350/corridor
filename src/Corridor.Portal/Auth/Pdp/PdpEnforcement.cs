using System.Security.Claims;

namespace Corridor.Portal.Auth.Pdp;

/// <summary>
/// Turns the portal API boundary into a policy enforcement point: after authentication,
/// the caller's role, the endpoint's resource, and its action go to the central PDP, and
/// anything but a Permit becomes a 403 problem detail with errorCode cor:PdpDenied.
/// Razor pages stay on plain role attributes; the JSON API is the machine contract and
/// therefore the enforcement point (ADR 0007).
/// </summary>
public static class PdpEnforcement
{
    public static async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next,
        string resource,
        string action)
    {
        var role = ReadRole(context.HttpContext.User);
        if (role is null)
        {
            return Denied("The authenticated identity carries no role claim for the policy decision point.");
        }
        var pdp = context.HttpContext.RequestServices.GetRequiredService<IPdpClient>();
        var decision = await pdp.DecideAsync(role, resource, action, context.HttpContext.RequestAborted);
        return decision.Permit ? await next(context) : Denied(decision.StatusMessage);
    }

    /// <summary>
    /// First role claim on the principal. Portal cookie principals carry ClaimTypes.Role
    /// (PortalClaims.Transform); okta-sim bearer principals carry the raw "role" claim.
    /// </summary>
    public static string? ReadRole(ClaimsPrincipal principal)
    {
        return principal.Claims
            .Where(claim => claim.Type == ClaimTypes.Role || claim.Type == "role")
            .Select(claim => claim.Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    /// <summary>The shared 403 shape for a PDP deny, used by the endpoint filter and the assignments handler.</summary>
    public static IResult Denied(string? statusMessage)
    {
        return Results.Problem(
            title: "The policy decision point denied this request.",
            statusCode: StatusCodes.Status403Forbidden,
            detail: string.IsNullOrWhiteSpace(statusMessage)
                ? "No policy permits this role, resource, and action."
                : statusMessage,
            extensions: new Dictionary<string, object?> { ["errorCode"] = PdpAuthorization.PdpDeniedCode });
    }
}

public static class PdpEndpointFilterExtensions
{
    /// <summary>Guards an endpoint with a central PDP decision for one (resource, action) pair.</summary>
    public static IEndpointConventionBuilder WithPdpDecision(this IEndpointConventionBuilder builder, string resource, string action)
        => builder.AddEndpointFilter((context, next) => PdpEnforcement.InvokeAsync(context, next, resource, action));
}
