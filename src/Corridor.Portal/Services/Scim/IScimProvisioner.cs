namespace Corridor.Portal.Services.Scim;

/// <summary>What one directory account looks like on the wire to the SCIM endpoint.</summary>
public sealed record ScimUser(string UserName, string DisplayName, string Role);

/// <summary>
/// The provisioning operations the migration dashboard needs against the target
/// directory's SCIM 2.0 Users endpoint (ADR 0006). Kept as an interface so unit
/// tests run the whole provisioning flow against a fake.
/// </summary>
public interface IScimProvisioner
{
    /// <summary>Creates the user and returns the SCIM id the provider assigned.</summary>
    Task<string> CreateAsync(ScimUser user, CancellationToken ct = default);

    /// <summary>Replaces userName, displayName, active, and the role extension.</summary>
    Task ReplaceAsync(string externalId, ScimUser user, CancellationToken ct = default);

    /// <summary>Patches active to false, leaving the rest of the resource alone.</summary>
    Task DeactivateAsync(string externalId, CancellationToken ct = default);
}

/// <summary>Raised when the SCIM endpoint refuses or cannot answer a provisioning call.</summary>
public sealed class ScimProvisioningException(string message, Exception? inner = null) : Exception(message, inner);
