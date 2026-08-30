using Corridor.AdfsSim.Identity;
using Corridor.AdfsSim.Saml;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Corridor.AdfsSim.Pages.Adfs;

/// <summary>The SAML 2.0 SSO endpoint (HTTP-POST and HTTP-Redirect binding entry).
/// Antiforgery is deliberately not enforced here: SP-initiated AuthnRequests POST to
/// this endpoint cross-origin and cannot carry a same-origin token. Issuance is
/// constrained to relying parties registered in appsettings, and credentials are
/// still required before any assertion is produced.</summary>
[IgnoreAntiforgeryToken]
public sealed class LsModel : PageModel
{
    private readonly IUserStore _users;
    private readonly RelyingPartyRegistry _registry;
    private readonly SamlResponseBuilder _responseBuilder;
    private readonly ILogger<LsModel> _logger;

    public LsModel(
        IUserStore users,
        RelyingPartyRegistry registry,
        SamlResponseBuilder responseBuilder,
        ILogger<LsModel> logger)
    {
        _users = users;
        _registry = registry;
        _responseBuilder = responseBuilder;
        _logger = logger;
    }

    public LoginViewModel Login { get; private set; } = new();

    /// <summary>HTTP-Redirect binding: hand the request context to the login page at /.</summary>
    public IActionResult OnGet(string? samlRequest, string? relayState)
    {
        var query = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(samlRequest))
        {
            query.Add("SAMLRequest=" + Uri.EscapeDataString(samlRequest));
        }

        if (!string.IsNullOrWhiteSpace(relayState))
        {
            query.Add("RelayState=" + Uri.EscapeDataString(relayState));
        }

        var queryString = query.Count == 0 ? string.Empty : "?" + string.Join("&", query);
        return Redirect("/" + queryString);
    }

    public async Task<IActionResult> OnPostAsync(string? samlRequest, string? relayState, string? userName, string? password)
    {
        Login = new LoginViewModel
        {
            UserName = userName,
            SamlRequest = samlRequest,
            RelayState = relayState,
        };

        var parsed = AuthnRequestParser.Parse(samlRequest);
        if (!parsed.Success)
        {
            _logger.LogWarning("Rejected a sign-in post with an unusable SAML request: {Error}", parsed.Error);
            Login.ErrorMessage = "The sign-in request from the application could not be read. Return to the application and try again.";
            return Page();
        }

        var request = parsed.Request!;

        var party = _registry.Find(request.Issuer, request.AssertionConsumerServiceUrl);
        if (party is null)
        {
            _logger.LogWarning(
                "Rejected a sign-in request from unregistered issuer {Issuer} with ACS {Acs}.",
                request.Issuer,
                request.AssertionConsumerServiceUrl ?? "(none)");
            Login.ErrorMessage = "The application that sent this sign-in request is not registered with this federation service.";
            return Page();
        }

        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
        {
            Login.ErrorMessage = "Enter both a user name and a password.";
            return Page();
        }

        var user = await _users.FindByCredentialsAsync(userName, password);
        if (user is null)
        {
            _logger.LogWarning("Sign-in failed for {UserName} on relying party {RelyingParty}.", userName, party.Name);
            Login.ErrorMessage = "The user name or password is incorrect. Enter credentials and try again.";
            return Page();
        }

        _logger.LogInformation(
            "Issuing a SAML assertion for {Upn} (role {Role}) to {RelyingParty} at {Acs}, InResponseTo {InResponseTo}.",
            user.Upn, user.Role, party.Name, party.AcsUrl, request.Id);

        var response = _responseBuilder.Build(user, request, party);
        var html = SamlPostBinding.BuildAutoSubmitForm(party.AcsUrl, response.ResponseBase64, relayState);
        return new ContentResult
        {
            Content = html,
            ContentType = "text/html; charset=utf-8",
        };
    }
}
