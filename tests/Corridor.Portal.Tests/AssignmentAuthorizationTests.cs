using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Corridor.Portal.Tests;

/// <summary>
/// PATCH /api/assignments/{id} writes checklist state, so it requires the caller to be the
/// assigned Inspector or an Admin: the SpaBearer policy only proves the token is valid, not
/// that its holder owns the assignment. These tests pin the 403 contract (stable errorCode
/// cor:NotAssignmentOwner) for a non-owner inspector, and for a clerk whose token the policy
/// engine would never permit to write assignments at all.
/// </summary>
public class AssignmentAuthorizationTests : IClassFixture<PortalFactory>
{
    private readonly PortalFactory _factory;

    public AssignmentAuthorizationTests(PortalFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task OwnerInspector_TogglesOwnChecklistItem()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Upn", "inspector@corridor.example");
        client.DefaultRequestHeaders.Add("X-Test-Role", "Inspector");

        var response = await client.PatchAsJsonAsync("/api/assignments/1", new { itemIndex = 0, done = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("inspector@corridor.example", body.GetProperty("inspectorUpn").GetString());
        Assert.True(body.GetProperty("checklist")[0].GetProperty("done").GetBoolean());
    }

    [Fact]
    public async Task OtherInspector_GetsProblemDetailsWithNotAssignmentOwner()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Upn", "second-inspector@corridor.example");
        client.DefaultRequestHeaders.Add("X-Test-Role", "Inspector");

        var response = await client.PatchAsJsonAsync("/api/assignments/1", new { itemIndex = 0, done = true });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(403, body.GetProperty("status").GetInt32());
        Assert.Equal("cor:NotAssignmentOwner", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Clerk_GetsProblemDetailsWithNotAssignmentOwner()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Upn", "clerk@corridor.example");
        client.DefaultRequestHeaders.Add("X-Test-Role", "Clerk");

        var response = await client.PatchAsJsonAsync("/api/assignments/1", new { itemIndex = 0, done = true });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("cor:NotAssignmentOwner", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Admin_CanToggleAnyInspectorsChecklist()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Upn", "admin@corridor.example");
        client.DefaultRequestHeaders.Add("X-Test-Role", "Admin");

        var response = await client.PatchAsJsonAsync("/api/assignments/3", new { itemIndex = 2, done = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("inspector@corridor.example", body.GetProperty("inspectorUpn").GetString());
        Assert.True(body.GetProperty("checklist")[2].GetProperty("done").GetBoolean());
    }
}
