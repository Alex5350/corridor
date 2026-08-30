using System.Data;
using Corridor.Legacy.Contracts;

namespace Corridor.Legacy.DataAccess;

/// <summary>
/// Stored-procedure data access for trace cases. Every method is raw ADO.NET
/// (IDbConnection/IDbCommand/IDataReader over Microsoft.Data.SqlClient at
/// runtime) calling the procs in db/sql/002_trace_procs.sql. Status transition
/// legality lives in the procs; this layer only maps SQL errors to SOAP faults.
/// </summary>
public sealed class TraceCaseRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public TraceCaseRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public IReadOnlyList<TraceCase> SearchCases(string requester, string? statusFilter, int maxRows)
    {
        using IDbConnection connection = _connectionFactory.CreateConnection();
        using IDbCommand command = connection.CreateCommand();
        command.CommandText = "trace.usp_SearchCases";
        command.CommandType = CommandType.StoredProcedure;
        command.AddParameter("@Requester", DbType.String, requester, 120);
        command.AddParameter("@StatusFilter", DbType.String, statusFilter, 20);
        command.AddParameter("@MaxRows", DbType.Int32, maxRows);

        connection.Open();
        using IDataReader reader = command.ExecuteReader();
        var cases = new List<TraceCase>();
        while (reader.Read())
        {
            cases.Add(TraceCaseMapper.Map(reader));
        }

        return cases;
    }

    public TraceCase? GetCase(string caseNumber)
    {
        using IDbConnection connection = _connectionFactory.CreateConnection();
        using IDbCommand command = connection.CreateCommand();
        command.CommandText = "trace.usp_GetCase";
        command.CommandType = CommandType.StoredProcedure;
        command.AddParameter("@CaseNumber", DbType.String, caseNumber, 16);

        connection.Open();
        using IDataReader reader = command.ExecuteReader();
        return reader.Read() ? TraceCaseMapper.Map(reader) : null;
    }

    public string CreateTraceRequest(TraceRequestCreate request)
    {
        using IDbConnection connection = _connectionFactory.CreateConnection();
        using IDbCommand command = connection.CreateCommand();
        command.CommandText = "trace.usp_CreateTraceRequest";
        command.CommandType = CommandType.StoredProcedure;
        command.AddParameter("@LicenseeName", DbType.String, request.LicenseeName, 160);
        command.AddParameter("@ItemDescription", DbType.String, request.ItemDescription, 200);
        command.AddParameter("@Serial", DbType.String, request.Serial, 32);
        command.AddParameter("@RequesterUpn", DbType.String, request.RequesterUpn, 120);
        IDbDataParameter caseNumber = command.AddParameter("@CaseNumber", DbType.String, null, 16, ParameterDirection.Output);

        connection.Open();
        command.ExecuteNonQuery();
        return caseNumber.Value as string ?? throw new InvalidOperationException("usp_CreateTraceRequest returned no case number.");
    }

    public int UpdateStatus(string caseNumber, string newStatus, string actor)
    {
        using IDbConnection connection = _connectionFactory.CreateConnection();
        using IDbCommand command = connection.CreateCommand();
        command.CommandText = "trace.usp_UpdateStatus";
        command.CommandType = CommandType.StoredProcedure;
        command.AddParameter("@CaseNumber", DbType.String, caseNumber, 16);
        command.AddParameter("@NewStatus", DbType.String, newStatus, 20);
        command.AddParameter("@Actor", DbType.String, actor, 120);
        IDbDataParameter rowsAffected = command.AddParameter("@RowsAffected", DbType.Int32, null, 0, ParameterDirection.Output);

        connection.Open();
        command.ExecuteNonQuery();
        return rowsAffected.Value is int count ? count : 0;
    }
}
