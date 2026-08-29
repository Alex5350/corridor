using Corridor.OktaSim.Models;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Corridor.OktaSim.Services;

/// <summary>
/// Mints and validates the simulated org's RS256 tokens: access tokens (15 min),
/// id tokens (60 min, nonce honored), and service tokens for the legacy client's
/// client-credentials grant. Signing is always the current kid; the retired kid
/// stays published in the JWKS so relying parties exercise rotation handling.
/// </summary>
public sealed class TokenService
{
    public static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan IdTokenLifetime = TimeSpan.FromMinutes(60);

    private readonly SigningKeys _keys;
    private readonly ClientRegistry _clients;
    private readonly string _issuer;
    private readonly ILogger<TokenService> _logger;
    private readonly JsonWebTokenHandler _handler = new();

    public TokenService(SigningKeys keys, ClientRegistry clients, IConfiguration config, ILogger<TokenService> logger)
    {
        _keys = keys;
        _clients = clients;
        _issuer = config["OktaSim:Issuer"] ?? "http://localhost:8080";
        _logger = logger;
    }

    public string Issuer => _issuer;

    public string CreateAccessToken(DirectoryUser user, OAuthClient client, string scope)
    {
        var now = DateTimeOffset.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _issuer,
            Audience = client.ClientId,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = now.Add(AccessTokenLifetime).UtcDateTime,
            SigningCredentials = new SigningCredentials(_keys.Current, SecurityAlgorithms.RsaSha256),
            TokenType = "at+jwt",
            Claims = new Dictionary<string, object>
            {
                ["sub"] = user.Id,
                ["upn"] = user.UserName,
                ["preferred_username"] = user.UserName,
                ["name"] = user.DisplayName,
                ["email"] = user.Email,
                ["email_verified"] = true,
                ["role"] = user.Role,
                ["groups"] = user.Groups,
                ["azp"] = client.ClientId,
                ["scope"] = scope,
                ["jti"] = Guid.NewGuid().ToString("N"),
            },
        };
        return _handler.CreateToken(descriptor);
    }

    public string CreateIdToken(DirectoryUser user, OAuthClient client, string? nonce)
    {
        var now = DateTimeOffset.UtcNow;
        var claims = new Dictionary<string, object>
        {
            ["sub"] = user.Id,
            ["upn"] = user.UserName,
            ["preferred_username"] = user.UserName,
            ["name"] = user.DisplayName,
            ["email"] = user.Email,
            ["email_verified"] = true,
            ["role"] = user.Role,
            ["groups"] = user.Groups,
            ["auth_time"] = now.ToUnixTimeSeconds(),
            ["jti"] = Guid.NewGuid().ToString("N"),
        };
        if (!string.IsNullOrEmpty(nonce))
        {
            claims["nonce"] = nonce;
        }
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _issuer,
            Audience = client.ClientId,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = now.Add(IdTokenLifetime).UtcDateTime,
            SigningCredentials = new SigningCredentials(_keys.Current, SecurityAlgorithms.RsaSha256),
            Claims = claims,
        };
        return _handler.CreateToken(descriptor);
    }

    public string CreateServiceToken(OAuthClient client)
    {
        var now = DateTimeOffset.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _issuer,
            Audience = client.ClientId,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = now.Add(AccessTokenLifetime).UtcDateTime,
            SigningCredentials = new SigningCredentials(_keys.Current, SecurityAlgorithms.RsaSha256),
            TokenType = "at+jwt",
            Claims = new Dictionary<string, object>
            {
                ["sub"] = client.ClientId,
                ["client_id"] = client.ClientId,
                ["scope"] = "corridor.service",
                ["jti"] = Guid.NewGuid().ToString("N"),
            },
        };
        return _handler.CreateToken(descriptor);
    }

    /// <summary>
    /// Validates a bearer token against the current signing key. Audiences are the
    /// registered client ids so tokens from one client cannot be replayed at another.
    /// </summary>
    public async Task<(JsonWebToken? Token, string? Error)> ValidateAccessTokenAsync(string accessToken)
    {
        var audiences = _clients.All().Select(c => c.ClientId).ToArray();
        var parameters = new TokenValidationParameters
        {
            ValidIssuer = _issuer,
            ValidAudiences = audiences,
            IssuerSigningKey = _keys.Current,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            ValidTypes = ["at+jwt"],
        };
        var result = await _handler.ValidateTokenAsync(accessToken, parameters);
        if (!result.IsValid)
        {
            _logger.LogInformation("Bearer token rejected: {Reason}", result.Exception?.Message);
            return (null, "invalid_token");
        }
        return ((JsonWebToken)result.SecurityToken, null);
    }
}
