using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Corridor.Portal.Services.TraceLink;

namespace Corridor.Portal.Tests;

public class EndpointTests : IClassFixture<PortalFactory>
{
    private readonly PortalFactory _factory;

    public EndpointTests(PortalFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Healthz_IsAnonymousAndReportsOk()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task CasesList_ReturnsCasesFromTheTraceClient()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/cases");
        var cases = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, cases.GetArrayLength());
        Assert.Equal("TRC-100101", cases[0].GetProperty("caseNumber").GetString());
    }

    [Fact]
    public async Task CasesList_AppliesTheStatusFilter()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/cases?statusFilter=UnderReview");
        var cases = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, cases.GetArrayLength());
        Assert.Equal("TRC-100102", cases[0].GetProperty("caseNumber").GetString());
    }

    [Fact]
    public async Task CreateCase_ReturnsCreatedWithCaseNumber()
    {
        using var client = _factory.CreateClient();
        _factory.TraceClient.NextCaseNumber = "TRC-200042";

        var response = await client.PostAsJsonAsync("/api/cases", new
        {
            licenseeName = "Test Licensee Company",
            itemDescription = "Kalvin KB-7 .22 bolt rifle",
            serial = "KB7-9900001"
        });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("TRC-200042", body.GetProperty("caseNumber").GetString());
        Assert.Equal("/api/cases/TRC-200042", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task CreateCase_RejectsIncompletePayloadAsProblemDetails()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/cases", new { licenseeName = "Only a name" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task GetCase_ReturnsProblemDetailsWhenCaseIsUnknown()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/cases/TRC-999999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(404, body.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task GetCase_MapsSoapFaultSubcodeToProblemDetails()
    {
        using var client = _factory.CreateClient();
        _factory.TraceClient.FaultSubcodeForNextCall = TraceLinkFaults.IllegalStatusTransition;

        var response = await client.GetAsync("/api/cases/TRC-100101");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(409, body.GetProperty("status").GetInt32());
        Assert.Equal("cor:IllegalStatusTransition", body.GetProperty("faultSubcode").GetString());
    }

    [Fact]
    public async Task AssignmentsList_ReturnsTheCallersAssignments()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Upn", "inspector@corridor.example");

        var response = await client.GetAsync("/api/assignments");
        var assignments = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, assignments.GetArrayLength());
        Assert.NotEqual(0, assignments[0].GetProperty("checklist").GetArrayLength());
    }

    [Fact]
    public async Task AssignmentsPatch_TogglesOneChecklistItem()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Upn", "inspector@corridor.example");

        var response = await client.PatchAsJsonAsync("/api/assignments/1", new { itemIndex = 1, done = true });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var checklist = body.GetProperty("checklist");
        Assert.False(checklist[0].GetProperty("done").GetBoolean());
        Assert.True(checklist[1].GetProperty("done").GetBoolean());

        // Persisted: a follow-up read sees the toggled item.
        var followUp = await client.GetAsync("/api/assignments");
        var assignments = await followUp.Content.ReadFromJsonAsync<JsonElement>();
        var first = assignments[0];
        Assert.True(first.GetProperty("checklist")[1].GetProperty("done").GetBoolean());
    }

    [Fact]
    public async Task AssignmentsPatch_RejectsOutOfRangeIndexAsProblemDetails()
    {
        using var client = _factory.CreateClient();

        var response = await client.PatchAsJsonAsync("/api/assignments/2", new { itemIndex = 99, done = true });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task AssignmentsPatch_ReturnsProblemDetailsForUnknownAssignment()
    {
        using var client = _factory.CreateClient();

        var response = await client.PatchAsJsonAsync("/api/assignments/999", new { itemIndex = 0, done = true });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}
