using System.Data;
using Corridor.OktaSim.Models;
using Microsoft.Data.SqlClient;

namespace Corridor.OktaSim.Stores;

/// <summary>
/// SQL Server user store on idn.Users (contract schema). The SCIM id is stored in
/// idn.Users.ScimExternalId; group membership lives in a small side table,
/// idn.ScimUserGroups, created idempotently here so no shared schema script changes.
/// Raw ADO.NET only, matching the repo's data-access convention for identity sims.
/// </summary>
public sealed class SqlUserStore : IUserStore
{
    private readonly string _connectionString;
    private readonly ILogger<SqlUserStore> _logger;
    private int _initialized;

    public string StoreKind => "sql (idn.Users)";

    public SqlUserStore(string connectionString, ILogger<SqlUserStore> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1)
        {
            return;
        }

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Side table for SCIM groups; the shared idn.Users schema stays untouched.
        const string groupsTable = """
            IF OBJECT_ID('idn.ScimUserGroups') IS NULL
            BEGIN
                CREATE TABLE idn.ScimUserGroups (
                    ScimExternalId NVARCHAR(64) NOT NULL,
                    GroupName NVARCHAR(80) NOT NULL,
                    CONSTRAINT PK_ScimUserGroups PRIMARY KEY (ScimExternalId, GroupName)
                );
            END;
            """;
        await ExecuteAsync(conn, groupsTable, [], ct);

