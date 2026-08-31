namespace Corridor.Portal.Auth;

public sealed class OktaOptions
{
    public string Authority { get; set; } = "http://localhost:8080";

    public string ClientId { get; set; } = "portal";

    public string? ClientSecret { get; set; }

    public string LogoutPath { get; set; } = "/logout";

    public string SamlMetadataPath { get; set; } = "/saml/metadata";
}

public sealed class AdfsOptions
{
    public string BaseAddress { get; set; } = "http://localhost:8090";

    public string SsoPath { get; set; } = "/adfs/ls";

    public string Issuer { get; set; } = "http://localhost:8090/adfs/services/trust";

    public string CertificatePath { get; set; } = "../../certs/adfs-sim-cert.pem";

    public string PrivateKeyPath { get; set; } = "../../certs/adfs-sim-key.pem";
}

public sealed class PortalSiteOptions
{
    public string BaseUrl { get; set; } = "http://localhost:5200";

    /// <summary>The portal's SAML SP entity id. The ADFS simulation registry requires exactly this value.</summary>
    public string EntityId { get; set; } = "http://localhost:5200/saml";

    /// <summary>Origin allowed to call the assignments API (the FieldInsight SPA dev server).</summary>
    public string SpaOrigin { get; set; } = "http://localhost:5173";

    /// <summary>Base URL of the XACML policy decision point on okta-sim (ADR 0007).</summary>
    public string PdpBaseUrl { get; set; } = "http://localhost:8080";
}

public sealed class LegacyOptions
{
    public string ServiceUrl { get; set; } = "http://localhost:8000/TraceLink.svc";

    public string Namespace { get; set; } = "http://corridor.example/tracelink/2026/08";

    public string ServiceUpn { get; set; } = "portal-service@corridor.example";

    public string OktaClientId { get; set; } = "legacy";

    public string? OktaClientSecret { get; set; }
}
