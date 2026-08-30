using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Corridor.Legacy.Security;

/// <summary>
/// Strategy validating RS256 JWTs issued by okta-sim (client-credentials tokens
/// for the legacy client). Signature keys come from the okta-sim JWKS endpoint
/// via <see cref="IJwksProvider"/>; issuer, audience, and lifetime are checked
/// with a one minute clock skew.
/// </summary>
public sealed class JwtTokenValidator : ITokenValidationStrategy
{
    private static readonly string[] UpnClaims = { "upn", "preferred_username", "sub" };

    private readonly IJwksProvider _jwksProvider;
    private readonly string _issuer;
    private readonly string _audience;

    public JwtTokenValidator(IJwksProvider jwksProvider, string issuer, string audience)
    {
        _jwksProvider = jwksProvider;
        _issuer = issuer;
        _audience = audience;
    }

    public IdentityTokenKind Kind => IdentityTokenKind.Jwt;

    public ValidatedIdentity Validate(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            throw new IdentityTokenException(CorridorFaultSubcodes.InvalidToken, "JWT payload is empty.");
        }

        IReadOnlyList<SecurityKey> keys = _jwksProvider.GetSigningKeys();
        var parameters = new TokenValidationParameters
        {
            ValidIssuer = _issuer,
            ValidAudiences = new[] { _audience },
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = keys,
            // Resolve by whatever kid the token header carries; all cached keys are eligible.
            IssuerSigningKeyResolver = (_, _, _, _) => keys,
            ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256 },
            ClockSkew = TimeSpan.FromMinutes(1)
        };

        var handler = new JsonWebTokenHandler();
        // The dispatch inspector pipeline is synchronous; validation itself is
        // CPU work once the JWKS is cached, so blocking here is acceptable.
        TokenValidationResult result = handler.ValidateTokenAsync(payload, parameters).GetAwaiter().GetResult();
        if (!result.IsValid)
        {
            string detail = result.Exception?.Message ?? "unknown error";
            throw new IdentityTokenException(CorridorFaultSubcodes.InvalidToken, $"JWT validation failed: {detail}");
        }

        foreach (string claimName in UpnClaims)
        {
            if (result.Claims.TryGetValue(claimName, out object? value) && value is string text && text.Length > 0)
            {
                return new ValidatedIdentity(IdentityTokenKind.Jwt, text);
            }
        }

        throw new IdentityTokenException(CorridorFaultSubcodes.InvalidToken,
            "JWT carries none of the identity claims upn, preferred_username, sub.");
    }
}
