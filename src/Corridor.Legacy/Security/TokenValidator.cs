using Corridor.Legacy.DataAccess;

namespace Corridor.Legacy.Security;

/// <summary>
/// One validation strategy per token kind. Implementations throw
/// <see cref="IdentityTokenException"/> when a token is rejected.
/// </summary>
public interface ITokenValidationStrategy
{
    IdentityTokenKind Kind { get; }

    ValidatedIdentity Validate(string payload);
}

/// <summary>
/// Facade the SOAP layer calls: gates the token kind against the app's current
/// TrustMode (idn.MigrationApps row for this app), then delegates to the
/// matching strategy. This is the dual-mode identity decision point of the
/// migration demo.
/// </summary>
public interface ITokenValidator
{
    ValidatedIdentity Validate(IdentityToken token);
}

public sealed class TokenValidator : ITokenValidator
{
    private readonly IMigrationState _migrationState;
    private readonly IReadOnlyDictionary<IdentityTokenKind, ITokenValidationStrategy> _strategies;
    private readonly string _appKey;

    public TokenValidator(IMigrationState migrationState, IEnumerable<ITokenValidationStrategy> strategies, string appKey)
    {
        _migrationState = migrationState;
        _appKey = appKey;
        _strategies = strategies.ToDictionary(strategy => strategy.Kind);
    }

    public ValidatedIdentity Validate(IdentityToken token)
    {
        TrustMode mode = _migrationState.GetTrustMode(_appKey);
        if (!IsTokenKindAllowed(mode, token.Kind))
        {
            throw new IdentityTokenException(CorridorFaultSubcodes.InvalidIdentityMode,
                $"TrustMode '{mode}' for app '{_appKey}' does not accept {Describe(token.Kind)} tokens.");
        }

        return _strategies[token.Kind].Validate(token.Payload);
    }

    internal static bool IsTokenKindAllowed(TrustMode mode, IdentityTokenKind kind) => mode switch
    {
        TrustMode.Adfs => kind == IdentityTokenKind.SamlAssertion,
        TrustMode.Okta => kind == IdentityTokenKind.Jwt,
        TrustMode.Dual => true,
        _ => false
    };

    private static string Describe(IdentityTokenKind kind) => kind switch
    {
        IdentityTokenKind.SamlAssertion => "SAML assertion",
        IdentityTokenKind.Jwt => "JWT",
        _ => kind.ToString()
    };
}
