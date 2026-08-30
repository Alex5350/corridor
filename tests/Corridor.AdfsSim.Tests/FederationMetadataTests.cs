using System.Xml.Linq;
using Corridor.AdfsSim.Saml;
using Microsoft.Extensions.DependencyInjection;

namespace Corridor.AdfsSim.Tests;

public sealed class FederationMetadataTests
{
    [Fact]
    public void Build_ProducesIdpDescriptor_WithSigningCertificate_PostBinding_AndNameIdFormat()
    {
        var signing = TestSetup.CreateSigningCertificate();
        var xml = FederationMetadata.Build(TestSetup.IdpEntityId, "http://localhost:8090/adfs/ls", signing.Certificate);

        var root = XDocument.Parse(xml).Root!;

        Assert.Equal("EntityDescriptor", root.Name.LocalName);
        Assert.Equal(SamlXml.MetadataNs, root.Name.Namespace.NamespaceName);
        Assert.Equal(TestSetup.IdpEntityId, (string?)root.Attribute("entityID"));

        var idp = root.Element(XName.Get("IDPSSODescriptor", SamlXml.MetadataNs))!;
        Assert.NotNull(idp);
        Assert.Contains("urn:oasis:names:tc:SAML:2.0:protocol", (string?)idp.Attribute("protocolSupportEnumeration"));

        var certText = idp
            .Element(XName.Get("KeyDescriptor", SamlXml.MetadataNs))!
            .Element(XName.Get("KeyInfo", SamlXml.DsNs))!
            .Element(XName.Get("X509Data", SamlXml.DsNs))!
            .Element(XName.Get("X509Certificate", SamlXml.DsNs))!
            .Value.Trim();

        Assert.Equal(
            Convert.ToBase64String(signing.Certificate.RawData),
            certText);

        Assert.NotNull(idp.Element(XName.Get("NameIDFormat", SamlXml.MetadataNs)));

        var sso = idp.Element(XName.Get("SingleSignOnService", SamlXml.MetadataNs))!;
        Assert.Equal("urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST", (string?)sso.Attribute("Binding"));
        Assert.Equal("http://localhost:8090/adfs/ls", (string?)sso.Attribute("Location"));
    }

    [Fact]
    public async Task MetadataEndpoint_ServesWellFormedXml_WithCertificate()
    {
        await using var factory = new AdfsSimFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/federationmetadata/2007-06/federationmetadata.xml");

        response.EnsureSuccessStatusCode();
        Assert.Equal("application/xml", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        var root = XDocument.Parse(body).Root!;
        Assert.Equal("EntityDescriptor", root.Name.LocalName);

        var signing = factory.Services.GetRequiredService<Corridor.AdfsSim.SigningCertificate>();
        Assert.Contains(Convert.ToBase64String(signing.Certificate.RawData), body);
    }
}
