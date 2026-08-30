using System.Security.Cryptography;
using System.Text;

namespace Corridor.AdfsSim.Identity;

/// <summary>Demo password hashing used by the seed data: lowercase hex SHA256 of
/// "corridor-demo-" + password. This mirrors HASHBYTES('SHA2_256', ...) in db/sql/seed.
/// DEMO ONLY: real systems salt and stretch passwords; this scheme exists so the
/// simulation can share one documented hash shape between SQL and in-memory stores.</summary>
public static class DemoPasswordHash
{
    public const string Prefix = "corridor-demo-";

    public static string Hash(string password)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Prefix + password));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static bool Verify(string password, string storedHash) =>
        string.Equals(Hash(password), storedHash, StringComparison.OrdinalIgnoreCase);
}
