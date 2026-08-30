using System.Collections.Concurrent;

namespace Corridor.AdfsSim.Identity;

/// <summary>In-memory seed of the four contract users (db/sql/seed/003_seed.sql), shared
/// password Demo1234!. Used when no connection string is configured and by unit tests.</summary>
public sealed class InMemoryUserStore : IUserStore
{
    public const string DemoPassword = "Demo1234!";

    private readonly ConcurrentDictionary<string, (SimUser User, string Hash)> _users;

    public InMemoryUserStore()
    {
        var seed = new (string Upn, string DisplayName, string Role)[]
        {
            ("admin@corridor.example", "Dana Whitfield", "Admin"),
            ("inspector@corridor.example", "Miguel Sandoval", "Inspector"),
            ("officer@corridor.example", "Priya Raman", "Officer"),
            ("clerk@corridor.example", "Tom Biestecker", "Clerk"),
        };

        _users = new(seed.Select(s =>
            KeyValuePair.Create(s.Upn, (new SimUser(s.Upn, s.DisplayName, s.Role), DemoPasswordHash.Hash(DemoPassword)))));
    }

    public Task<SimUser?> FindByCredentialsAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        if (_users.TryGetValue(username.Trim(), out var entry) &&
            DemoPasswordHash.Verify(password, entry.Hash))
        {
            return Task.FromResult<SimUser?>(entry.User);
        }

        return Task.FromResult<SimUser?>(null);
    }
}
