using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Corridor.IntegrationTests.Infrastructure;

namespace Corridor.IntegrationTests;

/// <summary>
/// The REST to SOAP migration bridge: /api/cases create and read while the legacy
/// app walks Adfs (service SAML), Dual, then Okta (client credentials JWT), with the
/// rows verified directly in SQL.
/// </summary>
[Collection(CorridorStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PortalBridgeTests(CorridorStackFixture fixture)
{
    [Fact]
    public async Task PortalBridge_CaseCreateAndGet_WorksAcrossAdfsDualAndOktaTrustModes()
    {
        var token = await GetPortalUserTokenAsync("officer@corridor.example");

        try
        {
            foreach (var mode in new[] { "Adfs", "Dual", "Okta" })
            {
                await Sql.SetTrustModeAsync(fixture.CorridorConnectionString, "legacy", mode);
                var marker = $"IT Bridge {mode} {Guid.NewGuid():N}"[..40];

                using var http = fixture.CreateHttpClient();
                using var authorize = new HttpRequestMessage(HttpMethod.Post, new Uri(fixture.PortalBase, "/api/cases"));
                authorize.Headers.Authorization = new("Bearer", token);
                authorize.Content = JsonContent.Create(new
                {
                    licenseeName = marker,
                    itemDescription = "Integration test trace item",
                    serial = $"IT-{mode}-{Random.Shared.Next(100000, 999999)}",
                });
                using var created = await http.SendAsync(authorize);
                var createBody = await created.Content.ReadAsStringAsync();
                Assert.True(created.IsSuccessStatusCode,
                    $"create in {mode} mode failed HTTP {(int)created.StatusCode}: {createBody}");
                var caseNumber = JsonDocument.Parse(createBody).RootElement.GetProperty("caseNumber").GetString();
                Assert.Matches("^TRC-[0-9]{6}$", caseNumber);

                using var fetch = new HttpRequestMessage(HttpMethod.Get, new Uri(fixture.PortalBase, $"/api/cases/{caseNumber}"));
                fetch.Headers.Authorization = new("Bearer", token);
                using var fetched = await http.SendAsync(fetch);
                Assert.True(fetched.IsSuccessStatusCode);
                var body = JsonDocument.Parse(await fetched.Content.ReadAsStringAsync());
                Assert.Equal(caseNumber, body.RootElement.GetProperty("caseNumber").GetString());
                Assert.Equal("Received", body.RootElement.GetProperty("status").GetString());

                // The row must be in trace.TraceCases, not just in the response.
                var row = await Sql.RowAsync(fixture.CorridorConnectionString,
                    "SELECT Status, SubmittedBy FROM trace.TraceCases WHERE CaseNumber = @case",
                    ("@case", caseNumber!));
                Assert.Equal("Received", row[0]);
                Assert.Equal("officer@corridor.example", row[1]);
            }

            var finalMode = await Sql.GetTrustModeAsync(fixture.CorridorConnectionString, "legacy");
            Assert.Equal("Okta", finalMode);
        }
        finally
        {
            await Sql.SetTrustModeAsync(fixture.CorridorConnectionString, "legacy", "Adfs");
        }
    }

    [Fact]
    public async Task PortalBridge_ValidationProblem_MissingFields_ReturnsRfc9457Shape()
    {
        var token = await GetPortalUserTokenAsync("officer@corridor.example");
        using var http = fixture.CreateHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(fixture.PortalBase, "/api/cases"));
        request.Headers.Authorization = new("Bearer", token);
        request.Content = JsonContent.Create(new { licenseeName = "only a licensee" });
        using var response = await http.SendAsync(request);

        Assert.Equal(400, (int)response.StatusCode);
        Assert.Contains("application/problem+json", response.Content.Headers.ContentType?.ToString() ?? string.Empty, StringComparison.Ordinal);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(400, body.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(body.RootElement.GetProperty("title").GetString()));
    }

    [Fact]
    public async Task PortalBridge_WithoutAuthentication_IsRejected()
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        using var response = await http.GetAsync(new Uri(fixture.PortalBase, "/api/cases"));
        // The ApiOrSpa scheme falls back to the cookie handler, which redirects to /Login.
        Assert.Equal(302, (int)response.StatusCode);
        Assert.Contains("/Login", response.Headers.Location?.ToString() ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PortalBridge_UnknownCase_MapsTheSoapCaseNotFoundFaultOntoA404Problem()
    {
        var token = await GetPortalUserTokenAsync("officer@corridor.example");
        using var http = fixture.CreateHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(fixture.PortalBase, "/api/cases/TRC-999999"));
        request.Headers.Authorization = new("Bearer", token);
        using var response = await http.SendAsync(request);

        Assert.Equal(404, (int)response.StatusCode);
        Assert.Contains("application/problem+json", response.Content.Headers.ContentType?.ToString() ?? string.Empty, StringComparison.Ordinal);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(404, body.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("cor:CaseNotFound", body.RootElement.GetProperty("faultSubcode").GetString());
    }

    private async Task<string> GetPortalUserTokenAsync(string username)
    {
        var (code, _, _) = await Oidc.DriveCodeFlowAsync(
            fixture.OktaBase, Oidc.PortalClientId, "http://localhost:5200/signin-oidc",
            username, Oidc.DemoPassword, "openid profile", withPkce: false);
        var tokens = await Oidc.ExchangeCodeAsync(
            fixture.OktaBase, Oidc.PortalClientId, Oidc.PortalSecret, code, "http://localhost:5200/signin-oidc");
        return tokens.AccessToken;
    }
}
