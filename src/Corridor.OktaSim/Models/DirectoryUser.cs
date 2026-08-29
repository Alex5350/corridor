using System.Security.Cryptography;
using System.Text;

namespace Corridor.OktaSim.Models;

/// <summary>
/// One directory user as seen by every subsystem of this simulation: OIDC claims,
/// SAML assertions, SCIM resources, and the admin console. The Id is the SCIM id
/// (persisted in idn.Users.ScimExternalId when the SQL store is active).
/// </summary>
public sealed record DirectoryUser(
    string Id,
    string UserName,
    string DisplayName,
    string Role,
    bool Active,
    IReadOnlyList<string> Groups,
    string PasswordHash)
{
    public string Email => UserName;

    /// <summary>
    /// Demo-only password scheme, mirrored from db/sql/seed/003_seed.sql:
    /// uppercase hex of SHA-256 over "corridor-demo-" + password. Documented as
    /// not production hardening anywhere it is mentioned.
    /// </summary>
    public static string HashDemoPassword(string password)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("corridor-demo-" + password));
        return Convert.ToHexString(bytes);
    }

    public bool MatchesDemoPassword(string password) =>
        string.Equals(PasswordHash, HashDemoPassword(password), StringComparison.Ordinal);
}

public static class DirectoryRoles
{
    public const string Admin = "Admin";
    public const string Inspector = "Inspector";
    public const string Officer = "Officer";
    public const string Clerk = "Clerk";
    public const string User = "User";
}
