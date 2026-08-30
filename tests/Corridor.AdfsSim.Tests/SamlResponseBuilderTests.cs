using System.Xml;
using Corridor.AdfsSim.Identity;
using Corridor.AdfsSim.Saml;
using Microsoft.Extensions.Options;

namespace Corridor.AdfsSim.Tests;

public sealed class SamlResponseBuilderTests
{
    private readonly SamlResponseBuilder _builder = new(
        Options.Create(TestSetup.DefaultOptions()),
        TestSetup.CreateSigningCertificate());

    private IssuedSamlResponse IssueForAdmin(string requestId = "_req-7") =>
        _builder.Build(new SimUser("admin@corridor.example", "Dana Whitfield", "Admin"), TestSetup.PortalRequest(requestId), TestSetup.PortalParty());

    [Fact]
    public void Build_ProducesSignedAssertion_ThatValidatesWithTheCertificate()
    {
        var issued = IssueForAdmin();

        var result = SamlValidator.ValidateAssertion(
            issued.AssertionXml,
            TestSetup.PortalIssuer,
            now: issued.IssuedAt,
            trustedCertificate: TestSetup.CreateSigningCertificate().Certificate);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Equal("admin@corridor.example", result.NameId);
        Assert.Equal(SamlResponseBuilder.NameIdFormat, result.NameIdFormat);
        Assert.Equal(TestSetup.IdpEntityId, result.Issuer);
        Assert.Equal(TestSetup.PortalIssuer, result.Audience);
    }

    [Fact]
    public void Build_CarriesInResponseTo_Destination_AndSuccessStatus()
    {
        var issued = IssueForAdmin(requestId: "_req-abc");

        var doc = new XmlDocument();
        doc.LoadXml(issued.ResponseXml);
        var root = doc.DocumentElement!;

        Assert.Equal("Response", root.LocalName);
        Assert.Equal("2.0", root.GetAttribute("Version"));
        Assert.Equal("_req-abc", root.GetAttribute("InResponseTo"));
        Assert.Equal(TestSetup.PortalAcs, root.GetAttribute("Destination"));

        var statusCode = root["Status", SamlXml.ProtocolNs]?["StatusCode", SamlXml.ProtocolNs];
        Assert.Equal("urn:oasis:names:tc:SAML:2.0:status:Success", statusCode?.GetAttribute("Value"));

        var issuer = root["Issuer", SamlXml.AssertionNs];
        Assert.Equal(TestSetup.IdpEntityId, issuer?.InnerText);
    }

    [Fact]
    public void Build_AppliesContractClockSkew_AndLifetimeWindow()
    {
        var before = DateTime.UtcNow;
        var issued = IssueForAdmin();
        var after = DateTime.UtcNow;

        // NotBefore is back-dated by the 5 minute skew allowance.
        Assert.True(issued.NotBefore <= before.AddMinutes(-4.9), $"NotBefore {issued.NotBefore:O} was not back-dated.");
        Assert.True(issued.NotBefore >= after.AddMinutes(-5.1), $"NotBefore {issued.NotBefore:O} drifted too far back.");

        // NotOnOrAfter covers the 60 minute token lifetime.
        Assert.True(issued.NotOnOrAfter >= before.AddMinutes(59.9), $"NotOnOrAfter {issued.NotOnOrAfter:O} is short of 60 minutes.");
        Assert.True(issued.NotOnOrAfter <= after.AddMinutes(60.1), $"NotOnOrAfter {issued.NotOnOrAfter:O} exceeds 60 minutes.");
    }

    [Fact]
    public void Build_StampsUpnAndRoleClaims()
    {
        var issued = IssueForAdmin();

        var doc = new XmlDocument();
        doc.LoadXml(issued.AssertionXml);
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("saml", SamlXml.AssertionNs);

        var attributes = doc.SelectNodes("//saml:AttributeStatement/saml:Attribute", ns)!
            .Cast<XmlElement>()
            .ToDictionary(
                a => a.GetAttribute("Name"),
                a => a["AttributeValue", SamlXml.AssertionNs]!.InnerText);

        Assert.Equal("admin@corridor.example", attributes[SamlResponseBuilder.UpnClaim]);
        Assert.Equal("Admin", attributes[SamlResponseBuilder.RoleClaim]);
    }

    [Fact]
    public void Build_Base64Payload_RoundTripsToTheResponseXml()
    {
        var issued = IssueForAdmin();

        var decoded = SamlTestParsing.Decode(issued.ResponseBase64);

        Assert.StartsWith("<samlp:Response", decoded);
        Assert.Equal(issued.ResponseXml, decoded);
    }
}
