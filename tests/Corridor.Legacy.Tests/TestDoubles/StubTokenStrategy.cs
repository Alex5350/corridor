using Corridor.Legacy.Security;

namespace Corridor.Legacy.Tests.TestDoubles;

/// <summary>
/// Token strategy that always succeeds; used where a test exercises gating or
/// plumbing rather than the cryptographic validation itself.
/// </summary>
public sealed class StubTokenStrategy : ITokenValidationStrategy
{
    public const string StubUpn = "stub@corridor.example";

    public StubTokenStrategy(IdentityTokenKind kind) => Kind = kind;

    public IdentityTokenKind Kind { get; }

    public ValidatedIdentity Validate(string payload) => new(Kind, StubUpn);
}
