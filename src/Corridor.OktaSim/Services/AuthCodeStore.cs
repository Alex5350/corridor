using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Corridor.OktaSim.Services;

/// <summary>
/// Short-lived authorization codes (single use, five minutes). Stores the PKCE
/// challenge so /token can prove the verifier; nothing about the verifier itself.
/// </summary>
public sealed class AuthCodeStore
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, StoredAuthorizationCode> _codes = new(StringComparer.Ordinal);

    public sealed record StoredAuthorizationCode(
        string ClientId,
        string RedirectUri,
        string Scope,
        string Upn,
        string? CodeChallenge,
        string? Nonce,
        DateTime ExpiresAtUtc);

    public string Issue(StoredAuthorizationCode payload)
    {
        var code = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        _codes[code] = payload with { ExpiresAtUtc = DateTime.UtcNow.Add(Lifetime) };
        PruneExpired();
        return code;
    }

    /// <summary>Atomically consumes a code; codes are single use per the spec.</summary>
    public StoredAuthorizationCode? TryConsume(string code)
    {
        if (!_codes.TryRemove(code, out var stored))
        {
            return null;
        }
        return stored.ExpiresAtUtc <= DateTime.UtcNow ? null : stored;
    }

    private void PruneExpired()
    {
        foreach (var (code, stored) in _codes)
        {
            if (stored.ExpiresAtUtc <= DateTime.UtcNow)
            {
                _codes.TryRemove(code, out _);
            }
        }
    }
}
