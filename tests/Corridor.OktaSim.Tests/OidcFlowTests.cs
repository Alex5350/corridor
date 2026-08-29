using System.Text;
using System.Text.Json.Nodes;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Corridor.OktaSim.Tests;

/// <summary>
/// The authorization-code flow, honestly exercised: PKCE happy path and tampered
/// verifier, code single use, confidential client authentication, refresh
/// rotation with reuse detection, service tokens, userinfo, and logout.
/// Access and id tokens are validated against the JWKS the server publishes.
/// </summary>
public class OidcFlowTests(OktaSimFactory factory) : IClassFixture<OktaSimFactory>
{
    private const string SpaRedirect = "http://localhost:5173/callback";
    private const string PortalRedirect = "http://localhost:5200/signin-oidc";

    private readonly OktaSimFactory _factory = factory;

    private static async Task<JsonWebKeySet> FetchJwksAsync(HttpClient client)
    {
        var jwksJson = await (await client.GetAsync("/jwks")).Content.ReadAsStringAsync();
        return new JsonWebKeySet(jwksJson);
    }

    private static async Task<string> AuthorizeSpaAsync(
        HttpClient client, string verifier, string loginHint, string scope = "openid profile", string nonce = "n")
    {
        var authorize = await client.GetAsync(
            "/authorize?client_id=spa&redirect_uri=" + Uri.EscapeDataString(SpaRedirect)
            + "&response_type=code&scope=" + Uri.EscapeDataString(scope)
            + "&state=s&nonce=" + Uri.EscapeDataString(nonce)
            + "&code_challenge=" + TestHelpers.ChallengeFrom(verifier)
            + "&code_challenge_method=S256&login_hint=" + Uri.EscapeDataString(loginHint));
        return TokenHarness.QueryValue(authorize.Headers.Location!.Query, "code");
    }

    private static FormUrlEncodedContent CodeGrant(string code, string verifier) => new(new Dictionary<string, string>
    {
        ["grant_type"] = "authorization_code",
        ["code"] = code,
        ["client_id"] = "spa",
        ["redirect_uri"] = SpaRedirect,
        ["code_verifier"] = verifier,
    });

