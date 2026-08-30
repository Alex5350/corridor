using System.Text;
using System.Xml.Linq;
using Corridor.IntegrationTests.Infrastructure;

namespace Corridor.IntegrationTests;

/// <summary>
/// SAML end to end: adfs-sim metadata, a generated deflated AuthnRequest posted to
/// /adfs/ls, the signed SAMLResponse, and the assertion's acceptance at the portal ACS.
/// </summary>
[Collection(CorridorStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SamlEndToEndTests(CorridorStackFixture fixture)
{
    private static readonly XNamespace SamlAssertNs = "urn:oasis:names:tc:SAML:2.0:assertion";
    private static readonly XNamespace SamlProtocolNs = "urn:oasis:names:tc:SAML:2.0:protocol";
    private static readonly XNamespace SamlMetadataNs = "urn:oasis:names:tc:SAML:2.0:metadata";
    private static readonly XNamespace DsNs = "http://www.w3.org/2000/09/xmldsig#";

    [Fact]
    public async Task Saml_Metadata_DescribesTheIdpWithSigningCertificate()
    {
        using var http = fixture.CreateHttpClient();
        using var response = await http.GetAsync(
            new Uri(fixture.AdfsBase, "/federationmetadata/2007-06/federationmetadata.xml"));
        Assert.True(response.IsSuccessStatusCode);
        var xml = await response.Content.ReadAsStringAsync();
        var doc = XDocument.Parse(xml);
        var root = doc.Root ?? throw new InvalidOperationException("Empty metadata document.");
        Assert.Equal("EntityDescriptor", root.Name.LocalName);
        Assert.Equal("http://localhost:8090/adfs/services/trust", root.Attribute("entityID")?.Value);
        var descriptor = root.Element(SamlMetadataNs + "IDPSSODescriptor");
        Assert.NotNull(descriptor);
        var sso = descriptor?.Element(SamlMetadataNs + "SingleSignOnService");
        Assert.Equal("urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST", sso?.Attribute("Binding")?.Value);
        Assert.EndsWith("/adfs/ls", sso?.Attribute("Location")?.Value, StringComparison.Ordinal);
        var certificate = descriptor?.Descendants(DsNs + "X509Certificate").FirstOrDefault()?.Value;
        Assert.False(string.IsNullOrWhiteSpace(certificate));
    }

    [Fact]
    public async Task Saml_AuthnRequestLogin_YieldsSignedAssertionThePortalAcsAccepts()
    {
        // The portal only consumes SAML while it trusts ADFS (Adfs or Dual).
        await Sql.SetTrustModeAsync(fixture.CorridorConnectionString, "portal", "Adfs");

        var requestId = Saml.NewRequestId();
        var encoded = Saml.DeflateBase64(
            Saml.BuildAuthnRequest(requestId, Saml.PortalEntityId, Saml.PortalAcs));

        using var http = fixture.CreateHttpClient();
        var loginHtml = await Saml.PostLoginAsync(
            http, fixture.AdfsBase, encoded, "/CaseList", "admin@corridor.example", Oidc.DemoPassword);
        var responseBase64 = Saml.ResponseFromAutoPostHtml(loginHtml);
        Assert.NotEmpty(responseBase64);

        var responseXml = Encoding.UTF8.GetString(Convert.FromBase64String(responseBase64));
        var doc = XDocument.Parse(responseXml);
        var response = doc.Root ?? throw new InvalidOperationException("Empty SAML response.");
        Assert.Equal("Response", response.Name.LocalName);
        Assert.Equal(SamlProtocolNs, response.Name.Namespace);
        Assert.Equal(requestId, response.Attribute("InResponseTo")?.Value);
        Assert.Contains("status:Success", responseXml, StringComparison.Ordinal);

        var assertion = response.Element(SamlAssertNs + "Assertion");
        Assert.NotNull(assertion);
        Assert.Equal(Saml.PortalEntityId, assertion?.Descendants(SamlAssertNs + "Audience").FirstOrDefault()?.Value);
        Assert.Equal(
            "admin@corridor.example",
            assertion?.Element(SamlAssertNs + "Subject")?.Element(SamlAssertNs + "NameID")?.Value);
        var attributeNames = assertion?.Descendants(SamlAssertNs + "Attribute")
            .Select(a => a.Attribute("Name")?.Value ?? string.Empty).ToList();
        Assert.NotNull(attributeNames);
        Assert.Contains("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/upn", attributeNames!);
        Assert.Contains("http://schemas.microsoft.com/ws/2008/06/identity/claims/role", attributeNames!);
        Assert.NotNull(assertion?.Element(DsNs + "Signature"));

        // The assertion must clear the portal's own ACS checks: posting it signs the
        // admin into the portal (cookie set, redirect to the local return url).
        using var acsClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        using var acs = await acsClient.PostAsync(
            new Uri(fixture.PortalBase, "/saml/acs"),
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["SAMLResponse"] = responseBase64,
                ["RelayState"] = "/CaseList",
            }));
        Assert.Equal(302, (int)acs.StatusCode);
        Assert.Equal("/CaseList", acs.Headers.Location?.ToString());
        Assert.Contains(acs.Headers.GetValues("Set-Cookie"),
            cookie => cookie.StartsWith(".AspNetCore.Cookies=", StringComparison.Ordinal));
    }
}
