namespace Corridor.AdfsSim.Identity;

/// <summary>A synthetic directory user (idn.Users row, or the in-memory seed).</summary>
public sealed record SimUser(string Upn, string DisplayName, string Role);

/// <summary>Credential validation behind an interface so the web app can run against the
/// SQL database or against the in-memory demo seed (no database, used by tests).</summary>
public interface IUserStore
{
    Task<SimUser?> FindByCredentialsAsync(string username, string password, CancellationToken cancellationToken = default);
}
