using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Corridor.OktaSim.Tests;

/// <summary>PKCE, XACML, and SAML request helpers shared by the test classes.</summary>
public static class TestHelpers
{
    public static string CreatePkceVerifier() =>
        Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    public static string ChallengeFrom(string verifier) =>
        Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string BasicHeader(string clientId, string secret) =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{secret}"));

    /// <summary>A minimal XACML 2.0 context request with role/resource/action.</summary>
    public static string XacmlRequest(string role, string resource, string action) => $"""
        <Request xmlns="urn:oasis:names:tc:xacml:2.0:context:schema:os">
          <Subject>
            <Attribute AttributeId="urn:oasis:names:tc:xacml:2.0:subject:role">
              <AttributeValue>{role}</AttributeValue>
            </Attribute>
          </Subject>
          <Resource>
            <Attribute AttributeId="urn:oasis:names:tc:xacml:1.0:resource:resource-id">
              <AttributeValue>{resource}</AttributeValue>
            </Attribute>
          </Resource>
          <Action>
            <Attribute AttributeId="urn:oasis:names:tc:xacml:1.0:action:action-id">
              <AttributeValue>{action}</AttributeValue>
            </Attribute>
          </Action>
        </Request>
        """;

    /// <summary>An AuthnRequest encoded for the redirect binding (DEFLATE + base64).</summary>
    public static string DeflatedAuthnRequest(string requestId, string acs = "http://localhost:5200/saml/acs")
    {
        var xml = $"""
            <samlp:AuthnRequest xmlns:samlp="urn:oasis:names:tc:SAML:2.0:protocol"
                ID="{requestId}" Version="2.0" IssueInstant="2026-09-01T12:00:00Z"
                Destination="http://localhost:8080/saml/sso"
                AssertionConsumerServiceURL="{acs}">
              <saml:Issuer xmlns:saml="urn:oasis:names:tc:SAML:2.0:assertion">http://localhost:5200/saml/metadata</saml:Issuer>
            </samlp:AuthnRequest>
            """;
        using var output = new MemoryStream();
        using (var deflater = new DeflateStream(output, CompressionLevel.Optimal))
        {
            deflater.Write(Encoding.UTF8.GetBytes(xml));
        }
        return Convert.ToBase64String(output.ToArray());
    }

    /// <summary>Pulls the value of a named hidden input out of an auto-submit form page.</summary>
    public static string HiddenFieldValue(string html, string name)
    {
        var marker = $"name=\"{name}\" value=\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"expected hidden input {name} in the form page");
        start += marker.Length;
        var end = html.IndexOf('"', start);
        return System.Net.WebUtility.HtmlDecode(html[start..end]);
    }
}