        // Sync per contract: every idn.Users row gets a SCIM external id exactly once.
        const string backfill =
            "UPDATE idn.Users SET ScimExternalId = LOWER(CONVERT(NVARCHAR(64), NEWID())) WHERE ScimExternalId IS NULL;";
        await ExecuteAsync(conn, backfill, [], ct);
        _logger.LogInformation("SqlUserStore initialized: ScimExternalId backfilled, group side table ensured");
    }

    public async Task<IReadOnlyList<DirectoryUser>> ListAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var users = new List<DirectoryUser>();
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT u.ScimExternalId, u.Upn, u.DisplayName, u.Role, u.Active, u.PasswordHash,
                   COALESCE(STRING_AGG(g.GroupName, ',') WITHIN GROUP (ORDER BY g.GroupName), '') AS Groups
            FROM idn.Users u
            LEFT JOIN idn.ScimUserGroups g ON g.ScimExternalId = u.ScimExternalId
            WHERE u.ScimExternalId IS NOT NULL
            GROUP BY u.ScimExternalId, u.Upn, u.DisplayName, u.Role, u.Active, u.PasswordHash
            ORDER BY u.Upn;
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            users.Add(ReadUser(reader));
        }
        return users;
    }

    public async Task<DirectoryUser?> FindByUserNameAsync(string userName, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await QuerySingleAsync("u.Upn = @key", new SqlParameter("@key", SqlDbType.NVarChar, 160) { Value = userName }, ct);
    }

    public async Task<DirectoryUser?> FindByIdAsync(string id, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await QuerySingleAsync("u.ScimExternalId = @key", new SqlParameter("@key", SqlDbType.NVarChar, 64) { Value = id }, ct);
    }

    public async Task<DirectoryUser?> CreateAsync(DirectoryUser user, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (await FindByUserNameAsync(user.UserName, ct) is not null)
        {
            return null; // SCIM uniqueness: userName already provisioned
        }

        var id = string.IsNullOrWhiteSpace(user.Id) ? Guid.NewGuid().ToString() : user.Id;
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            await ExecuteAsync(conn, tx, """
                INSERT INTO idn.Users (Upn, DisplayName, Role, PasswordHash, ScimExternalId, Active)
                VALUES (@upn, @displayName, @role, @passwordHash, @externalId, @active);
                """,
                [
                    new SqlParameter("@upn", SqlDbType.NVarChar, 160) { Value = user.UserName },
                    new SqlParameter("@displayName", SqlDbType.NVarChar, 120) { Value = user.DisplayName },
                    new SqlParameter("@role", SqlDbType.NVarChar, 40) { Value = user.Role },
                    new SqlParameter("@passwordHash", SqlDbType.NVarChar, 128) { Value = user.PasswordHash },
                    new SqlParameter("@externalId", SqlDbType.NVarChar, 64) { Value = id },
                    new SqlParameter("@active", SqlDbType.Bit) { Value = user.Active },
                ], ct);
            await ReplaceGroupsAsync(conn, tx, id, user.Groups, ct);
            await tx.CommitAsync(ct);
            return user with { Id = id };
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            await tx.RollbackAsync(ct);
            return null; // lost a uniqueness race
        }
    }

    public async Task<DirectoryUser?> ReplaceAsync(DirectoryUser user, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var existing = await FindByUserNameAsync(user.UserName, ct);
        if (existing is not null && !string.Equals(existing.Id, user.Id, StringComparison.Ordinal))
        {
            return null; // renaming onto another user
        }
        var current = await FindByIdAsync(user.Id, ct);
        if (current is null)
        {
            return null;
        }

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            await ExecuteAsync(conn, tx, """
                UPDATE idn.Users
                SET Upn = @upn, DisplayName = @displayName, Role = @role, Active = @active
                WHERE ScimExternalId = @externalId;
                """,
                [
                    new SqlParameter("@upn", SqlDbType.NVarChar, 160) { Value = user.UserName },
                    new SqlParameter("@displayName", SqlDbType.NVarChar, 120) { Value = user.DisplayName },
                    new SqlParameter("@role", SqlDbType.NVarChar, 40) { Value = user.Role },
                    new SqlParameter("@active", SqlDbType.Bit) { Value = user.Active },
                    new SqlParameter("@externalId", SqlDbType.NVarChar, 64) { Value = user.Id },
                ], ct);
            await ReplaceGroupsAsync(conn, tx, user.Id, user.Groups, ct);
            await tx.CommitAsync(ct);
            return user with { PasswordHash = current.PasswordHash };
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            await tx.RollbackAsync(ct);
            return null;
        }
    }

    private async Task<DirectoryUser?> QuerySingleAsync(string predicate, SqlParameter key, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT TOP (1) u.ScimExternalId, u.Upn, u.DisplayName, u.Role, u.Active, u.PasswordHash,
                   COALESCE(STRING_AGG(g.GroupName, ',') WITHIN GROUP (ORDER BY g.GroupName), '') AS Groups
            FROM idn.Users u
            LEFT JOIN idn.ScimUserGroups g ON g.ScimExternalId = u.ScimExternalId
            WHERE u.ScimExternalId IS NOT NULL AND {predicate}
            GROUP BY u.ScimExternalId, u.Upn, u.DisplayName, u.Role, u.Active, u.PasswordHash;
            """;
        cmd.Parameters.Add(key);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadUser(reader) : null;
    }

    private static DirectoryUser ReadUser(SqlDataReader reader)
    {
        var groups = reader.GetString(6);
        return new DirectoryUser(
            Id: reader.GetString(0),
            UserName: reader.GetString(1),
            DisplayName: reader.GetString(2),
            Role: reader.GetString(3),
            Active: reader.GetBoolean(4),
            PasswordHash: reader.GetString(5),
            Groups: groups.Length == 0 ? [] : groups.Split(','));
    }

    private static async Task ReplaceGroupsAsync(SqlConnection conn, SqlTransaction tx, string id, IEnumerable<string> groups, CancellationToken ct)
    {
        await ExecuteAsync(conn, tx, "DELETE FROM idn.ScimUserGroups WHERE ScimExternalId = @externalId;",
            [new SqlParameter("@externalId", SqlDbType.NVarChar, 64) { Value = id }], ct);
        foreach (var group in groups.Distinct(StringComparer.Ordinal).OrderBy(g => g, StringComparer.Ordinal))
        {
            await ExecuteAsync(conn, tx, "INSERT INTO idn.ScimUserGroups (ScimExternalId, GroupName) VALUES (@externalId, @groupName);",
                [
                    new SqlParameter("@externalId", SqlDbType.NVarChar, 64) { Value = id },
                    new SqlParameter("@groupName", SqlDbType.NVarChar, 80) { Value = group },
                ], ct);
        }
    }

    private static async Task ExecuteAsync(SqlConnection conn, string sql, SqlParameter[] parameters, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var p in parameters)
        {
            cmd.Parameters.Add(p);
        }
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task ExecuteAsync(SqlConnection conn, SqlTransaction tx, string sql, SqlParameter[] parameters, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var p in parameters)
        {
            cmd.Parameters.Add(p);
        }
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
