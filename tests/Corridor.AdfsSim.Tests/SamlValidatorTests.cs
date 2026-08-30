using System.Xml;
using Corridor.AdfsSim.Saml;

namespace Corridor.AdfsSim.Tests;

/// <summary>Exercises the SamlValidator helper the legacy SOAP service will reuse when it
/// validates SAML assertions carried in its cor:Security header.</summary>
public sealed class SamlValidatorTests
{
    private readonly SamlResponseBuilder _builder = new(
        Microsoft.Extensions.Options.Options.Create(TestSetup.DefaultOptions()),
        TestSetup.CreateSigningCertificate());

    private IssuedSamlResponse Issue() =>
        _builder.Build(
            new Identity.SimUser("inspector@corridor.example", "Miguel Sandoval", "Inspector"),
            TestSetup.PortalRequest(),
            TestSetup.PortalParty());

    [Fact]
    public void Validate_TamperedAssertion_FailsTheSignatureCheck()
    {
        var issued = Issue();

        var doc = new XmlDocument();
        doc.LoadXml(issued.AssertionXml);
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("saml", SamlXml.AssertionNs);
        doc.SelectSingleNode("//saml:NameID", ns)!.InnerText = "attacker@corridor.example";
        var tampered = doc.DocumentElement!.OuterXml;

        var result = SamlValidator.ValidateAssertion(
            tampered,
            TestSetup.PortalIssuer,
            now: issued.IssuedAt,
            trustedCertificate: TestSetup.CreateSigningCertificate().Certificate);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("signature", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_WrongExpectedAudience_IsRejected()
    {
        var issued = Issue();

        var result = SamlValidator.ValidateAssertion(
            issued.AssertionXml,
            "http://somebody-else.example/sp",
            now: issued.IssuedAt,
            trustedCertificate: TestSetup.CreateSigningCertificate().Certificate);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("audience", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_NotYetValid_NotBefore_IsRejected()
    {
        var issued = Issue();

        // The validator sits 40 minutes in the past: NotBefore (now-5m) plus the 5 minute
        // skew allowance has not arrived yet, so the assertion must be refused.
        var result = SamlValidator.ValidateAssertion(
            issued.AssertionXml,
            TestSetup.PortalIssuer,
            now: issued.IssuedAt.AddMinutes(-40),
            trustedCertificate: TestSetup.CreateSigningCertificate().Certificate);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("not yet valid", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_ExpiredAssertion_IsRejected()
    {
        var issued = Issue();

        // NotOnOrAfter is now+60m; the default skew adds 5m, so now+70m must be refused.
        var result = SamlValidator.ValidateAssertion(
            issued.AssertionXml,
            TestSetup.PortalIssuer,
            now: issued.IssuedAt.AddMinutes(70),
            trustedCertificate: TestSetup.CreateSigningCertificate().Certificate);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("expired", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_WithinSkewBoundaries_IsAccepted()
    {
        var issued = Issue();

        // Inside the NotBefore..NotOnOrAfter window (including the skew allowance).
        var early = SamlValidator.ValidateAssertion(
            issued.AssertionXml, TestSetup.PortalIssuer,
            now: issued.NotBefore.AddMinutes(1),
            trustedCertificate: TestSetup.CreateSigningCertificate().Certificate);

        var late = SamlValidator.ValidateAssertion(
            issued.AssertionXml, TestSetup.PortalIssuer,
            now: issued.NotOnOrAfter.AddMinutes(4),
            trustedCertificate: TestSetup.CreateSigningCertificate().Certificate);

        Assert.True(early.IsValid, string.Join("; ", early.Errors));
        Assert.True(late.IsValid, string.Join("; ", late.Errors));
    }
}
