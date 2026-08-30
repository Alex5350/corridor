using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;

namespace Corridor.Legacy.Security;

/// <summary>
/// Strategy validating SAML 2.0 assertions issued by adfs-sim. Validation
/// rules mirror what adfs-sim produces (see the parity note in
/// docs/TECHNICAL.md): enveloped XML signature over the assertion verified
/// against certs/adfs-sim-cert.pem, audience restriction, NotBefore/NotOnOrAfter
/// with a 5 minute clock skew.
/// </summary>
public sealed class SamlTokenValidator : ITokenValidationStrategy
{
    public const string AssertionNamespace = "urn:oasis:names:tc:SAML:2.0:assertion";

    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    private readonly X509Certificate2? _signingCertificate;
    private readonly string _expectedAudience;
    private readonly TimeProvider _clock;

    public SamlTokenValidator(X509Certificate2? signingCertificate, string expectedAudience, TimeProvider? clock = null)
    {
        _signingCertificate = signingCertificate;
        _expectedAudience = expectedAudience;
        _clock = clock ?? TimeProvider.System;
    }

    public IdentityTokenKind Kind => IdentityTokenKind.SamlAssertion;

    public ValidatedIdentity Validate(string payload)
    {
        XmlElement assertion = LoadAssertion(payload);
        VerifySignature(assertion);
        VerifyConditions(assertion);
        return new ValidatedIdentity(IdentityTokenKind.SamlAssertion, ExtractNameIdentifier(assertion));
    }

    private static XmlElement LoadAssertion(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            throw new IdentityTokenException(CorridorFaultSubcodes.InvalidToken, "SAML assertion payload is empty.");
        }

        var document = new XmlDocument { XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(payload), new XmlReaderSettings
        {
            // Safe XML profile: DTD prohibited, no resolver (see docs/security-findings-log.md pattern).
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        });
        try
        {
            document.Load(reader);
        }
        catch (XmlException exception)
        {
            throw new IdentityTokenException(CorridorFaultSubcodes.InvalidToken, $"SAML assertion is not well-formed XML: {exception.Message}");
        }

        XmlElement? root = document.DocumentElement;
        if (root is null || root.LocalName != "Assertion" || root.NamespaceURI != AssertionNamespace)
        {
            throw new IdentityTokenException(CorridorFaultSubcodes.InvalidToken, "Payload is not a SAML 2.0 assertion.");
        }

