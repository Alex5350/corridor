namespace Corridor.AdfsSim.Pages;

/// <summary>View model for the forms-style login page. Carries the pending SAML request
/// context (SAMLRequest/RelayState) through credential retries.</summary>
public sealed class LoginViewModel
{
    public string? UserName { get; set; }

    public string? Password { get; set; }

    public string? SamlRequest { get; set; }

    public string? RelayState { get; set; }

    public string? ErrorMessage { get; set; }
}
