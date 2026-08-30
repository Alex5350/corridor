using Corridor.IntegrationTests.Infrastructure;

namespace Corridor.IntegrationTests;

/// <summary>
/// The XACML policy decision point over the three seeded policies: permits for the
/// two positive rules, deny for everyone else, and a safe deny with a status message
/// on malformed requests.
/// </summary>
[Collection(CorridorStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class XacmlDecisionTests(CorridorStackFixture fixture)
{
    [Fact]
    public async Task Xacml_OfficerReadingTraceCases_IsPermitted()
    {
        using var http = fixture.CreateHttpClient();
        var response = await Xacml.DecideAsync(http, fixture.OktaBase,
            Xacml.Request("Officer", "trace-cases", "read"));
        Assert.Equal("Permit", response.Decision);
        Assert.Contains("corridor:policy:trace-read", response.RawXml, StringComparison.Ordinal);
        Assert.Contains("status:ok", response.StatusCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Xacml_InspectorWritingAssignments_IsPermitted()
    {
        using var http = fixture.CreateHttpClient();
        var response = await Xacml.DecideAsync(http, fixture.OktaBase,
            Xacml.Request("Inspector", "assignments", "write"));
        Assert.Equal("Permit", response.Decision);
        Assert.Contains("corridor:policy:assignments-write", response.RawXml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Xacml_ClerkReadingTraceCases_IsDenied()
    {
        using var http = fixture.CreateHttpClient();
        var response = await Xacml.DecideAsync(http, fixture.OktaBase,
            Xacml.Request("Clerk", "trace-cases", "read"));
        Assert.Equal("Deny", response.Decision);
        Assert.Contains("corridor:policy:deny-all", response.RawXml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Xacml_MalformedRequest_IsDeniedWithSyntaxErrorStatus()
    {
        using var http = fixture.CreateHttpClient();
        var response = await Xacml.DecideAsync(http, fixture.OktaBase, "this is not xml");
        Assert.Equal("Deny", response.Decision);
        Assert.Contains("syntax-error", response.StatusCode, StringComparison.Ordinal);
        Assert.Contains("Malformed XACML request", response.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("Obligation", response.RawXml, StringComparison.Ordinal);
    }
}
