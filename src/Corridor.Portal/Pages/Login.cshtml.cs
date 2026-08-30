using Corridor.Portal.Auth;
using Corridor.Portal.Auth.Saml;
using Corridor.Portal.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Corridor.Portal.Pages;

/// <summary>
/// Routes sign-in by the portal's CURRENT trust mode from idn.MigrationApps:
/// Adfs redirects to the adfs-sim SSO endpoint with a generated AuthnRequest,
/// Okta challenges the OIDC handler, Dual shows a chooser.
/// </summary>
public class LoginModel(
    IMigrationAppRepository apps,
    IOptions<AdfsOptions> adfs,
    IOptions<PortalSiteOptions> site) : PageModel
{
    public bool ShowChooser { get; private set; }
    public string TrustModeLabel { get; private set; } = "Adfs";
    public string SafeReturnUrl { get; private set; } = "/";
    public string? ErrorMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? provider = null, string? returnUrl = null, string? error = null)
    {
        ErrorMessage = error;
        SafeReturnUrl = LocalReturnUrl(returnUrl);
        if (User.Identity?.IsAuthenticated == true)
        {
            return LocalRedirect(SafeReturnUrl);
        }

        var portalApp = await apps.GetAsync("portal");
        var mode = portalApp?.TrustMode ?? Models.TrustMode.Adfs;
        TrustModeLabel = Models.TrustModes.Label(mode);
        var route = LoginRouteSelector.Select(mode);

        // In Dual mode the chooser links back here with an explicit provider.
        if (provider is "Adfs")
        {
            route = new LoginRoute(LoginRouteKind.SamlRedirect, mode);
        }
        else if (provider is "Okta")
        {
            route = new LoginRoute(LoginRouteKind.OidcChallenge, mode);
        }

        switch (route.Kind)
        {
            case LoginRouteKind.SamlRedirect:
            {
                var ssoEndpoint = adfs.Value.BaseAddress.TrimEnd('/') + adfs.Value.SsoPath;
                var acsUrl = site.Value.BaseUrl.TrimEnd('/') + "/saml/acs";
                var redirectUrl = SamlAuthnRequests.BuildRedirectUrl(ssoEndpoint, site.Value.EntityId, acsUrl, SafeReturnUrl);
                return Redirect(redirectUrl);
            }
            case LoginRouteKind.OidcChallenge:
                return Challenge(new AuthenticationProperties { RedirectUri = SafeReturnUrl },
                    OpenIdConnectDefaults.AuthenticationScheme);
            default:
                ShowChooser = true;
                return Page();
        }
    }

    internal static string LocalReturnUrl(string? candidate)
    {
        if (candidate is not null
            && candidate.StartsWith("/", StringComparison.Ordinal)
            && !candidate.StartsWith("//", StringComparison.Ordinal)
            && !candidate.Contains(':', StringComparison.Ordinal))
        {
            return candidate;
        }
        return "/";
    }
}