        return root;
    }

    private void VerifySignature(XmlElement assertion)
    {
        if (_signingCertificate is null)
        {
            throw new IdentityTokenException(CorridorFaultSubcodes.InvalidToken,
                "ADFS signing certificate is not configured; SAML assertions cannot be validated.");
        }

        XmlElement? signature = assertion["Signature", SignedXml.XmlDsigNamespaceUrl];
        if (signature is null)
        {
            throw new IdentityTokenException(CorridorFaultSubcodes.InvalidToken, "SAML assertion is not signed.");
        }

        var signedXml = new SamlSignedXml(assertion);
        signedXml.LoadXml(signature);
        if (signedXml.SignedInfo is null)
        {
            throw new IdentityTokenException(CorridorFaultSubcodes.InvalidToken, "SAML signature has no SignedInfo element.");
        }

        // The signature must cover the assertion itself, not merely a child element.
        string assertionId = assertion.GetAttribute("ID");
        if (string.IsNullOrEmpty(assertionId) || !signedXml.SignedInfo.References.Cast<Reference>().Any(r => r.Uri == "#" + assertionId))
        {
            throw new IdentityTokenException(CorridorFaultSubcodes.InvalidToken,
                "SAML signature does not cover the assertion (missing or mismatched reference URI).");
        }

        using RSA? publicKey = _signingCertificate.GetRSAPublicKey();
        if (publicKey is null)
        {
            throw new IdentityTokenException(CorridorFaultSubcodes.InvalidToken, "ADFS signing certificate has no RSA public key.");
        }

        if (!signedXml.CheckSignature(publicKey))
        {
            throw new IdentityTokenException(CorridorFaultSubcodes.InvalidToken,
                "SAML assertion signature verification failed (tampered content or a different signing key).");
        }
    }

    private void VerifyConditions(XmlElement assertion)
    {
        DateTime now = _clock.GetUtcNow().UtcDateTime;
        XmlElement? conditions = assertion["Conditions", AssertionNamespace];
        if (conditions is null)
        {
            throw new IdentityTokenException(CorridorFaultSubcodes.InvalidToken, "SAML assertion has no Conditions element.");
        }

        string? notBefore = conditions.GetAttribute("NotBefore");
        if (!string.IsNullOrEmpty(notBefore) && TryParseUtc(notBefore, out DateTime notBeforeUtc) && now.Add(ClockSkew) < notBeforeUtc)
        {
            throw new IdentityTokenException(CorridorFaultSubcodes.InvalidToken,
                $"SAML assertion is not valid yet (NotBefore {notBefore}).");
        }

        string? notOnOrAfter = conditions.GetAttribute("NotOnOrAfter");
        if (string.IsNullOrEmpty(notOnOrAfter))
        {
            throw new IdentityTokenException(CorridorFaultSubcodes.InvalidToken, "SAML assertion has no NotOnOrAfter condition.");
        }

        if (!TryParseUtc(notOnOrAfter, out DateTime expiry))
        {
            throw new IdentityTokenException(CorridorFaultSubcodes.InvalidToken, "SAML NotOnOrAfter is not a valid datetime.");
        }

        if (now.Subtract(ClockSkew) >= expiry)
        {
            throw new IdentityTokenException(CorridorFaultSubcodes.InvalidToken, $"SAML assertion expired at {notOnOrAfter}.");
        }

        XmlElement? audienceRestriction = conditions["AudienceRestriction", AssertionNamespace];
        if (audienceRestriction is not null)
        {
            bool audienceMatches = false;
            foreach (XmlElement audience in audienceRestriction.GetElementsByTagName("Audience", AssertionNamespace).OfType<XmlElement>())
            {
                if (string.Equals(audience.InnerText.Trim(), _expectedAudience, StringComparison.Ordinal))
                {
                    audienceMatches = true;
                    break;
                }
            }

            if (!audienceMatches)
            {
                throw new IdentityTokenException(CorridorFaultSubcodes.InvalidToken,
                    $"SAML assertion audience does not include the expected value '{_expectedAudience}'.");
            }
        }
    }

    private static string ExtractNameIdentifier(XmlElement assertion)
    {
        XmlElement? subject = assertion["Subject", AssertionNamespace];
        XmlElement? nameIdentifier = subject?["NameID", AssertionNamespace];
        string upn = nameIdentifier?.InnerText.Trim() ?? string.Empty;
        if (upn.Length == 0)
        {
            throw new IdentityTokenException(CorridorFaultSubcodes.InvalidToken, "SAML assertion has no Subject/NameID.");
        }

        return upn;
    }

    private static bool TryParseUtc(string value, out DateTime utc)
    {
        try
        {
            utc = XmlConvert.ToDateTime(value, XmlDateTimeSerializationMode.Utc);
            return true;
        }
        catch (FormatException)
        {
            utc = default;
            return false;
        }
    }

    /// <summary>
    /// SignedXml that resolves SAML ID attributes (upper case), which the base
    /// implementation does not look for by default.
    /// </summary>
    private sealed class SamlSignedXml : SignedXml
    {
        public SamlSignedXml(XmlElement assertion)
            : base(assertion)
        {
        }

        public override XmlElement? GetIdElement(XmlDocument? document, string idValue)
        {
            if (document is null)
            {
                return base.GetIdElement(document, idValue);
            }

            foreach (XmlElement element in document.SelectNodes("//*[@ID]")!.Cast<XmlElement>())
            {
                if (element.GetAttribute("ID") == idValue)
                {
                    return element;
                }
            }

            return base.GetIdElement(document, idValue);
        }
    }
}
