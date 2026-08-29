using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml;
using Corridor.OktaSim.Saml;
using Corridor.OktaSim.Services;
using Corridor.OktaSim.Stores;

namespace Corridor.OktaSim.Endpoints;

/// <summary>
/// SAML 2.0 IdP mode used by the portal during dual trust. Metadata publishes the
/// development signing certificate; /saml/sso accepts an AuthnRequest (redirect
/// binding: DEFLATE+base64, or POST binding: base64 XML) and answers with an
/// auto-submitting form carrying the signed SAMLResponse to the portal ACS.
/// </summary>
public static class SamlEndpoints
{
    public static IEndpointRouteBuilder MapSamlEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/saml/metadata", (SigningKeys keys, TokenService tokens) =>
        {
            var certBase64 = Convert.ToBase64String(
                keys.SamlCertificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Cert));
            var xml = $"""
                <EntityDescriptor xmlns="urn:oasis:names:tc:SAML:2.0:metadata" entityID="{WebUtility.HtmlEncode(tokens.Issuer)}">
                  <IDPSSODescriptor protocolSupportEnumeration="urn:oasis:names:tc:SAML:2.0:protocol">
                    <KeyDescriptor use="signing">
                      <ds:KeyInfo xmlns:ds="http://www.w3.org/2000/09/xmldsig#">
                        <ds:X509Data><ds:X509Certificate>{certBase64}</ds:X509Certificate></ds:X509Data>
                      </ds:KeyInfo>
                    </KeyDescriptor>
                    <NameIDFormat>urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress</NameIDFormat>
                    <SingleSignOnService Binding="urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST" Location="{WebUtility.HtmlEncode(tokens.Issuer)}/saml/sso"/>
                  </IDPSSODescriptor>
                </EntityDescriptor>
                """;
            return Results.Content(xml, "application/samlmetadata+xml");
        });

        app.MapPost("/saml/sso", PostSsoAsync);

        return app;
    }

    private static async Task<IResult> PostSsoAsync(
        HttpContext context,
        SigningKeys keys,
        TokenService tokens,
        IUserStore users,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Saml.Sso");
        var form = await context.Request.ReadFormAsync();
        var samlRequest = form["SAMLRequest"].ToString();
        if (string.IsNullOrEmpty(samlRequest))
        {
            return SamlError("Missing SAMLRequest field.");
        }

        string requestId;
        string? acsFromRequest;
        try
        {
            (requestId, acsFromRequest) = ParseAuthnRequest(samlRequest);
        }
        catch (Exception ex) when (ex is FormatException or XmlException or InvalidOperationException)
        {
            logger.LogInformation("AuthnRequest rejected: {Reason}", ex.Message);
            return SamlError("AuthnRequest could not be decoded or parsed.");
        }

        // Demo login hint selects the synthetic user; the shared demo password is
        // also accepted via username/password fields for interactive flows.
        var user = await ResolveUserAsync(form, users);
        if (user is null || !user.Active)
        {
            return SamlError("Unknown or inactive user: pass login_hint (or username/password) for a seeded demo user.");
        }

        var acs = string.IsNullOrEmpty(acsFromRequest) ? SamlResponseBuilder.PortalAcs : acsFromRequest;
        var builder = new SamlResponseBuilder(
            keys.SamlCertificate,
            keys.Current.Rsa!,
            tokens.Issuer);
        var responseXml = builder.Build(
            new SamlResponseBuilder.SamlSubject(user.UserName, user.DisplayName, user.Role),
            acs,
            requestId);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(responseXml));
        var relayState = WebUtility.HtmlEncode(form["RelayState"].ToString());

        logger.LogInformation(
            "SAML response issued: user {Upn}, InResponseTo {RequestId}, acs {Acs}",
            user.UserName, requestId, acs);

        var html = $"""
            <!doctype html>
            <html lang="en">
            <head><meta charset="utf-8"><title>Returning to the service provider</title></head>
            <body onload="document.forms[0].submit()">
            <p>Continuing to the service provider...</p>
            <form method="post" action="{WebUtility.HtmlEncode(acs)}">
            <input type="hidden" name="SAMLResponse" value="{WebUtility.HtmlEncode(encoded)}">
            <input type="hidden" name="RelayState" value="{relayState}">
            <noscript><button type="submit">Continue</button></noscript>
            </form>
            </body>
            </html>
            """;
        return Results.Content(html, "text/html; charset=utf-8");
    }

    private static async Task<Models.DirectoryUser?> ResolveUserAsync(IFormCollection form, IUserStore users)
    {
        var loginHint = form["login_hint"].ToString();
        if (!string.IsNullOrEmpty(loginHint))
        {
            return await users.FindByUserNameAsync(loginHint);
        }
        var username = form["username"].ToString();
        if (string.IsNullOrEmpty(username))
        {
            return null;
        }
        var user = await users.FindByUserNameAsync(username);
        return user is not null && user.MatchesDemoPassword(form["password"].ToString()) ? user : null;
    }

    /// <summary>Returns (request id, optional ACS URL) from a redirect- or POST-bound AuthnRequest.</summary>
    private static (string RequestId, string? Acs) ParseAuthnRequest(string encoded)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("SAMLRequest is not valid base64.");
        }

        string xml;
        try
        {
            // HTTP-Redirect binding: raw DEFLATE. If it is not deflated we treat it
            // as HTTP-POST binding raw XML.
            using var compressed = new MemoryStream(bytes);
            using var inflater = new DeflateStream(compressed, CompressionMode.Decompress);
            using var output = new MemoryStream();
            inflater.CopyTo(output);
            xml = Encoding.UTF8.GetString(output.ToArray());
        }
        catch (InvalidDataException)
        {
            xml = Encoding.UTF8.GetString(bytes);
        }

        // First two bytes are a zlib header in some stacks: strip and retry.
        if (!xml.StartsWith('<'))
        {
            using var compressed = new MemoryStream(bytes, 2, bytes.Length - 2);
            using var inflater = new DeflateStream(compressed, CompressionMode.Decompress);
            using var output = new MemoryStream();
            inflater.CopyTo(output);
            xml = Encoding.UTF8.GetString(output.ToArray());
        }

        var doc = SafeXml.LoadDocument(xml);
        var request = doc.DocumentElement
            ?? throw new XmlException("AuthnRequest has no document element.");
        if (!string.Equals(request.LocalName, "AuthnRequest", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected AuthnRequest, found {request.LocalName}.");
        }
        var id = request.GetAttribute("ID");
        if (string.IsNullOrEmpty(id))
        {
            throw new InvalidOperationException("AuthnRequest has no ID attribute.");
        }
        var acs = request.GetAttribute("AssertionConsumerServiceURL");
        return (id, string.IsNullOrEmpty(acs) ? null : acs);
    }

    private static IResult SamlError(string message) =>
        Results.Content(
            "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>SAML error</title></head>"
            + $"<body style=\"font-family:system-ui;margin:3rem\"><h1>SAML error</h1><p>{WebUtility.HtmlEncode(message)}</p></body></html>",
            "text/html; charset=utf-8",
            statusCode: 400);
}
