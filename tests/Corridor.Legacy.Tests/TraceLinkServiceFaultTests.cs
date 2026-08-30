using System.Data;
using Corridor.Legacy.Contracts;
using Corridor.Legacy.DataAccess;
using Corridor.Legacy.Services;
using Corridor.Legacy.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;

namespace Corridor.Legacy.Tests;

// The service maps SqlExceptions (simulated with a throwing connection
// factory) to SOAP faults; the repository is the real one so the call path is
// exercised end to end minus SQL Server.

public class TraceLinkServiceFaultTests
{
    private static TraceLinkService CreateService(Func<Exception> exceptionFactory) =>
        new(new TraceCaseRepository(new ThrowingDbConnectionFactory(exceptionFactory)), NullLogger<TraceLinkService>.Instance);

    [Fact]
    public void UpdateStatus_maps_proc_not_found_to_cor_CaseNotFound()
    {
        TraceLinkService service = CreateService(() => SqlExceptionFactory.Create(50000, "Case TRC-424242 not found"));

        CoreWCF.FaultException fault = Assert.Throws<CoreWCF.FaultException>(
            () => service.UpdateStatus("TRC-424242", "Closed", "admin@corridor.example"));

        Assert.Equal("CaseNotFound", fault.Code.SubCode!.Name);
    }

    [Fact]
    public void UpdateStatus_maps_illegal_transition_to_cor_IllegalTransition()
    {
        TraceLinkService service = CreateService(() => SqlExceptionFactory.Create(50000, "Illegal transition Closed to UnderReview for case TRC-100104"));

        CoreWCF.FaultException fault = Assert.Throws<CoreWCF.FaultException>(
            () => service.UpdateStatus("TRC-100104", "UnderReview", "admin@corridor.example"));

        Assert.Equal("IllegalTransition", fault.Code.SubCode!.Name);
    }

    [Fact]
    public void UpdateStatus_rejects_blank_input_before_touching_sql()
    {
        TraceLinkService service = CreateService(() => SqlExceptionFactory.Create(50000, "should never be raised"));

        CoreWCF.FaultException fault = Assert.Throws<CoreWCF.FaultException>(
            () => service.UpdateStatus(" ", "Closed", "admin@corridor.example"));

        Assert.Equal("InvalidRequest", fault.Code.SubCode!.Name);
    }

    [Fact]
    public void CreateTraceRequest_rejects_missing_fields_with_cor_InvalidRequest()
    {
        TraceLinkService service = CreateService(() => SqlExceptionFactory.Create(50000, "should never be raised"));

        CoreWCF.FaultException fault = Assert.Throws<CoreWCF.FaultException>(
            () => service.CreateTraceRequest(new TraceRequestCreate { LicenseeName = "Riverside Sporting Goods" }));

        Assert.Equal("InvalidRequest", fault.Code.SubCode!.Name);
    }
}

// ADO mapping: a fake IDataReader (DataTableReader) onto the TraceCase
// contract, including the nullable Disposition column.

public class TraceCaseMapperTests
{
    [Fact]
    public void Map_reads_every_column_and_treats_null_disposition_as_null()
    {
        var table = new DataTable();
        table.Columns.Add("CaseNumber", typeof(string));
        table.Columns.Add("LicenseeName", typeof(string));
        table.Columns.Add("ItemDescription", typeof(string));
        table.Columns.Add("Serial", typeof(string));
        table.Columns.Add("Status", typeof(string));
        table.Columns.Add("SubmittedAt", typeof(DateTime));
        table.Columns.Add("SubmittedBy", typeof(string));
        table.Columns.Add("Disposition", typeof(string));
        table.Rows.Add(
            "TRC-100101",
            "Riverside Sporting Goods",
            "Kalvin KB-7 .22 bolt rifle",
            "KB7-0041882",
            "Received",
            new DateTime(2026, 8, 30, 14, 3, 0, DateTimeKind.Utc),
            "officer@corridor.example",
            DBNull.Value);

        using DataTableReader reader = table.CreateDataReader();
        Assert.True(reader.Read());

        TraceCase traceCase = TraceCaseMapper.Map(reader);

        Assert.Equal("TRC-100101", traceCase.CaseNumber);
        Assert.Equal("Riverside Sporting Goods", traceCase.LicenseeName);
        Assert.Equal("Kalvin KB-7 .22 bolt rifle", traceCase.ItemDescription);
        Assert.Equal("KB7-0041882", traceCase.Serial);
        Assert.Equal("Received", traceCase.Status);
        Assert.Equal(new DateTime(2026, 8, 30, 14, 3, 0, DateTimeKind.Utc), traceCase.SubmittedAt);
        Assert.Equal("officer@corridor.example", traceCase.SubmittedBy);
        Assert.Null(traceCase.Disposition);
    }

    [Fact]
    public void Map_reads_a_populated_disposition()
    {
        var table = new DataTable();
        table.Columns.Add("CaseNumber", typeof(string));
        table.Columns.Add("LicenseeName", typeof(string));
        table.Columns.Add("ItemDescription", typeof(string));
        table.Columns.Add("Serial", typeof(string));
        table.Columns.Add("Status", typeof(string));
        table.Columns.Add("SubmittedAt", typeof(DateTime));
        table.Columns.Add("SubmittedBy", typeof(string));
        table.Columns.Add("Disposition", typeof(string));
        table.Rows.Add(
            "TRC-100104",
            "Cedar Valley Pawn",
            "Orlan bolt rifle .308",
            "ORL-330991",
            "Closed",
            new DateTime(2026, 8, 28, 21, 0, 0, DateTimeKind.Utc),
            "officer@corridor.example",
            "Set by officer@corridor.example at 2026-08-28 21:14:03");

        using DataTableReader reader = table.CreateDataReader();
        Assert.True(reader.Read());

        TraceCase traceCase = TraceCaseMapper.Map(reader);

        Assert.Equal("Set by officer@corridor.example at 2026-08-28 21:14:03", traceCase.Disposition);
    }
}
