using System.Security.Cryptography;
using Corridor.Legacy.Security;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Corridor.Legacy.Tests.TestDoubles;

/// <summary>
/// Mints RS256 JWTs like okta-sim's client-credentials tokens for the legacy
/// client, plus a matching JWKS document for the fake HTTP handler to serve.
/// </summary>
public static class TestJwt
{
    public const string Issuer = "http://localhost:8080";
    public const string Audience = "legacy";
    public const string KeyId = "test-key-1";

    public static string CreateToken(
        RSA signingKey,
        string upn,
        string? issuer = null,
        string? audience = null,
        DateTime? expires = null,
        DateTime? issuedAt = null)
    {
        var securityKey = new RsaSecurityKey(signingKey) { KeyId = KeyId };
        var handler = new JsonWebTokenHandler();
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer ?? Issuer,
            Audience = audience ?? Audience,
            IssuedAt = issuedAt,
            Expires = expires,
            SigningCredentials = new SigningCredentials(securityKey, SecurityAlgorithms.RsaSha256),
            Claims = new Dictionary<string, object> { ["upn"] = upn }
        };
        return handler.CreateToken(descriptor);
    }

    /// <summary>A JWKS document describing the given RSA public key.</summary>
    public static string CreateJwks(RSA signingKey)
    {
        RSAParameters parameters = signingKey.ExportParameters(includePrivateParameters: false);
        string jwk =
            $$"""
            {"kty":"RSA","use":"sig","alg":"RS256","kid":"{{KeyId}}","n":"{{Base64UrlEncoder.Encode(parameters.Modulus)}}","e":"{{Base64UrlEncoder.Encode(parameters.Exponent)}}"}
            """;
        return "{\"keys\":[" + jwk.Replace("\n", string.Empty).Replace(" ", string.Empty) + "]}";
    }
}
