using System.Data;

namespace Corridor.Legacy.DataAccess;

/// <summary>
/// Small helper so repository code can size parameters without leaning on
/// SqlClient-specific types. DbProvider-agnostic on purpose: tests inject fake
/// connections that also implement IDbConnection/IDbCommand.
/// </summary>
internal static class DbParameterExtensions
{
    public static IDbDataParameter AddParameter(
        this IDbCommand command,
        string name,
        DbType type,
        object? value,
        int size = 0,
        ParameterDirection direction = ParameterDirection.Input)
    {
        IDbDataParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        if (size > 0)
        {
            parameter.Size = size;
        }

        parameter.Direction = direction;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
        return parameter;
    }
}
