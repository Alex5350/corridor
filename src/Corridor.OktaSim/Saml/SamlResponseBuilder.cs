using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;

namespace Corridor.OktaSim.Saml;

/// <summary>
/// Builds signed SAML 2.0 responses for the IdP side of the dual-trust demo.
/// The assertion carries NameID (upn) plus upn/role/displayName attributes and
/// is signed with SignedXml (rsa-sha256, enveloped, exclusive C14N) using the
/// development certificate derived from the committed signing PEM.
/// </summary>
public sealed class SamlResponseBuilder
{
    public const string PortalAcs = "http://localhost:5200/saml/acs";
    public const string PortalSpEntityId = "http://localhost:5200/saml/metadata";

    private static readonly string AssertionNs = "urn:oasis:names:tc:SAML:2.0:assertion";
    private static readonly string ProtocolNs = "urn:oasis:names:tc:SAML:2.0:protocol";

    private readonly X509Certificate2 _certificate;
    private readonly RSA _privateKey;
    private readonly string _issuer;

    public SamlResponseBuilder(X509Certificate2 certificate, RSA privateKey, string issuer)
    {
        _certificate = certificate;
        _privateKey = privateKey;
        _issuer = issuer;
    }

    public sealed record SamlSubject(string Upn, string DisplayName, string Role);

    /// <summary>
    /// Produces the signed SAMLResponse XML. InResponseTo honors the AuthnRequest
    /// id; conditions mirror the contract's realistic handling of clock skew
    /// (NotBefore 5 minutes in the past, NotOnOrAfter 40 minutes out).
    /// </summary>
    public string Build(SamlSubject subject, string acsUrl, string? inResponseTo)
    {
        var now = DateTime.UtcNow;
        var responseId = "_" + Guid.NewGuid().ToString("N");
        var assertionId = "_" + Guid.NewGuid().ToString("N");
        var sessionId = "_" + Guid.NewGuid().ToString("N");
        var issueInstant = FormatInstant(now);
        var notBefore = FormatInstant(now.AddMinutes(-5));
        var notOnOrAfter = FormatInstant(now.AddMinutes(40));

        var xml = new StringBuilder();
        xml.Append($"<samlp:Response xmlns:samlp=\"{ProtocolNs}\" xmlns:saml=\"{AssertionNs}\" ");
        xml.Append($"ID=\"{responseId}\" Version=\"2.0\" IssueInstant=\"{issueInstant}\" Destination=\"{acsUrl}\"");
        if (inResponseTo is not null)
        {
            xml.Append($" InResponseTo=\"{XmlEscape(inResponseTo)}\"");
        }
        xml.Append('>');
        xml.Append($"<saml:Issuer>{XmlEscape(_issuer)}</saml:Issuer>");
        xml.Append("<samlp:Status><samlp:StatusCode Value=\"urn:oasis:names:tc:SAML:2.0:status:Success\"/></samlp:Status>");
        xml.Append($"<saml:Assertion ID=\"{assertionId}\" Version=\"2.0\" IssueInstant=\"{issueInstant}\">");
        xml.Append($"<saml:Issuer>{XmlEscape(_issuer)}</saml:Issuer>");
        xml.Append("<saml:Subject>");
        xml.Append($"<saml:NameID Format=\"urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress\">{XmlEscape(subject.Upn)}</saml:NameID>");
        xml.Append("<saml:SubjectConfirmation Method=\"urn:oasis:names:tc:SAML:2.0:cm:bearer\">");
        xml.Append($"<saml:SubjectConfirmationData Recipient=\"{acsUrl}\"");
        if (inResponseTo is not null)
        {
            xml.Append($" InResponseTo=\"{XmlEscape(inResponseTo)}\"");
        }
        xml.Append($" NotOnOrAfter=\"{notOnOrAfter}\"/>");
        xml.Append("</saml:SubjectConfirmation>");
        xml.Append("</saml:Subject>");
        xml.Append($"<saml:Conditions NotBefore=\"{notBefore}\" NotOnOrAfter=\"{notOnOrAfter}\">");
        xml.Append($"<saml:AudienceRestriction><saml:Audience>{XmlEscape(PortalSpEntityId)}</saml:Audience></saml:AudienceRestriction>");
        xml.Append("</saml:Conditions>");
        xml.Append($"<saml:AuthnStatement AuthnInstant=\"{issueInstant}\" SessionIndex=\"{sessionId}\">");
        xml.Append("<saml:AuthnContext><saml:AuthnContextClassRef>urn:oasis:names:tc:SAML:2.0:ac:classes:PasswordProtectedTransport</saml:AuthnContextClassRef></saml:AuthnContext>");
        xml.Append("</saml:AuthnStatement>");
        xml.Append("<saml:AttributeStatement>");
        AppendAttribute(xml, "upn", subject.Upn);
        AppendAttribute(xml, "role", subject.Role);
        AppendAttribute(xml, "displayName", subject.DisplayName);
        xml.Append("</saml:AttributeStatement>");
        xml.Append("</saml:Assertion>");
        xml.Append("</samlp:Response>");

        return SignAssertion(xml.ToString(), assertionId);
    }

    private static void AppendAttribute(StringBuilder xml, string name, string value)
    {
        xml.Append($"<saml:Attribute Name=\"{name}\">");
        xml.Append($"<saml:AttributeValue xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:type=\"xs:string\">{XmlEscape(value)}</saml:AttributeValue>");
        xml.Append("</saml:Attribute>");
    }

    private string SignAssertion(string responseXml, string assertionId)
    {
        var doc = SafeXml.LoadDocument(responseXml);
        var assertion = (XmlElement)doc.GetElementsByTagName("Assertion", AssertionNs)[0]!;

        var signed = new SignedXml(doc)
        {
            SigningKey = _privateKey,
        };
        var signedInfo = signed.SignedInfo
            ?? throw new InvalidOperationException("SignedXml did not initialize SignedInfo.");
        signedInfo.CanonicalizationMethod = SignedXml.XmlDsigExcC14NTransformUrl;
        signedInfo.SignatureMethod = SignedXml.XmlDsigRSASHA256Url;
        var reference = new Reference($"#{assertionId}");
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        reference.AddTransform(new XmlDsigExcC14NTransform());
        signed.AddReference(reference);

        var keyInfo = new KeyInfo();
        keyInfo.AddClause(new KeyInfoX509Data(_certificate, X509IncludeOption.EndCertOnly));
        signed.KeyInfo = keyInfo;

        signed.ComputeSignature();
        var signature = signed.GetXml();

        // Convention: the signature follows the Issuer inside the assertion.
        var issuer = (XmlElement?)assertion.GetElementsByTagName("Issuer", AssertionNs)[0];
        if (issuer is not null)
        {
            assertion.InsertAfter(doc.ImportNode(signature, deep: true), issuer);
        }
        else
        {
            assertion.AppendChild(doc.ImportNode(signature, deep: true));
        }
        return doc.OuterXml;
    }

    private static string FormatInstant(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ssZ");

    private static string XmlEscape(string value) => WebUtility.HtmlEncode(value);
}
