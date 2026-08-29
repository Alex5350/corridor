using System.Text;
using System.Xml.Linq;
using Corridor.OktaSim.Xacml;
using Microsoft.Extensions.DependencyInjection;

namespace Corridor.OktaSim.Tests;

/// <summary>
/// PDP decisions for the three seeded policies (permit Officers+Admins
/// trace-read, permit Inspectors assignments-write, deny-all else), malformed
/// input handling, and parity between the on-disk and fallback policy sets.
/// </summary>
public class XacmlTests(OktaSimFactory factory) : IClassFixture<OktaSimFactory>
{
    private readonly OktaSimFactory _factory = factory;

    private static readonly XNamespace Ns = "urn:oasis:names:tc:xacml:2.0:context:schema:os";

    private static async Task<XElement> DecideAsync(HttpClient client, string body)
    {
        var response = await client.PostAsync("/pdp/decide",
            new StringContent(body, Encoding.UTF8, "application/xacml+xml"));
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        return XElement.Parse(await response.Content.ReadAsStringAsync());
    }

    private static string Decision(XElement response) =>
        response.Element(Ns + "Result")!.Element(Ns + "Decision")!.Value;

    [Fact]
    public async Task Officer_And_Admin_Read_Of_Trace_Cases_Is_Permitted()
    {
        var client = _factory.CreateClient();

        var officer = await DecideAsync(client, TestHelpers.XacmlRequest("Officer", "trace-cases", "read"));
        Assert.Equal("Permit", Decision(officer));

        var admin = await DecideAsync(client, TestHelpers.XacmlRequest("Admin", "trace-cases", "read"));
        Assert.Equal("Permit", Decision(admin));

        // The permit names the policy that fired.
        Assert.Equal("corridor:policy:trace-read:1",
            officer.Element(Ns + "Result")!.Element(Ns + "PolicyIdReference")!.Value);
    }

    [Fact]
    public async Task Inspector_Write_Of_Assignments_Is_Permitted()
    {
        var client = _factory.CreateClient();
        var decision = await DecideAsync(client, TestHelpers.XacmlRequest("Inspector", "assignments", "write"));
        Assert.Equal("Permit", Decision(decision));
    }

    [Fact]
    public async Task Everyone_Else_Falls_Through_To_Deny_All()
    {
        var client = _factory.CreateClient();

        var clerkReads = await DecideAsync(client, TestHelpers.XacmlRequest("Clerk", "trace-cases", "read"));
        Assert.Equal("Deny", Decision(clerkReads));

        var officerWrites = await DecideAsync(client, TestHelpers.XacmlRequest("Officer", "trace-cases", "write"));
        Assert.Equal("Deny", Decision(officerWrites));

        var inspectorReads = await DecideAsync(client, TestHelpers.XacmlRequest("Inspector", "trace-cases", "read"));
        Assert.Equal("Deny", Decision(inspectorReads));

        var noRole = await DecideAsync(client, TestHelpers.XacmlRequest("Nobody", "trace-cases", "read"));
        Assert.Equal("Deny", Decision(noRole));
    }

    [Fact]
    public async Task Malformed_Request_Returns_Deny_With_Status_Message_Never_A_500()
    {
        var client = _factory.CreateClient();

        var notXml = await client.PostAsync("/pdp/decide",
            new StringContent("this is not xml <<<", Encoding.UTF8, "application/xacml+xml"));
        Assert.Equal(System.Net.HttpStatusCode.OK, notXml.StatusCode);
        var payload = XElement.Parse(await notXml.Content.ReadAsStringAsync());
        Assert.Equal("Deny", Decision(payload));
        Assert.Contains("Malformed XACML request",
            payload.Element(Ns + "Result")!.Element(Ns + "Status")!.Element(Ns + "StatusMessage")!.Value);

        // DTD payloads must be rejected by the hardened parser, still as a Deny.
        var dtd = await client.PostAsync("/pdp/decide", new StringContent(
            """<!DOCTYPE Request [<!ENTITY xxe SYSTEM "file:///etc/passwd">]><Request xmlns="urn:oasis:names:tc:xacml:2.0:context:schema:os"/>""",
            Encoding.UTF8, "application/xacml+xml"));
        Assert.Equal(System.Net.HttpStatusCode.OK, dtd.StatusCode);
        Assert.Equal("Deny", Decision(XElement.Parse(await dtd.Content.ReadAsStringAsync())));

        var empty = await client.PostAsync("/pdp/decide",
            new StringContent(string.Empty, Encoding.UTF8, "application/xacml+xml"));
        Assert.Equal(System.Net.HttpStatusCode.OK, empty.StatusCode);
        Assert.Equal("Deny", Decision(XElement.Parse(await empty.Content.ReadAsStringAsync())));
    }

    [Fact]
    public void Fallback_Policy_Set_Decides_The_Same_As_The_Committed_Files()
    {
        var engine = new PdpEngine(PdpEngine.FallbackPolicies());
        var cases = new[]
        {
            (TestHelpers.XacmlRequest("Officer", "trace-cases", "read"), "Permit"),
            (TestHelpers.XacmlRequest("Admin", "trace-cases", "read"), "Permit"),
            (TestHelpers.XacmlRequest("Inspector", "assignments", "write"), "Permit"),
            (TestHelpers.XacmlRequest("Clerk", "trace-cases", "read"), "Deny"),
            (TestHelpers.XacmlRequest("Inspector", "trace-cases", "read"), "Deny"),
        };
        foreach (var (request, expected) in cases)
        {
            var response = engine.Decide(request);
            Assert.Contains($"<Decision>{expected}</Decision>", response);
        }
    }

    [Fact]
    public async Task Server_Loaded_The_Committed_Policy_Files_From_The_Repo()
    {
        var engine = _factory.Services.GetRequiredService<PdpEngine>();
        Assert.True(engine.PolicyCount >= 3, $"expected the 3 committed policies, loaded {engine.PolicyCount}");
        Assert.Contains("policies/", engine.SourceDescription);
    }
}
