using System.Security.Claims;
using Corridor.Portal.Auth;
using Corridor.Portal.Auth.Saml;
using Corridor.Portal.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Corridor.Portal.Api;

/// <summary>
/// The portal's SAML assertion consumer service. Accepts POSTed SAMLResponse documents from
/// either simulated IdP, validates them against the certificates trusted for the portal's
/// CURRENT trust mode, then issues the portal's own auth cookie.
/// </summary>
public static class SamlAcsEndpoint
{
    public static IEndpointRouteBuilder MapSamlAcs(this IEndpointRouteBuilder app)
    {
        app.MapPost("/saml/acs", async (HttpRequest request, HttpContext httpContext,
            [FromServices] IMigrationAppRepository apps,
            [FromServices] ITrustedCertificateProvider certificates,
            [FromServices] SamlValidator validator,
            [FromServices] IOptions<PortalSiteOptions> site,
            [FromServices] ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("Corridor.Portal.SamlAcs");
            IFormCollection form;
            try
            {
                form = await request.ReadFormAsync();
            }
            catch (InvalidOperationException)
            {
                return Results.Problem(title: "The assertion consumer service expects a form POST.",
                    statusCode: 400);
            }
            var samlResponse = form["SAMLResponse"].ToString();
            if (samlResponse.Length == 0)
            {
                return Results.Problem(title: "Missing SAMLResponse.", statusCode: 400);
            }
            var relayState = form["RelayState"].ToString();
            var returnUrl = LocalReturnUrl(relayState);

            var portalApp = await apps.GetAsync("portal", httpContext.RequestAborted);
            var mode = portalApp?.TrustMode ?? Models.TrustMode.Adfs;
            if (mode == Models.TrustMode.Okta)
            {
                logger.LogWarning("SAML response rejected: portal trust mode is Okta, cutover complete.");
                return RedirectWithError("The portal no longer accepts ADFS sign-in. Use Okta.");
            }

            var trusted = await certificates.GetTrustedAsync(mode, httpContext.RequestAborted);
            // Assertions from both providers restrict audience to this SP's entity id.
            var audience = site.Value.EntityId;
            var result = validator.Validate(samlResponse, trusted, audience, DateTime.UtcNow);
            if (!result.IsValid || result.Principal is null)
            {
                logger.LogWarning("SAML response rejected: {Error}", result.Error);
                return RedirectWithError(result.Error ?? "The SAML response was rejected.");
            }

            var principal = PortalClaims.Transform(BuildIncomingPrincipal(result.Principal), result.Principal.IdentityProvider, "saml");
            await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
            logger.LogInformation("SAML sign-in for {Upn} via {IdentityProvider}", result.Principal.Upn, result.Principal.IdentityProvider);
            return Results.LocalRedirect(returnUrl);

            IResult RedirectWithError(string message)
            {
                return Results.LocalRedirect("/Login?error=" + Uri.EscapeDataString(message));
            }
        });

        return app;
    }

    internal static ClaimsPrincipal BuildIncomingPrincipal(SamlPrincipalData data)
    {
        var claims = new List<Claim> { new("upn", data.Upn) };
        claims.AddRange(data.Roles.Select(role => new Claim("role", role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "saml-external"));
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
