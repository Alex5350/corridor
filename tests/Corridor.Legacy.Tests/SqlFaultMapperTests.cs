using Corridor.Legacy.DataAccess;
using Corridor.Legacy.Security;
using Corridor.Legacy.Tests.TestDoubles;
using Microsoft.Data.SqlClient;

namespace Corridor.Legacy.Tests;

// SQL error to SOAP fault mapping. The trace procs RAISERROR with severity 16
// (Number 50000); the message text picks the cor: subcode. Real-SQL coverage
// of the procs themselves lives in tests/Corridor.IntegrationTests
// (Testcontainers SQL), not in this unit suite.

public class SqlFaultMapperTests
{
    [Theory]
    [InlineData("Case TRC-999999 not found", CorridorFaultSubcodes.CaseNotFound)]
    [InlineData("Illegal transition Closed to UnderReview for case TRC-100104", CorridorFaultSubcodes.IllegalTransition)]
    [InlineData("Unknown status Shredded", CorridorFaultSubcodes.UnknownStatus)]
    public void Proc_errors_map_to_the_contracted_subcodes(string message, string expectedSubcode)
    {
        SqlException exception = SqlExceptionFactory.Create(50000, message);

        CoreWCF.FaultException fault = SqlFaultMapper.Map(exception);

        Assert.Equal(expectedSubcode, fault.Code.SubCode!.Name);
        Assert.Equal(CorridorSecurityNamespaces.Security, fault.Code.SubCode.Namespace);
        Assert.True(fault.Code.IsSenderFault);
    }

    [Fact]
    public void NonProcSqlErrors_map_to_a_receiver_DataAccessError_fault()
    {
        SqlException exception = SqlExceptionFactory.Create(53, "A network-related or instance-specific error occurred");

        CoreWCF.FaultException fault = SqlFaultMapper.Map(exception);

        Assert.Equal(CorridorFaultSubcodes.DataAccessError, fault.Code.SubCode!.Name);
        Assert.Equal(CorridorSecurityNamespaces.Security, fault.Code.SubCode.Namespace);
        Assert.True(fault.Code.IsReceiverFault);
    }
}
