using Corridor.Portal.Models;

namespace Corridor.Portal.Auth;

public enum LoginRouteKind
{
    /// <summary>Redirect to the ADFS simulation SSO endpoint with a generated AuthnRequest.</summary>
    SamlRedirect,

    /// <summary>Challenge the OpenID Connect handler against okta-sim.</summary>
    OidcChallenge,

    /// <summary>Dual trust: let the user pick ADFS or Okta.</summary>
    Chooser
}

public sealed record LoginRoute(LoginRouteKind Kind, TrustMode Mode);

public static class LoginRouteSelector
{
    public static LoginRoute Select(TrustMode mode)
    {
        return mode switch
        {
            TrustMode.Adfs => new LoginRoute(LoginRouteKind.SamlRedirect, mode),
            TrustMode.Okta => new LoginRoute(LoginRouteKind.OidcChallenge, mode),
            TrustMode.Dual => new LoginRoute(LoginRouteKind.Chooser, mode),
            _ => new LoginRoute(LoginRouteKind.SamlRedirect, mode)
        };
    }
}
