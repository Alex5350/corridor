using System.Net;
using System.Text;
using System.Text.Json;
using Corridor.IntegrationTests.Infrastructure;

namespace Corridor.IntegrationTests;

/// <summary>
/// SCIM 2.0 provisioning against okta-sim with the SQL-backed store: create, filtered
/// list, patch, with every change verified straight in idn.Users.
/// </summary>
[Collection(CorridorStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ScimProvisioningTests(CorridorStackFixture fixture)
{
    private const string ScimToken = "corridor-scim-token";

    [Fact]
    public async Task Scim_CreateFilterAndPatch_AreReflectedInTheSqlDirectory()
    {
        var upn = $"it-{Guid.NewGuid():N}@corridor.example";
        using var http = fixture.CreateHttpClient();

        // Create.
        using var create = await http.SendAsync(CreateRequest(
            new Uri(fixture.OktaBase, "/scim/v2/Users"), HttpMethod.Post, Json(new
            {
                schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:User" },
                userName = upn,
                displayName = "Integration Test User",
                active = true,
                groups = new[] { new { display = "trace-reviewers" } },
            })));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        AssertScimJson(create);
        var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var id = created.RootElement.GetProperty("id").GetString();
        Assert.False(string.IsNullOrEmpty(id));

        var sqlRow = await Sql.RowAsync(fixture.CorridorConnectionString,
            "SELECT Upn, DisplayName, Active FROM idn.Users WHERE ScimExternalId = @id",
            ("@id", id!));
        Assert.Equal(3, sqlRow.Count);
        Assert.Equal(upn, sqlRow[0]);
        Assert.Equal("Integration Test User", sqlRow[1]);
        Assert.Equal("True", sqlRow[2]);

        // Filter by userName.
        var filter = Uri.EscapeDataString($"userName eq \"{upn}\"");
        using var listRequest = new HttpRequestMessage(HttpMethod.Get,
            new Uri(fixture.OktaBase, $"/scim/v2/Users?filter={filter}"));
        listRequest.Headers.Authorization = new("Bearer", ScimToken);
        using var list = await http.SendAsync(listRequest);
        Assert.True(list.IsSuccessStatusCode);
        AssertScimJson(list);
        var listBody = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        Assert.Equal(1, listBody.RootElement.GetProperty("totalResults").GetInt32());
        Assert.Equal(upn, listBody.RootElement.GetProperty("Resources")[0].GetProperty("userName").GetString());

        // Patch active to false.
        using var patch = await http.SendAsync(CreateRequest(
            new Uri(fixture.OktaBase, $"/scim/v2/Users/{id}"), HttpMethod.Patch, Json(new
            {
                schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:PatchOp" },
                Operations = new object[]
                {
                    new { op = "replace", path = "active", value = false },
                },
            })));
        Assert.True(patch.IsSuccessStatusCode);
        AssertScimJson(patch);
        var patched = JsonDocument.Parse(await patch.Content.ReadAsStringAsync());
        Assert.False(patched.RootElement.GetProperty("active").GetBoolean());

        var activeFlag = await Sql.ScalarAsync(fixture.CorridorConnectionString,
            "SELECT CAST(Active AS NVARCHAR(1)) FROM idn.Users WHERE ScimExternalId = @id",
            ("@id", id!));
        Assert.Equal("0", activeFlag);
    }

    [Fact]
    public async Task Scim_WithoutBearerToken_IsRejectedWithScimErrorShape()
    {
        using var http = fixture.CreateHttpClient();
        using var response = await http.GetAsync(new Uri(fixture.OktaBase, "/scim/v2/Users"));
        Assert.Equal(401, (int)response.StatusCode);
        AssertScimJson(response);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("401", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("urn:ietf:params:scim:api:messages:2.0:Error", body.RootElement.GetProperty("schemas")[0].GetString());
    }

    private static string Json(object body) => JsonSerializer.Serialize(body);

    private static HttpRequestMessage CreateRequest(Uri uri, HttpMethod method, string json)
    {
        var request = new HttpRequestMessage(method, uri) { Content = Content(json) };
        request.Headers.Authorization = new("Bearer", ScimToken);
        return request;
    }

    private static StringContent Content(string json)
        => new(json, Encoding.UTF8, "application/scim+json");

    private static void AssertScimJson(HttpResponseMessage response)
        => Assert.StartsWith("application/scim+json", response.Content.Headers.ContentType?.ToString() ?? string.Empty, StringComparison.Ordinal);
}
