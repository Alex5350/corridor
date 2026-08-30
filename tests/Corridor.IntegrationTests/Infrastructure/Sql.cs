using Microsoft.Data.SqlClient;

namespace Corridor.IntegrationTests.Infrastructure;

/// <summary>Direct SQL access for asserting what the services actually persisted.</summary>
public static class Sql
{
    public static async Task<string?> ScalarAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.Add(new SqlParameter(name, value));
        }
        return await command.ExecuteScalarAsync() as string;
    }

    public static async Task<int> ExecuteAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.Add(new SqlParameter(name, value));
        }
        return await command.ExecuteNonQueryAsync();
    }

    /// <summary>Reads a single row of strings; returns an empty list when no row matches.</summary>
    public static async Task<List<string>> RowAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.Add(new SqlParameter(name, value));
        }
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        if (!await reader.ReadAsync())
        {
            return values;
        }
        for (var i = 0; i < reader.FieldCount; i++)
        {
            values.Add(reader.IsDBNull(i) ? string.Empty : reader.GetValue(i).ToString() ?? string.Empty);
        }
        return values;
    }

    public static async Task<string> GetTrustModeAsync(string connectionString, string appKey) =>
        await ScalarAsync(connectionString,
            "SELECT TrustMode FROM idn.MigrationApps WHERE AppKey = @appKey",
            ("@appKey", appKey)) ?? "";

    public static Task SetTrustModeAsync(string connectionString, string appKey, string mode) =>
        ExecuteAsync(connectionString,
            "UPDATE idn.MigrationApps SET TrustMode = @mode WHERE AppKey = @appKey",
            ("@mode", mode), ("@appKey", appKey));
}
