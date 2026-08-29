using Corridor.OktaSim.Xacml;

namespace Corridor.OktaSim.Endpoints;

/// <summary>
/// XACML policy decision point endpoint. Accepts an XACML 2.0 or 3.0 context
/// Request and always answers with a real XACML Response document; malformed
/// input gets a Deny with a StatusMessage, never a bare 500.
/// </summary>
public static class XacmlEndpoints
{
    public static IEndpointRouteBuilder MapXacmlEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/pdp/decide", async (HttpRequest request, PdpEngine pdp, ILoggerFactory loggerFactory) =>
        {
            string body;
            using (var reader = new StreamReader(request.Body))
            {
                body = await reader.ReadToEndAsync();
            }

            var responseXml = pdp.Decide(body);
            var decided = responseXml.Contains("<Decision>Permit</Decision>", StringComparison.Ordinal) ? "Permit" : "Deny";
            loggerFactory.CreateLogger("Xacml.Decide").LogInformation(
                "PDP decision {Decision} against {PolicyCount} policies ({Source})",
                decided, pdp.PolicyCount, pdp.SourceDescription);
            return Results.Content(responseXml, "application/xacml+xml; charset=utf-8");
        });

        return app;
    }
}
