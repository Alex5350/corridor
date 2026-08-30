using System.Security.Claims;
using Corridor.Portal.Auth;

namespace Corridor.Portal.Tests;

public class ClaimsTransformationTests
{
    [Fact]
    public void Transform_MapsUpnToNameAndKeepsRoles()
    {
        var incoming = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("upn", "officer@corridor.example"),
            new Claim("role", "Officer"),
            new Claim("role", "Clerk")
        ], "external-test"));

        var transformed = PortalClaims.Transform(incoming, "adfs", "saml");

        Assert.Equal("officer@corridor.example", transformed.Identity!.Name);
        Assert.True(transformed.IsInRole("Officer"));
        Assert.True(transformed.IsInRole("Clerk"));
        Assert.Equal("adfs", PortalClaims.ReadIdentityProvider(transformed));
        Assert.Equal("officer@corridor.example",
            transformed.FindFirst(ClaimTypes.NameIdentifier)!.Value);
    }

    [Fact]
    public void Transform_DeduplicatesRepeatedRoles()
    {
        var incoming = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("upn", "inspector@corridor.example"),
            new Claim("role", "Inspector"),
            new Claim("role", "Inspector")
        ], "external-test"));

        var transformed = PortalClaims.Transform(incoming, "okta-saml", "saml");

        Assert.Single(transformed.FindAll(ClaimTypes.Role));
    }

    [Fact]
    public void Transform_RejectsIdentityWithoutUpn()
    {
        var incoming = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("display", "No Upn Here")
        ], "external-test"));

        Assert.Throws<InvalidOperationException>(() => PortalClaims.Transform(incoming, "adfs", "saml"));
    }
}
