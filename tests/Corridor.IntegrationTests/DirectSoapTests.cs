using Corridor.IntegrationTests.Infrastructure;

namespace Corridor.IntegrationTests;

/// <summary>
/// Raw SOAP 1.1 against TraceLink with hand-built envelopes: the JWT happy path
/// against seeded rows, the garbage-token fault, the wrong-trust-mode fault, and the
/// missing header fault.
/// </summary>
[Collection(CorridorStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DirectSoapTests(CorridorStackFixture fixture)
{
    [Fact]
    public async Task DirectSoap_SearchCases_WithServiceJwt_ReturnsSeededRows()
    {
        await Sql.SetTrustModeAsync(fixture.CorridorConnectionString, "legacy", "Dual");
        try
        {
            var jwt = await Oidc.ClientCredentialsTokenAsync(fixture.OktaBase, Oidc.LegacyClientId, Oidc.LegacySecret);
            using var http = fixture.CreateHttpClient();

            var result = await TraceLinkSoap.CallAsync(
                http, fixture.LegacyBase, "SearchCases",
                TraceLinkSoap.BuildJwtEnvelope(
                    TraceLinkSoap.SearchCasesBody("it@corridor.example", null, 50), jwt));

            var cases = TraceLinkSoap.ReadCases(result);
            Assert.True(cases.Count >= 12, $"Expected the 12 seeded cases, got {cases.Count}");
            Assert.Contains(cases, c => c.CaseNumber == "TRC-100101" && c.Status == "Received");
            Assert.Contains(cases, c => c.CaseNumber == "TRC-100104" && c.Status == "Closed");
        }
        finally
        {
            await Sql.SetTrustModeAsync(fixture.CorridorConnectionString, "legacy", "Adfs");
        }
    }

    [Fact]
    public async Task DirectSoap_GarbageJwt_ProducesInvalidTokenFault()
    {
        await Sql.SetTrustModeAsync(fixture.CorridorConnectionString, "legacy", "Okta");
        try
        {
            using var http = fixture.CreateHttpClient();
            var result = await TraceLinkSoap.CallAsync(
                http, fixture.LegacyBase, "SearchCases",
                TraceLinkSoap.BuildJwtEnvelope(
                    TraceLinkSoap.SearchCasesBody("it@corridor.example", null, 5), "not.a.jwt"));

            Assert.True(result.IsFault, "A garbage token must produce a SOAP fault");
            Assert.Equal("InvalidToken", result.Subcode);
            Assert.Equal(500, result.StatusCode);
        }
        finally
        {
            await Sql.SetTrustModeAsync(fixture.CorridorConnectionString, "legacy", "Adfs");
        }
    }

    [Fact]
    public async Task DirectSoap_JwtWhileTrustModeIsAdfs_ProducesInvalidIdentityModeFault()
    {
        await Sql.SetTrustModeAsync(fixture.CorridorConnectionString, "legacy", "Adfs");
        try
        {
            var jwt = await Oidc.ClientCredentialsTokenAsync(fixture.OktaBase, Oidc.LegacyClientId, Oidc.LegacySecret);
            using var http = fixture.CreateHttpClient();
            var result = await TraceLinkSoap.CallAsync(
                http, fixture.LegacyBase, "SearchCases",
                TraceLinkSoap.BuildJwtEnvelope(
                    TraceLinkSoap.SearchCasesBody("it@corridor.example", null, 5), jwt));

            Assert.True(result.IsFault, "A JWT while the app trusts ADFS only must fault");
            Assert.Equal("InvalidIdentityMode", result.Subcode);
            Assert.Contains("does not accept JWT tokens", result.FaultString, StringComparison.Ordinal);

            // And the mirror image: a SAML assertion while the app trusts Okta only.
            await Sql.SetTrustModeAsync(fixture.CorridorConnectionString, "legacy", "Okta");
            var assertion = await MintPortalServiceAssertionAsync();
            var samlResult = await TraceLinkSoap.CallAsync(
                http, fixture.LegacyBase, "SearchCases",
                TraceLinkSoap.BuildSamlEnvelope(
                    TraceLinkSoap.SearchCasesBody("it@corridor.example", null, 5), assertion));
            Assert.True(samlResult.IsFault, "A SAML assertion while the app trusts Okta only must fault");
            Assert.Equal("InvalidIdentityMode", samlResult.Subcode);
        }
        finally
        {
            await Sql.SetTrustModeAsync(fixture.CorridorConnectionString, "legacy", "Adfs");
        }
    }

    [Fact]
    public async Task DirectSoap_WithoutSecurityHeader_ProducesMissingSecurityHeaderFault()
    {
        using var http = fixture.CreateHttpClient();
        var result = await TraceLinkSoap.CallAsync(
            http, fixture.LegacyBase, "SearchCases",
            TraceLinkSoap.BuildUnsecuredEnvelope(
                TraceLinkSoap.SearchCasesBody("it@corridor.example", null, 5)));

        Assert.True(result.IsFault, "An unsecured call must fault");
        Assert.Equal("MissingSecurityHeader", result.Subcode);
    }

    /// <summary>
    /// The adfs-sim login mints the signed assertion; the legacy side only accepts it in
    /// ADFS or Dual mode, which is exactly what this negative test exercises.
    /// </summary>
    private async Task<string> MintPortalServiceAssertionAsync()
    {
        var requestId = Saml.NewRequestId();
        var encoded = Saml.DeflateBase64(
            Saml.BuildAuthnRequest(requestId, Saml.PortalEntityId, Saml.PortalAcs));
        using var http = fixture.CreateHttpClient();
        var loginHtml = await Saml.PostLoginAsync(
            http, fixture.AdfsBase, encoded, "/", "admin@corridor.example", Oidc.DemoPassword);
        var responseBase64 = Saml.ResponseFromAutoPostHtml(loginHtml);
        var responseXml = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(responseBase64));
        var doc = System.Xml.Linq.XDocument.Parse(responseXml);
        var assertion = doc.Root?.Element(System.Xml.Linq.XNamespace.Get("urn:oasis:names:tc:SAML:2.0:assertion") + "Assertion")
            ?? throw new InvalidOperationException("The SAML response carried no assertion.");
        return assertion.ToString(System.Xml.Linq.SaveOptions.DisableFormatting);
    }
}
