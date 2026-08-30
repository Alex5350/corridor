using System.Security.Cryptography;
using Corridor.Legacy.Security;
using Corridor.Legacy.Tests.TestDoubles;

namespace Corridor.Legacy.Tests;

// JWKS retrieval and the 15 minute cache.

public class JwksProviderTests : IDisposable
{
    private readonly RSA _signingKey = RSA.Create(2048);

    [Fact]
    public void Keys_are_fetched_once_per_cache_window()
    {
        string jwks = TestJwt.CreateJwks(_signingKey);
        var handler = new FakeHttpMessageHandler(_ => jwks);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero));
        var provider = new CachedJwksProvider(new HttpClient(handler), TestJwt.Issuer + "/jwks", TimeSpan.FromMinutes(15), clock);

        Assert.NotEmpty(provider.GetSigningKeys());
        Assert.NotEmpty(provider.GetSigningKeys());
        Assert.Equal(1, handler.RequestCount);

        // Still inside the window: no refetch.
        clock.Advance(TimeSpan.FromMinutes(14));
        Assert.NotEmpty(provider.GetSigningKeys());
        Assert.Equal(1, handler.RequestCount);

        // Past the window: refetch.
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.NotEmpty(provider.GetSigningKeys());
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public void EmptyKeySet_is_rejected()
    {
        var handler = new FakeHttpMessageHandler(_ => "{\"keys\":[]}");
        var provider = new CachedJwksProvider(new HttpClient(handler), TestJwt.Issuer + "/jwks", TimeSpan.FromMinutes(15));

        IdentityTokenException exception = Assert.Throws<IdentityTokenException>(() => provider.GetSigningKeys());
        Assert.Equal(CorridorFaultSubcodes.InvalidToken, exception.Subcode);
    }

    [Fact]
    public void UnreachableJwks_is_rejected_with_a_token_fault()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("connection refused"));
        var provider = new CachedJwksProvider(new HttpClient(handler), TestJwt.Issuer + "/jwks", TimeSpan.FromMinutes(15));

        IdentityTokenException exception = Assert.Throws<IdentityTokenException>(() => provider.GetSigningKeys());
        Assert.Equal(CorridorFaultSubcodes.InvalidToken, exception.Subcode);
        Assert.Contains("JWKS", exception.Message, StringComparison.Ordinal);
    }

    public void Dispose() => _signingKey.Dispose();
}
