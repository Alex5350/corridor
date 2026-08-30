using System.Security.Claims;

namespace Corridor.Portal.Auth;

/// <summary>
/// Normalizes claims from either identity provider into the portal's own cookie principal:
/// the upn claim becomes both NameIdentifier and Name, role claims become role claims, and
/// an idp marker records which provider issued the identity.
/// </summary>
public static class PortalClaims
{
    public const string IdpClaimType = "idp";

    public static ClaimsPrincipal Transform(ClaimsPrincipal incoming, string identityProvider, string authenticationType)
    {
        var upn = ReadUpn(incoming)
            ?? throw new InvalidOperationException("The incoming identity carries no upn claim.");
        var roles = incoming.FindAll("role")
            .Concat(incoming.FindAll(ClaimTypes.Role))
            .Select(c => c.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, upn),
            new(ClaimTypes.Name, upn),
            new(IdpClaimType, identityProvider)
        };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType, ClaimTypes.Name, ClaimTypes.Role));
    }

    public static string? ReadUpn(ClaimsPrincipal principal)
    {
        return principal.FindFirst("upn")?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? principal.FindFirst(ClaimTypes.Upn)?.Value
            ?? principal.FindFirst(ClaimTypes.Email)?.Value;
    }

    public static string? ReadIdentityProvider(ClaimsPrincipal principal)
    {
        return principal.FindFirst(IdpClaimType)?.Value;
    }
}
