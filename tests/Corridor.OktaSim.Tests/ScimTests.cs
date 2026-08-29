using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Corridor.OktaSim.Tests;

/// <summary>
/// SCIM 2.0 CRUD with RFC 7644 error shapes: list, filter, create (unique
/// userName), get, put, patch (replace active/groups), and bearer enforcement.
/// </summary>
public class ScimTests(OktaSimFactory factory) : IClassFixture<OktaSimFactory>
{
    private readonly OktaSimFactory _factory = factory;

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "corridor-scim-token");
        return client;
    }

    private static HttpContent ScimBody(string json) =>
        new StringContent(json, Encoding.UTF8, "application/scim+json");

    [Fact]
    public async Task List_Returns_Seeded_Users_And_Filters_By_UserName()
    {
        var client = Client();
        var response = await client.GetAsync("/scim/v2/Users");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("application/scim+json", response.Content.Headers.ContentType!.ToString());
        var list = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("urn:ietf:params:scim:api:messages:2.0:ListResponse",
            (string?)list["schemas"]![0]);
        Assert.True(list["totalResults"]!.GetValue<int>() >= 4, "seeded users missing");
        var userNames = ((JsonArray)list["Resources"]!).Select(r => (string?)r!["userName"]).ToArray();
        Assert.Contains("admin@corridor.example", userNames);
        Assert.Contains("inspector@corridor.example", userNames);
        Assert.Contains("officer@corridor.example", userNames);
        Assert.Contains("clerk@corridor.example", userNames);

        var filtered = await client.GetAsync(
            "/scim/v2/Users?filter=" + Uri.EscapeDataString("userName eq \"officer@corridor.example\""));
        var filteredPayload = JsonNode.Parse(await filtered.Content.ReadAsStringAsync())!;
        Assert.Equal(1, filteredPayload["totalResults"]!.GetValue<int>());
        Assert.Equal("officer@corridor.example",
            (string?)filteredPayload["Resources"]![0]!["userName"]);

        var badFilter = await client.GetAsync(
            "/scim/v2/Users?filter=" + Uri.EscapeDataString("name sw \"x\""));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, badFilter.StatusCode);
    }

    [Fact]
    public async Task Create_Get_Put_Patch_Roundtrip()
    {
        var client = Client();
        var userName = $"analyst-{Guid.NewGuid():N}@corridor.example";

        var create = await client.PostAsync("/scim/v2/Users", ScimBody($$"""
            {
              "schemas": ["urn:ietf:params:scim:schemas:core:2.0:User", "urn:corridor:scim:1.0:User"],
              "userName": "{{userName}}",
              "displayName": "Rosa Kellerman",
              "active": true,
              "groups": [{ "display": "trace-reviewers" }],
              "urn:corridor:scim:1.0:User": { "role": "Clerk" }
            }
            """));
        Assert.Equal(System.Net.HttpStatusCode.Created, create.StatusCode);
        var created = JsonNode.Parse(await create.Content.ReadAsStringAsync())!;
        var id = created["id"]!.GetValue<string>();
        Assert.Equal(userName, (string?)created["userName"]);
        Assert.Equal("Clerk", (string?)created["urn:corridor:scim:1.0:User"]!["role"]);
        Assert.NotNull(create.Headers.Location);
        Assert.EndsWith($"/scim/v2/Users/{id}", create.Headers.Location.ToString());

        var get = await client.GetAsync($"/scim/v2/Users/{id}");
        var fetched = JsonNode.Parse(await get.Content.ReadAsStringAsync())!;
        Assert.Equal("Rosa Kellerman", (string?)fetched["displayName"]);
        Assert.True(fetched["active"]!.GetValue<bool>());

        var put = await client.PutAsync($"/scim/v2/Users/{id}", ScimBody($$"""
            {
              "schemas": ["urn:ietf:params:scim:schemas:core:2.0:User", "urn:corridor:scim:1.0:User"],
              "userName": "{{userName}}",
              "displayName": "Rosa Kellerman-Tam",
              "active": true,
              "groups": [],
              "urn:corridor:scim:1.0:User": { "role": "Admin" }
            }
            """));
        var putPayload = JsonNode.Parse(await put.Content.ReadAsStringAsync())!;
        Assert.Equal(System.Net.HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("Rosa Kellerman-Tam", (string?)putPayload["displayName"]);
        Assert.Equal("Admin", (string?)putPayload["urn:corridor:scim:1.0:User"]!["role"]);

        var patchActive = await client.PatchAsync($"/scim/v2/Users/{id}", ScimBody("""
            {
              "schemas": ["urn:ietf:params:scim:api:messages:2.0:PatchOp"],
              "Operations": [{ "op": "replace", "path": "active", "value": false }]
            }
            """));
        var patchPayload = JsonNode.Parse(await patchActive.Content.ReadAsStringAsync())!;
        Assert.Equal(System.Net.HttpStatusCode.OK, patchActive.StatusCode);
        Assert.False(patchPayload["active"]!.GetValue<bool>());

        var patchGroups = await client.PatchAsync($"/scim/v2/Users/{id}", ScimBody("""
            {
              "schemas": ["urn:ietf:params:scim:api:messages:2.0:PatchOp"],
              "Operations": [{ "op": "replace", "path": "groups", "value": [{ "display": "field-inspectors" }] }]
            }
            """));
        var groupsPayload = JsonNode.Parse(await patchGroups.Content.ReadAsStringAsync())!;
        Assert.Equal(System.Net.HttpStatusCode.OK, patchGroups.StatusCode);
        var groups = (JsonArray)groupsPayload["groups"]!;
        var groupName = Assert.Single(groups);
        Assert.Equal("field-inspectors", (string?)groupName!["display"]);

        var patchUnsupported = await client.PatchAsync($"/scim/v2/Users/{id}", ScimBody("""
            {
              "schemas": ["urn:ietf:params:scim:api:messages:2.0:PatchOp"],
              "Operations": [{ "op": "remove", "path": "userName" }]
            }
            """));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, patchUnsupported.StatusCode);
        var errorPayload = JsonNode.Parse(await patchUnsupported.Content.ReadAsStringAsync())!;
        Assert.Equal("400", (string?)errorPayload["status"]);
        Assert.Contains("replace", (string?)errorPayload["detail"]);
    }

    [Fact]
    public async Task Create_Duplicate_UserName_Returns_Scim_Error()
    {
        var client = Client();
        var body = ScimBody("""
            {
              "schemas": ["urn:ietf:params:scim:schemas:core:2.0:User"],
              "userName": "clerk@corridor.example",
              "displayName": "Duplicate Attempt"
            }
            """);

        var response = await client.PostAsync("/scim/v2/Users", body);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("application/scim+json", response.Content.Headers.ContentType!.ToString());
        var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("urn:ietf:params:scim:api:messages:2.0:Error",
            (string?)payload["schemas"]![0]);
        Assert.Equal("400", (string?)payload["status"]);
        Assert.Contains("already provisioned", (string?)payload["detail"]);
    }

    [Fact]
    public async Task Get_Missing_User_Returns_404_Scim_Error()
    {
        var client = Client();
        var response = await client.GetAsync("/scim/v2/Users/does-not-exist");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("404", (string?)payload["status"]);
    }

    [Fact]
    public async Task Endpoints_Reject_Missing_Or_Wrong_Bearer_Token()
    {
        using var anonymous = _factory.CreateClient();
        var missing = await anonymous.GetAsync("/scim/v2/Users");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, missing.StatusCode);

        using var wrong = _factory.CreateClient();
        wrong.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-the-token");
        var rejected = await wrong.GetAsync("/scim/v2/Users");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, rejected.StatusCode);
    }
}
