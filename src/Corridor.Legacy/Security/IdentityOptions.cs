using System.Security.Cryptography.X509Certificates;

namespace Corridor.Legacy.Security;

/// <summary>
/// Options for validating SAML assertions from adfs-sim (Corridor:Adfs section).
/// </summary>
public sealed class CorridorAdfsOptions
{
    /// <summary>SP audience URI every assertion must carry for this service.</summary>
    public string AudienceUri { get; set; } = "http://localhost:8000/TraceLink.svc";

    /// <summary>Path to the adfs-sim signing certificate (public PEM, certs/adfs-sim-cert.pem).</summary>
    public string SigningCertPath { get; set; } = "../../certs/adfs-sim-cert.pem";
}

/// <summary>
/// Options for validating JWTs from okta-sim (Corridor:Okta section).
/// </summary>
public sealed class CorridorOktaOptions
{
    public const string JwksHttpClientName = "corridor-jwks";

    /// <summary>Expected iss claim of okta-sim tokens.</summary>
    public string Issuer { get; set; } = "http://localhost:8080";

    /// <summary>Expected aud claim (the legacy client id registered in okta-sim).</summary>
    public string Audience { get; set; } = "legacy";

    /// <summary>okta-sim JWKS endpoint.</summary>
    public string JwksUrl { get; set; } = "http://localhost:8080/jwks";

    /// <summary>How long a fetched JWKS document is cached. Default 15 minutes.</summary>
    public int CacheSeconds { get; set; } = 900;
}
