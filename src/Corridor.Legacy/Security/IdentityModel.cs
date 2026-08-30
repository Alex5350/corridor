namespace Corridor.Legacy.Security;

/// <summary>Identity trust modes stored in idn.MigrationApps.TrustMode.</summary>
public enum TrustMode
{
    /// <summary>Pre-migration: only SAML assertions from adfs-sim are accepted.</summary>
    Adfs,

    /// <summary>Cutover window: both SAML assertions and JWTs are accepted.</summary>
    Dual,

    /// <summary>Post-migration: only JWTs from okta-sim are accepted.</summary>
    Okta
}

/// <summary>Which kind of identity token a caller presented in the cor:Security header.</summary>
public enum IdentityTokenKind
{
    /// <summary>A SAML 2.0 assertion issued by adfs-sim.</summary>
    SamlAssertion,

    /// <summary>A JWT issued by okta-sim (RS256, keys from its JWKS endpoint).</summary>
    Jwt
}

/// <summary>The raw token pulled out of the cor:Security header.</summary>
public sealed record IdentityToken(IdentityTokenKind Kind, string Payload);

/// <summary>The validated caller identity the inspector attaches to the message.</summary>
public sealed record ValidatedIdentity(IdentityTokenKind Kind, string Upn);