    [Fact]
    public async Task Authorize_Pkce_Happy_Path_Redirects_With_Code_And_State()
    {
        var client = _factory.CreateNoRedirectClient();
        var verifier = TestHelpers.CreatePkceVerifier();
        var response = await client.GetAsync(
            "/authorize?client_id=spa&redirect_uri=" + Uri.EscapeDataString(SpaRedirect)
            + "&response_type=code&scope=" + Uri.EscapeDataString("openid profile offline_access")
            + "&state=state-42&nonce=nonce-7&code_challenge=" + TestHelpers.ChallengeFrom(verifier)
            + "&code_challenge_method=S256&login_hint=inspector@corridor.example");

        Assert.Equal(System.Net.HttpStatusCode.Found, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.StartsWith(SpaRedirect, location.ToString());
        Assert.Equal("state-42", TokenHarness.QueryValue(location.Query, "state"));
        Assert.Equal("http://localhost:8080", TokenHarness.QueryValue(location.Query, "iss"));
        Assert.False(string.IsNullOrEmpty(TokenHarness.QueryValue(location.Query, "code")));
    }

    [Fact]
    public async Task Token_Exchange_Mints_Tokens_That_Validate_Against_The_Jwks()
    {
        var client = _factory.CreateNoRedirectClient();
        var verifier = TestHelpers.CreatePkceVerifier();
        var code = await AuthorizeSpaAsync(client, verifier, "officer@corridor.example",
            "openid profile offline_access", nonce: "nonce-7");

        var tokenResponse = await client.PostAsync("/token", CodeGrant(code, verifier));

        Assert.Equal(System.Net.HttpStatusCode.OK, tokenResponse.StatusCode);
        var payload = JsonNode.Parse(await tokenResponse.Content.ReadAsStringAsync())!;
        var accessToken = payload["access_token"]!.GetValue<string>();
        var idToken = payload["id_token"]!.GetValue<string>();
        Assert.Equal("Bearer", payload["token_type"]!.GetValue<string>());
        Assert.Equal(900, payload["expires_in"]!.GetValue<int>());
        Assert.False(string.IsNullOrEmpty(payload["refresh_token"]!.GetValue<string>()));

        var jwks = await FetchJwksAsync(client);
        var handler = new JsonWebTokenHandler();

        var accessResult = await handler.ValidateTokenAsync(accessToken, new TokenValidationParameters
        {
            ValidIssuer = "http://localhost:8080",
            ValidAudience = "spa",
            IssuerSigningKeys = jwks.GetSigningKeys(),
            ValidTypes = ["at+jwt"],
        });
        Assert.True(accessResult.IsValid, accessResult.Exception?.ToString());
        var accessJwt = (JsonWebToken)accessResult.SecurityToken;
        Assert.Equal("officer@corridor.example", accessJwt.GetClaim("upn")!.Value);
        Assert.Equal("Officer", accessJwt.GetClaim("role")!.Value);
        Assert.Equal(TimeSpan.FromMinutes(15), accessJwt.ValidTo - accessJwt.IssuedAt);

        var idResult = await handler.ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidIssuer = "http://localhost:8080",
            ValidAudience = "spa",
            IssuerSigningKeys = jwks.GetSigningKeys(),
        });
        Assert.True(idResult.IsValid, idResult.Exception?.ToString());
        var idJwt = (JsonWebToken)idResult.SecurityToken;
        Assert.Equal("nonce-7", idJwt.GetClaim("nonce")!.Value);
        Assert.Equal(TimeSpan.FromMinutes(60), idJwt.ValidTo - idJwt.IssuedAt);
    }

    [Fact]
    public async Task Token_Rejects_Tampered_Pkce_Verifier()
    {
        var client = _factory.CreateNoRedirectClient();
        var verifier = TestHelpers.CreatePkceVerifier();
        var code = await AuthorizeSpaAsync(client, verifier, "clerk@corridor.example");

        var tampered = verifier[..^2] + (verifier[^1] == 'A' ? 'B' : 'A');
        var response = await client.PostAsync("/token", CodeGrant(code, tampered));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("invalid_grant", (string?)payload["error"]);
    }

    [Fact]
    public async Task Token_Rejects_Unknown_And_Reused_Codes()
    {
        var client = _factory.CreateNoRedirectClient();

        var bogus = await client.PostAsync("/token", CodeGrant("no-such-code", TestHelpers.CreatePkceVerifier()));
        var bogusPayload = JsonNode.Parse(await bogus.Content.ReadAsStringAsync())!;
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, bogus.StatusCode);
        Assert.Equal("invalid_grant", (string?)bogusPayload["error"]);

        // A real code must be single use: the second redemption fails.
        var verifier = TestHelpers.CreatePkceVerifier();
        var code = await AuthorizeSpaAsync(client, verifier, "clerk@corridor.example");
        Assert.Equal(System.Net.HttpStatusCode.OK,
            (await client.PostAsync("/token", CodeGrant(code, verifier))).StatusCode);
        var replay = await client.PostAsync("/token", CodeGrant(code, verifier));
        var replayPayload = JsonNode.Parse(await replay.Content.ReadAsStringAsync())!;
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Equal("invalid_grant", (string?)replayPayload["error"]);
    }

    [Fact]
    public async Task Token_Rejects_Wrong_Confidential_Client_Secret()
    {
        using var wrongSecret = _factory.CreateNoRedirectClient();
        wrongSecret.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("portal:wrong-secret")));

        var response = await wrongSecret.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = "whatever",
            ["redirect_uri"] = PortalRedirect,
        }));

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotNull(response.Headers.WwwAuthenticate);
        var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("invalid_client", (string?)payload["error"]);
    }

    [Fact]
    public async Task Confidential_Client_Exchange_Works_With_Basic_Auth_And_No_Pkce()
    {
        var client = _factory.CreateNoRedirectClient();
        var authorize = await client.GetAsync(
            "/authorize?client_id=portal&redirect_uri=" + Uri.EscapeDataString(PortalRedirect)
            + "&response_type=code&scope=" + Uri.EscapeDataString("openid profile offline_access")
            + "&state=s&login_hint=admin@corridor.example");
        Assert.Equal(System.Net.HttpStatusCode.Found, authorize.StatusCode);
        var code = TokenHarness.QueryValue(authorize.Headers.Location!.Query, "code");

        using var portal = _factory.CreateNoRedirectClient();
        portal.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("portal:corridor-portal-secret")));
        var response = await portal.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = PortalRedirect,
        }));

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.False(string.IsNullOrEmpty(payload["access_token"]!.GetValue<string>()));
        Assert.False(string.IsNullOrEmpty(payload["id_token"]!.GetValue<string>()));
        Assert.False(string.IsNullOrEmpty(payload["refresh_token"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Authorize_Rejects_Public_Client_Without_Pkce()
    {
        var client = _factory.CreateNoRedirectClient();
        var response = await client.GetAsync(
            "/authorize?client_id=spa&redirect_uri=" + Uri.EscapeDataString(SpaRedirect)
            + "&response_type=code&scope=openid&state=no-pkce&login_hint=clerk@corridor.example");

        Assert.Equal(System.Net.HttpStatusCode.Found, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.StartsWith(SpaRedirect, location.ToString());
        Assert.Equal("invalid_request", TokenHarness.QueryValue(location.Query, "error"));
        Assert.Equal("no-pkce", TokenHarness.QueryValue(location.Query, "state"));
    }

    [Fact]
    public async Task Refresh_Tokens_Rotate_And_Reuse_Revokes_The_Family()
    {
        var client = _factory.CreateNoRedirectClient();
        var verifier = TestHelpers.CreatePkceVerifier();
        var code = await AuthorizeSpaAsync(client, verifier, "inspector@corridor.example",
            "openid profile offline_access");

        var first = await client.PostAsync("/token", CodeGrant(code, verifier));
        var firstPayload = JsonNode.Parse(await first.Content.ReadAsStringAsync())!;
        var originalRefresh = firstPayload["refresh_token"]!.GetValue<string>();

        var rotated = await client.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = originalRefresh,
            ["client_id"] = "spa",
        }));
        Assert.Equal(System.Net.HttpStatusCode.OK, rotated.StatusCode);
        var rotatedPayload = JsonNode.Parse(await rotated.Content.ReadAsStringAsync())!;
        var rotatedRefresh = rotatedPayload["refresh_token"]!.GetValue<string>();
        Assert.NotEqual(originalRefresh, rotatedRefresh);
        Assert.False(string.IsNullOrEmpty(rotatedPayload["access_token"]!.GetValue<string>()));

        // Replaying the consumed original must fail AND revoke its rotation.
        var replay = await client.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = originalRefresh,
            ["client_id"] = "spa",
        }));
        var replayPayload = JsonNode.Parse(await replay.Content.ReadAsStringAsync())!;
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Equal("invalid_grant", (string?)replayPayload["error"]);

        var revokedUse = await client.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = rotatedRefresh,
            ["client_id"] = "spa",
        }));
        var revokedPayload = JsonNode.Parse(await revokedUse.Content.ReadAsStringAsync())!;
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, revokedUse.StatusCode);
        Assert.Equal("invalid_grant", (string?)revokedPayload["error"]);
    }

    [Fact]
    public async Task Client_Credentials_Issues_Legacy_Service_Token()
    {
        using var legacy = _factory.CreateNoRedirectClient();
        legacy.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("legacy:corridor-legacy-secret")));
        var response = await legacy.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
        }));

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var accessToken = payload["access_token"]!.GetValue<string>();
        Assert.Equal("corridor.service", payload["scope"]!.GetValue<string>());

        var jwks = await FetchJwksAsync(legacy);
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(accessToken, new TokenValidationParameters
        {
            ValidIssuer = "http://localhost:8080",
            ValidAudience = "legacy",
            IssuerSigningKeys = jwks.GetSigningKeys(),
            ValidTypes = ["at+jwt"],
        });
        Assert.True(result.IsValid, result.Exception?.ToString());
        var jwt = (JsonWebToken)result.SecurityToken;
        Assert.Equal("legacy", jwt.Subject);
    }

    [Fact]
    public async Task Userinfo_Returns_Claims_With_Bearer_And_Rejects_Without()
    {
        var client = _factory.CreateNoRedirectClient();

        var anonymous = await client.GetAsync("/userinfo");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var accessToken = await TokenHarness.GetSpaAccessTokenAsync(client);
        using var bearer = _factory.CreateNoRedirectClient();
        bearer.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        var userinfo = await bearer.GetAsync("/userinfo");

        Assert.Equal(System.Net.HttpStatusCode.OK, userinfo.StatusCode);
        var claims = JsonNode.Parse(await userinfo.Content.ReadAsStringAsync())!;
        Assert.Equal("inspector@corridor.example", (string?)claims["preferred_username"]);
        Assert.Equal("Inspector", (string?)claims["role"]);
        Assert.NotNull(claims["sub"]);

        using var forged = _factory.CreateNoRedirectClient();
        forged.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not.a.jwt");
        var rejected = await forged.GetAsync("/userinfo");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, rejected.StatusCode);
    }

    [Fact]
    public async Task Logout_Redirects_Only_To_Registered_Post_Logout_Uri()
    {
        var client = _factory.CreateNoRedirectClient();

        var registered = await client.GetAsync(
            "/logout?client_id=portal&post_logout_redirect_uri=" + Uri.EscapeDataString("http://localhost:5200/") + "&state=bye");
        Assert.Equal(System.Net.HttpStatusCode.Found, registered.StatusCode);
        Assert.StartsWith("http://localhost:5200/", registered.Headers.Location!.ToString());
        Assert.Contains("state=bye", registered.Headers.Location!.ToString());

        var unregistered = await client.GetAsync(
            "/logout?client_id=portal&post_logout_redirect_uri=" + Uri.EscapeDataString("http://evil.example/"));
        Assert.Equal(System.Net.HttpStatusCode.OK, unregistered.StatusCode);
    }

    [Fact]
    public async Task Authorize_Shows_Login_Form_Without_Login_Hint()
    {
        var client = _factory.CreateNoRedirectClient();
        var response = await client.GetAsync(
            "/authorize?client_id=portal&redirect_uri=" + Uri.EscapeDataString(PortalRedirect)
            + "&response_type=code&scope=openid&state=form-login");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Sign in", html);
        Assert.Contains("name=\"client_id\" value=\"portal\"", html);
        Assert.Contains("name=\"state\" value=\"form-login\"", html);
    }

    [Fact]
    public async Task Authorize_Form_Login_Issues_Code_With_Demo_Password()
    {
        var client = _factory.CreateNoRedirectClient();
        var response = await client.PostAsync("/authorize", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = "portal",
            ["redirect_uri"] = PortalRedirect,
            ["response_type"] = "code",
            ["scope"] = "openid profile",
            ["state"] = "form-state",
            ["username"] = "officer@corridor.example",
            ["password"] = "Demo1234!",
        }));

        Assert.Equal(System.Net.HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("code=", response.Headers.Location!.Query);
        Assert.Equal("form-state", TokenHarness.QueryValue(response.Headers.Location!.Query, "state"));

        var badPassword = await client.PostAsync("/authorize", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = "portal",
            ["redirect_uri"] = PortalRedirect,
            ["response_type"] = "code",
            ["scope"] = "openid profile",
            ["username"] = "officer@corridor.example",
            ["password"] = "WrongPassword1!",
        }));
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, badPassword.StatusCode);
    }
}
