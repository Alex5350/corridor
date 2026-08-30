using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Corridor.Portal.Auth.Saml;

namespace Corridor.Portal.Tests;

public class SamlValidatorTests
{
    private const string AcsUrl = "http://localhost:5200/saml/acs";
    private static readonly DateTime Now = DateTime.UtcNow;

    private readonly SamlAssertionFactory _factory = new();
    private readonly SamlValidator _validator = new();

    private static X509Certificate2 CreateSigningCertificate(string subject)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private string BuildSignedResponse(X509Certificate2 signingCertificate,
        string audience = AcsUrl,
        DateTime? notOnOrAfter = null,
        string upn = "inspector@corridor.example",
        string role = "Inspector")
    {
        var spec = new SignedAssertionSpec(
            "http://test-idp.example/adfs/services/trust",
            audience,
            upn,
            upn,
            [role],
            Now.AddMinutes(-1),
            notOnOrAfter ?? Now.AddMinutes(10));
        var assertionXml = _factory.BuildSignedAssertion(spec, signingCertificate);
        return _factory.WrapInResponse(assertionXml);
    }

    private static string ToBase64(string xml) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(xml));

    private SamlValidationResult Validate(string xml, params X509Certificate2[] trusted) =>
        _validator.Validate(ToBase64(xml),
            trusted.Select(c => new TrustedCertificate("adfs", c)).ToList(),
            AcsUrl, Now);

    [Fact]
    public void Validate_AcceptsSignedResponseAndReadsClaims()
    {
        var certificate = CreateSigningCertificate("saml-validator-test");

        var result = Validate(BuildSignedResponse(certificate), certificate);

        Assert.True(result.IsValid, result.Error);
        Assert.Equal("inspector@corridor.example", result.Principal!.Upn);
        Assert.Equal("Inspector", Assert.Single(result.Principal.Roles));
        Assert.Equal("adfs", result.Principal.IdentityProvider);
    }

    [Fact]
    public void Validate_RejectsTamperedAssertion()
    {
        var certificate = CreateSigningCertificate("saml-validator-test");
        var response = BuildSignedResponse(certificate)
            .Replace("inspector@corridor.example", "admin@corridor.example", StringComparison.Ordinal);

        var result = Validate(response, certificate);

        Assert.False(result.IsValid);
        Assert.Contains("signature", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Validate_RejectsExpiredOrWrongAudience(bool expire)
    {
        var certificate = CreateSigningCertificate("saml-validator-test");
        var response = expire
            ? BuildSignedResponse(certificate, notOnOrAfter: Now.AddMinutes(-1))
            : BuildSignedResponse(certificate, audience: "http://other-app.example/acs");

        var result = Validate(response, certificate);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_RejectsSignatureFromUntrustedCertificate()
    {
        var signer = CreateSigningCertificate("signer");
        var trusted = CreateSigningCertificate("trusted");

        var result = Validate(BuildSignedResponse(signer), trusted);

        Assert.False(result.IsValid);
        Assert.Contains("signature", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_RejectsUnsignedResponse()
    {
        var certificate = CreateSigningCertificate("saml-validator-test");
        var signed = BuildSignedResponse(certificate);
        var stripped = System.Text.RegularExpressions.Regex.Replace(
            signed,
            "<(?:[A-Za-z0-9]+:)?Signature[^>]*>.*</(?:[A-Za-z0-9]+:)?Signature>",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.Singleline);

        var result = Validate(stripped, certificate);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_ReadsAdfsSimLongClaimTypeUris()
    {
        // adfs-sim sends WS-Fed style claim type URIs and puts the upn in the NameID.
        var certificate = CreateSigningCertificate("saml-validator-test");
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var notBefore = DateTime.UtcNow.AddMinutes(-5).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var notOnOrAfter = DateTime.UtcNow.AddMinutes(60).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var xml = $"""
            <saml:Assertion xmlns:saml="urn:oasis:names:tc:SAML:2.0:assertion" ID="_adfsim001" IssueInstant="{now}" Version="2.0">
              <saml:Issuer>http://test-idp.example/adfs/services/trust</saml:Issuer>
              <saml:Subject>
                <saml:NameID>officer@corridor.example</saml:NameID>
                <saml:SubjectConfirmation Method="urn:oasis:names:tc:SAML:2.0:cm:bearer"/>
              </saml:Subject>
              <saml:Conditions NotBefore="{notBefore}" NotOnOrAfter="{notOnOrAfter}">
                <saml:AudienceRestriction>
                  <saml:Audience>{AcsUrl}</saml:Audience>
                </saml:AudienceRestriction>
              </saml:Conditions>
              <saml:AttributeStatement>
                <saml:Attribute Name="http://schemas.xmlsoap.org/ws/2005/05/identity/claims/upn">
                  <saml:AttributeValue>officer@corridor.example</saml:AttributeValue>
                </saml:Attribute>
                <saml:Attribute Name="http://schemas.microsoft.com/ws/2008/06/identity/claims/role">
                  <saml:AttributeValue>Officer</saml:AttributeValue>
                </saml:Attribute>
              </saml:AttributeStatement>
              <saml:AuthnStatement AuthnInstant="{now}" AuthnContextClassRef="urn:oasis:names:tc:SAML:2.0:ac:classes:Password"/>
            </saml:Assertion>
            """;
        var document = new System.Xml.XmlDocument { PreserveWhitespace = true };
        document.LoadXml(xml);
        var assertion = document.DocumentElement!;
        assertion.InsertAfter(SamlXml.SignElement(document, assertion, certificate), assertion.FirstChild!);
        var response = _factory.WrapInResponse(document.OuterXml);

        var result = Validate(response, certificate);

        Assert.True(result.IsValid, result.Error);
        Assert.Equal("officer@corridor.example", result.Principal!.Upn);
        Assert.Equal("Officer", Assert.Single(result.Principal.Roles));
    }
}
