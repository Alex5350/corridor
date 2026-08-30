using System.Net;
using System.Text;

namespace Corridor.Ops.Tool.Tests;

public class ScimDumpTests
{
    private const string ScimJson =
        """
        {
          "totalResults": 2,
          "Resources": [
            { "userName": "inspector@corridor.example", "active": true, "externalId": "ext-inspector-0001" },
            { "userName": "admin@corridor.example", "active": false }
          ]
        }
        """;

    [Fact]
    public void BuildUrl_AppendsUsersPathToABaseUrl()
    {
        Assert.Equal("http://localhost:8080/scim/v2/Users", ScimDump.BuildUrl("http://localhost:8080"));
        Assert.Equal("http://localhost:8080/scim/v2/Users", ScimDump.BuildUrl("http://localhost:8080/"));
    }

    [Fact]
    public void BuildUrl_HonorsAnAlreadyCompleteEndpoint()
    {
        Assert.Equal(
            "http://localhost:8080/scim/v2/Users",
            ScimDump.BuildUrl("http://localhost:8080/scim/v2/Users"));
    }

    [Fact]
    public void ParseUsers_MapsUserNameActiveAndExternalId()
    {
        var users = ScimDump.ParseUsers(ScimJson);

        Assert.Equal(2, users.Count);
        Assert.Equal("inspector@corridor.example", users[0].UserName);
        Assert.True(users[0].Active);
        Assert.Equal("ext-inspector-0001", users[0].ExternalId);
        Assert.False(users[1].Active);
        // Missing optional fields come back empty rather than null.
        Assert.Equal(string.Empty, users[1].ExternalId);
    }

    [Fact]
    public void ParseUsers_AcceptABareArrayBody()
    {
        const string json = """[{ "userName": "clerk@corridor.example" }]""";

        var users = ScimDump.ParseUsers(json);

        var user = Assert.Single(users);
        Assert.Equal("clerk@corridor.example", user.UserName);
        Assert.Null(user.Active);
    }

    [Fact]
    public void RenderTable_TruncatesLongValuesSafely()
    {
        const string longName = "an-extremely-long-user-name-that-should-not-wreck-the-layout";
        var users = ScimDump.ParseUsers(
            $$"""{"Resources":[{"userName":"{{longName}}","active":true,"externalId":"ext-1"}]}""");

        var rendered = ScimDump.RenderTable(users);

        // userName column is capped at 28: 25 characters plus an ellipsis.
        Assert.Contains(longName.Substring(0, 25) + "...", rendered);
        Assert.DoesNotContain("wreck-the-layout", rendered);
        Assert.Contains("true", rendered);
        Assert.DoesNotContain("\n", rendered.Replace(Environment.NewLine, ""));
    }

    [Fact]
    public async Task FetchAsync_SendsBearerTokenAndReturnsBody()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ScimJson, Encoding.UTF8, "application/scim+json"),
        };
        var handler = new FakeHandler(response);
        using var client = new HttpClient(handler);

        var body = await ScimDump.FetchAsync(
            client, "http://localhost:8080/scim/v2/Users", "corridor-scim-token");

        Assert.Equal(ScimJson, body);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("corridor-scim-token", handler.LastRequest.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task FetchAsync_NonSuccessThrowsWithoutLeakingTheToken()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("{}"),
        };
        var handler = new FakeHandler(response);
        using var client = new HttpClient(handler);

        var failure = await Assert.ThrowsAsync<ScimRequestException>(
            () => ScimDump.FetchAsync(client, "http://localhost:8080/scim/v2/Users", "corridor-scim-token"));

        Assert.Contains("403", failure.Message);
        Assert.DoesNotContain("corridor-scim-token", failure.Message);
    }
}
