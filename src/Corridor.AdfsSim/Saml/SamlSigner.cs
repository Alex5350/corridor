using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;

namespace Corridor.AdfsSim.Saml;

/// <summary>Enveloped XML signatures (RSA + SHA256) over saml:Assertion elements, the
/// shape ADFS itself emits.</summary>
public static class SamlSigner
{
    public static void SignAssertion(XmlDocument document, XmlElement assertionElement, X509Certificate2 certificate)
    {
        var rsa = certificate.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("The adfs-sim signing certificate has no RSA private key.");

        var signedXml = new SignedXml(document)
        {
            SigningKey = rsa,
        };
        var signedInfo = signedXml.SignedInfo ?? throw new InvalidOperationException("SignedXml produced no SignedInfo.");
        signedInfo.CanonicalizationMethod = SignedXml.XmlDsigExcC14NTransformUrl;
        signedInfo.SignatureMethod = SignedXml.XmlDsigRSASHA256Url;

        var reference = new Reference("#" + assertionElement.GetAttribute("ID"))
        {
            DigestMethod = SignedXml.XmlDsigSHA256Url,
        };
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        reference.AddTransform(new XmlDsigExcC14NTransform());
        signedXml.AddReference(reference);

        var keyInfo = new KeyInfo();
        keyInfo.AddClause(new KeyInfoX509Data(certificate, X509IncludeOption.EndCertOnly));
        signedXml.KeyInfo = keyInfo;

        signedXml.ComputeSignature();

        // Schema order inside saml:Assertion: Issuer, Signature, Subject, ...
        var issuer = assertionElement["Issuer", SamlXml.AssertionNs]
            ?? throw new InvalidOperationException("The assertion has no saml:Issuer to anchor the signature after.");
        assertionElement.InsertAfter(signedXml.GetXml(), issuer);
    }
}
