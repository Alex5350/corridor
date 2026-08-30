using System.IO.Compression;
using Corridor.Portal.Auth;
using Corridor.Portal.Auth.Saml;
using Corridor.Portal.Models;

namespace Corridor.Portal.Tests;

public class LoginRouteTests
{
    [Theory]
    [InlineData(TrustMode.Adfs, LoginRouteKind.SamlRedirect)]
    [InlineData(TrustMode.Okta, LoginRouteKind.OidcChallenge)]
    [InlineData(TrustMode.Dual, LoginRouteKind.Chooser)]
    public void Select_RoutesByCurrentTrustMode(TrustMode mode, LoginRouteKind expected)
    {
        var route = LoginRouteSelector.Select(mode);

        Assert.Equal(expected, route.Kind);
        Assert.Equal(mode, route.Mode);
    }

    [Fact]
    public void BuildRedirectUrl_CarriesDeflatedAuthnRequestAndRelayState()
    {
        var url = SamlAuthnRequests.BuildRedirectUrl(
            "http://localhost:8090/adfs/ls",
            "http://localhost:5200",
            "http://localhost:5200/saml/acs",
            "/Permits");

        Assert.StartsWith("http://localhost:8090/adfs/ls?SAMLRequest=", url, StringComparison.Ordinal);
        Assert.Contains("&RelayState=%2FPermits", url, StringComparison.Ordinal);

        var encoded = url[(url.IndexOf("SAMLRequest=", StringComparison.Ordinal) + 12)..];
        encoded = encoded[..encoded.IndexOf('&')];
        var deflated = Convert.FromBase64String(Uri.UnescapeDataString(encoded));
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(new MemoryStream(deflated), CompressionMode.Decompress))
        {
            deflate.CopyTo(output);
        }
        var xml = System.Text.Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("AuthnRequest", xml, StringComparison.Ordinal);
        Assert.Contains("http://localhost:5200/saml/acs", xml, StringComparison.Ordinal);
    }
}
