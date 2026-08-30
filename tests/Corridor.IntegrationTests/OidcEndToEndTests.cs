using System.Text.Json;
using Corridor.IntegrationTests.Infrastructure;

namespace Corridor.IntegrationTests;

/// <summary>
/// OIDC end to end against okta-sim: discovery, the login form POST for the
/// authorization code, PKCE S256 computed in the test, token exchange, userinfo.
/// </summary>
[Collection(CorridorStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class OidcEndToEndTests(CorridorStackFixture fixture)
{
    private const string SpaRedirect = "http://localhost:5173/callback";
    private const string PortalRedirect = "http://localhost:5200/signin-oidc";

    [Fact]
    public async Task Oidc_Discovery_AdvertisesTheCodeFlowWithS256()
    {
        using var http = fixture.CreateHttpClient();
        using var response = await http.GetAsync(new Uri(fixture.OktaBase, "/.well-known/openid-configuration"));
        Assert.True(response.IsSuccessStatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("http://localhost:8080", root.GetProperty("issuer").GetString());
        Assert.Equal("http://localhost:8080/token", root.GetProperty("token_endpoint").GetString());
        Assert.Contains("code", root.GetProperty("response_types_supported").EnumerateArray().Select(v => v.GetString()));
        Assert.Contains("S256", root.GetProperty("code_challenge_methods_supported").EnumerateArray().Select(v => v.GetString()));
        Assert.Contains("client_credentials", root.GetProperty("grant_types_supported").EnumerateArray().Select(v => v.GetString()));
    }

    [Fact]
    public async Task Oidc_PublicClient_PkceCodeFlow_EndsInTokensAndUserinfo()
    {
        var (code, state, verifier) = await Oidc.DriveCodeFlowAsync(
            fixture.OktaBase, Oidc.SpaClientId, SpaRedirect,
            "inspector@corridor.example", Oidc.DemoPassword, "openid profile email", withPkce: true);

        var tokens = await Oidc.ExchangeCodeAsync(
            fixture.OktaBase, Oidc.SpaClientId, clientSecret: null, code, SpaRedirect, verifier);

        Assert.Equal("Bearer", tokens.TokenType);
        Assert.NotEmpty(tokens.IdToken);
        Assert.Contains("openid", tokens.Scope, StringComparison.Ordinal);

        var payload = Oidc.DecodeJwtPayload(tokens.AccessToken);
        Assert.Equal("http://localhost:8080", payload.GetProperty("iss").GetString());
        Assert.Equal(Oidc.SpaClientId, payload.GetProperty("aud").GetString());
        Assert.Equal("inspector@corridor.example", payload.GetProperty("upn").GetString());
        Assert.Equal("Inspector", payload.GetProperty("role").GetString());

        var idPayload = Oidc.DecodeJwtPayload(tokens.IdToken);
        Assert.Equal(Oidc.SpaClientId, idPayload.GetProperty("aud").GetString());

        var userinfo = await Oidc.UserinfoAsync(fixture.OktaBase, tokens.AccessToken);
        Assert.Equal("inspector@corridor.example", userinfo.GetProperty("preferred_username").GetString());
        Assert.Equal("Inspector", userinfo.GetProperty("role").GetString());
        _ = state;
    }

    [Fact]
    public async Task Oidc_ConfidentialClient_CodeFlow_UsesBasicAuthentication()
    {
        var (code, _, _) = await Oidc.DriveCodeFlowAsync(
            fixture.OktaBase, Oidc.PortalClientId, PortalRedirect,
            "admin@corridor.example", Oidc.DemoPassword, "openid profile offline_access", withPkce: false);

        var tokens = await Oidc.ExchangeCodeAsync(
            fixture.OktaBase, Oidc.PortalClientId, Oidc.PortalSecret, code, PortalRedirect);

        Assert.NotEmpty(tokens.AccessToken);
        Assert.NotEmpty(tokens.RefreshToken);
        Assert.Equal(900, tokens.ExpiresIn);

        var payload = Oidc.DecodeJwtPayload(tokens.AccessToken);
        Assert.Equal("admin@corridor.example", payload.GetProperty("upn").GetString());
        Assert.Equal("Admin", payload.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Oidc_Pkce_WithWrongVerifier_IsRejected()
    {
        var (code, _, _) = await Oidc.DriveCodeFlowAsync(
            fixture.OktaBase, Oidc.SpaClientId, SpaRedirect,
            "officer@corridor.example", Oidc.DemoPassword, "openid profile", withPkce: true);

        var wrong = new string('x', 43);
        using var http = fixture.CreateHttpClient();
        var basic = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{Oidc.SpaClientId}:"));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(fixture.OktaBase, "/token"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = SpaRedirect,
                ["code_verifier"] = wrong,
                ["client_id"] = Oidc.SpaClientId,
            }),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Basic {basic}");
        using var response = await http.SendAsync(request);
        Assert.Equal(400, (int)response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("PKCE verification failed", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oidc_LoginForm_WithWrongPassword_Returns401AndNoCode()
    {
        var (code, _, _) = await Oidc.DriveCodeFlowAsync(
            fixture.OktaBase, Oidc.SpaClientId, SpaRedirect,
            "admin@corridor.example", "wrong-password", "openid profile", withPkce: true,
            expectedErrorOnLogin: "Sign-in failed");
        Assert.Equal(string.Empty, code);
    }

    [Fact]
    public async Task Oidc_Jwks_PublishesRsaKeysWithRotation()
    {
        using var http = fixture.CreateHttpClient();
        using var response = await http.GetAsync(new Uri(fixture.OktaBase, "/jwks"));
        Assert.True(response.IsSuccessStatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var keys = doc.RootElement.GetProperty("keys");
        Assert.True(keys.GetArrayLength() >= 2, "The JWKS should publish a current and a retired key.");
        Assert.All(keys.EnumerateArray().ToList(), key =>
        {
            Assert.Equal("RSA", key.GetProperty("kty").GetString());
            Assert.False(string.IsNullOrEmpty(key.GetProperty("kid").GetString()));
            Assert.Equal("RS256", key.GetProperty("alg").GetString());
        });
    }
}
