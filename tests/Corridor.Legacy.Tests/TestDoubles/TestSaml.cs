using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;

namespace Corridor.Legacy.Tests.TestDoubles;

/// <summary>
/// Builds SAML 2.0 assertions shaped like the ones adfs-sim issues (NameID,
/// upn and role claims, audience restriction, 60 minute lifetime) and signs
/// them with an in-test RSA certificate. Happy path and every tampering
/// variant are produced from the same builder.
/// </summary>
public static class TestSaml
{
    public const string AssertionNamespace = "urn:oasis:names:tc:SAML:2.0:assertion";
    public const string Issuer = "http://adfs-sim.corridor.local/adfs/services/trust";

    public static X509Certificate2 CreateSigningCertificate()
    {
        using RSA rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=corridor-test-adfs", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddDays(-1), now.AddDays(2));
    }

    public static string BuildAssertion(
        string audience,
        string upn,
        DateTime notBeforeUtc,
        DateTime notOnOrAfterUtc,
        bool sign,
        X509Certificate2? signingCertificate = null)
    {
        string id = "_t" + Guid.NewGuid().ToString("N");
        string xml =
            $$"""
            <saml:Assertion xmlns:saml="{{AssertionNamespace}}" Version="2.0" ID="{{id}}" IssueInstant="{{notBeforeUtc:yyyy-MM-ddTHH:mm:ssZ}}">
              <saml:Issuer>{{Issuer}}</saml:Issuer>
              <saml:Subject><saml:NameID>{{upn}}</saml:NameID></saml:Subject>
              <saml:Conditions NotBefore="{{notBeforeUtc:yyyy-MM-ddTHH:mm:ssZ}}" NotOnOrAfter="{{notOnOrAfterUtc:yyyy-MM-ddTHH:mm:ssZ}}">
                <saml:AudienceRestriction><saml:Audience>{{audience}}</saml:Audience></saml:AudienceRestriction>
              </saml:Conditions>
              <saml:AttributeStatement>
                <saml:Attribute Name="upn"><saml:AttributeValue>{{upn}}</saml:AttributeValue></saml:Attribute>
                <saml:Attribute Name="role"><saml:AttributeValue>Officer</saml:AttributeValue></saml:Attribute>
              </saml:AttributeStatement>
            </saml:Assertion>
            """;

        var document = new XmlDocument { XmlResolver = null };
        document.LoadXml(xml);
        if (sign)
        {
            if (signingCertificate is null)
            {
                throw new ArgumentException("A signing certificate is required to sign the assertion.", nameof(signingCertificate));
            }

            Sign(document, signingCertificate);
        }

        return document.OuterXml;
    }

    private static void Sign(XmlDocument document, X509Certificate2 certificate)
    {
        XmlElement assertion = document.DocumentElement!;
        var signedXml = new SignedXml(assertion)
        {
            SigningKey = certificate.GetRSAPrivateKey()
        };

        var reference = new Reference("#" + assertion.GetAttribute("ID"));
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        reference.AddTransform(new XmlDsigExcC14NTransform());
        signedXml.AddReference(reference);
        signedXml.SignedInfo!.CanonicalizationMethod = SignedXml.XmlDsigExcC14NTransformUrl;

        var keyInfo = new KeyInfo();
        keyInfo.AddClause(new KeyInfoX509Data(certificate));
        signedXml.KeyInfo = keyInfo;

        signedXml.ComputeSignature();
        XmlElement signature = signedXml.GetXml()!;
        XmlElement issuer = (XmlElement)assertion.GetElementsByTagName("Issuer", AssertionNamespace)[0]!;
        assertion.InsertAfter(document.ImportNode(signature, true), issuer);
    }
}
