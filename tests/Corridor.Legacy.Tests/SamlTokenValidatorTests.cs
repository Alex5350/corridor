using System.Security.Cryptography.X509Certificates;
using System.Xml;
using Corridor.Legacy.Security;
using Corridor.Legacy.Tests.TestDoubles;

namespace Corridor.Legacy.Tests;

// SAML strategy: same validation rules the adfs-sim issuer applies (signature
// via the signing certificate, audience restriction, NotOnOrAfter with a five
// minute skew). Happy path plus one rejection per rule.

public class SamlTokenValidatorTests : IDisposable
{
    private const string Audience = "http://localhost:8000/TraceLink.svc";

    private readonly X509Certificate2 _signingCertificate = TestSaml.CreateSigningCertificate();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero));

    private SamlTokenValidator CreateValidator(X509Certificate2? certificate = null) =>
        new(certificate ?? _signingCertificate, Audience, _clock);

    private string BuildValidAssertion() => TestSaml.BuildAssertion(
        Audience,
        "officer@corridor.example",
        _clock.GetUtcNow().UtcDateTime.AddMinutes(-5),
        _clock.GetUtcNow().UtcDateTime.AddMinutes(55),
        sign: true,
        signingCertificate: _signingCertificate);

    [Fact]
    public void SignedAssertion_validates_and_returns_the_name_id()
    {
        ValidatedIdentity identity = CreateValidator().Validate(BuildValidAssertion());

        Assert.Equal(IdentityTokenKind.SamlAssertion, identity.Kind);
        Assert.Equal("officer@corridor.example", identity.Upn);
    }

    [Fact]
    public void TamperedAssertion_is_rejected()
    {
        string assertion = BuildValidAssertion();
        var document = new XmlDocument { XmlResolver = null };
        document.LoadXml(assertion);
        XmlElement nameId = (XmlElement)document.GetElementsByTagName("NameID", TestSaml.AssertionNamespace)[0]!;
        nameId.InnerText = "attacker@corridor.example"; // digest no longer matches

        IdentityTokenException exception = Assert.Throws<IdentityTokenException>(() => CreateValidator().Validate(document.OuterXml));
        Assert.Equal(CorridorFaultSubcodes.InvalidToken, exception.Subcode);
        Assert.Contains("signature", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Assertion_for_another_audience_is_rejected()
    {
        string assertion = TestSaml.BuildAssertion(
            "https://portal.example/other",
            "officer@corridor.example",
            _clock.GetUtcNow().UtcDateTime.AddMinutes(-5),
            _clock.GetUtcNow().UtcDateTime.AddMinutes(55),
            sign: true,
            signingCertificate: _signingCertificate);

        IdentityTokenException exception = Assert.Throws<IdentityTokenException>(() => CreateValidator().Validate(assertion));
        Assert.Equal(CorridorFaultSubcodes.InvalidToken, exception.Subcode);
        Assert.Contains("audience", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExpiredAssertion_is_rejected()
    {
        string assertion = TestSaml.BuildAssertion(
            Audience,
            "officer@corridor.example",
            _clock.GetUtcNow().UtcDateTime.AddHours(-2),
            _clock.GetUtcNow().UtcDateTime.AddHours(-1), // past NotOnOrAfter, far beyond the skew
            sign: true,
            signingCertificate: _signingCertificate);

        IdentityTokenException exception = Assert.Throws<IdentityTokenException>(() => CreateValidator().Validate(assertion));
        Assert.Equal(CorridorFaultSubcodes.InvalidToken, exception.Subcode);
        Assert.Contains("expired", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnsignedAssertion_is_rejected()
    {
        string assertion = TestSaml.BuildAssertion(
            Audience,
            "officer@corridor.example",
            _clock.GetUtcNow().UtcDateTime.AddMinutes(-5),
            _clock.GetUtcNow().UtcDateTime.AddMinutes(55),
            sign: false);

        IdentityTokenException exception = Assert.Throws<IdentityTokenException>(() => CreateValidator().Validate(assertion));
        Assert.Equal(CorridorFaultSubcodes.InvalidToken, exception.Subcode);
    }

    [Fact]
    public void Assertion_signed_by_another_certificate_is_rejected()
    {
        using X509Certificate2 wrongCertificate = TestSaml.CreateSigningCertificate();
        string assertion = TestSaml.BuildAssertion(
            Audience,
            "officer@corridor.example",
            _clock.GetUtcNow().UtcDateTime.AddMinutes(-5),
            _clock.GetUtcNow().UtcDateTime.AddMinutes(55),
            sign: true,
            signingCertificate: wrongCertificate);

        IdentityTokenException exception = Assert.Throws<IdentityTokenException>(() => CreateValidator().Validate(assertion));
        Assert.Equal(CorridorFaultSubcodes.InvalidToken, exception.Subcode);
        Assert.Contains("signature", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _signingCertificate.Dispose();
}
