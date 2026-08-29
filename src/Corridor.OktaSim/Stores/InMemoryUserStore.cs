using System.Collections.Concurrent;
using Corridor.OktaSim.Models;

namespace Corridor.OktaSim.Stores;

/// <summary>
/// Default user store when ConnectionStrings:Corridor is not configured. Seeds the
/// four contract users with the shared demo password (Demo1234!, documented demo-only).
/// </summary>
public sealed class InMemoryUserStore : IUserStore
{
    public const string DemoPassword = "Demo1234!";

    private readonly ConcurrentDictionary<string, DirectoryUser> _users = new(StringComparer.Ordinal);

    public string StoreKind => "in-memory";

    public InMemoryUserStore()
    {
        var passwordHash = DirectoryUser.HashDemoPassword(DemoPassword);
        Seed("0192f6a1-1000-7000-8000-000000000001", "admin@corridor.example", "Dana Whitfield",
            DirectoryRoles.Admin, ["corridor-admins", "trace-reviewers"], passwordHash);
        Seed("0192f6a1-1000-7000-8000-000000000002", "inspector@corridor.example", "Miguel Sandoval",
            DirectoryRoles.Inspector, ["field-inspectors"], passwordHash);
        Seed("0192f6a1-1000-7000-8000-000000000003", "officer@corridor.example", "Priya Raman",
            DirectoryRoles.Officer, ["trace-reviewers"], passwordHash);
        Seed("0192f6a1-1000-7000-8000-000000000004", "clerk@corridor.example", "Tom Biestecker",
            DirectoryRoles.Clerk, [], passwordHash);
    }

    private void Seed(string id, string upn, string displayName, string role, string[] groups, string passwordHash) =>
        _users[upn] = new DirectoryUser(id, upn, displayName, role, Active: true, Groups: groups, PasswordHash: passwordHash);

    public Task<IReadOnlyList<DirectoryUser>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DirectoryUser>>(
            _users.Values.OrderBy(u => u.UserName, StringComparer.Ordinal).ToArray());

    public Task<DirectoryUser?> FindByUserNameAsync(string userName, CancellationToken ct = default) =>
        Task.FromResult(_users.TryGetValue(userName, out var user) ? user : null);

    public Task<DirectoryUser?> FindByIdAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(_users.Values.FirstOrDefault(u => string.Equals(u.Id, id, StringComparison.Ordinal)));

    public Task<DirectoryUser?> CreateAsync(DirectoryUser user, CancellationToken ct = default)
    {
        var id = string.IsNullOrWhiteSpace(user.Id) ? Guid.NewGuid().ToString() : user.Id;
        var stored = user with { Id = id };
        var existing = _users.GetOrAdd(stored.UserName, stored);
        return Task.FromResult(ReferenceEquals(existing, stored)
            ? existing
            : null); // userName already taken
    }

    public Task<DirectoryUser?> ReplaceAsync(DirectoryUser user, CancellationToken ct = default)
    {
        var current = _users.Values.FirstOrDefault(u => string.Equals(u.Id, user.Id, StringComparison.Ordinal));
        if (current is null)
        {
            return Task.FromResult<DirectoryUser?>(null);
        }
        if (!string.Equals(current.UserName, user.UserName, StringComparison.Ordinal)
            && _users.ContainsKey(user.UserName))
        {
            return Task.FromResult<DirectoryUser?>(null); // rename would collide
        }
        var updated = user with { PasswordHash = current.PasswordHash };
        _users.TryRemove(current.UserName, out _);
        _users[user.UserName] = updated;
        return Task.FromResult<DirectoryUser?>(updated);
    }

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
}
