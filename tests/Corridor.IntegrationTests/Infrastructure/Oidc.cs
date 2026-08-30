using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Corridor.IntegrationTests.Infrastructure;

/// <summary>Tokens returned by a successful OIDC code exchange.</summary>
public sealed record OidcTokens(string AccessToken, string IdToken, string RefreshToken, string TokenType, int ExpiresIn, string Scope);

/// <summary>
/// Drives okta-sim's real OIDC surface the way a browser would: fetch the login form,
/// POST credentials with the hidden authorize fields, take the code from the redirect,
/// then exchange it at /token. PKCE S256 is computed here, not delegated.
/// </summary>
public static class Oidc
{
    public const string PortalClientId = "portal";
    public const string SpaClientId = "spa";
    public const string LegacyClientId = "legacy";
    public const string PortalSecret = "corridor-portal-secret";
    public const string LegacySecret = "corridor-legacy-secret";
    public const string DemoPassword = "Demo1234!";

    public static (string Verifier, string Challenge) CreatePkce()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    /// <summary>Full authorization code flow against okta-sim; returns the code, the echoed state, and the PKCE verifier used.</summary>
    public static async Task<(string Code, string State, string Verifier)> DriveCodeFlowAsync(
        Uri oktaBase,
        string clientId,
        string redirectUri,
        string username,
        string password,
        string scope,
        bool withPkce,
        string? expectedErrorOnLogin = null)
    {
        var (verifier, challenge) = CreatePkce();
        var state = Base64Url(RandomNumberGenerator.GetBytes(12));
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = scope,
            ["state"] = state,
        };
        if (withPkce)
        {
            query["code_challenge"] = challenge;
            query["code_challenge_method"] = "S256";
        }

        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var authorizeUri = new Uri(oktaBase, "/authorize?" + QueryString(query));
        using var loginPage = await http.GetAsync(authorizeUri);
        var loginHtml = await loginPage.Content.ReadAsStringAsync();
        if (!loginPage.IsSuccessStatusCode || !loginHtml.Contains("name=\"username\"", StringComparison.Ordinal))
        {
            Assert.Fail($"Expected the okta-sim login form, got HTTP {(int)loginPage.StatusCode}: {Truncate(loginHtml)}");
        }

        // Reproduce what a browser submits: the hidden authorize context plus credentials.
        var form = HtmlForms.ParseHiddenFields(loginHtml);
        form["username"] = username;
        form["password"] = password;
        using var login = new HttpRequestMessage(HttpMethod.Post, new Uri(oktaBase, "/authorize"))
        {
            Content = new FormUrlEncodedContent(form),
        };
        using var posted = await http.SendAsync(login);
        var body = await posted.Content.ReadAsStringAsync();

        if (expectedErrorOnLogin is not null)
        {
            Assert.Equal(401, (int)posted.StatusCode);
            Assert.Contains(expectedErrorOnLogin, body, StringComparison.Ordinal);
            return (string.Empty, state, verifier);
        }

        Assert.Equal(302, (int)posted.StatusCode);
        var location = posted.Headers.Location?.ToString() ?? string.Empty;
        Assert.False(string.IsNullOrEmpty(location),
            $"Login POST returned no redirect: HTTP {(int)posted.StatusCode} {Truncate(body)}");
        var redirect = ParseQuery(location);
        Assert.True(redirect.TryGetValue("code", out var code) && !string.IsNullOrEmpty(code),
            $"Redirect carried no code: {location}");
        Assert.Equal(state, redirect["state"]);
        return (code!, state, verifier);
    }

    public static async Task<OidcTokens> ExchangeCodeAsync(
        Uri oktaBase,
        string clientId,
        string? clientSecret,
        string code,
        string redirectUri,
        string? codeVerifier = null)
    {
        using var http = new HttpClient();
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
        };
        if (codeVerifier is not null)
        {
            form["code_verifier"] = codeVerifier;
        }
        if (clientSecret is not null)
        {
            var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{clientId}:{clientSecret}"));
            using var secretRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(oktaBase, "/token"))
            {
                Content = new FormUrlEncodedContent(form),
            };
            secretRequest.Headers.Authorization = new("Basic", basic);
            return await SendTokenRequestAsync(http, secretRequest);
        }
        form["client_id"] = clientId;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(oktaBase, "/token"))
        {
            Content = new FormUrlEncodedContent(form),
        };
        return await SendTokenRequestAsync(http, request);

    }

    private static async Task<OidcTokens> SendTokenRequestAsync(HttpClient http, HttpRequestMessage request)
    {
        using var response = await http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Token exchange failed HTTP {(int)response.StatusCode}: {Truncate(json)}");
        return ParseTokens(json);
    }

    public static async Task<string> ClientCredentialsTokenAsync(Uri oktaBase, string clientId, string clientSecret)
    {
        using var http = new HttpClient();
        var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{clientId}:{clientSecret}"));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(oktaBase, "/token"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
            }),
        };
        request.Headers.Authorization = new("Basic", basic);
        using var response = await http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"client_credentials grant failed HTTP {(int)response.StatusCode}: {Truncate(json)}");
        var tokens = ParseTokens(json);
        Assert.NotEmpty(tokens.AccessToken);
        return tokens.AccessToken;
    }

    public static async Task<JsonElement> UserinfoAsync(Uri oktaBase, string accessToken)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(oktaBase, "/userinfo"));
        request.Headers.Authorization = new("Bearer", accessToken);
        using var response = await http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"userinfo failed HTTP {(int)response.StatusCode}: {Truncate(json)}");
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    public static JsonElement DecodeJwtPayload(string jwt)
    {
        var parts = jwt.Split('.');
        Assert.True(parts.Length == 3, "The token is not a JWS compact serialization.");
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        switch (payload.Length % 4)
        {
            case 2: payload += "=="; break;
            case 3: payload += "="; break;
        }
        return JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload))).RootElement.Clone();
    }

    private static OidcTokens ParseTokens(string json)
    {
        var root = JsonDocument.Parse(json).RootElement;
        return new OidcTokens(
            RequireString(root, "access_token"),
            root.TryGetProperty("id_token", out var idToken) ? idToken.GetString()! : string.Empty,
            root.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString()! : string.Empty,
            RequireString(root, "token_type"),
            root.TryGetProperty("expires_in", out var expiresIn) ? expiresIn.GetInt32() : 0,
            root.TryGetProperty("scope", out var scope) ? scope.GetString()! : string.Empty);
    }

    private static string RequireString(JsonElement root, string name)
    {
        Assert.True(root.TryGetProperty(name, out var value) && value.GetString() is { Length: > 0 },
            $"The token response has no {name}.");
        return value.GetString()!;
    }

    private static string QueryString(Dictionary<string, string?> fields)
        => string.Join("&", fields.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value ?? string.Empty)}"));

    internal static Dictionary<string, string> ParseQuery(string url)
    {
        var query = url.Contains('?', StringComparison.Ordinal) ? url[..].Split('?', 2)[1] : url;
        var result = new Dictionary<string, string>();
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = pair.Split('=', 2);
            result[Uri.UnescapeDataString(pieces[0])] = pieces.Length == 2 ? Uri.UnescapeDataString(pieces[1]) : string.Empty;
        }
        return result;
    }

    internal static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Truncate(string value) => value.Length <= 500 ? value : value[..500];
}
