using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Corridor.IntegrationTests.Infrastructure;

/// <summary>
/// Client-side SAML plumbing: build a deflated AuthnRequest like the portal does,
/// POST credentials to the adfs-sim SSO endpoint, and read the SAMLResponse out of
/// the auto-submitting return form.
/// </summary>
public static class Saml
{
    public const string PortalEntityId = "http://localhost:5200/saml";
    public const string PortalAcs = "http://localhost:5200/saml/acs";

    public static string NewRequestId() => "_it" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8));

    public static string BuildAuthnRequest(string id, string issuer, string assertionConsumerServiceUrl)
    {
        return $"""
            <samlp:AuthnRequest xmlns:samlp="urn:oasis:names:tc:SAML:2.0:protocol" xmlns:saml="urn:oasis:names:tc:SAML:2.0:assertion" ID="{id}" Version="2.0" IssueInstant="{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}" AssertionConsumerServiceURL="{assertionConsumerServiceUrl}"><saml:Issuer>{issuer}</saml:Issuer></samlp:AuthnRequest>
            """;
    }

    /// <summary>HTTP-POST binding encoding: raw DEFLATE of the XML, then base64.</summary>
    public static string DeflateBase64(string xml)
    {
        using var raw = new MemoryStream();
        using (var deflater = new DeflateStream(raw, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflater.Write(Encoding.UTF8.GetBytes(xml));
        }
        return Convert.ToBase64String(raw.ToArray());
    }

    public static string InflateFromBase64(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        using var compressed = new MemoryStream(bytes);
        using var inflater = new DeflateStream(compressed, CompressionMode.Decompress);
        using var output = new MemoryStream();
        inflater.CopyTo(output);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    /// <summary>Submits credentials plus the AuthnRequest to /adfs/ls and returns the HTML auto-post page.</summary>
    public static async Task<string> PostLoginAsync(
        HttpClient http,
        Uri adfsBase,
        string authnRequestBase64,
        string relayState,
        string username,
        string password)
    {
        using var response = await http.PostAsync(
            new Uri(adfsBase, "/adfs/ls"),
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["SAMLRequest"] = authnRequestBase64,
                ["RelayState"] = relayState,
                ["UserName"] = username,
                ["Password"] = password,
            }));
        var html = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"ADFS login failed HTTP {(int)response.StatusCode}");
        return html;
    }

    /// <summary>Pulls the base64 SAMLResponse out of the auto-submitting form HTML.</summary>
    public static string ResponseFromAutoPostHtml(string html)
    {
        var match = Regex.Match(html, @"name=""SAMLResponse"" value=""(?<value>[^""]+)""");
        Assert.True(match.Success, "The ADFS response page carries no SAMLResponse field.");
        return match.Groups["value"].Value;
    }
}
