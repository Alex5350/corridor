using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Corridor.Ops.Tool.Tests;

public class MetadataParserTests
{
    private static (X509Certificate2 Certificate, string Xml) BuildAdfsMetadata()
    {
        using var rsaKey = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=ops-tests.corridor.local", rsaKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
        var xml = $"""
            <EntityDescriptor xmlns="urn:oasis:names:tc:SAML:2.0:metadata" entityID="http://adfs-sim.corridor.local/adfs/services/trust">
              <IDPSSODescriptor protocolSupportEnumeration="urn:oasis:names:tc:SAML:2.0:protocol">
                <KeyDescriptor use="signing">
                  <KeyInfo xmlns="http://www.w3.org/2000/09/xmldsig#">
                    <X509Data><X509Certificate>{Convert.ToBase64String(certificate.RawData)}</X509Certificate></X509Data>
                  </KeyInfo>
                </KeyDescriptor>
                <SingleSignOnService Binding="urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST" Location="http://localhost:8090/adfs/ls"/>
              </IDPSSODescriptor>
            </EntityDescriptor>
            """;
        return (certificate, xml);
    }

    private const string DiscoveryJson =
        """
        {
          "issuer": "http://localhost:8080",
          "authorization_endpoint": "http://localhost:8080/authorize",
          "token_endpoint": "http://localhost:8080/token",
          "jwks_uri": "http://localhost:8080/jwks"
        }
        """;

    [Fact]
    public void ParseAdfsMetadata_ExtractsEntityEndpointsAndThumbprint()
    {
        var (certificate, xml) = BuildAdfsMetadata();
        using var _ = certificate;

        var parsed = MetadataParser.ParseAdfsMetadata(xml);

        Assert.Equal("http://adfs-sim.corridor.local/adfs/services/trust", parsed.EntityId);
        Assert.Equal("http://localhost:8090/adfs/ls", parsed.SingleSignOnEndpoint);
        Assert.Equal("urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST", parsed.Binding);
        Assert.Equal(certificate.Thumbprint, parsed.CertificateThumbprint);
        Assert.Contains("CN=ops-tests.corridor.local", parsed.CertificateSubject);
    }

    [Fact]
    public void ParseAdfsMetadata_RejectsWrongRootElement()
    {
        Assert.Throws<MetadataInvalidException>(() => MetadataParser.ParseAdfsMetadata("<Thing/>"));
    }

    [Fact]
    public void ParseAdfsMetadata_RejectsMalformedXml()
    {
        Assert.Throws<MetadataInvalidException>(
            () => MetadataParser.ParseAdfsMetadata("<EntityDescriptor><IDPSSODescriptor>"));
    }

    [Fact]
    public void ParseAdfsMetadata_RejectsDtd()
    {
        // Entity expansion attempts must die at the parser, never evaluate.
        const string xml =
            """<?xml version="1.0"?><!DOCTYPE EntityDescriptor [<!ENTITY xxe "http://evil.example">]><EntityDescriptor entityID="&xxe;"></EntityDescriptor>""";

        Assert.Throws<MetadataInvalidException>(() => MetadataParser.ParseAdfsMetadata(xml));
    }

    [Fact]
    public void ParseAdfsMetadata_RejectsMissingEntityId()
    {
        const string xml =
            """<EntityDescriptor xmlns="urn:oasis:names:tc:SAML:2.0:metadata"><IDPSSODescriptor/></EntityDescriptor>""";

        var failure = Assert.Throws<MetadataInvalidException>(() => MetadataParser.ParseAdfsMetadata(xml));
        Assert.Contains("entityID", failure.Message);
    }

    [Fact]
    public void ParseDiscovery_ExtractsIssuerAndEndpoints()
    {
        var parsed = MetadataParser.ParseDiscovery(DiscoveryJson);

        Assert.Equal("http://localhost:8080", parsed.Issuer);
        Assert.Equal("http://localhost:8080/authorize", parsed.AuthorizationEndpoint);
        Assert.Equal("http://localhost:8080/token", parsed.TokenEndpoint);
        Assert.Equal("http://localhost:8080/jwks", parsed.JwksUri);
    }

    [Fact]
    public void ParseDiscovery_RejectsMissingIssuer()
    {
        const string json = """{"jwks_uri":"http://localhost:8080/jwks"}""";

        Assert.Throws<MetadataInvalidException>(() => MetadataParser.ParseDiscovery(json));
    }

    [Fact]
    public void ParseDiscovery_RejectsMalformedJson()
    {
        Assert.Throws<MetadataInvalidException>(() => MetadataParser.ParseDiscovery("{not json"));
    }

    [Fact]
    public void ParseJwksKids_ListsEveryKid()
    {
        const string jwks =
            """{"keys":[{"kty":"RSA","kid":"okta-sim-2026-08","n":"AA","e":"AQAB"},{"kty":"RSA","kid":"okta-sim-2026-02","n":"AA","e":"AQAB"}]}""";

        var kids = MetadataParser.ParseJwksKids(jwks);

        Assert.Equal(new[] { "okta-sim-2026-08", "okta-sim-2026-02" }, kids);
    }

    [Fact]
    public void ForMetadataFailure_MapsExceptionsToExitCodes()
    {
        Assert.Equal(ExitCodes.InvalidMetadata, ExitCodes.ForMetadataFailure(new MetadataInvalidException("bad")));
        Assert.Equal(ExitCodes.InvalidMetadata, ExitCodes.ForMetadataFailure(new System.Text.Json.JsonException("bad")));
        Assert.Equal(ExitCodes.InvalidMetadata, ExitCodes.ForMetadataFailure(new System.Xml.XmlException("bad")));
        Assert.Equal(ExitCodes.Unreachable, ExitCodes.ForMetadataFailure(new HttpRequestException("down")));
        Assert.Equal(ExitCodes.Unreachable, ExitCodes.ForMetadataFailure(new TaskCanceledException("timeout")));
        Assert.Equal(ExitCodes.Usage, ExitCodes.ForMetadataFailure(new ArgumentException("odd")));
    }
}
