using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;

namespace Corridor.AdfsSim.Saml;

public sealed record SamlValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors,
    string? NameId,
    string? NameIdFormat,
    string? Issuer,
    string? Audience,
    string? InResponseTo,
    DateTime? NotBefore,
    DateTime? NotOnOrAfter)
{
    public static SamlValidationResult Invalid(IEnumerable<string> errors) =>
        new(false, [.. errors], null, null, null, null, null, null, null);
}

/// <summary>Assertion validation shared by the adfs-sim test suite and by the legacy SOAP
/// service (which consumes these assertions inside its cor:Security header). Checks:
/// signature (RSA SHA256, enveloped), issuer, audience restriction, NotBefore/NotOnOrAfter
/// with clock skew, and extracts NameID plus InResponseTo.</summary>
public static class SamlValidator
{
    public static readonly TimeSpan DefaultClockSkew = TimeSpan.FromMinutes(5);

    public static SamlValidationResult ValidateAssertion(
        string assertionXml,
        string expectedAudience,
        DateTime now,
        X509Certificate2? trustedCertificate = null,
        TimeSpan? clockSkew = null)
    {
        var errors = new List<string>();
        var skew = clockSkew ?? DefaultClockSkew;

        XmlDocument doc;
        try
        {
            doc = SamlXml.LoadDocument(assertionXml);
        }
        catch (XmlException ex)
        {
            return SamlValidationResult.Invalid(["The assertion is not well-formed XML: " + ex.Message]);
        }

        var assertion = doc.DocumentElement;
        if (assertion is null || assertion.LocalName != "Assertion" || assertion.NamespaceURI != SamlXml.AssertionNs)
        {
            return SamlValidationResult.Invalid(["The document is not a saml:Assertion."]);
        }

        var signature = assertion["Signature", SamlXml.DsNs];
        if (signature is null)
        {
            return SamlValidationResult.Invalid(["The assertion is not signed."]);
        }

        var (signatureValid, _) = VerifySignature(assertion, signature, trustedCertificate, errors);

        var issuer = assertion["Issuer", SamlXml.AssertionNs]?.InnerText.Trim();

        var nameIdElement = assertion.SelectSingleNode("saml:Subject/saml:NameID", NsManager(doc)) as XmlElement;
        var nameId = nameIdElement?.InnerText.Trim();
        var nameIdFormat = nameIdElement?.GetAttribute("Format");

        var confirmation = assertion.SelectSingleNode(
            "saml:Subject/saml:SubjectConfirmation/saml:SubjectConfirmationData", NsManager(doc)) as XmlElement;
        var inResponseTo = confirmation?.GetAttribute("InResponseTo");
        if (string.IsNullOrWhiteSpace(inResponseTo))
        {
            inResponseTo = null;
        }

        var conditions = assertion["Conditions", SamlXml.AssertionNs];
        DateTime? notBefore = null;
        DateTime? notOnOrAfter = null;
        var audience = assertion.SelectSingleNode(
            "saml:Conditions/saml:AudienceRestriction/saml:Audience", NsManager(doc))?.InnerText.Trim();

        if (conditions is null)
        {
            errors.Add("The assertion has no saml:Conditions.");
        }
        else
        {
            var notBeforeText = conditions.GetAttribute("NotBefore");
            var notOnOrAfterText = conditions.GetAttribute("NotOnOrAfter");

            if (!string.IsNullOrWhiteSpace(notBeforeText))
            {
                notBefore = ParseUtc(notBeforeText, "NotBefore", errors);
            }
            else
            {
                errors.Add("The assertion Conditions carry no NotBefore.");
            }

            if (!string.IsNullOrWhiteSpace(notOnOrAfterText))
            {
                notOnOrAfter = ParseUtc(notOnOrAfterText, "NotOnOrAfter", errors);
            }
            else
            {
                errors.Add("The assertion Conditions carry no NotOnOrAfter.");
            }

            if (string.IsNullOrWhiteSpace(audience))
            {
                errors.Add("The assertion has no AudienceRestriction.");
            }
            else if (!string.Equals(audience, expectedAudience.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"The assertion audience '{audience}' does not match the expected '{expectedAudience}'.");
            }
        }

        if (notBefore is not null && now < notBefore.Value - skew)
        {
            errors.Add($"The assertion is not yet valid: NotBefore {notBefore:O} is after now {now:O} plus {skew.TotalMinutes} minutes of skew.");
        }

        if (notOnOrAfter is not null && now >= notOnOrAfter.Value + skew)
        {
            errors.Add($"The assertion has expired: NotOnOrAfter {notOnOrAfter:O} has passed for now {now:O} plus {skew.TotalMinutes} minutes of skew.");
        }

        if (string.IsNullOrWhiteSpace(issuer))
        {
            errors.Add("The assertion has no saml:Issuer.");
        }

        if (string.IsNullOrWhiteSpace(nameId))
        {
            errors.Add("The assertion has no saml:NameID.");
        }

        return new SamlValidationResult(
            signatureValid && errors.Count == 0,
            errors,
            nameId,
            nameIdFormat,
            issuer,
            audience,
            inResponseTo,
            notBefore,
            notOnOrAfter);
    }

