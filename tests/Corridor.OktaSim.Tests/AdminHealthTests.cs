using System.Text.Json.Nodes;

namespace Corridor.OktaSim.Tests;

/// <summary>Admin persona console and liveness endpoint.</summary>
public class AdminHealthTests(OktaSimFactory factory) : IClassFixture<OktaSimFactory>
{
    private readonly OktaSimFactory _factory = factory;

    [Fact]
    public async Task Healthz_Returns_Anonymous_Ok_Status()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/healthz");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("ok", (string?)payload["status"]);
    }

    [Fact]
    public async Task Admin_Console_Renders_Directory_And_App_Tables_Read_Only()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("text/html", response.Content.Headers.ContentType!.ToString());
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("Directory: users", html);
        Assert.Contains("admin@corridor.example", html);
        Assert.Contains("inspector@corridor.example", html);
        Assert.Contains("officer@corridor.example", html);
        Assert.Contains("clerk@corridor.example", html);

        Assert.Contains("Applications", html);
        Assert.Contains("PermitPortal", html);
        Assert.Contains("FieldInsight", html);
        Assert.Contains("TraceLink", html);
        Assert.Contains("IdP assignment", html);

        // Read-only: no client-side scripting anywhere.
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }
}
