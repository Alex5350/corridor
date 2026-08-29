namespace Corridor.OktaSim.Models;

/// <summary>A client application registered in this simulated Okta org.</summary>
public sealed record OAuthClient(
    string ClientId,
    string? ClientSecret,
    bool IsPublic,
    bool RequirePkce,
    IReadOnlyList<string> RedirectUris,
    IReadOnlyList<string> PostLogoutRedirectUris,
    IReadOnlyList<string> AllowedScopes,
    IReadOnlyList<string> AllowedGrants,
    string Description)
{
    public bool IsConfidential => ClientSecret is not null;

    public bool AllowsGrant(string grantType) => AllowedGrants.Contains(grantType, StringComparer.Ordinal);

    public bool AllowsScopeSubset(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return false;
        }
        foreach (var item in scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!AllowedScopes.Contains(item, StringComparer.Ordinal))
            {
                return false;
            }
        }
        return true;
    }
}

/// <summary>
/// The client registry for the three synthetic applications (contract: portal,
/// spa, legacy). Secrets are deliberately trivial constants, demo-only.
/// </summary>
public sealed class ClientRegistry
{
    public const string PortalClientId = "portal";
    public const string SpaClientId = "spa";
    public const string LegacyClientId = "legacy";

    public const string PortalSecret = "corridor-portal-secret";
    public const string LegacySecret = "corridor-legacy-secret";

    public static readonly string[] SupportedScopes = ["openid", "profile", "email", "offline_access"];

    public static readonly string[] SupportedGrants =
        ["authorization_code", "refresh_token", "client_credentials"];

    private static readonly IReadOnlyList<OAuthClient> Clients =
    [
        new OAuthClient(
            PortalClientId,
            PortalSecret,
            IsPublic: false,
            RequirePkce: false,
            RedirectUris: ["http://localhost:5200/signin-oidc"],
            PostLogoutRedirectUris: ["http://localhost:5200/"],
            AllowedScopes: ["openid", "profile", "email", "offline_access"],
            AllowedGrants: ["authorization_code", "refresh_token"],
            Description: "PermitPortal web app, OIDC confidential client"),
        new OAuthClient(
            SpaClientId,
            ClientSecret: null,
            IsPublic: true,
            RequirePkce: true,
            RedirectUris: ["http://localhost:5173/callback"],
            PostLogoutRedirectUris: ["http://localhost:5173/"],
            AllowedScopes: ["openid", "profile", "email", "offline_access"],
            AllowedGrants: ["authorization_code", "refresh_token"],
            Description: "FieldInsight SPA, OIDC public client, PKCE S256 required"),
        new OAuthClient(
            LegacyClientId,
            LegacySecret,
            IsPublic: false,
            RequirePkce: false,
            RedirectUris: [],
            PostLogoutRedirectUris: [],
            AllowedScopes: ["openid"],
            AllowedGrants: ["client_credentials"],
            Description: "TraceLink SOAP service, client-credentials service token"),
    ];

    public OAuthClient? Find(string? clientId) =>
        clientId is null
            ? null
            : Clients.FirstOrDefault(c => string.Equals(c.ClientId, clientId, StringComparison.Ordinal));

    public IReadOnlyList<OAuthClient> All() => Clients;
}
