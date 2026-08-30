using Corridor.Legacy.DataAccess;
using Corridor.Legacy.Security;
using Corridor.Legacy.Tests.TestDoubles;

namespace Corridor.Legacy.Tests;

// TrustMode gating matrix: 3 modes x 2 token kinds. Adfs accepts only SAML,
// Okta accepts only JWT, Dual accepts both; a wrong-kind token faults with
// cor:InvalidIdentityMode.

public class TokenValidatorModeGatingTests
{
    private static TokenValidator CreateValidator(TrustMode mode)
    {
        var migrationState = new InMemoryMigrationState(mode);
        ITokenValidationStrategy[] strategies =
        {
            new StubTokenStrategy(IdentityTokenKind.SamlAssertion),
            new StubTokenStrategy(IdentityTokenKind.Jwt)
        };
        return new TokenValidator(migrationState, strategies, "legacy");
    }

    [Theory]
    [InlineData(TrustMode.Adfs, IdentityTokenKind.SamlAssertion)]
    [InlineData(TrustMode.Okta, IdentityTokenKind.Jwt)]
    [InlineData(TrustMode.Dual, IdentityTokenKind.SamlAssertion)]
    [InlineData(TrustMode.Dual, IdentityTokenKind.Jwt)]
    public void Allowed_combinations_validate(TrustMode mode, IdentityTokenKind kind)
    {
        ValidatedIdentity identity = CreateValidator(mode).Validate(new IdentityToken(kind, "payload"));

        Assert.Equal(kind, identity.Kind);
        Assert.Equal(StubTokenStrategy.StubUpn, identity.Upn);
    }

    [Theory]
    [InlineData(TrustMode.Adfs, IdentityTokenKind.Jwt)]
    [InlineData(TrustMode.Okta, IdentityTokenKind.SamlAssertion)]
    public void Wrong_kind_for_the_mode_faults_with_InvalidIdentityMode(TrustMode mode, IdentityTokenKind kind)
    {
        IdentityTokenException exception = Assert.Throws<IdentityTokenException>(
            () => CreateValidator(mode).Validate(new IdentityToken(kind, "payload")));

        Assert.Equal(CorridorFaultSubcodes.InvalidIdentityMode, exception.Subcode);
        Assert.Contains(mode.ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Mode_is_read_on_every_call_so_a_cutover_flip_applies_immediately()
    {
        var migrationState = new InMemoryMigrationState(TrustMode.Adfs);
        ITokenValidationStrategy[] strategies =
        {
            new StubTokenStrategy(IdentityTokenKind.SamlAssertion),
            new StubTokenStrategy(IdentityTokenKind.Jwt)
        };
        var validator = new TokenValidator(migrationState, strategies, "legacy");
        var jwt = new IdentityToken(IdentityTokenKind.Jwt, "payload");

        Assert.Throws<IdentityTokenException>(() => validator.Validate(jwt));
        migrationState.SetTrustMode(TrustMode.Dual);
        Assert.Equal(StubTokenStrategy.StubUpn, validator.Validate(jwt).Upn);
        migrationState.SetTrustMode(TrustMode.Okta);
        Assert.Equal(StubTokenStrategy.StubUpn, validator.Validate(jwt).Upn);
    }
}
