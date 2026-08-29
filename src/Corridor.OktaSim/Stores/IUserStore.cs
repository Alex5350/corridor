using Corridor.OktaSim.Models;

namespace Corridor.OktaSim.Stores;

/// <summary>
/// Directory storage behind SCIM, token issuance, and the admin console.
/// Two implementations: in-memory (default, and used by tests) and SQL Server
/// backed by idn.Users with ScimExternalId as the SCIM id (per contract).
/// </summary>
public interface IUserStore
{
    string StoreKind { get; }

    Task<IReadOnlyList<DirectoryUser>> ListAsync(CancellationToken ct = default);

    Task<DirectoryUser?> FindByUserNameAsync(string userName, CancellationToken ct = default);

    Task<DirectoryUser?> FindByIdAsync(string id, CancellationToken ct = default);

    /// <summary>Creates a user; returns null when the userName is already taken.</summary>
    Task<DirectoryUser?> CreateAsync(DirectoryUser user, CancellationToken ct = default);

    /// <summary>Replaces a user (SCIM PUT semantics); returns null when the id is unknown.</summary>
    Task<DirectoryUser?> ReplaceAsync(DirectoryUser user, CancellationToken ct = default);

    /// <summary>Initializes the store on first use (schema sync, seed backfill).</summary>
    Task InitializeAsync(CancellationToken ct = default);
}
