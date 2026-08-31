using System.Data;
using Corridor.Portal.Models;
using Corridor.Portal.Services;
using Microsoft.Data.SqlClient;

namespace Corridor.Portal.Data.Sql;

/// <summary>Raw ADO.NET access to the Corridor database. The portal keeps stored logic inline; the schema is shared with the legacy service.</summary>
public sealed class SqlConnectionFactory
{
    private readonly string _connectionString;

    public SqlConnectionFactory(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<SqlConnection> OpenAsync(CancellationToken ct = default)
    {
        var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}

public sealed class SqlPermitRepository(SqlConnectionFactory factory) : IPermitRepository
{
    public async Task<IReadOnlyList<Permit>> ListAsync(string? statusFilter, CancellationToken ct = default)
    {
        const string sql = """
            SELECT Id, PermitNumber, LicenseeName, ItemDescription, Quantity, Purpose, Status, SubmittedAt, SubmittedBy
            FROM perm.ImportPermits
            WHERE (@Status IS NULL OR Status = @Status)
            ORDER BY SubmittedAt DESC;
            """;
        await using var connection = await factory.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Status", SqlDbType.NVarChar, 20).Value = (object?)statusFilter ?? DBNull.Value;
        var permits = new List<Permit>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            permits.Add(new Permit(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetDateTime(7).ToUniversalTime(),
                reader.GetString(8)));
        }
        return permits;
    }

    public async Task<Permit> CreateAsync(NewPermit permit, CancellationToken ct = default)
    {
        await using var connection = await factory.OpenAsync(ct);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var existing = await ListPermitNumbers(connection, ct);
            var permitNumber = PermitNumberSequence.Next(existing, DateTime.UtcNow.Year);
            try
            {
                const string sql = """
                    INSERT perm.ImportPermits (PermitNumber, LicenseeName, ItemDescription, Quantity, Purpose, Status, SubmittedAt, SubmittedBy)
                    OUTPUT INSERTED.Id, INSERTED.SubmittedAt
                    VALUES (@PermitNumber, @LicenseeName, @ItemDescription, @Quantity, @Purpose, N'UnderReview', SYSUTCDATETIME(), @SubmittedBy);
                    """;
                await using var command = new SqlCommand(sql, connection);
                command.Parameters.Add("@PermitNumber", SqlDbType.NVarChar, 20).Value = permitNumber;
                command.Parameters.Add("@LicenseeName", SqlDbType.NVarChar, 160).Value = permit.LicenseeName;
                command.Parameters.Add("@ItemDescription", SqlDbType.NVarChar, 200).Value = permit.ItemDescription;
                command.Parameters.Add("@Quantity", SqlDbType.Int).Value = permit.Quantity;
                command.Parameters.Add("@Purpose", SqlDbType.NVarChar, 300).Value = permit.Purpose;
                command.Parameters.Add("@SubmittedBy", SqlDbType.NVarChar, 120).Value = permit.SubmittedBy;
                await using var reader = await command.ExecuteReaderAsync(ct);
                await reader.ReadAsync(ct);
                return new Permit(
                    reader.GetInt32(0),
                    permitNumber,
                    permit.LicenseeName,
                    permit.ItemDescription,
                    permit.Quantity,
                    permit.Purpose,
                    "UnderReview",
                    reader.GetDateTime(1).ToUniversalTime(),
                    permit.SubmittedBy);
            }
            catch (SqlException ex) when (ex.Number is 2627 or 2601 && attempt < 2)
            {
                // Two clerks raced for the same sequence value: recompute and retry.
            }
        }
        throw new InvalidOperationException("Permit number sequence stayed occupied after retries.");
    }

    public async Task<IReadOnlyList<string>> ListPermitNumbersAsync(CancellationToken ct = default)
    {
        await using var connection = await factory.OpenAsync(ct);
        return await ListPermitNumbers(connection, ct);
    }

    private static async Task<IReadOnlyList<string>> ListPermitNumbers(SqlConnection connection, CancellationToken ct)
    {
        const string sql = "SELECT PermitNumber FROM perm.ImportPermits;";
        await using var command = new SqlCommand(sql, connection);
        var numbers = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            numbers.Add(reader.GetString(0));
        }
        return numbers;
    }
}

public sealed class SqlMigrationAppRepository(SqlConnectionFactory factory) : IMigrationAppRepository
{
    public async Task<IReadOnlyList<MigrationApp>> ListAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT AppKey, AppName, TrustMode, LastFlippedAt, FlippedBy
            FROM idn.MigrationApps
            ORDER BY AppKey;
            """;
        await using var connection = await factory.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        var apps = new List<MigrationApp>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            apps.Add(new MigrationApp(
                reader.GetString(0),
                reader.GetString(1),
                TrustModes.Parse(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetDateTime(3).ToUniversalTime(),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        return apps;
    }

    public async Task<MigrationApp?> GetAsync(string appKey, CancellationToken ct = default)
    {
        const string sql = """
            SELECT AppKey, AppName, TrustMode, LastFlippedAt, FlippedBy
            FROM idn.MigrationApps
            WHERE AppKey = @AppKey;
            """;
        await using var connection = await factory.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@AppKey", SqlDbType.NVarChar, 20).Value = appKey;
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }
        return new MigrationApp(
            reader.GetString(0),
            reader.GetString(1),
            TrustModes.Parse(reader.GetString(2)),
            reader.IsDBNull(3) ? null : reader.GetDateTime(3).ToUniversalTime(),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    public async Task UpdateTrustModeAsync(string appKey, TrustMode mode, string flippedBy, DateTime flippedAtUtc, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE idn.MigrationApps
            SET TrustMode = @TrustMode, LastFlippedAt = @FlippedAt, FlippedBy = @FlippedBy
            WHERE AppKey = @AppKey;
            """;
        await using var connection = await factory.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@TrustMode", SqlDbType.NVarChar, 10).Value = TrustModes.Label(mode);
        command.Parameters.Add("@FlippedAt", SqlDbType.DateTime2).Value = flippedAtUtc;
        command.Parameters.Add("@FlippedBy", SqlDbType.NVarChar, 120).Value = flippedBy;
        command.Parameters.Add("@AppKey", SqlDbType.NVarChar, 20).Value = appKey;
        await command.ExecuteNonQueryAsync(ct);
    }
}

