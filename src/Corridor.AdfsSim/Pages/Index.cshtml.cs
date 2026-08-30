using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Corridor.AdfsSim.Pages;

/// <summary>The forms login page. Accepts an incoming SAML request context through the
/// query string (SP-initiated Redirect binding lands on /adfs/ls which forwards here).</summary>
public sealed class IndexModel : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? SAMLRequest { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? RelayState { get; set; }

    public LoginViewModel Login { get; private set; } = new();

    public void OnGet()
    {
        Login = new LoginViewModel
        {
            SamlRequest = SAMLRequest,
            RelayState = RelayState,
        };
    }
}
