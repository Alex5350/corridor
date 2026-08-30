using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Corridor.Portal.Tests;

/// <summary>
/// Stands in for the cookie and bearer schemes in WebApplicationFactory tests. The upn and
/// role come from X-Test-Upn and X-Test-Role headers, with an Officer default.
/// </summary>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "TestScheme";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var upn = Request.Headers.TryGetValue("X-Test-Upn", out var upnValues)
            ? upnValues.ToString()
            : "officer@corridor.example";
        var role = Request.Headers.TryGetValue("X-Test-Role", out var roleValues)
            ? roleValues.ToString()
            : "Officer";
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, upn),
            new(ClaimTypes.Name, upn),
            new("upn", upn),
            new(ClaimTypes.Role, role)
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
