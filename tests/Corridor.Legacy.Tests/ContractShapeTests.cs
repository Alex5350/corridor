using System.Reflection;
using System.Runtime.Serialization;
using Corridor.Legacy.Contracts;
using CoreWCF;

namespace Corridor.Legacy.Tests;

// The WSDL shape is a contract: these tests fail if an operation name, a
// parameter name, or a data member name drifts from
// docs/contracts/ARCHITECTURE-CONTRACT.md.

public class ContractShapeTests
{
    private const string ExpectedNamespace = "http://corridor.example/tracelink/2026/08";

    [Fact]
    public void ServiceContract_exposes_exactly_the_four_contracted_operations()
    {
        ServiceContractAttribute? contract = typeof(ITraceLinkService).GetCustomAttribute<ServiceContractAttribute>();
        Assert.NotNull(contract);
        Assert.Equal(ExpectedNamespace, contract.Namespace);
        Assert.Equal("TraceLinkService", contract.Name);

        List<MethodInfo> operations = typeof(ITraceLinkService).GetMethods()
            .Where(m => m.GetCustomAttribute<OperationContractAttribute>() is not null)
            .OrderBy(m => m.Name)
            .ToList();

        Assert.Equal(new[] { "CreateTraceRequest", "GetCase", "SearchCases", "UpdateStatus" }, operations.Select(m => m.Name).ToArray());
        Assert.Equal(new[] { "requester", "statusFilter", "maxRows" }, Parameters("SearchCases"));
        Assert.Equal(new[] { "caseNumber" }, Parameters("GetCase"));
        Assert.Equal(new[] { "request" }, Parameters("CreateTraceRequest"));
        Assert.Equal(new[] { "caseNumber", "newStatus", "actor" }, Parameters("UpdateStatus"));
    }

    [Fact]
    public void SearchCases_returns_TraceCase_array_and_UpdateStatus_returns_bool()
    {
        Assert.Equal(typeof(TraceCase[]), typeof(ITraceLinkService).GetMethod("SearchCases")!.ReturnType);
        Assert.Equal(typeof(TraceCase), typeof(ITraceLinkService).GetMethod("GetCase")!.ReturnType);
        Assert.Equal(typeof(string), typeof(ITraceLinkService).GetMethod("CreateTraceRequest")!.ReturnType);
        Assert.Equal(typeof(bool), typeof(ITraceLinkService).GetMethod("UpdateStatus")!.ReturnType);
    }

    [Theory]
    [InlineData(typeof(TraceCase), new[] { "CaseNumber", "LicenseeName", "ItemDescription", "Serial", "Status", "SubmittedAt", "SubmittedBy", "Disposition" })]
    [InlineData(typeof(TraceRequestCreate), new[] { "LicenseeName", "ItemDescription", "Serial", "RequesterUpn" })]
    public void DataContracts_expose_exactly_the_contracted_member_names(Type contractType, string[] expectedMembers)
    {
        DataContractAttribute? dataContract = contractType.GetCustomAttribute<DataContractAttribute>();
        Assert.NotNull(dataContract);
        Assert.Equal(ExpectedNamespace, dataContract.Namespace);
        Assert.Equal(contractType.Name, dataContract.Name);

        string[] memberNames = contractType.GetProperties()
            .Select(p => p.GetCustomAttribute<DataMemberAttribute>())
            .Where(attribute => attribute is not null)
            .Select(attribute => attribute!.Name!)
            .ToArray();

        Assert.Equal(expectedMembers, memberNames);
    }

    private static string[] Parameters(string operationName)
    {
        return typeof(ITraceLinkService).GetMethod(operationName)!.GetParameters().Select(p => p.Name!).ToArray();
    }
}
