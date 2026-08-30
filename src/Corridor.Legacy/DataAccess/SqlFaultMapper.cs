using Corridor.Legacy.Security;
using Microsoft.Data.SqlClient;

namespace Corridor.Legacy.DataAccess;

/// <summary>
/// Maps SqlException errors raised by the trace procs onto SOAP faults with
/// cor: subcodes. The procs use RAISERROR(msg, 16, 1), which surfaces as
/// SqlException.Number 50000; the message text distinguishes the cases:
/// "not found" -> cor:CaseNotFound, "Illegal transition" -> cor:IllegalTransition,
/// "Unknown status" -> cor:UnknownStatus. Anything else (including non-50000
/// SQL errors) becomes a Receiver fault cor:DataAccessError.
/// </summary>
public static class SqlFaultMapper
{
    public static CoreWCF.FaultException Map(SqlException exception)
    {
        if (exception.Number == 50000)
        {
            string message = exception.Message;
            if (message.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                return CorridorFault.Sender(CorridorFaultSubcodes.CaseNotFound, message);
            }

            if (message.Contains("Illegal transition", StringComparison.OrdinalIgnoreCase))
            {
                return CorridorFault.Sender(CorridorFaultSubcodes.IllegalTransition, message);
            }

            if (message.Contains("Unknown status", StringComparison.OrdinalIgnoreCase))
            {
                return CorridorFault.Sender(CorridorFaultSubcodes.UnknownStatus, message);
            }
        }

        return CorridorFault.Receiver(CorridorFaultSubcodes.DataAccessError,
            $"Trace database error {exception.Number}: {exception.Message}");
    }
}
