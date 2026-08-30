using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml;

namespace Corridor.Portal.Auth.Saml;

public sealed record SignedAssertionSpec(
    string Issuer,
    string Audience,
    string SubjectNameId,
    string? Upn,
    IReadOnlyList<string> Roles,
    DateTime NotBeforeUtc,
    DateTime NotOnOrAfterUtc,
    string? Id = null);

/// <summary>
/// Builds signed SAML 2.0 bearer assertions. The portal uses these as its service credential
/// for the legacy SOAP service in ADFS mode; tests use the same builder with their own RSA key.
/// </summary>
public sealed class SamlAssertionFactory
{
    public string BuildSignedAssertion(SignedAssertionSpec spec, X509Certificate2 signingCertificate)
    {
        var id = spec.Id ?? SamlXml.NewId();
        var instant = FormatUtc(DateTime.UtcNow);
        var xml = new StringBuilder();
        xml.Append($"<saml:Assertion xmlns:saml=\"{SamlXml.AssertionNamespace}\" ID=\"{id}\" IssueInstant=\"{instant}\" Version=\"2.0\">");
        xml.Append($"<saml:Issuer>{Escape(spec.Issuer)}</saml:Issuer>");
        xml.Append("<saml:Subject>");
        xml.Append($"<saml:NameID>{Escape(spec.SubjectNameId)}</saml:NameID>");
        xml.Append("<saml:SubjectConfirmation Method=\"urn:oasis:names:tc:SAML:2.0:cm:bearer\"/>");
        xml.Append("</saml:Subject>");
        xml.Append($"<saml:Conditions NotBefore=\"{FormatUtc(spec.NotBeforeUtc)}\" NotOnOrAfter=\"{FormatUtc(spec.NotOnOrAfterUtc)}\">");
        xml.Append($"<saml:AudienceRestriction><saml:Audience>{Escape(spec.Audience)}</saml:Audience></saml:AudienceRestriction>");
        xml.Append("</saml:Conditions>");
        xml.Append("<saml:AttributeStatement>");
        if (spec.Upn is not null)
        {
            xml.Append($"<saml:Attribute Name=\"upn\"><saml:AttributeValue>{Escape(spec.Upn)}</saml:AttributeValue></saml:Attribute>");
        }
        foreach (var role in spec.Roles)
        {
            xml.Append($"<saml:Attribute Name=\"role\"><saml:AttributeValue>{Escape(role)}</saml:AttributeValue></saml:Attribute>");
        }
        xml.Append("</saml:AttributeStatement>");
        xml.Append($"<saml:AuthnStatement AuthnInstant=\"{instant}\" AuthnContextClassRef=\"urn:oasis:names:tc:SAML:2.0:ac:classes:Password\"/>");
        xml.Append("</saml:Assertion>");

        var document = new XmlDocument { PreserveWhitespace = true };
        document.LoadXml(xml.ToString());
        var assertion = document.DocumentElement!;
        var signature = SamlXml.SignElement(document, assertion, signingCertificate);
        assertion.InsertAfter(signature, assertion.FirstChild!);
        return document.OuterXml;
    }

    /// <summary>Wraps an assertion in a samlp:Response shell the ACS validator accepts.</summary>
    public string WrapInResponse(string assertionXml)
    {
        var document = new XmlDocument { PreserveWhitespace = true };
        document.LoadXml(assertionXml);
        var assertion = document.DocumentElement!;
        var response = document.CreateElement("samlp", "Response", SamlXml.ProtocolNamespace);
        response.SetAttribute("ID", SamlXml.NewId());
        response.SetAttribute("Version", "2.0");
        response.SetAttribute("IssueInstant", FormatUtc(DateTime.UtcNow));
        var status = document.CreateElement("samlp", "Status", SamlXml.ProtocolNamespace);
        var statusCode = document.CreateElement("samlp", "StatusCode", SamlXml.ProtocolNamespace);
        statusCode.SetAttribute("Value", "urn:oasis:names:tc:SAML:2.0:status:Success");
        status.AppendChild(statusCode);
        response.AppendChild(status);
        response.AppendChild(document.ImportNode(assertion, true));
        return response.OuterXml;
    }

    internal static string FormatUtc(DateTime utc)
    {
        return utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static string Escape(string value)
    {
        return System.Security.SecurityElement.Escape(value) ?? value;
    }
}
