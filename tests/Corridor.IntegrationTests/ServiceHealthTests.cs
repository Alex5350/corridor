using Corridor.IntegrationTests.Infrastructure;

namespace Corridor.IntegrationTests;

/// <summary>Every service exposes the anonymous healthz contract endpoint.</summary>
[Collection(CorridorStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ServiceHealthTests(CorridorStackFixture fixture)
{
    [Theory]
    [InlineData("http://localhost:8080", "okta-sim")]
    [InlineData("http://localhost:8090", "adfs-sim")]
    [InlineData("http://localhost:8000", "legacy TraceLink")]
    [InlineData("http://localhost:5200", "portal")]
    public async Task Healthz_ReportsOk_ForEveryService(string baseUrl, string serviceName)
    {
        using var http = fixture.CreateHttpClient();
        using var response = await http.GetAsync($"{baseUrl}/healthz");
        Assert.True(response.IsSuccessStatusCode, $"{serviceName} healthz failed HTTP {(int)response.StatusCode}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"ok\"", body, StringComparison.Ordinal);
    }
}
