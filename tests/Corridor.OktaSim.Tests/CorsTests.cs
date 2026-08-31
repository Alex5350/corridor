using System.Text;

namespace Corridor.OktaSim.Tests;

/// <summary>
/// CORS on the OIDC endpoint group: the SPA origin gets real preflight and
/// actual-response headers (no test shim needed for the browser flow), other
/// origins get nothing, and the non-OIDC surface (SCIM, PDP) stays CORS-free.
/// </summary>
public class CorsTests(OktaSimFactory factory) : IClassFixture<OktaSimFactory>
{
    private const string SpaOrigin = "http://localhost:5173";

    private readonly OktaSimFactory _factory = factory;

    /// <summary>An OPTIONS preflight exactly as a browser sends it: headers, no body.</summary>
    private static HttpRequestMessage Preflight(string path, string origin, string method)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.TryAddWithoutValidation("Origin", origin);
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", method);
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", "authorization,content-type");
        return request;
    }

    private static async Task<HttpResponseMessage> GetWithOriginAsync(
        HttpClient client, string path, string origin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("Origin", origin);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Token_Preflight_From_The_Spa_Origin_Returns_204_With_Allowances()
    {
        using var client = _factory.CreateNoRedirectClient();
        using var response = await client.SendAsync(Preflight("/token", SpaOrigin, "POST"));

        Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(SpaOrigin,
            string.Join(",", response.Headers.GetValues("Access-Control-Allow-Origin")));
        var allowMethods = string.Join(",", response.Headers.GetValues("Access-Control-Allow-Methods"));
        Assert.True(allowMethods.Contains("POST", StringComparison.OrdinalIgnoreCase),
            $"allow-methods missing POST: {allowMethods}");
        var allowHeaders = string.Join(",", response.Headers
            .GetValues("Access-Control-Allow-Headers")
            .SelectMany(value => value.Split(',').Select(part => part.Trim())));
        Assert.True(allowHeaders.Contains("Authorization", StringComparison.OrdinalIgnoreCase),
            $"allow-headers missing Authorization: {allowHeaders}");
        Assert.True(allowHeaders.Contains("Content-Type", StringComparison.OrdinalIgnoreCase),
            $"allow-headers missing Content-Type: {allowHeaders}");
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"),
            "the public SPA client must not need credentialed CORS");
    }

    [Fact]
    public async Task Actual_Oidc_Requests_From_The_Spa_Origin_Carry_Allow_Origin()
    {
        using var client = _factory.CreateNoRedirectClient();

        var jwks = await GetWithOriginAsync(client, "/jwks", SpaOrigin);
        Assert.Equal(System.Net.HttpStatusCode.OK, jwks.StatusCode);
        Assert.Equal(SpaOrigin,
            string.Join(",", jwks.Headers.GetValues("Access-Control-Allow-Origin")));

        var discovery = await GetWithOriginAsync(client, "/.well-known/openid-configuration", SpaOrigin);
        Assert.Equal(System.Net.HttpStatusCode.OK, discovery.StatusCode);
        Assert.Equal(SpaOrigin,
            string.Join(",", discovery.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task A_Different_Origin_Gets_No_Cors_Headers()
    {
        using var client = _factory.CreateNoRedirectClient();
        const string stranger = "http://localhost:9999";

        var actual = await GetWithOriginAsync(client, "/jwks", stranger);
        Assert.Equal(System.Net.HttpStatusCode.OK, actual.StatusCode);
        Assert.False(actual.Headers.Contains("Access-Control-Allow-Origin"),
            "a non-registered origin must not receive CORS headers");

        using var preflight = await client.SendAsync(Preflight("/token", stranger, "POST"));
        Assert.False(preflight.Headers.Contains("Access-Control-Allow-Origin"),
            "a non-registered origin must not receive preflight allowances");
    }

    [Fact]
    public async Task Scim_And_Pdp_Remain_Cors_Free()
    {
        using var client = _factory.CreateClient();

        var scim = await GetWithOriginAsync(client, "/scim/v2/Users", SpaOrigin);
        Assert.False(scim.Headers.Contains("Access-Control-Allow-Origin"),
            "SCIM is a server-to-server API and must stay CORS-free");

        using var pdp = new HttpRequestMessage(HttpMethod.Post, "/pdp/decide");
        pdp.Headers.TryAddWithoutValidation("Origin", SpaOrigin);
        pdp.Content = new StringContent(
            TestHelpers.XacmlRequest("Inspector", "assignments", "write"),
            Encoding.UTF8,
            "application/xacml+xml");
        using var pdpResponse = await client.SendAsync(pdp);
        Assert.Equal(System.Net.HttpStatusCode.OK, pdpResponse.StatusCode);
        Assert.False(pdpResponse.Headers.Contains("Access-Control-Allow-Origin"),
            "the PDP is a server-to-server API and must stay CORS-free");
    }
}
