using Corridor.Portal.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Corridor.Portal.Pages;

/// <summary>
/// Clears the portal cookie and, for sessions that came from Okta, redirects to the okta-sim
/// logout endpoint with a post logout redirect back to the portal.
/// </summary>
public class LogoutModel(IOptions<OktaOptions> okta, IOptions<PortalSiteOptions> site) : PageModel
{
    public async Task<IActionResult> OnPostAsync()
    {
        var identityProvider = PortalClaims.ReadIdentityProvider(User);
        var idToken = await HttpContext.GetTokenAsync("id_token");
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        if (identityProvider == "okta")
        {
            var path = okta.Value.LogoutPath.StartsWith("/", StringComparison.Ordinal)
                ? okta.Value.LogoutPath
                : "/" + okta.Value.LogoutPath;
            var logoutUrl = okta.Value.Authority.TrimEnd('/') + path;
            var query = "?post_logout_redirect_uri=" + Uri.EscapeDataString(site.Value.BaseUrl);
            if (!string.IsNullOrEmpty(idToken))
            {
                query += "&id_token_hint=" + Uri.EscapeDataString(idToken);
            }
            return Redirect(logoutUrl + query);
        }
        return RedirectToPage("/Logout");
    }
}
