using System.Net.Http.Json;
using System.Text.Json;
using Corridor.IntegrationTests.Infrastructure;

namespace Corridor.IntegrationTests;

/// <summary>
/// The legal transition machine enforced inside trace.usp_UpdateStatus, exercised
/// through the whole stack: the case is created over the portal REST bridge, each
/// transition is applied through the portal's Cases handler (which calls SOAP), and
/// the illegal Closed to UnderReview move surfaces the cor:IllegalTransition subcode.
/// </summary>
[Collection(CorridorStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class StatusTransitionTests(CorridorStackFixture fixture)
{
    [Fact]
    public async Task StatusTransitions_WalkReceivedToClosed_ThenRejectTheIllegalReopen()
    {
        await Sql.SetTrustModeAsync(fixture.CorridorConnectionString, "legacy", "Dual");
        await Sql.SetTrustModeAsync(fixture.CorridorConnectionString, "portal", "Dual");
        try
        {
            var portal = await PortalLogin.SignInViaOktaAsync(
                fixture.PortalBase, fixture.OktaBase, "admin@corridor.example", Oidc.DemoPassword);

            // Create through the REST to SOAP bridge.
            var caseNumber = await CreateCaseViaApiAsync(portal);
            await AssertStatusViaApiAsync(portal, caseNumber, "Received");

            // Legal walk enforced by the stored procedure.
            foreach (var status in new[] { "UnderReview", "Traced", "Closed" })
            {
                var page = await MoveStatusViaCasesPageAsync(portal, caseNumber, status);
                Assert.Contains($"Case {caseNumber} moved to {status}", page, StringComparison.Ordinal);
                await AssertStatusViaApiAsync(portal, caseNumber, status);
            }

            // Closed is terminal: the reopen attempt surfaces the illegal transition subcode.
            var rejected = await MoveStatusViaCasesPageAsync(portal, caseNumber, "UnderReview");
            Assert.Contains("rejected the update", rejected, StringComparison.Ordinal);
            Assert.Contains("cor:IllegalTransition", rejected, StringComparison.Ordinal);
            await AssertStatusViaApiAsync(portal, caseNumber, "Closed");

            var row = await Sql.RowAsync(fixture.CorridorConnectionString,
                "SELECT Status, Disposition FROM trace.TraceCases WHERE CaseNumber = @case",
                ("@case", caseNumber));
            Assert.Equal("Closed", row[0]);
            Assert.StartsWith("Set by admin@corridor.example", row[1], StringComparison.Ordinal);
        }
        finally
        {
            await Sql.SetTrustModeAsync(fixture.CorridorConnectionString, "legacy", "Adfs");
            await Sql.SetTrustModeAsync(fixture.CorridorConnectionString, "portal", "Adfs");
        }
    }

    [Fact]
    public async Task StatusTransitions_UnknownStatus_ProducesValidationFaultOnTheWire()
    {
        await Sql.SetTrustModeAsync(fixture.CorridorConnectionString, "legacy", "Dual");
        try
        {
            var jwt = await Oidc.ClientCredentialsTokenAsync(fixture.OktaBase, Oidc.LegacyClientId, Oidc.LegacySecret);
            using var http = fixture.CreateHttpClient();
            var result = await TraceLinkSoap.CallAsync(
                http, fixture.LegacyBase, "UpdateStatus",
                TraceLinkSoap.BuildJwtEnvelope(
                    TraceLinkSoap.UpdateStatusBody("TRC-100101", "Exploded", "it@corridor.example"), jwt));

            Assert.True(result.IsFault);
            Assert.Equal("UnknownStatus", result.Subcode);
        }
        finally
        {
            await Sql.SetTrustModeAsync(fixture.CorridorConnectionString, "legacy", "Adfs");
        }
    }

    private static async Task<string> CreateCaseViaApiAsync(HttpClient portal)
    {
        using var response = await portal.PostAsJsonAsync(
            "/api/cases",
            new
            {
                licenseeName = "IT Transitions LLC",
                itemDescription = "Transition walk trace item",
                serial = $"TR-{Random.Shared.Next(100000, 999999)}",
            });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Create via /api/cases failed: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("caseNumber").GetString()!;
    }

    private static async Task AssertStatusViaApiAsync(HttpClient portal, string caseNumber, string expected)
    {
        using var response = await portal.GetAsync($"/api/cases/{caseNumber}");
        Assert.True(response.IsSuccessStatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expected, body.RootElement.GetProperty("status").GetString());
    }

    /// <summary>
    /// POSTs the Cases Razor page handler (antiforgery included), the same surface the
    /// migration dashboard operators use; the handler relays to SOAP UpdateStatus.
    /// </summary>
    private static async Task<string> MoveStatusViaCasesPageAsync(HttpClient portal, string caseNumber, string newStatus)
    {
        using var page = await portal.GetAsync("/Cases");
        var html = await page.Content.ReadAsStringAsync();
        var token = HtmlForms.AntiforgeryToken(html);

        using var response = await portal.PostAsync(
            "/Cases?handler=UpdateStatus",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["StatusUpdate.CaseNumber"] = caseNumber,
                ["StatusUpdate.NewStatus"] = newStatus,
            }));
        return await response.Content.ReadAsStringAsync();
    }

}
