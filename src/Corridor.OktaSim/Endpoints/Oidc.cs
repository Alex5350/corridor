using System.Security.Cryptography;
using System.Text;
using Corridor.OktaSim.Models;
using Corridor.OktaSim.Services;
using Corridor.OktaSim.Stores;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Corridor.OktaSim.Endpoints;

/// <summary>
/// OIDC provider surface: discovery, authorize (code + PKCE), token (code,
/// refresh rotation, client credentials), JWKS with a rotating kid, userinfo,
/// and logout. The authorize endpoint also serves a minimal login form for
/// synthetic users; login_hint short-circuits it for scripted demo flows.
/// Every endpoint here carries the "spa" CORS policy so the SPA's browser-side
/// oidc-client-ts can reach it cross-origin; no other endpoint group does.
/// </summary>
public static class OidcEndpoints
{
    /// <summary>Name of the CORS policy (defined in Program) that lets the browser SPA run OIDC against this simulator.</summary>
    public const string SpaCorsPolicy = "spa";

    /// <summary>Default SPA origin allowed by the CORS policy when OktaSim:SpaOrigins is not configured.</summary>
    public const string DefaultSpaOrigin = "http://localhost:5173";

    public static IEndpointRouteBuilder MapOidcEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/openid-configuration", (TokenService tokens) =>
        {
            var issuer = tokens.Issuer;
            return Results.Json(new Dictionary<string, object>
            {
                ["issuer"] = issuer,
                ["authorization_endpoint"] = $"{issuer}/authorize",
                ["token_endpoint"] = $"{issuer}/token",
                ["userinfo_endpoint"] = $"{issuer}/userinfo",
                ["jwks_uri"] = $"{issuer}/jwks",
                ["end_session_endpoint"] = $"{issuer}/logout",
                ["response_types_supported"] = new[] { "code" },
                ["response_modes_supported"] = new[] { "query" },
                ["grant_types_supported"] = ClientRegistry.SupportedGrants,
                ["subject_types_supported"] = new[] { "public" },
                ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
                ["scopes_supported"] = ClientRegistry.SupportedScopes,
                ["token_endpoint_auth_methods_supported"] = new[] { "client_secret_basic", "none" },
                ["code_challenge_methods_supported"] = new[] { "S256" },
                ["claims_supported"] = new[] { "sub", "name", "preferred_username", "email", "role", "groups", "upn" },
            });
        }).RequireCors(SpaCorsPolicy);

        app.MapGet("/authorize", GetAuthorizeAsync).RequireCors(SpaCorsPolicy);
        app.MapPost("/authorize", PostAuthorizeAsync).RequireCors(SpaCorsPolicy);

        app.MapPost("/token", ExchangeTokenAsync).RequireCors(SpaCorsPolicy);

        app.MapMethods("/jwks", [HttpMethods.Get, HttpMethods.Post], (SigningKeys keys) =>
        {
            var body = new Dictionary<string, object>
            {
                ["keys"] = keys.ExportJwks().Select(k => new Dictionary<string, object>
                {
                    ["kty"] = k.Kty,
                    ["use"] = k.Use!,
                    ["kid"] = k.Kid!,
                    ["alg"] = k.Alg!,
                    ["n"] = k.N!,
                    ["e"] = k.E!,
                }).ToArray(),
            };
            return Results.Json(body);
        }).RequireCors(SpaCorsPolicy);

        app.MapGet("/userinfo", GetUserinfoAsync).RequireCors(SpaCorsPolicy);

        app.MapGet("/logout", (HttpRequest request, ClientRegistry clients) =>
        {
            var clientId = request.Query["client_id"].ToString();
            var redirectUri = request.Query["post_logout_redirect_uri"].ToString();
            var state = request.Query["state"].ToString();
            var client = clients.Find(clientId);
            if (client is not null
                && client.PostLogoutRedirectUris.Contains(redirectUri, StringComparer.Ordinal))
            {
                var separator = redirectUri.Contains('?', StringComparison.Ordinal) ? '&' : '?';
                var target = string.IsNullOrEmpty(state) ? redirectUri : $"{redirectUri}{separator}state={Uri.EscapeDataString(state)}";
                return Results.Redirect(target);
            }
            return Results.Content(
                "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Signed out</title></head>"
                + "<body style=\"font-family:system-ui;margin:3rem\"><h1>Signed out</h1>"
                + "<p>You are signed out of the Corridor Okta simulation.</p></body></html>",
                "text/html; charset=utf-8");
        }).RequireCors(SpaCorsPolicy);

        return app;
    }

    private static async Task<IResult> GetAuthorizeAsync(
        HttpRequest request,
        ClientRegistry clients,
        IUserStore users,
        AuthCodeStore codes,
        TokenService tokens,
        ILoggerFactory loggerFactory)
    {
        var p = new AuthorizeParameters(request);
        var client = clients.Find(p.ClientId);
        if (client is null)
        {
            return PlainError(400, "Unknown client_id: only portal, spa, and legacy are registered in this simulation.");
        }
        var redirectCheck = CheckRedirectAndScope(client, p);
        if (redirectCheck is not null)
        {
            return redirectCheck;
        }

        var user = string.IsNullOrWhiteSpace(p.LoginHint)
            ? null
            : await users.FindByUserNameAsync(p.LoginHint);
        if (user is not null && user.Active)
        {
            return IssueCode(user, client, p, codes, tokens, loggerFactory);
        }

        // No usable login hint: show the demo login form (or the credentials
        // failed silently and we re-show it with a hint).
        return LoginPage(p, failed: false);
    }

    private static async Task<IResult> PostAuthorizeAsync(
        HttpRequest request,
        ClientRegistry clients,
        IUserStore users,
        AuthCodeStore codes,
        TokenService tokens,
        ILoggerFactory loggerFactory)
    {
        var form = await request.ReadFormAsync();
        var p = new AuthorizeParameters(request, form);
        var client = clients.Find(p.ClientId);
        if (client is null)
        {
            return PlainError(400, "Unknown client_id.");
        }
        var redirectCheck = CheckRedirectAndScope(client, p);
        if (redirectCheck is not null)
        {
            return redirectCheck;
        }

        var user = await users.FindByUserNameAsync(form["username"].ToString());
        if (user is null || !user.Active || !user.MatchesDemoPassword(form["password"].ToString()))
        {
            return LoginPage(p, failed: true);
        }
        return IssueCode(user, client, p, codes, tokens, loggerFactory);
    }

    private static IResult? CheckRedirectAndScope(OAuthClient client, AuthorizeParameters p)
    {
        if (!client.RedirectUris.Contains(p.RedirectUri, StringComparer.Ordinal))
        {
            // Never redirect an unregistered redirect_uri: that is an open-redirect bug.
            return PlainError(400, "redirect_uri is not registered for this client.");
        }
        if (!string.Equals(p.ResponseType, "code", StringComparison.Ordinal))
        {
            return OAuthErrorRedirect(p, "unsupported_response_type", "Only response_type=code is supported.");
        }
        if (!client.AllowsScopeSubset(p.Scope))
        {
            return OAuthErrorRedirect(p, "invalid_scope", "Requested scope is not allowed for this client.");
        }
        if (client.RequirePkce && string.IsNullOrEmpty(p.CodeChallenge))
        {
            return OAuthErrorRedirect(p, "invalid_request", "This public client requires PKCE with code_challenge_method=S256.");
        }
        if (!string.IsNullOrEmpty(p.CodeChallenge)
            && !string.Equals(p.CodeChallengeMethod, "S256", StringComparison.Ordinal))
        {
            return OAuthErrorRedirect(p, "invalid_request", "Only code_challenge_method=S256 is supported.");
        }
        if (!string.IsNullOrEmpty(p.CodeChallenge) && (p.CodeChallenge.Length is < 43 or > 128))
        {
            return OAuthErrorRedirect(p, "invalid_request", "code_challenge must be 43 to 128 characters.");
        }
        return null;
    }

    private static IResult IssueCode(
        DirectoryUser user,
        OAuthClient client,
        AuthorizeParameters p,
        AuthCodeStore codes,
        TokenService tokens,
        ILoggerFactory loggerFactory)
    {
        var code = codes.Issue(new AuthCodeStore.StoredAuthorizationCode(
            ClientId: client.ClientId,
            RedirectUri: p.RedirectUri,
            Scope: p.Scope,
            Upn: user.UserName,
            CodeChallenge: string.IsNullOrEmpty(p.CodeChallenge) ? null : p.CodeChallenge,
            Nonce: string.IsNullOrEmpty(p.Nonce) ? null : p.Nonce,
            ExpiresAtUtc: DateTime.UtcNow));

        loggerFactory.CreateLogger("Oidc.Authorize").LogInformation(
            "Authorization code issued: client {ClientId}, user {Upn}, pkce {Pkce}",
            client.ClientId, user.UserName, p.CodeChallenge is null ? "off" : "S256");

        var query = $"code={Uri.EscapeDataString(code)}";
        if (!string.IsNullOrEmpty(p.State))
        {
            query += $"&state={Uri.EscapeDataString(p.State)}";
        }
        query += $"&iss={Uri.EscapeDataString(tokens.Issuer)}";
        var separator = p.RedirectUri.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return Results.Redirect(p.RedirectUri + separator + query);
    }

    private static IResult OAuthErrorRedirect(AuthorizeParameters p, string error, string description)
    {
        var query = $"error={Uri.EscapeDataString(error)}&error_description={Uri.EscapeDataString(description)}";
        if (!string.IsNullOrEmpty(p.State))
        {
            query += $"&state={Uri.EscapeDataString(p.State)}";
        }
        var separator = p.RedirectUri.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return Results.Redirect(p.RedirectUri + separator + query);
    }

    private static IResult PlainError(int status, string message) =>
        Results.Content(
            "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Request error</title></head>"
            + $"<body style=\"font-family:system-ui;margin:3rem\"><h1>Request error</h1><p>{System.Net.WebUtility.HtmlEncode(message)}</p></body></html>",
            "text/html; charset=utf-8",
            statusCode: status);

    private static IResult LoginPage(AuthorizeParameters p, bool failed)
    {
        var hidden = new StringBuilder();
        foreach (var (key, value) in p.ToFormFields())
        {
            hidden.Append($"<input type=\"hidden\" name=\"{key}\" value=\"{System.Net.WebUtility.HtmlEncode(value)}\">");
        }
        var banner = failed
            ? "<p style=\"color:#b3261e\">Sign-in failed: unknown user or wrong demo password.</p>"
            : "<p>Simulation directory: any seeded user, demo password <code>Demo1234!</code>.</p>";
        var html = """
            <!doctype html>
            <html lang="en">
            <head><meta charset="utf-8"><title>Sign in: Corridor Okta simulation</title></head>
            <body style="font-family:system-ui;margin:3rem;max-width:26rem">
            <h1>Sign in</h1>
            """ + banner + """
            <form method="post" action="/authorize">
            """ + hidden + """
            <label for="username">Username (upn)</label>
            <input id="username" name="username" autocomplete="username" required style="display:block;margin-bottom:1rem">
            <label for="password">Password</label>
            <input id="password" name="password" type="password" autocomplete="current-password" required style="display:block;margin-bottom:1rem">
            <button type="submit">Sign in</button>
            </form>
            </body>
            </html>
            """;
        return Results.Content(html, "text/html; charset=utf-8", statusCode: failed ? 401 : 200);
    }

    private static async Task<IResult> ExchangeTokenAsync(
        HttpRequest request,
        ClientRegistry clients,
        AuthCodeStore codes,
        RefreshTokenStore refreshTokens,
        IUserStore users,
        TokenService tokens,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Oidc.Token");
        var form = await request.ReadFormAsync();

        var (client, clientError) = AuthenticateClient(request, form, clients);
        if (clientError is not null)
        {
            logger.LogInformation("Client authentication failed: {Reason}", clientError.Description);
            return TokenError(401, "invalid_client", clientError.Description, challenge: "Basic realm=\"corridor-okta-sim\"");
        }

        var grantType = form["grant_type"].ToString();
        if (client is null || string.IsNullOrEmpty(grantType) || !client.AllowsGrant(grantType))
        {
            return TokenError(400, "unsupported_grant_type", "The client is not allowed the requested grant_type.");
        }

        return grantType switch
        {
            "authorization_code" => await AuthorizationCodeGrantAsync(form, client!, codes, refreshTokens, users, tokens, logger),
            "refresh_token" => await RefreshGrantAsync(form, client!, refreshTokens, users, tokens, logger),
            "client_credentials" => ClientCredentialsGrant(client!, tokens, logger),
            _ => TokenError(400, "unsupported_grant_type", "Unknown grant_type."),
        };
    }

    private sealed record ClientAuthError(string Description);

    private static (OAuthClient? Client, ClientAuthError? Error) AuthenticateClient(
        HttpRequest request, IFormCollection form, ClientRegistry registry)
    {
        string? clientId = null;
        string? clientSecret = null;

        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = System.Text.Encoding.UTF8.GetString(
                    Convert.FromBase64String(header["Basic ".Length..].Trim()));
                var separator = decoded.IndexOf(':', StringComparison.Ordinal);
                if (separator > 0)
                {
                    clientId = decoded[..separator];
                    clientSecret = decoded[(separator + 1)..];
                }
            }
            catch (FormatException)
            {
                return (null, new ClientAuthError("Malformed Basic authorization header."));
            }
        }
        else
        {
            clientId = form["client_id"].ToString();
            clientSecret = string.IsNullOrEmpty(form["client_secret"]) ? null : form["client_secret"].ToString();
        }

        var client = registry.Find(clientId);
        if (client is null)
        {
            return (null, new ClientAuthError("Unknown client."));
        }
        if (client.IsConfidential)
        {
            if (string.IsNullOrEmpty(clientSecret))
            {
                return (null, new ClientAuthError("Confidential clients must authenticate with client_secret_basic."));
            }
            if (!string.Equals(clientSecret, client.ClientSecret, StringComparison.Ordinal))
            {
                return (null, new ClientAuthError("Client authentication failed: bad credentials."));
            }
        }
        else if (!string.IsNullOrEmpty(clientSecret))
        {
            return (null, new ClientAuthError("Public clients must not send a client secret."));
        }
        return (client, null);
    }

    private static async Task<IResult> AuthorizationCodeGrantAsync(
        IFormCollection form,
        OAuthClient client,
        AuthCodeStore codes,
        RefreshTokenStore refreshTokens,
        IUserStore users,
        TokenService tokens,
        ILogger logger)
    {
        var code = form["code"].ToString();
        var stored = codes.TryConsume(code);
        if (stored is null)
        {
            return TokenError(400, "invalid_grant", "Authorization code is unknown, expired, or already redeemed.");
        }
        if (!string.Equals(stored.ClientId, client.ClientId, StringComparison.Ordinal)
            || !string.Equals(stored.RedirectUri, form["redirect_uri"].ToString(), StringComparison.Ordinal))
        {
            return TokenError(400, "invalid_grant", "Code was issued to a different client or redirect_uri.");
        }
        if (stored.CodeChallenge is not null)
        {
            var verifier = form["code_verifier"].ToString();
            if (string.IsNullOrEmpty(verifier))
            {
                return TokenError(400, "invalid_grant", "code_verifier is required: the code was issued with PKCE.");
            }
            var derived = Base64UrlEncoder.Encode(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)));
            if (!CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(derived),
                    System.Text.Encoding.ASCII.GetBytes(stored.CodeChallenge)))
            {
                return TokenError(400, "invalid_grant", "PKCE verification failed: code_verifier does not match the challenge.");
            }
        }
        else if (client.RequirePkce)
        {
            return TokenError(400, "invalid_grant", "PKCE is mandatory for this client.");
        }

        var user = await users.FindByUserNameAsync(stored.Upn);
        if (user is null || !user.Active)
        {
            return TokenError(400, "invalid_grant", "The user behind this code is no longer active.");
        }

        logger.LogInformation(
            "Tokens minted: client {ClientId}, user {Upn}, scopes {Scope}, pkce {Pkce}",
            client.ClientId, user.UserName, stored.Scope, stored.CodeChallenge is null ? "off" : "S256");

        var payload = new Dictionary<string, object>
        {
            ["access_token"] = tokens.CreateAccessToken(user, client, stored.Scope),
            ["token_type"] = "Bearer",
            ["expires_in"] = (int)TokenService.AccessTokenLifetime.TotalSeconds,
            ["scope"] = stored.Scope,
            ["refresh_token"] = refreshTokens.Issue(client.ClientId, user.UserName, stored.Scope),
        };
        if (stored.Scope.Contains("openid", StringComparison.Ordinal))
        {
            payload["id_token"] = tokens.CreateIdToken(user, client, stored.Nonce);
        }
        return Results.Json(payload);
    }

    private static async Task<IResult> RefreshGrantAsync(
        IFormCollection form,
        OAuthClient client,
        RefreshTokenStore refreshTokens,
        IUserStore users,
        TokenService tokens,
        ILogger logger)
    {
        var token = form["refresh_token"].ToString();
        if (string.IsNullOrEmpty(token))
        {
            return TokenError(400, "invalid_request", "refresh_token is required.");
        }
        var (result, grant) = refreshTokens.Redeem(token);
        if (result == RefreshTokenStore.RedeemResult.ReuseDetected)
        {
            logger.LogWarning("Refresh token reuse detected: family {Family} revoked", grant?.FamilyId);
            return TokenError(400, "invalid_grant", "Refresh token reuse detected: the token family has been revoked.");
        }
        if (result != RefreshTokenStore.RedeemResult.Redeemed || grant is null)
        {
            return TokenError(400, "invalid_grant", "Refresh token is unknown, expired, or revoked.");
        }
        if (!string.Equals(grant.ClientId, client.ClientId, StringComparison.Ordinal))
        {
            return TokenError(400, "invalid_grant", "Refresh token belongs to a different client.");
        }
        var user = await users.FindByUserNameAsync(grant.Upn);
        if (user is null || !user.Active)
        {
            return TokenError(400, "invalid_grant", "The user behind this refresh token is no longer active.");
        }

        logger.LogInformation("Refresh token rotated: client {ClientId}, user {Upn}", client.ClientId, user.UserName);
        var payload = new Dictionary<string, object>
        {
            ["access_token"] = tokens.CreateAccessToken(user, client, grant.Scope),
            ["token_type"] = "Bearer",
            ["expires_in"] = (int)TokenService.AccessTokenLifetime.TotalSeconds,
            ["scope"] = grant.Scope,
            ["refresh_token"] = grant.Value,
        };
        if (grant.Scope.Contains("openid", StringComparison.Ordinal))
        {
            payload["id_token"] = tokens.CreateIdToken(user, client, nonce: null);
        }
        return Results.Json(payload);
    }

    private static IResult ClientCredentialsGrant(OAuthClient client, TokenService tokens, ILogger logger)
    {
        logger.LogInformation("Service token minted via client credentials: client {ClientId}", client.ClientId);
        return Results.Json(new Dictionary<string, object>
        {
            ["access_token"] = tokens.CreateServiceToken(client),
            ["token_type"] = "Bearer",
            ["expires_in"] = (int)TokenService.AccessTokenLifetime.TotalSeconds,
            ["scope"] = "corridor.service",
        });
    }

    private static async Task<IResult> GetUserinfoAsync(
        HttpContext context,
        TokenService tokens,
        IUserStore users)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Headers.WWWAuthenticate = "Bearer realm=\"corridor-okta-sim\"";
            return Results.Json(
                new Dictionary<string, string> { ["error"] = "invalid_token", ["error_description"] = "Bearer token required." },
                statusCode: 401,
                contentType: "application/json");
        }

        var (token, error) = await tokens.ValidateAccessTokenAsync(header["Bearer ".Length..].Trim());
        if (token is null)
        {
            context.Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
            return Results.Json(
                new Dictionary<string, string> { ["error"] = error ?? "invalid_token", ["error_description"] = "The access token failed validation." },
                statusCode: 401,
                contentType: "application/json");
        }

        var payload = new Dictionary<string, object>();
        foreach (var claim in token.Claims)
        {
            payload[claim.Type] = claim.Value;
        }

        // SCIM-style canonical claims plus the resolved directory entry.
        var sub = token.Subject;
        var user = await users.FindByIdAsync(sub);
        if (user is not null)
        {
            payload["sub"] = user.Id;
            payload["preferred_username"] = user.UserName;
            payload["name"] = user.DisplayName;
            payload["email"] = user.Email;
            payload["role"] = user.Role;
            payload["groups"] = user.Groups;
            payload["active"] = user.Active;
        }
        return Results.Json(payload);
    }

    private static IResult TokenError(int status, string error, string description, string? challenge = null)
    {
        var json = Results.Json(
            new Dictionary<string, string> { ["error"] = error, ["error_description"] = description },
            statusCode: status,
            contentType: "application/json");
        if (challenge is not null)
        {
            return new ResultWithChallenge(json, challenge);
        }
        return json;
    }

    /// <summary>Wraps a result to add a WWW-Authenticate challenge on the way out.</summary>
    private sealed class ResultWithChallenge(IResult inner, string challenge) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.WWWAuthenticate = challenge;
            await inner.ExecuteAsync(httpContext);
        }
    }

    private sealed record AuthorizeParameters
    {
        public string ClientId { get; }
        public string RedirectUri { get; }
        public string ResponseType { get; }
        public string Scope { get; }
        public string State { get; }
        public string Nonce { get; }
        public string CodeChallenge { get; }
        public string CodeChallengeMethod { get; }
        public string LoginHint { get; }
        public string Username { get; }
        public string Password { get; }

        public AuthorizeParameters(HttpRequest request, IFormCollection? form = null)
        {
            string Pick(string key)
            {
                if (form is not null && form.TryGetValue(key, out var formValue) && formValue.Count > 0)
                {
                    return formValue.ToString();
                }
                return request.Query[key].ToString();
            }

            ClientId = Pick("client_id");
            RedirectUri = Pick("redirect_uri");
            ResponseType = Pick("response_type");
            Scope = string.IsNullOrEmpty(Pick("scope")) ? "openid profile" : Pick("scope");
            State = Pick("state");
            Nonce = Pick("nonce");
            CodeChallenge = Pick("code_challenge");
            CodeChallengeMethod = Pick("code_challenge_method");
            LoginHint = Pick("login_hint");
            Username = Pick("username");
            Password = Pick("password");
        }

        public IEnumerable<(string Key, string Value)> ToFormFields()
        {
            yield return ("client_id", ClientId);
            yield return ("redirect_uri", RedirectUri);
            yield return ("response_type", ResponseType);
            yield return ("scope", Scope);
            if (!string.IsNullOrEmpty(State))
            {
                yield return ("state", State);
            }
            if (!string.IsNullOrEmpty(Nonce))
            {
                yield return ("nonce", Nonce);
            }
            if (!string.IsNullOrEmpty(CodeChallenge))
            {
                yield return ("code_challenge", CodeChallenge);
                yield return ("code_challenge_method", CodeChallengeMethod);
            }
        }
    }
}
