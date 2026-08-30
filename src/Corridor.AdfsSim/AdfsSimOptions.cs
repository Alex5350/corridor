namespace Corridor.AdfsSim;

/// <summary>adfs-sim settings bound from the "AdfsSim" configuration section.</summary>
public sealed class AdfsSimOptions
{
    public const string SectionName = "AdfsSim";

    /// <summary>Base URL the IdP advertises in metadata, for example http://localhost:8090.</summary>
    public string BaseUrl { get; set; } = "http://localhost:8090";

    /// <summary>The entityID of this IdP, advertised in federation metadata.</summary>
    public string EntityId { get; set; } = "http://localhost:8090/adfs/services/trust";

    /// <summary>Path of the SAML 2.0 SSO POST endpoint.</summary>
    public string SsoPath { get; set; } = "/adfs/ls";

    /// <summary>PEM certificate path, relative to the content root (repo layout: ../../certs).</summary>
    public string CertificatePath { get; set; } = "../../certs/adfs-sim-cert.pem";

    /// <summary>PEM private key path, relative to the content root.</summary>
    public string KeyPath { get; set; } = "../../certs/adfs-sim-key.pem";

    /// <summary>Assertion NotOnOrAfter lifetime in minutes. Contract: 60.</summary>
    public int AssertionLifetimeMinutes { get; set; } = 60;

    /// <summary>Clock skew applied to NotBefore. Contract: 5 minutes back-dated.</summary>
    public int NotBeforeSkewMinutes { get; set; } = 5;

    /// <summary>Registered relying parties (allowed ACS per SP issuer). The SAML flow is
    /// restricted to these parties; legacy/spa use other protocols.</summary>
    public List<RelyingPartyOptions> RelyingParties { get; set; } = [];
}

public sealed class RelyingPartyOptions
{
    /// <summary>Display name used in logs.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>SP entityID, the Issuer expected inside AuthnRequests.</summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>Allowed AssertionConsumerService URL for this party.</summary>
    public string AcsUrl { get; set; } = string.Empty;

    /// <summary>Audience value written into assertions. Defaults to the Issuer.</summary>
    public string? Audience { get; set; }
}
