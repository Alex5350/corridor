using System.Data;
using Corridor.Legacy.Security;

namespace Corridor.Legacy.DataAccess;

/// <summary>
/// Reads an app's identity trust mode from idn.MigrationApps. The portal's
/// cutover dashboard flips this row; this service reads it on every call so a
/// mode flip takes effect immediately.
/// </summary>
public interface IMigrationState
{
    TrustMode GetTrustMode(string appKey);
}

/// <summary>SQL Server implementation: SELECT TrustMode FROM idn.MigrationApps.</summary>
public sealed class SqlMigrationState : IMigrationState
{
    private readonly IDbConnectionFactory _connectionFactory;

    public SqlMigrationState(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public TrustMode GetTrustMode(string appKey)
    {
        using IDbConnection connection = _connectionFactory.CreateConnection();
        using IDbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT TrustMode FROM idn.MigrationApps WHERE AppKey = @AppKey";
        command.CommandType = CommandType.Text;
        command.AddParameter("@AppKey", DbType.String, appKey, 20);

        connection.Open();
        string? value = command.ExecuteScalar() as string;
        return value switch
        {
            "Adfs" => TrustMode.Adfs,
            "Dual" => TrustMode.Dual,
            "Okta" => TrustMode.Okta,
            // Missing row: fail closed to the pre-migration default (Adfs only).
            null => TrustMode.Adfs,
            _ => throw new InvalidOperationException($"Unknown TrustMode '{value}' for app '{appKey}'.")
        };
    }
}

/// <summary>
/// In-memory implementation for tests and demos of the cutover flip; the
/// mode-gating unit tests drive the TokenValidator through this class.
/// </summary>
public sealed class InMemoryMigrationState : IMigrationState
{
    private TrustMode _mode;

    public InMemoryMigrationState(TrustMode initialMode) => _mode = initialMode;

    public TrustMode GetTrustMode(string appKey) => _mode;

    public void SetTrustMode(TrustMode mode) => _mode = mode;
}