    private static (bool Valid, X509Certificate2? Embedded) VerifySignature(
        XmlElement assertion,
        XmlElement signature,
        X509Certificate2? trustedCertificate,
        List<string> errors)
    {
        try
        {
            var signedXml = new SignedXml(assertion.OwnerDocument!);
            signedXml.LoadXml(signature);

            var id = assertion.GetAttribute("ID");
            var reference = (signedXml.SignedInfo ?? throw new InvalidOperationException("The signature has no SignedInfo."))
                .References.Cast<Reference>().FirstOrDefault();
            if (reference is null || reference.Uri != "#" + id)
            {
                errors.Add("The signature does not cover the assertion (missing or mismatched reference URI).");
                return (false, null);
            }

            var embedded = GetEmbeddedCertificate(signedXml);

            X509Certificate2 verificationCertificate;
            if (trustedCertificate is not null)
            {
                if (embedded is not null &&
                    !string.Equals(embedded.Thumbprint, trustedCertificate.Thumbprint, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add("The embedded signing certificate does not match the trusted certificate.");
                    return (false, embedded);
                }

                verificationCertificate = trustedCertificate;
            }
            else if (embedded is not null)
            {
                // Caller opted to trust the embedded KeyInfo certificate.
                verificationCertificate = embedded;
            }
            else
            {
                errors.Add("The signature carries no certificate and no trusted certificate was supplied.");
                return (false, null);
            }

            if (!signedXml.CheckSignature(verificationCertificate, verifySignatureOnly: true))
            {
                errors.Add("The assertion signature is not valid.");
                return (false, embedded);
            }

            return (true, embedded);
        }
        catch (CryptographicException ex)
        {
            errors.Add("The assertion signature could not be processed: " + ex.Message);
            return (false, null);
        }
        catch (XmlException ex)
        {
            errors.Add("The assertion signature could not be parsed: " + ex.Message);
            return (false, null);
        }
    }

    private static X509Certificate2? GetEmbeddedCertificate(SignedXml signedXml)
    {
        foreach (var clause in signedXml.KeyInfo)
        {
            if (clause is KeyInfoX509Data x509Data &&
                x509Data.Certificates is { Count: > 0 } certificates &&
                certificates[0] is X509Certificate2 cert)
            {
                return cert;
            }
        }

        return null;
    }

    private static DateTime? ParseUtc(string value, string attribute, List<string> errors)
    {
        try
        {
            return XmlConvert.ToDateTime(value, XmlDateTimeSerializationMode.Utc);
        }
        catch (FormatException)
        {
            errors.Add($"The {attribute} value '{value}' is not a valid xs:dateTime.");
            return null;
        }
    }

    private static XmlNamespaceManager NsManager(XmlDocument doc)
    {
        var ns = new XmlNamespaceManager(doc.NameTable!);
        ns.AddNamespace("saml", SamlXml.AssertionNs);
        ns.AddNamespace("samlp", SamlXml.ProtocolNs);
        ns.AddNamespace("ds", SamlXml.DsNs);
        return ns;
    }
}
