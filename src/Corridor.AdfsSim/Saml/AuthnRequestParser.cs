using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Corridor.AdfsSim.Saml;

public sealed record AuthnRequestData(string Id, string Issuer, string? AssertionConsumerServiceUrl, string? Destination);

public sealed record AuthnRequestResult(bool Success, AuthnRequestData? Request, string? Error)
{
    public static AuthnRequestResult Ok(AuthnRequestData request) => new(true, request, null);

    public static AuthnRequestResult Fail(string error) => new(false, null, error);
}

/// <summary>Parses HTTP-POST binding SAMLRequest values: Base64 of a raw-deflated (or
/// plain) samlp:AuthnRequest document. Extracts ID, Issuer, and ACS URL.</summary>
public static class AuthnRequestParser
{
    public static AuthnRequestResult Parse(string? base64Request)
    {
        if (string.IsNullOrWhiteSpace(base64Request))
        {
            return AuthnRequestResult.Fail("The SAMLRequest parameter is missing.");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64Request);
        }
        catch (FormatException)
        {
            return AuthnRequestResult.Fail("The SAMLRequest parameter is not valid Base64.");
        }

        // The POST profile normally sends Deflate-compressed XML. Accept uncompressed
        // XML too: some tooling omits the compression step.
        var xml = TryInflate(bytes) ?? TryRawUtf8(bytes);
        if (xml is null)
        {
            return AuthnRequestResult.Fail("The SAMLRequest parameter does not contain a Deflated or plain XML document.");
        }

        try
        {
            var doc = SamlXml.LoadDocument(xml);
            var root = doc.DocumentElement;
            if (root is null || root.NamespaceURI != SamlXml.ProtocolNs || root.LocalName != "AuthnRequest")
            {
                return AuthnRequestResult.Fail("The SAMLRequest document is not a samlp:AuthnRequest.");
            }

            var id = root.GetAttribute("ID");
            if (string.IsNullOrWhiteSpace(id))
            {
                return AuthnRequestResult.Fail("The AuthnRequest has no ID attribute.");
            }

            var issuer = root["Issuer", SamlXml.AssertionNs]?.InnerText.Trim();
            if (string.IsNullOrWhiteSpace(issuer))
            {
                return AuthnRequestResult.Fail("The AuthnRequest has no saml:Issuer.");
            }

            var acs = root.GetAttribute("AssertionConsumerServiceURL");
            var destination = root.GetAttribute("Destination");

            return AuthnRequestResult.Ok(new AuthnRequestData(id, issuer,
                string.IsNullOrWhiteSpace(acs) ? null : acs,
                string.IsNullOrWhiteSpace(destination) ? null : destination));
        }
        catch (XmlException ex)
        {
            return AuthnRequestResult.Fail("The SAMLRequest XML could not be parsed: " + ex.Message);
        }
    }

    private static string? TryInflate(byte[] bytes)
    {
        try
        {
            using var compressed = new MemoryStream(bytes);
            using var inflater = new DeflateStream(compressed, CompressionMode.Decompress);
            using var output = new MemoryStream();
            inflater.CopyTo(output);
            return Encoding.UTF8.GetString(output.ToArray());
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? TryRawUtf8(byte[] bytes)
    {
        try
        {
            var text = Encoding.UTF8.GetString(bytes);
            return text.TrimStart().StartsWith('<') ? text : null;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}
