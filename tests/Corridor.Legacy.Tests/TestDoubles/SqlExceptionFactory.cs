using System.Reflection;
using Corridor.Legacy.Security;
using Microsoft.Data.SqlClient;

namespace Corridor.Legacy.Tests.TestDoubles;

/// <summary>
/// Builds SqlException instances the way the trace procs raise them on the
/// wire: RAISERROR(msg, 16, 1) surfaces as Number 50000 with the message text.
/// SqlException has no public constructor, so this reaches the internal
/// SqlError/SqlErrorCollection surface via reflection; the constructor scan
/// tolerates parameter additions across Microsoft.Data.SqlClient versions.
/// </summary>
public static class SqlExceptionFactory
{
    public static SqlException Create(int number, string message)
    {
        object errorCollection = Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        object error = CreateError(number, message);
        typeof(SqlErrorCollection)
            .GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(errorCollection, new[] { error });

        return (SqlException)typeof(SqlException)
            .GetMethod("CreateException", BindingFlags.Static | BindingFlags.NonPublic, new[] { typeof(SqlErrorCollection), typeof(string) })!
            .Invoke(null, new[] { errorCollection, "15.00.0000" })!;
    }

    private static object CreateError(int number, string message)
    {
        foreach (ConstructorInfo constructor in typeof(SqlError).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic))
        {
            ParameterInfo[] parameters = constructor.GetParameters();
            if (parameters.Length < 7
                || parameters[0].ParameterType != typeof(int)
                || parameters[1].ParameterType != typeof(byte)
                || parameters[2].ParameterType != typeof(byte))
            {
                continue;
            }

            object?[] arguments = new object?[parameters.Length];
            arguments[0] = number;                        // infoNumber: surfaces as SqlException.Number
            arguments[1] = (byte)1;                       // errorState
            arguments[2] = (byte)16;                      // errorClass: the severity our procs use
            arguments[3] = "localhost";                   // server
            arguments[4] = message;                       // errorMessage
            arguments[5] = "trace.usp_UpdateStatus";      // procedure
            arguments[6] = 1;                             // lineNumber
            for (int i = 7; i < parameters.Length; i++)
            {
                Type type = parameters[i].ParameterType;
                arguments[i] = type.IsValueType ? Activator.CreateInstance(type) : null;
            }

            return constructor.Invoke(arguments);
        }

        throw new InvalidOperationException("No usable SqlError constructor found in Microsoft.Data.SqlClient.");
    }
}

/// <summary>Test seam: a Corridor fault subcode name for quick assertions.</summary>
public static class FaultSubcodeExtensions
{
    public static string SubcodeName(this CoreWCF.FaultException fault) => fault.Code.SubCode!.Name;
}
