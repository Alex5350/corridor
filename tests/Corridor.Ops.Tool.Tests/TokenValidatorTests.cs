namespace Corridor.Ops.Tool.Tests;

/// <summary>
/// validate-token coverage: a generated RSA key signs each token in-test and
/// its JWKS lands in a temp file, so no network is involved.
/// </summary>
public class TokenValidatorTests : IDisposable
{
    private readonly TempFile _jwksFile;
    private readonly string _token;
    private readonly string _jwks;

    public TokenValidatorTests()
    {
        (_token, _jwks) = JwtFactory.CreateSignedToken();
        _jwksFile = TempFile.Write(_jwks);
    }

    public void Dispose() => _jwksFile.Dispose();

    [Fact]
    public void Validate_SignedTokenPassesEveryCheck()
    {
        var result = TokenValidator.Validate(
            _token, _jwks, JwtFactory.DefaultIssuer, JwtFactory.DefaultAudience, DateTimeOffset.UtcNow);

        Assert.True(result.AllPassed, string.Join(" | ", result.Checks.Select(check => check.ToString())));
        Assert.Contains(result.Checks, check => check.Name == "signature" && check.Passed);
        Assert.Contains(result.Checks, check => check.Name == "issuer" && check.Passed);
        Assert.Contains(result.Checks, check => check.Name == "audience" && check.Passed);
        Assert.Contains(result.Checks, check => check.Name == "expiry" && check.Passed);
        Assert.Contains(result.Checks, check => check.Name == "not-before" && check.Passed);
    }

    [Fact]
    public void Validate_TamperedPayloadFailsSignature()
    {
        var tampered = JwtFactory.TamperPayload(_token);

        var result = TokenValidator.Validate(tampered, _jwks, null, null, DateTimeOffset.UtcNow);

        Assert.False(result.AllPassed);
        var signature = result.Checks.Single(check => check.Name == "signature");
        Assert.False(signature.Passed);
        // The structure still decodes; only the signature lies.
        Assert.True(result.Checks.Single(check => check.Name == "structure").Passed);
    }

    [Fact]
    public void Validate_UnknownKidFailsKeyLookup()
    {
        var (otherToken, _) = JwtFactory.CreateSignedToken(kid: "a-key-nobody-published");

        var result = TokenValidator.Validate(otherToken, _jwks, null, null, DateTimeOffset.UtcNow);

        Assert.False(result.AllPassed);
        Assert.False(result.Checks.Single(check => check.Name == "key").Passed);
        var signature = result.Checks.Single(check => check.Name == "signature");
        Assert.False(signature.Passed);
        Assert.Contains("no matching key", signature.Detail);
    }

    [Fact]
    public void Validate_WrongIssuerAndAudienceFailThoseChecks()
    {
        var result = TokenValidator.Validate(
            _token, _jwks, "http://wrong-issuer.example", "wrong-audience", DateTimeOffset.UtcNow);

        Assert.False(result.AllPassed);
        Assert.False(result.Checks.Single(check => check.Name == "issuer").Passed);
        Assert.False(result.Checks.Single(check => check.Name == "audience").Passed);
        // The signature itself is genuine.
        Assert.True(result.Checks.Single(check => check.Name == "signature").Passed);
    }

    [Fact]
    public void Validate_ExpiredTokenFailsExpiry()
    {
        var (expiredToken, jwks) = JwtFactory.CreateSignedToken(expired: true);

        var result = TokenValidator.Validate(expiredToken, jwks, null, null, DateTimeOffset.UtcNow);

        Assert.False(result.AllPassed);
        Assert.False(result.Checks.Single(check => check.Name == "expiry").Passed);
    }

    [Fact]
    public void Validate_AudienceListMatchesAnyEntry()
    {
        // aud as an array: the decoder flattens it, the check must still match.
        var payload = $$"""{"iss":"{{JwtFactory.DefaultIssuer}}","aud":["other-api","{{JwtFactory.DefaultAudience}}"],"exp":{{DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds()}}}""";
        var token = JwtFactory.BuildToken(payload);

        var result = TokenValidator.Validate(token, "{}", null, JwtFactory.DefaultAudience, DateTimeOffset.UtcNow);

        Assert.True(result.Checks.Single(check => check.Name == "audience").Passed);
    }

    [Fact]
    public void Validate_GarbageTokenFailsStructure()
    {
        var result = TokenValidator.Validate("not-a-token", _jwks, null, null, DateTimeOffset.UtcNow);

        Assert.False(result.AllPassed);
        Assert.False(result.Checks.Single(check => check.Name == "structure").Passed);
    }

    [Fact]
    public void Program_ValidateToken_FromJwksFileExitsZero()
    {
        Assert.Equal(ExitCodes.Success, Program.Main(new[]
        {
            "validate-token", _token,
            "--jwks", _jwksFile.Path,
            "--iss", JwtFactory.DefaultIssuer,
            "--aud", JwtFactory.DefaultAudience,
        }));
    }

    [Fact]
    public void Program_ValidateToken_TamperedTokenExitsFour()
    {
        Assert.Equal(ExitCodes.InvalidToken, Program.Main(new[]
        {
            "validate-token", JwtFactory.TamperPayload(_token),
            "--jwks", _jwksFile.Path,
        }));
    }

    [Fact]
    public void Program_ValidateToken_MissingJwksFileExitsFour()
    {
        Assert.Equal(ExitCodes.InvalidToken, Program.Main(new[]
        {
            "validate-token", _token,
            "--jwks", "/tmp/corridor-ops-definitely-missing-jwks.json",
        }));
    }
}
