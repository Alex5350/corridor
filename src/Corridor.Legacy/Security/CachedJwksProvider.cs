using Microsoft.IdentityModel.Tokens;

namespace Corridor.Legacy.Security;

/// <summary>
/// Provides the okta-sim signing keys (JWKS). Implementations cache; the JWT
/// strategy asks for keys on every validation.
/// </summary>
public interface IJwksProvider
{
    IReadOnlyList<SecurityKey> GetSigningKeys();
}

/// <summary>
/// Fetches the JWKS document over HTTP and caches the parsed signing keys for
/// a configurable time to live (default 15 minutes). The dispatch inspector
/// pipeline is synchronous, so the fetch blocks the calling request thread at
/// most once per cache window per process.
/// </summary>
public sealed class CachedJwksProvider : IJwksProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _jwksUrl;
    private readonly TimeSpan _timeToLive;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private IReadOnlyList<SecurityKey> _cachedKeys = Array.Empty<SecurityKey>();
    private DateTimeOffset _fetchedAt;

    public CachedJwksProvider(HttpClient httpClient, string jwksUrl, TimeSpan timeToLive, TimeProvider? clock = null)
    {
        _httpClient = httpClient;
        _jwksUrl = jwksUrl;
        _timeToLive = timeToLive;
        _clock = clock ?? TimeProvider.System;
    }

    public IReadOnlyList<SecurityKey> GetSigningKeys()
    {
        if (IsFresh())
        {
            return _cachedKeys;
        }

        lock (_gate)
        {
            if (IsFresh())
            {
                return _cachedKeys;
            }

            _cachedKeys = Fetch();
            _fetchedAt = _clock.GetUtcNow();
            return _cachedKeys;
        }
    }

    private bool IsFresh() => _cachedKeys.Count > 0 && _clock.GetUtcNow() - _fetchedAt < _timeToLive;

    private IReadOnlyList<SecurityKey> Fetch()
    {
        string json;
        try
        {
            json = _httpClient.GetStringAsync(_jwksUrl).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            throw new IdentityTokenException(CorridorFaultSubcodes.InvalidToken,
                $"Could not fetch JWKS from {_jwksUrl}: {exception.Message}");
        }

        JsonWebKeySet keySet;
        try
        {
            keySet = new JsonWebKeySet(json);
        }
        catch (Exception exception)
        {
            throw new IdentityTokenException(CorridorFaultSubcodes.InvalidToken,
                $"JWKS document at {_jwksUrl} is not valid JSON: {exception.Message}");
        }

        List<SecurityKey> keys = keySet.GetSigningKeys().ToList();
        if (keys.Count == 0)
        {
            throw new IdentityTokenException(CorridorFaultSubcodes.InvalidToken,
                $"JWKS document at {_jwksUrl} contained no signing keys.");
        }

        return keys;
    }
}
