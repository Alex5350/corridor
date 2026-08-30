using Corridor.Portal.Auth;
using Corridor.Portal.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Security.Claims;

namespace Corridor.Portal.Pages;

public class IndexModel(IMigrationAppRepository apps) : PageModel
{
    public string TrustModeLabel { get; private set; } = "Adfs";
    public string SignInPathDescription { get; private set; } = "";
    public DateTime? LastFlippedAt { get; private set; }
    public IReadOnlyList<string> Roles { get; private set; } = [];
    public string? IdentityProvider { get; private set; }
    public string? ErrorMessage { get; private set; }

    public async Task OnGetAsync(string? error = null)
    {
        ErrorMessage = error;
        var portalApp = await apps.GetAsync("portal");
        var mode = portalApp?.TrustMode ?? Models.TrustMode.Adfs;
        TrustModeLabel = Models.TrustModes.Label(mode);
        SignInPathDescription = mode switch
        {
            Models.TrustMode.Adfs => "SAML redirect to the ADFS provider",
            Models.TrustMode.Okta => "OpenID Connect authorization code with Okta",
            _ => "Chooser: ADFS SAML or Okta OIDC"
        };
        LastFlippedAt = portalApp?.LastFlippedAt;
        Roles = User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();
        IdentityProvider = User.Identity?.IsAuthenticated == true ? PortalClaims.ReadIdentityProvider(User) : null;
    }
}
