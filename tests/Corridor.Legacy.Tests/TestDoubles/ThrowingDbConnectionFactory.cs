using System.Data;
using Corridor.Legacy.DataAccess;

namespace Corridor.Legacy.Tests.TestDoubles;

/// <summary>
/// Connection factory whose connections never exist: every call throws the
/// exception the test wants the service to map (SqlException instances built
/// by SqlExceptionFactory). Keeps unit tests off SQL Server entirely.
/// </summary>
public sealed class ThrowingDbConnectionFactory : IDbConnectionFactory
{
    private readonly Func<Exception> _exceptionFactory;

    public ThrowingDbConnectionFactory(Func<Exception> exceptionFactory) => _exceptionFactory = exceptionFactory;

    public IDbConnection CreateConnection() => throw _exceptionFactory();
}
