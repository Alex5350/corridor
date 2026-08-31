using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Corridor.IntegrationTests.Infrastructure;

namespace Corridor.IntegrationTests;

/// <summary>
/// The portal API boundary as a live policy enforcement point: the same okta-sim PDP that
/// XacmlDecisionTests probes directly now decides who may call /api/cases. An Officer token
/// is permitted by policy 10; a Clerk token passes the AnyRole authentication gate and is
/// then denied by the deny-all fallback, surfacing as 403 problem details with errorCode
/// cor:PdpDenied (ADR 0007).
/// </summary>
[Collection(CorridorStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PortalPepEnforcementTests(CorridorStackFixture fixture)
{
    [Fact]
    public async Task PortalPep_OfficerReadsTraceCases_ClerkIsDeniedByTheLivePdp()
    {
        var officerToken = await GetAccessTokenAsync("officer@corridor.example");
        var clerkToken = await GetAccessTokenAsync("clerk@corridor.example");
        using var http = fixture.CreateHttpClient();

        using var permittedRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(fixture.PortalBase, "/api/cases"));
        permittedRequest.Headers.Authorization = new("Bearer", officerToken);
        using var permitted = await http.SendAsync(permittedRequest);
        var permittedBody = await permitted.Content.ReadAsStringAsync();
        Assert.True(permitted.IsSuccessStatusCode,
            $"officer read failed HTTP {(int)permitted.StatusCode}: {permittedBody}");
        Assert.Equal("application/json", permitted.Content.Headers.ContentType?.MediaType);

        using var deniedRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(fixture.PortalBase, "/api/cases"));
        deniedRequest.Headers.Authorization = new("Bearer", clerkToken);
        using var denied = await http.SendAsync(deniedRequest);

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("application/problem+json", denied.Content.Headers.ContentType?.MediaType);
        var body = await denied.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(403, body.GetProperty("status").GetInt32());
        Assert.Equal("cor:PdpDenied", body.GetProperty("errorCode").GetString());
    }

    /// <summary>The same OIDC code flow the portal bridge tests use, parameterized by username.</summary>
    private async Task<string> GetAccessTokenAsync(string username)
    {
        var redirectUri = "http://localhost:5200/signin-oidc";
        var (code, _, _) = await Oidc.DriveCodeFlowAsync(
            fixture.OktaBase, Oidc.PortalClientId, redirectUri, username, Oidc.DemoPassword,
            "openid profile", withPkce: false);
        var tokens = await Oidc.ExchangeCodeAsync(
            fixture.OktaBase, Oidc.PortalClientId, Oidc.PortalSecret, code, redirectUri);
        return tokens.AccessToken;
    }
}