public sealed class SqlDirectoryUserRepository(SqlConnectionFactory factory) : IDirectoryUserRepository
{
    public async Task<IReadOnlyList<DirectoryUserAccount>> ListAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT Id, Upn, DisplayName, Role, ScimExternalId, Active
            FROM idn.Users
            ORDER BY Upn;
            """;
        await using var connection = await factory.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        var accounts = new List<DirectoryUserAccount>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            accounts.Add(new DirectoryUserAccount(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetBoolean(5)));
        }
        return accounts;
    }

    public async Task UpdateScimExternalIdAsync(string upn, string scimExternalId, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE idn.Users
            SET ScimExternalId = @ScimExternalId
            WHERE Upn = @Upn;
            """;
        await using var connection = await factory.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ScimExternalId", SqlDbType.NVarChar, 64).Value = scimExternalId;
        command.Parameters.Add("@Upn", SqlDbType.NVarChar, 160).Value = upn;
        await command.ExecuteNonQueryAsync(ct);
    }
}

public sealed class SqlAuditEventRepository(SqlConnectionFactory factory) : IAuditEventRepository
{
    public async Task RecordAsync(AuditEvent auditEvent, CancellationToken ct = default)
    {
        const string sql = """
            INSERT idn.AuditEvents (At, Actor, AppKey, Event, Detail)
            VALUES (SYSUTCDATETIME(), @Actor, @AppKey, @Event, @Detail);
            """;
        await using var connection = await factory.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Actor", SqlDbType.NVarChar, 120).Value = auditEvent.Actor;
        command.Parameters.Add("@AppKey", SqlDbType.NVarChar, 20).Value = auditEvent.AppKey;
        command.Parameters.Add("@Event", SqlDbType.NVarChar, 60).Value = auditEvent.Event;
        command.Parameters.Add("@Detail", SqlDbType.NVarChar, 400).Value = (object?)auditEvent.Detail ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<AuditEvent>> ListRecentAsync(int count, CancellationToken ct = default)
    {
        const string sql = """
            SELECT TOP (@Count) Id, At, Actor, AppKey, Event, Detail
            FROM idn.AuditEvents
            ORDER BY Id DESC;
            """;
        await using var connection = await factory.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Count", SqlDbType.Int).Value = count;
        var events = new List<AuditEvent>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            events.Add(new AuditEvent(
                reader.GetInt32(0),
                reader.GetDateTime(1).ToUniversalTime(),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }
        return events;
    }
}

public sealed class SqlAssignmentRepository(SqlConnectionFactory factory) : IAssignmentRepository
{
    public async Task<IReadOnlyList<Assignment>> ListAsync(string? inspectorUpn, CancellationToken ct = default)
    {
        const string sql = """
            SELECT Id, InspectorUpn, LicenseeName, Focus, DueAt, ChecklistJson
            FROM idn.Assignments
            WHERE (@Inspector IS NULL OR InspectorUpn = @Inspector)
            ORDER BY DueAt;
            """;
        await using var connection = await factory.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Inspector", SqlDbType.NVarChar, 160).Value = (object?)inspectorUpn ?? DBNull.Value;
        var assignments = new List<Assignment>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            assignments.Add(ReadAssignment(reader));
        }
        return assignments;
    }

    public async Task<Assignment?> GetAsync(int id, CancellationToken ct = default)
    {
        const string sql = """
            SELECT Id, InspectorUpn, LicenseeName, Focus, DueAt, ChecklistJson
            FROM idn.Assignments
            WHERE Id = @Id;
            """;
        await using var connection = await factory.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Id", SqlDbType.Int).Value = id;
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadAssignment(reader) : null;
    }

    public async Task<Assignment> SaveChecklistAsync(int id, string checklistJson, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE idn.Assignments
            SET ChecklistJson = @ChecklistJson
            OUTPUT INSERTED.Id, INSERTED.InspectorUpn, INSERTED.LicenseeName, INSERTED.Focus, INSERTED.DueAt, INSERTED.ChecklistJson
            WHERE Id = @Id;
            """;
        await using var connection = await factory.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Id", SqlDbType.Int).Value = id;
        command.Parameters.Add("@ChecklistJson", SqlDbType.NVarChar, -1).Value = checklistJson;
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new InvalidOperationException($"Assignment {id} disappeared during checklist save.");
        }
        return ReadAssignment(reader);
    }

    private static Assignment ReadAssignment(SqlDataReader reader)
    {
        return new Assignment(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetDateTime(4).ToUniversalTime(),
            reader.GetString(5));
    }
}
