using System.Security.Cryptography;
using Corridor.Legacy.Security;
using Corridor.Legacy.Tests.TestDoubles;

namespace Corridor.Legacy.Tests;

// JWT strategy: signature against a JWKS served by a fake handler (fresh RSA
// key generated in-test), issuer and audience checks, expiry.

public class JwtTokenValidatorTests : IDisposable
{
    private readonly RSA _signingKey = RSA.Create(2048);

    private JwtTokenValidator CreateValidator(string? jwksJson = null, string? issuer = null, string? audience = null)
    {
        var handler = new FakeHttpMessageHandler(_ => jwksJson ?? TestJwt.CreateJwks(_signingKey));
        var jwksProvider = new CachedJwksProvider(new HttpClient(handler), TestJwt.Issuer + "/jwks", TimeSpan.FromMinutes(15));
        return new JwtTokenValidator(jwksProvider, issuer ?? TestJwt.Issuer, audience ?? TestJwt.Audience);
    }

    [Fact]
    public void ValidToken_validates_and_returns_the_upn_claim()
    {
        string token = TestJwt.CreateToken(_signingKey, "svc-portal@corridor.example", expires: DateTime.UtcNow.AddMinutes(30));

        ValidatedIdentity identity = CreateValidator().Validate(token);

        Assert.Equal(IdentityTokenKind.Jwt, identity.Kind);
        Assert.Equal("svc-portal@corridor.example", identity.Upn);
    }

    [Fact]
    public void Token_from_the_wrong_issuer_is_rejected()
    {
        string token = TestJwt.CreateToken(_signingKey, "svc-portal@corridor.example",
            issuer: "http://evil.example", expires: DateTime.UtcNow.AddMinutes(30));

        IdentityTokenException exception = Assert.Throws<IdentityTokenException>(() => CreateValidator().Validate(token));
        Assert.Equal(CorridorFaultSubcodes.InvalidToken, exception.Subcode);
        Assert.Contains("issuer", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Token_for_another_audience_is_rejected()
    {
        string token = TestJwt.CreateToken(_signingKey, "svc-portal@corridor.example",
            audience: "portal", expires: DateTime.UtcNow.AddMinutes(30));

        IdentityTokenException exception = Assert.Throws<IdentityTokenException>(() => CreateValidator().Validate(token));
        Assert.Equal(CorridorFaultSubcodes.InvalidToken, exception.Subcode);
        Assert.Contains("audience", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExpiredToken_is_rejected()
    {
        string token = TestJwt.CreateToken(_signingKey, "svc-portal@corridor.example",
            expires: DateTime.UtcNow.AddHours(-1));

        IdentityTokenException exception = Assert.Throws<IdentityTokenException>(() => CreateValidator().Validate(token));
        Assert.Equal(CorridorFaultSubcodes.InvalidToken, exception.Subcode);
    }

    [Fact]
    public void Token_signed_by_a_key_not_in_the_jwks_is_rejected()
    {
        using RSA otherKey = RSA.Create(2048);
        string token = TestJwt.CreateToken(otherKey, "svc-portal@corridor.example", expires: DateTime.UtcNow.AddMinutes(30));

        IdentityTokenException exception = Assert.Throws<IdentityTokenException>(() => CreateValidator().Validate(token));
        Assert.Equal(CorridorFaultSubcodes.InvalidToken, exception.Subcode);
    }

    public void Dispose() => _signingKey.Dispose();
}
