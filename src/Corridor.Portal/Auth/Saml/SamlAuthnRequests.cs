using System.Globalization;
using System.Text;

namespace Corridor.Portal.Auth.Saml;

/// <summary>Builds SP initiated AuthnRequests for the redirect binding to the ADFS simulation.</summary>
public static class SamlAuthnRequests
{
    public static string BuildXml(string issuer, string assertionConsumerServiceUrl, string id, DateTime issueInstantUtc)
    {
        return new StringBuilder()
            .Append($"<samlp:AuthnRequest xmlns:samlp=\"{SamlXml.ProtocolNamespace}\" ")
            .Append($"xmlns:saml=\"{SamlXml.AssertionNamespace}\" ")
            .Append($"ID=\"{id}\" Version=\"2.0\" ")
            .Append($"IssueInstant=\"{issueInstantUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)}\" ")
            .Append($"Destination=\"{{DESTINATION}}\" ")
            .Append($"AssertionConsumerServiceURL=\"{assertionConsumerServiceUrl.AttributeEscape()}\">")
            .Append($"<saml:Issuer>{issuer}</saml:Issuer>")
            .Append("</samlp:AuthnRequest>")
            .ToString();
    }

    public static string BuildRedirectUrl(string ssoEndpoint, string issuer, string assertionConsumerServiceUrl, string relayState)
    {
        var xml = BuildXml(issuer, assertionConsumerServiceUrl, SamlXml.NewId(), DateTime.UtcNow)
            .Replace("{DESTINATION}", ssoEndpoint.AttributeEscape());
        var encoded = Uri.EscapeDataString(SamlXml.DeflateBase64(xml));
        var relay = Uri.EscapeDataString(relayState);
        var separator = ssoEndpoint.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{ssoEndpoint}{separator}SAMLRequest={encoded}&RelayState={relay}";
    }
}

file static class StringEscapes
{
    public static string AttributeEscape(this string value) =>
        System.Security.SecurityElement.Escape(value) ?? value;
}
