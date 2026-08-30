using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;

namespace Corridor.Portal.Auth.Saml;

/// <summary>A signing certificate the portal trusts, tagged with which simulated IdP it belongs to.</summary>
public sealed record TrustedCertificate(string IdentityProvider, X509Certificate2 Certificate);

public sealed record SamlPrincipalData(
    string NameId,
    string Upn,
    IReadOnlyList<string> Roles,
    string IdentityProvider);

public sealed record SamlValidationResult(bool IsValid, string? Error, SamlPrincipalData? Principal)
{
    public static SamlValidationResult Fail(string error) => new(false, error, null);

    public static SamlValidationResult Ok(SamlPrincipalData principal) => new(true, null, principal);
}

/// <summary>
/// Validates inbound SAML 2.0 responses for the portal ACS: enveloped signature from a trusted
/// IdP certificate, audience restricted to this ACS, and NotBefore/NotOnOrAfter conditions with
/// a five minute clock skew. Works on Response and bare Assertion documents.
/// </summary>
public sealed class SamlValidator
{
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    public SamlValidationResult Validate(
        string base64SamlResponse,
        IReadOnlyList<TrustedCertificate> trustedCertificates,
        string expectedAudience,
        DateTime utcNow)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64SamlResponse);
        }
        catch (FormatException)
        {
            return SamlValidationResult.Fail("The SAML response is not valid base64.");
        }

        XmlDocument document;
        try
        {
            document = SamlXml.LoadDocument(bytes);
        }
        catch (XmlException)
        {
            return SamlValidationResult.Fail("The SAML response is not valid XML.");
        }

        var root = document.DocumentElement;
        if (root is null)
        {
            return SamlValidationResult.Fail("The SAML response has no root element.");
        }
        if (root.LocalName is not ("Response" or "Assertion"))
        {
            return SamlValidationResult.Fail("The document is not a SAML response or assertion.");
        }

        if (trustedCertificates.Count == 0)
        {
            return SamlValidationResult.Fail("No trusted signing certificate is configured for SAML.");
        }

        var status = FindStatusValue(root);
        if (status is not null && !status.EndsWith("status:Success", StringComparison.OrdinalIgnoreCase))
        {
            return SamlValidationResult.Fail($"The identity provider returned status {status}.");
        }

        var assertion = root.LocalName == "Assertion"
            ? root
            : FindFirstChild(root, SamlXml.AssertionNamespace, "Assertion");
        if (assertion is null)
        {
            return SamlValidationResult.Fail("The response carries no assertion.");
        }

        var signature = FindFirstChild(root, SamlXml.SignatureNamespace, "Signature", descendants: true);
        if (signature is null)
        {
            return SamlValidationResult.Fail("The response is unsigned.");
        }

        var signedElement = signature.ParentNode as XmlElement;
        var signedId = signedElement is null ? null : SamlXml.ReadIdAttribute(signedElement);
        if (signedId is null || !ReferenceTargets(signedId, assertion, root))
        {
            return SamlValidationResult.Fail("The signature does not cover the assertion.");
        }

        var verifiedProvider = VerifySignature(document, signature, trustedCertificates);
        if (verifiedProvider is null)
        {
            return SamlValidationResult.Fail("The signature did not verify against any trusted certificate.");
        }

        var conditionsError = CheckConditions(assertion, expectedAudience, utcNow);
        if (conditionsError is not null)
        {
            return SamlValidationResult.Fail(conditionsError);
        }

        var nameId = FindFirstChild(assertion, SamlXml.AssertionNamespace, "Subject") is { } subject
            ? FindFirstChild(subject, SamlXml.AssertionNamespace, "NameID")?.InnerText.Trim()
            : null;
        if (string.IsNullOrEmpty(nameId))
        {
            return SamlValidationResult.Fail("The assertion carries no subject NameID.");
        }

        var upn = nameId!;
        var roles = new List<string>();
        foreach (var attribute in assertion.GetElementsByTagName("Attribute", SamlXml.AssertionNamespace).OfType<XmlElement>())
        {
            var name = attribute.GetAttribute("Name");
            var values = attribute.GetElementsByTagName("AttributeValue", SamlXml.AssertionNamespace)
                .OfType<XmlElement>()
                .Select(v => v.InnerText.Trim())
                .Where(v => v.Length > 0)
                .ToList();
            if (IsUpnClaim(name) && values.Count > 0)
            {
                upn = values[0];
            }
            else if (IsRoleClaim(name))
            {
                roles.AddRange(values);
            }
        }

        return SamlValidationResult.Ok(new SamlPrincipalData(nameId!, upn, roles, verifiedProvider));
    }

    // The ADFS simulation sends long WS-Fed style claim type URIs; the short SAML names are
    // accepted as well so both providers interoperate during dual trust.
    internal static bool IsUpnClaim(string name)
    {
        return name is "upn" or "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/upn";
    }

    internal static bool IsRoleClaim(string name)
    {
        return name is "role"
            or "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/role"
            or "http://schemas.microsoft.com/ws/2008/06/identity/claims/role";
    }

    private static bool ReferenceTargets(string signedId, XmlElement assertion, XmlElement root)
    {
        // Enveloped signatures sit inside the element they cover: either the assertion itself
        // or the surrounding response. Anything else is not trusted to speak for the assertion.
        var assertionId = SamlXml.ReadIdAttribute(assertion);
        var responseId = SamlXml.ReadIdAttribute(root);
        return signedId == assertionId || signedId == responseId;
    }

    private static string? VerifySignature(XmlDocument document, XmlElement signature, IReadOnlyList<TrustedCertificate> trustedCertificates)
    {
        try
        {
            var signedXml = new SignedXml(document);
            signedXml.LoadXml(signature);
            foreach (var trusted in trustedCertificates)
            {
                var key = trusted.Certificate.GetRSAPublicKey();
                if (key is not null && signedXml.CheckSignature(key))
                {
                    return trusted.IdentityProvider;
                }
            }
        }
        catch (CryptographicException)
        {
            return null;
        }
        return null;
    }

    private static string? CheckConditions(XmlElement assertion, string expectedAudience, DateTime utcNow)
    {
        var conditions = FindFirstChild(assertion, SamlXml.AssertionNamespace, "Conditions");
        if (conditions is null)
        {
            return "The assertion carries no conditions.";
        }

        var notBefore = ParseUtc(conditions.GetAttribute("NotBefore"));
        if (notBefore is not null && notBefore > utcNow + ClockSkew)
        {
            return "The assertion is not valid yet.";
        }

        var notOnOrAfter = ParseUtc(conditions.GetAttribute("NotOnOrAfter"));
        if (notOnOrAfter is null)
        {
            return "The assertion has no NotOnOrAfter condition.";
        }
        if (notOnOrAfter <= utcNow)
        {
            return "The assertion is expired.";
        }

        var audiences = FindFirstChild(conditions, SamlXml.AssertionNamespace, "AudienceRestriction")
            ?.GetElementsByTagName("Audience", SamlXml.AssertionNamespace)
            .OfType<XmlElement>()
            .Select(a => a.InnerText.Trim())
            .ToList();
        if (audiences is null || audiences.Count == 0 || !audiences.Contains(expectedAudience, StringComparer.Ordinal))
        {
            return "The assertion audience does not include this service provider.";
        }
        return null;
    }

    private static string? FindStatusValue(XmlElement root)
    {
        if (root.LocalName != "Response")
        {
            return null;
        }
        return FindFirstChild(root, SamlXml.ProtocolNamespace, "Status") is { } status
            && FindFirstChild(status, SamlXml.ProtocolNamespace, "StatusCode") is { } code
            ? code.GetAttribute("Value")
            : null;
    }

    private static XmlElement? FindFirstChild(XmlElement parent, string ns, string localName, bool descendants = false)
    {
        if (descendants)
        {
            return parent.GetElementsByTagName(localName, ns).OfType<XmlElement>().FirstOrDefault();
        }
        foreach (XmlNode child in parent.ChildNodes)
        {
            if (child is XmlElement element
                && element.LocalName == localName
                && element.NamespaceURI == ns)
            {
                return element;
            }
        }
        return null;
    }

    private static DateTime? ParseUtc(string value)
    {
        return DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;
    }
}
