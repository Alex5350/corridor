using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Corridor.OktaSim.Tests;

/// <summary>
/// The credential-facing endpoints answer 429 once the fixed window is exhausted, and
/// unrelated endpoints (healthz) are never throttled. The default window is one minute,
/// so these tests run against a factory that shrinks it: the limiter options are bound
/// from configuration, and the test appsettings below set a tight permit limit.
/// </summary>
public class RateLimitTests
{
    [Fact]
    public async Task Token_Endpoint_Throttles_After_The_Window_Fills()
    {
        // Own factory per test: the window is per IP per host instance, so a shared
        // fixture would let one test's traffic throttle the other's.
        using var factory = new CredentialRateLimitFactory();
        var client = factory.CreateNoRedirectClient();

        var results = new List<HttpStatusCode>();
        for (var i = 0; i < 8; i++)
        {
            using var response = await client.PostAsync("/token", new FormUrlEncodedContent(
                new Dictionary<string, string> { ["grant_type"] = "authorization_code" }));
            results.Add(response.StatusCode);
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, results);
        // Requests inside the window are rejected on their merits (401: no client
        // authentication on this bare grant), not throttled.
        Assert.Contains(HttpStatusCode.Unauthorized, results);
    }

    [Fact]
    public async Task Healthz_Is_Never_Throttled()
    {
        using var factory = new CredentialRateLimitFactory();
        var client = factory.CreateNoRedirectClient();

        for (var i = 0; i < 8; i++)
        {
            using var fill = await client.PostAsync("/token", new FormUrlEncodedContent(
                new Dictionary<string, string> { ["grant_type"] = "authorization_code" }));
        }

        using var health = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }
}

public sealed class CredentialRateLimitFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Corridor", string.Empty);
        builder.UseSetting("OktaSim:Issuer", "http://localhost:8080");
        // Five credential requests per minute keeps the test fast while still proving
        // both sides of the window.
        builder.UseSetting("OktaSim:CredentialPermitLimit", "5");
    }

    public HttpClient CreateNoRedirectClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
}
