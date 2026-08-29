using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;

namespace Corridor.OktaSim.Tests;

/// <summary>
/// SAML IdP mode: metadata parses and exposes the signing certificate; /saml/sso
/// honors the AuthnRequest id (InResponseTo) and answers with a SAMLResponse
/// whose assertion signature verifies against the metadata certificate.
/// </summary>
public class SamlTests(OktaSimFactory factory) : IClassFixture<OktaSimFactory>
{
    private const string AssertionNs = "urn:oasis:names:tc:SAML:2.0:assertion";
    private const string DsNs = "http://www.w3.org/2000/09/xmldsig#";

    private readonly OktaSimFactory _factory = factory;

    private static async Task<X509Certificate2> FetchMetadataCertificateAsync(HttpClient client)
    {
        var metadata = await client.GetAsync("/saml/metadata");
        Assert.Equal(System.Net.HttpStatusCode.OK, metadata.StatusCode);
        Assert.Contains("application/samlmetadata+xml", metadata.Content.Headers.ContentType!.ToString());
        var xml = await metadata.Content.ReadAsStringAsync();

        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(xml);
        Assert.Equal("EntityDescriptor", doc.DocumentElement!.LocalName);
        Assert.Equal("http://localhost:8080", doc.DocumentElement.GetAttribute("entityID"));
        var sso = doc.GetElementsByTagName("SingleSignOnService")[0]!;
        Assert.Equal("http://localhost:8080/saml/sso", sso!.Attributes!["Location"]!.Value);

        var certText = doc.GetElementsByTagName("X509Certificate", "http://www.w3.org/2000/09/xmldsig#")[0]!.InnerText.Trim();
        return X509CertificateLoader.LoadCertificate(Convert.FromBase64String(certText));
    }

    [Fact]
    public async Task Metadata_Parses_And_The_Sso_Response_Verifies()
    {
        var client = _factory.CreateNoRedirectClient();
        var certificate = await FetchMetadataCertificateAsync(client);

        var response = await client.PostAsync("/saml/sso", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["SAMLRequest"] = TestHelpers.DeflatedAuthnRequest("authn-request-17"),
            ["RelayState"] = "relay-91",
            ["login_hint"] = "officer@corridor.example",
        }));

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("text/html", response.Content.Headers.ContentType!.ToString());
        var html = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("http://localhost:5200/saml/acs", ExtractFormAction(html));
        Assert.Equal("relay-91", TestHelpers.HiddenFieldValue(html, "RelayState"));

        var samlXml = Encoding.UTF8.GetString(
            Convert.FromBase64String(TestHelpers.HiddenFieldValue(html, "SAMLResponse")));
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(samlXml);

        var responseElement = doc.DocumentElement!;
        Assert.Equal("Response", responseElement.LocalName);
        Assert.Equal("authn-request-17", responseElement.GetAttribute("InResponseTo"));

        var assertion = (XmlElement)doc.GetElementsByTagName("Assertion", AssertionNs)[0]!;
        var nameId = assertion.GetElementsByTagName("NameID", AssertionNs)[0]!.InnerText;
        Assert.Equal("officer@corridor.example", nameId);
        var subjectConfirmationData = assertion.GetElementsByTagName("SubjectConfirmationData", AssertionNs)[0]!;
        Assert.Equal("authn-request-17", subjectConfirmationData!.Attributes!["InResponseTo"]!.Value);

        Assert.Equal("officer@corridor.example", AssertionAttributeValue(assertion, "upn"));
        Assert.Equal("Officer", AssertionAttributeValue(assertion, "role"));

        // The assertion signature must verify with the metadata certificate.
        var signature = (XmlElement)assertion.GetElementsByTagName("Signature", DsNs)[0]!;
        var verifier = new SignedXml(assertion);
        verifier.LoadXml(signature);
        using var rsa = certificate.GetRSAPublicKey();
        Assert.NotNull(rsa);
        Assert.True(verifier.CheckSignature(rsa), "SAML assertion signature did not verify");

        // Tampering with the assertion must break the signature.
        var tampered = new XmlDocument { PreserveWhitespace = true };
        tampered.LoadXml(samlXml.Replace("Officer</saml:AttributeValue>", "Admin</saml:AttributeValue>"));
        var tamperedAssertion = (XmlElement)tampered.GetElementsByTagName("Assertion", AssertionNs)[0]!;
        var tamperedSignature = (XmlElement)tamperedAssertion.GetElementsByTagName("Signature", DsNs)[0]!;
        var tamperedVerifier = new SignedXml(tamperedAssertion);
        tamperedVerifier.LoadXml(tamperedSignature);
        Assert.False(tamperedVerifier.CheckSignature(rsa), "tampered assertion must not verify");
    }

    [Fact]
    public async Task Sso_Without_A_Usable_User_Or_Request_Returns_A_Clear_Error()
    {
        var client = _factory.CreateNoRedirectClient();

        var missingUser = await client.PostAsync("/saml/sso", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["SAMLRequest"] = TestHelpers.DeflatedAuthnRequest("req-2"),
            ["login_hint"] = "ghost@corridor.example",
        }));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, missingUser.StatusCode);
        Assert.Contains("Unknown or inactive user", await missingUser.Content.ReadAsStringAsync());

        var garbage = await client.PostAsync("/saml/sso", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["SAMLRequest"] = Convert.ToBase64String("<<<not-a-request>>>"u8.ToArray()),
        }));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, garbage.StatusCode);
    }

    [Fact]
    public async Task Sso_Honors_Form_Login_With_The_Demo_Password()
    {
        var client = _factory.CreateNoRedirectClient();
        var response = await client.PostAsync("/saml/sso", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["SAMLRequest"] = TestHelpers.DeflatedAuthnRequest("authn-form-3"),
            ["username"] = "admin@corridor.example",
            ["password"] = "Demo1234!",
        }));

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        var samlXml = Encoding.UTF8.GetString(
            Convert.FromBase64String(TestHelpers.HiddenFieldValue(html, "SAMLResponse")));
        Assert.Contains("admin@corridor.example", samlXml);
        Assert.Contains("Admin", samlXml);
    }

    private static string AssertionAttributeValue(XmlElement assertion, string name)
    {
        foreach (XmlElement attribute in assertion.GetElementsByTagName("Attribute", AssertionNs))
        {
            if (attribute.GetAttribute("Name") == name)
            {
                return attribute.GetElementsByTagName("AttributeValue", AssertionNs)[0]!.InnerText;
            }
        }
        throw new Xunit.Sdk.XunitException($"attribute {name} missing from the assertion");
    }

    private static string ExtractFormAction(string html)
    {
        var marker = "action=\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        return System.Net.WebUtility.HtmlDecode(html[start..html.IndexOf('"', start)]);
    }
}
