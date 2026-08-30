using Corridor.AdfsSim.Saml;

namespace Corridor.AdfsSim.Tests;

public sealed class AuthnRequestParserTests
{
    [Fact]
    public void Parse_DeflatedBase64_ReadsIssuerIdAndAcs()
    {
        var encoded = TestSetup.DeflatedBase64(
            TestSetup.BuildAuthnRequestXml("_req-42", TestSetup.PortalIssuer, TestSetup.PortalAcs));

        var result = AuthnRequestParser.Parse(encoded);

        Assert.True(result.Success, result.Error);
        Assert.Equal("_req-42", result.Request!.Id);
        Assert.Equal(TestSetup.PortalIssuer, result.Request.Issuer);
        Assert.Equal(TestSetup.PortalAcs, result.Request.AssertionConsumerServiceUrl);
        Assert.Equal("http://localhost:8090/adfs/ls", result.Request.Destination);
    }

    [Fact]
    public void Parse_PlainBase64_WithoutDeflate_ReadsIssuerAndId()
    {
        var encoded = TestSetup.PlainBase64(
            TestSetup.BuildAuthnRequestXml("_plain-1", TestSetup.PortalIssuer, TestSetup.PortalAcs));

        var result = AuthnRequestParser.Parse(encoded);

        Assert.True(result.Success, result.Error);
        Assert.Equal("_plain-1", result.Request!.Id);
        Assert.Equal(TestSetup.PortalIssuer, result.Request.Issuer);
    }

    [Fact]
    public void Parse_RejectsGarbage()
    {
        var notBase64 = AuthnRequestParser.Parse("this is not base64!!");
        Assert.False(notBase64.Success);
        Assert.Contains("Base64", notBase64.Error);

        var wrongDocument = AuthnRequestParser.Parse(TestSetup.PlainBase64("<html><body>nope</body></html>"));
        Assert.False(wrongDocument.Success);
        Assert.Contains("AuthnRequest", wrongDocument.Error);

        var missing = AuthnRequestParser.Parse(null);
        Assert.False(missing.Success);
    }
}
