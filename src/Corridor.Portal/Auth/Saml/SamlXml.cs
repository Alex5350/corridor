using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;

namespace Corridor.Portal.Auth.Saml;

/// <summary>Shared SAML 2.0 XML plumbing: namespaces, raw deflate encoding, and enveloped signing.</summary>
public static class SamlXml
{
    public const string ProtocolNamespace = "urn:oasis:names:tc:SAML:2.0:protocol";
    public const string AssertionNamespace = "urn:oasis:names:tc:SAML:2.0:assertion";
    public const string SignatureNamespace = "http://www.w3.org/2000/09/xmldsig#";

    public static XmlDocument LoadDocument(byte[] bytes)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = false,
            IgnoreWhitespace = false
        };
        using var reader = XmlReader.Create(new MemoryStream(bytes), settings);
        var document = new XmlDocument { PreserveWhitespace = true };
        document.Load(reader);
        return document;
    }

    /// <summary>SAML redirect binding encoding: raw DEFLATE, then base64.</summary>
    public static string DeflateBase64(string xml)
    {
        var raw = Encoding.UTF8.GetBytes(xml);
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(raw);
        }
        return Convert.ToBase64String(output.ToArray());
    }

    /// <summary>
    /// Adds an enveloped RSA SHA256 signature over the element. Canonicalization is exclusive
    /// C14N so the digest stays identical whether the element travels alone, inside a
    /// samlp:Response, or inside the SOAP Security header of the legacy hop.
    /// </summary>
    public static XmlElement SignElement(XmlDocument document, XmlElement element, X509Certificate2 signingCertificate)
    {
        var id = ReadIdAttribute(element) ?? throw new InvalidOperationException("The element to sign has no ID attribute.");
        var key = signingCertificate.GetRSAPrivateKey() ?? throw new InvalidOperationException("The signing certificate has no RSA private key.");
        var signedXml = new SignedXml(document)
        {
            SigningKey = key
        };
        var signedInfo = signedXml.SignedInfo
            ?? throw new InvalidOperationException("SignedXml produced no SignedInfo.");
        signedInfo.CanonicalizationMethod = SignedXml.XmlDsigExcC14NTransformUrl;
        signedInfo.SignatureMethod = SignedXml.XmlDsigRSASHA256Url;
        var reference = new Reference("#" + id) { DigestMethod = SignedXml.XmlDsigSHA256Url };
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        reference.AddTransform(new XmlDsigExcC14NTransform());
        signedXml.AddReference(reference);
        var keyInfo = new KeyInfo();
        keyInfo.AddClause(new KeyInfoX509Data(signingCertificate, X509IncludeOption.EndCertOnly));
        signedXml.KeyInfo = keyInfo;
        signedXml.ComputeSignature();
        return signedXml.GetXml()!;
    }

    public static string? ReadIdAttribute(XmlElement element)
    {
        foreach (var name in new[] { "ID", "Id", "id" })
        {
            var value = element.GetAttribute(name);
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }
        return null;
    }

    public static string NewId()
    {
        return "_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    }
}
