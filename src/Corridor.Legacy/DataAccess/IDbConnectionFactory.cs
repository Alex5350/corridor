using System.Data;
using Microsoft.Data.SqlClient;

namespace Corridor.Legacy.DataAccess;

/// <summary>
/// Creates open-ready ADO.NET connections. Concrete SQL Server implementation is
/// <see cref="SqlConnectionFactory"/>; unit tests substitute fakes so no test
/// ever touches SQL Server. Raw ADO.NET only: this service deliberately has no
/// ORM dependency, that constraint is part of the migration demo.
/// </summary>
public interface IDbConnectionFactory
{
    IDbConnection CreateConnection();
}

/// <summary>Opens SqlConnection instances from ConnectionStrings:Corridor.</summary>
public sealed class SqlConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;

    public SqlConnectionFactory(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Connection string 'ConnectionStrings:Corridor' is not configured.", nameof(connectionString));
        }

        _connectionString = connectionString;
    }

    public IDbConnection CreateConnection() => new SqlConnection(_connectionString);
}
