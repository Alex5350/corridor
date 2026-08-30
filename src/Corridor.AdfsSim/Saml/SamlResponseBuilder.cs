using System.Globalization;
using System.Text;
using System.Xml;
using Corridor.AdfsSim.Identity;

namespace Corridor.AdfsSim.Saml;

public sealed record IssuedSamlResponse(
    string ResponseXml,
    string ResponseBase64,
    string AssertionXml,
    DateTime IssuedAt,
    DateTime NotBefore,
    DateTime NotOnOrAfter);

/// <summary>Builds and signs the samlp:Response document issued at /adfs/ls. The
/// assertion carries NameID (upn), audience restriction, and upn/role claims; it is
/// signed with the dev certificate (RSA SHA256, exclusive canonicalization).</summary>
public sealed class SamlResponseBuilder
{
    public const string UpnClaim = "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/upn";
    public const string RoleClaim = "http://schemas.microsoft.com/ws/2008/06/identity/claims/role";
    public const string NameIdFormat = "urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress";

    private readonly AdfsSimOptions _options;
    private readonly SigningCertificate _signing;

    public SamlResponseBuilder(Microsoft.Extensions.Options.IOptions<AdfsSimOptions> options, SigningCertificate signing)
    {
        _options = options.Value;
        _signing = signing;
    }

    public IssuedSamlResponse Build(SimUser user, AuthnRequestData authnRequest, RegisteredRelyingParty party)
    {
        var now = DateTime.UtcNow;
        var nowSeconds = new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
        var notBefore = nowSeconds.AddMinutes(-_options.NotBeforeSkewMinutes);
        var notOnOrAfter = nowSeconds.AddMinutes(_options.AssertionLifetimeMinutes);
        var confirmationNotOnOrAfter = nowSeconds.AddMinutes(5);

        var responseId = "_" + Guid.NewGuid().ToString("N");
        var assertionId = "_" + Guid.NewGuid().ToString("N");

        var xml = new StringBuilder();
        xml.Append($"<samlp:Response xmlns:samlp=\"{SamlXml.ProtocolNs}\" xmlns:saml=\"{SamlXml.AssertionNs}\" ");
        xml.Append($"ID=\"{responseId}\" Version=\"2.0\" IssueInstant=\"{Format(nowSeconds)}\" ");
        xml.Append($"Destination=\"{party.AcsUrl}\" InResponseTo=\"{authnRequest.Id}\">");
        xml.Append($"<saml:Issuer>{Encode(_options.EntityId)}</saml:Issuer>");
        xml.Append("<samlp:Status><samlp:StatusCode Value=\"urn:oasis:names:tc:SAML:2.0:status:Success\"/></samlp:Status>");
        xml.Append($"<saml:Assertion ID=\"{assertionId}\" IssueInstant=\"{Format(nowSeconds)}\" Version=\"2.0\">");
        xml.Append($"<saml:Issuer>{Encode(_options.EntityId)}</saml:Issuer>");
        xml.Append("<saml:Subject>");
        xml.Append($"<saml:NameID Format=\"{NameIdFormat}\">{Encode(user.Upn)}</saml:NameID>");
        xml.Append("<saml:SubjectConfirmation Method=\"urn:oasis:names:tc:SAML:2.0:cm:bearer\">");
        xml.Append($"<saml:SubjectConfirmationData InResponseTo=\"{Encode(authnRequest.Id)}\" NotOnOrAfter=\"{Format(confirmationNotOnOrAfter)}\" Recipient=\"{party.AcsUrl}\"/>");
        xml.Append("</saml:SubjectConfirmation>");
        xml.Append("</saml:Subject>");
        xml.Append($"<saml:Conditions NotBefore=\"{Format(notBefore)}\" NotOnOrAfter=\"{Format(notOnOrAfter)}\">");
        xml.Append($"<saml:AudienceRestriction><saml:Audience>{Encode(party.Audience)}</saml:Audience></saml:AudienceRestriction>");
        xml.Append("</saml:Conditions>");
        xml.Append($"<saml:AuthnStatement AuthnInstant=\"{Format(nowSeconds)}\" SessionIndex=\"{responseId}\">");
        xml.Append("<saml:AuthnContext><saml:AuthnContextClassRef>urn:oasis:names:tc:SAML:2.0:ac:classes:PasswordProtectedTransport</saml:AuthnContextClassRef></saml:AuthnContext>");
        xml.Append("</saml:AuthnStatement>");
        xml.Append("<saml:AttributeStatement>");
        AppendAttribute(xml, UpnClaim, user.Upn);
        AppendAttribute(xml, RoleClaim, user.Role);
        xml.Append("</saml:AttributeStatement>");
        xml.Append("</saml:Assertion>");
        xml.Append("</samlp:Response>");

        var doc = SamlXml.LoadDocument(xml.ToString());
        var assertion = (doc.GetElementsByTagName("Assertion", SamlXml.AssertionNs)[0]
            ?? throw new InvalidOperationException("The built response has no assertion."))
            as XmlElement ?? throw new InvalidOperationException("The assertion element could not be resolved.");

        SamlSigner.SignAssertion(doc, assertion, _signing.Certificate);

        var responseXml = SamlXml.ToXmlString(doc);
        // OuterXml of the assertion hoists the inherited namespace declarations, so the
        // assertion can be validated standalone (what the legacy service will consume).
        var assertionXml = assertion.OuterXml;

        return new IssuedSamlResponse(
            responseXml,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(responseXml)),
            assertionXml,
            nowSeconds,
            notBefore,
            notOnOrAfter);
    }

    private static void AppendAttribute(StringBuilder xml, string name, string value)
    {
        xml.Append($"<saml:Attribute Name=\"{name}\" NameFormat=\"urn:oasis:names:tc:SAML:2.0:attrname-format:uri\">");
        xml.Append($"<saml:AttributeValue>{Encode(value)}</saml:AttributeValue>");
        xml.Append("</saml:Attribute>");
    }

    private static string Encode(string value) => System.Net.WebUtility.HtmlEncode(value);

    private static string Format(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
