using System.Data;
using Microsoft.Data.SqlClient;

namespace Corridor.AdfsSim.Identity;

/// <summary>SQL Server user store over idn.Users (raw ADO.NET, matching the repo
/// conventions). Active users only. Password check uses the demo hash scheme.</summary>
public sealed class SqlUserStore : IUserStore
{
    private readonly string _connectionString;

    public SqlUserStore(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<SimUser?> FindByCredentialsAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            SELECT Upn, DisplayName, Role, PasswordHash
            FROM idn.Users
            WHERE Upn = @upn AND Active = 1;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@upn", SqlDbType.NVarChar, 160).Value = username.Trim();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var storedHash = reader.GetString(reader.GetOrdinal("PasswordHash"));
        if (!DemoPasswordHash.Verify(password, storedHash))
        {
            return null;
        }

        return new SimUser(
            reader.GetString(reader.GetOrdinal("Upn")),
            reader.GetString(reader.GetOrdinal("DisplayName")),
            reader.GetString(reader.GetOrdinal("Role")));
    }
}
