namespace Corridor.OktaSim.Endpoints;

/// <summary>Liveness endpoint, anonymous JSON per the repo-wide convention.</summary>
public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/healthz", () => Results.Json(new Dictionary<string, string> { ["status"] = "ok" }));
        return app;
    }
}
