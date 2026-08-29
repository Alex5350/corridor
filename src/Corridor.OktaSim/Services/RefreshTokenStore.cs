using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Corridor.OktaSim.Services;

/// <summary>
/// Refresh token family store: tokens rotate on every redemption and reuse of a
/// consumed token revokes the whole family (OAuth refresh-token reuse detection).
/// </summary>
public sealed class RefreshTokenStore
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(8);

    public enum RedeemResult
    {
        Redeemed,
        ExpiredOrUnknown,
        ReuseDetected,
    }

    public sealed record RefreshGrant(
        string Value,
        string FamilyId,
        string ClientId,
        string Upn,
        string Scope,
        DateTime ExpiresAtUtc,
        bool Consumed,
        bool Revoked);

    private readonly ConcurrentDictionary<string, RefreshGrant> _grants = new(StringComparer.Ordinal);

    public string Issue(string clientId, string upn, string scope, string? familyId = null)
    {
        var family = familyId ?? Guid.NewGuid().ToString("N");
        var value = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        _grants[value] = new RefreshGrant(value, family, clientId, upn, scope, DateTime.UtcNow.Add(Lifetime), Consumed: false, Revoked: false);
        return value;
    }

    public (RedeemResult Result, RefreshGrant? Grant) Redeem(string token)
    {
        if (!_grants.TryGetValue(token, out var grant)
            || grant.Revoked
            || grant.ExpiresAtUtc <= DateTime.UtcNow)
        {
            return (RedeemResult.ExpiredOrUnknown, null);
        }

        if (grant.Consumed)
        {
            // Replay of an already-rotated token: kill the family it descended from.
            foreach (var (value, sibling) in _grants)
            {
                if (string.Equals(sibling.FamilyId, grant.FamilyId, StringComparison.Ordinal))
                {
                    _grants[value] = sibling with { Revoked = true };
                }
            }
            return (RedeemResult.ReuseDetected, null);
        }

        _grants[token] = grant with { Consumed = true };
        var rotated = Issue(grant.ClientId, grant.Upn, grant.Scope, grant.FamilyId);
        return (RedeemResult.Redeemed, _grants[rotated]);
    }

    public bool IsUsable(string token) =>
        _grants.TryGetValue(token, out var grant)
        && !grant.Consumed
        && !grant.Revoked
        && grant.ExpiresAtUtc > DateTime.UtcNow;
}
