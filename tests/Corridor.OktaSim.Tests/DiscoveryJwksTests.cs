using System.Text;
using System.Text.Json.Nodes;
using Microsoft.IdentityModel.Tokens;

namespace Corridor.OktaSim.Tests;

/// <summary>Discovery document shape and the two-kid JWKS.</summary>
public class DiscoveryJwksTests(OktaSimFactory factory) : IClassFixture<OktaSimFactory>
{
    private readonly OktaSimFactory _factory = factory;

    private static IReadOnlyList<string?> Strings(JsonNode? node) =>
        ((JsonArray)node!).Select(item => (string?)item).ToArray();

    [Fact]
    public async Task Discovery_Document_Is_Complete()
    {
        var client = _factory.CreateNoRedirectClient();
        var response = await client.GetAsync("/.well-known/openid-configuration");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var doc = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("http://localhost:8080", (string?)doc["issuer"]);
        Assert.Equal("http://localhost:8080/authorize", (string?)doc["authorization_endpoint"]);
        Assert.Equal("http://localhost:8080/token", (string?)doc["token_endpoint"]);
        Assert.Equal("http://localhost:8080/jwks", (string?)doc["jwks_uri"]);
        Assert.Equal("http://localhost:8080/userinfo", (string?)doc["userinfo_endpoint"]);
        Assert.Equal("http://localhost:8080/logout", (string?)doc["end_session_endpoint"]);
        Assert.Contains("authorization_code", Strings(doc["grant_types_supported"]));
        Assert.Contains("refresh_token", Strings(doc["grant_types_supported"]));
        Assert.Contains("client_credentials", Strings(doc["grant_types_supported"]));
        Assert.Contains("openid", Strings(doc["scopes_supported"]));
        Assert.Contains("S256", Strings(doc["code_challenge_methods_supported"]));
        Assert.Contains("code", Strings(doc["response_types_supported"]));
    }

    [Fact]
    public async Task Jwks_Publishes_Two_Rsa_Kids_Including_The_Current_One()
    {
        var client = _factory.CreateNoRedirectClient();
        var response = await client.GetAsync("/jwks");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var doc = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var keys = (JsonArray)doc["keys"]!;
        Assert.Equal(2, keys.Count);

        var kids = keys.Select(k => (string?)k!["kid"]).ToArray();
        Assert.Contains("okta-sim-2026-08", kids);
        Assert.Equal(2, kids.Distinct().Count());
        Assert.All(keys, key =>
        {
            Assert.Equal("RSA", (string?)key!["kty"]);
            Assert.Equal("RS256", (string?)key["alg"]);
            Assert.Equal("sig", (string?)key["use"]);
            Assert.False(string.IsNullOrEmpty((string?)key["n"]), "modulus missing");
            Assert.False(string.IsNullOrEmpty((string?)key["e"]), "exponent missing");
        });

        // Tokens are signed by the current kid, so relying parties can always verify.
        var accessToken = await TokenHarness.GetSpaAccessTokenAsync(client);
        var header = accessToken.Split('.')[0];
        var decodedHeader = Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(header));
        Assert.Contains("okta-sim-2026-08", decodedHeader);
    }
}

/// <summary>Shared helper: run the SPA PKCE dance and return the access token.</summary>
internal static class TokenHarness
{
    public static async Task<string> GetSpaAccessTokenAsync(HttpClient client)
    {
        var verifier = TestHelpers.CreatePkceVerifier();
        var authorize = await client.GetAsync(
            "/authorize?client_id=spa&redirect_uri=" + Uri.EscapeDataString("http://localhost:5173/callback")
            + "&response_type=code&scope=" + Uri.EscapeDataString("openid profile")
            + "&state=s&nonce=n&code_challenge=" + TestHelpers.ChallengeFrom(verifier)
            + "&code_challenge_method=S256&login_hint=inspector@corridor.example");
        var code = QueryValue(authorize.Headers.Location!.Query, "code");
        var tokenResponse = await client.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = "spa",
            ["redirect_uri"] = "http://localhost:5173/callback",
            ["code_verifier"] = verifier,
        }));
        tokenResponse.EnsureSuccessStatusCode();
        var payload = JsonNode.Parse(await tokenResponse.Content.ReadAsStringAsync())!;
        return payload["access_token"]!.GetValue<string>();
    }

    public static string QueryValue(string query, string key)
    {
        var parameters = System.Web.HttpUtility.ParseQueryString(query);
        var value = parameters[key];
        Assert.False(string.IsNullOrEmpty(value), $"query parameter {key} missing from {query}");
        return value;
    }
}
