using Microsoft.Extensions.Options;

namespace Corridor.AdfsSim;

public sealed record RegisteredRelyingParty(string Name, string Issuer, string AcsUrl, string Audience);

/// <summary>Relying party registry loaded from appsettings. AuthnRequests are matched by
/// Issuer first, then by the AssertionConsumerServiceURL the SP carried in the request.</summary>
public sealed class RelyingPartyRegistry
{
    private readonly List<RegisteredRelyingParty> _parties;

    public RelyingPartyRegistry(IOptions<AdfsSimOptions> options)
    {
        _parties = [.. options.Value.RelyingParties
            .Where(p => !string.IsNullOrWhiteSpace(p.Issuer) && !string.IsNullOrWhiteSpace(p.AcsUrl))
            .Select(p => new RegisteredRelyingParty(
                p.Name,
                p.Issuer.TrimEnd('/'),
                p.AcsUrl,
                (p.Audience ?? p.Issuer).TrimEnd('/')))];
    }

    public IReadOnlyList<RegisteredRelyingParty> All => _parties;

    public RegisteredRelyingParty? Find(string? issuer, string? assertionConsumerServiceUrl)    {
        if (!string.IsNullOrWhiteSpace(issuer))
        {
            var normalized = issuer.TrimEnd('/');
            var byIssuer = _parties.FirstOrDefault(p => string.Equals(p.Issuer, normalized, StringComparison.OrdinalIgnoreCase));
            if (byIssuer is not null)
            {
                // An ACS carried in the request must match the registered one, otherwise refuse.
                if (!string.IsNullOrWhiteSpace(assertionConsumerServiceUrl) &&
                    !string.Equals(byIssuer.AcsUrl, assertionConsumerServiceUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return byIssuer;
            }
        }

        if (!string.IsNullOrWhiteSpace(assertionConsumerServiceUrl))
        {
            var normalizedAcs = assertionConsumerServiceUrl.TrimEnd('/');
            return _parties.FirstOrDefault(p => string.Equals(p.AcsUrl, normalizedAcs, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }
}
