namespace Corridor.Ops.Tool.Tests;

public class TokenDecoderTests
{
    private static string TokenWithTimes(long exp, long nbf) =>
        JwtFactory.BuildToken(
            $$"""{"iss":"http://localhost:8080","upn":"inspector@corridor.example","role":["Inspector","FieldOps"],"exp":{{exp}},"nbf":{{nbf}}}""");

    [Fact]
    public void TryDecode_RoundTripsHeaderAndPayloadClaims()
    {
        var token = TokenWithTimes(4102444800, 1756684800);

        var outcome = TokenDecoder.TryDecode(token);

        Assert.True(outcome.Ok);
        Assert.NotNull(outcome.Decoded);
        Assert.Equal("RS256", outcome.Decoded.Header["alg"]);
        Assert.Equal("JWT", outcome.Decoded.Header["typ"]);
        Assert.Equal("http://localhost:8080", outcome.Decoded.Payload["iss"]);
        Assert.Equal("inspector@corridor.example", outcome.Decoded.Payload["upn"]);
        // Array claims flatten to comma separated text for the table.
        Assert.Equal("Inspector, FieldOps", outcome.Decoded.Payload["role"]);
        // A two segment token carries the raw segments but no signature.
        Assert.NotEmpty(outcome.Decoded.HeaderSegment);
        Assert.NotEmpty(outcome.Decoded.PayloadSegment);
        Assert.Equal(string.Empty, outcome.Decoded.SignatureSegment);
    }

    [Theory]
    [InlineData("YQ", new byte[] { 0x61 })]
    [InlineData("YWE", new byte[] { 0x61, 0x61 })]
    [InlineData("YWJj", new byte[] { 0x61, 0x62, 0x63 })]
    [InlineData("YQ==", new byte[] { 0x61 })] // padding left in is tolerated
    [InlineData("YQ=", new byte[] { 0x61 })]  // stray single pad char is dropped
    // The url safe alphabet: '-' is '+' and '_' is '/', never swapped.
    [InlineData("-___", new byte[] { 0xFB, 0xFF, 0xFF })]
    [InlineData("-__-", new byte[] { 0xFB, 0xFF, 0xFE })]
    [InlineData("_7__", new byte[] { 0xFF, 0xBF, 0xFF })]
    public void Base64UrlDecode_AcceptsPaddedAndUnpaddedForms(string text, byte[] expected) =>
        Assert.Equal(expected, TokenDecoder.Base64UrlDecode(text));

    [Fact]
    public void Base64UrlDecode_RoundTripsRandomBytesThroughTheUrlSafeAlphabet()
    {
        var bytes = new byte[257];
        Random.Shared.NextBytes(bytes);

        var decoded = TokenDecoder.Base64UrlDecode(JwtFactory.Base64Url(bytes));

        Assert.Equal(bytes, decoded);
    }

    [Fact]
    public void Base64UrlDecode_RejectsImpossibleLength() =>
        Assert.Throws<FormatException>(() => TokenDecoder.Base64UrlDecode("A"));

    [Fact]
    public void TryDecode_MalformedTokenReportsFailure()
    {
        var outcome = TokenDecoder.TryDecode("definitely-not-a-token");

        Assert.False(outcome.Ok);
        Assert.Null(outcome.Decoded);
        Assert.Contains("two or three", outcome.ErrorMessage);
    }

    [Fact]
    public void TryDecode_RejectsNonJsonPayloadSegment()
    {
        var token = JwtFactory.Base64Url("""{"alg":"RS256"}"""u8.ToArray()) + "." +
                    JwtFactory.Base64Url("this is not json"u8.ToArray());

        var outcome = TokenDecoder.TryDecode(token);

        Assert.False(outcome.Ok);
        Assert.Contains("JSON", outcome.ErrorMessage);
    }

    [Fact]
    public void TimeWarnings_FlagsExpiredToken()
    {
        var token = TokenWithTimes(
            exp: DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds(),
            nbf: DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds());
        var payload = TokenDecoder.TryDecode(token).Decoded!.Payload;

        var warnings = TokenDecoder.TimeWarnings(payload, DateTimeOffset.UtcNow);

        var warning = Assert.Single(warnings);
        Assert.StartsWith("EXPIRES", warning);
    }

    [Fact]
    public void TimeWarnings_FlagsNotYetValidToken()
    {
        var token = TokenWithTimes(
            exp: DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds(),
            nbf: DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds());
        var payload = TokenDecoder.TryDecode(token).Decoded!.Payload;

        var warnings = TokenDecoder.TimeWarnings(payload, DateTimeOffset.UtcNow);

        var warning = Assert.Single(warnings);
        Assert.StartsWith("NOT-YET-VALID", warning);
    }

    [Fact]
    public void TimeWarnings_EmptyInsideValidityWindow()
    {
        var token = TokenWithTimes(
            exp: DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds(),
            nbf: DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds());
        var payload = TokenDecoder.TryDecode(token).Decoded!.Payload;

        Assert.Empty(TokenDecoder.TimeWarnings(payload, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Program_DecodeToken_ValidTokenExitsZero()
    {
        var token = TokenWithTimes(
            exp: DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds(),
            nbf: DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds());

        Assert.Equal(ExitCodes.Success, Program.Main(new[] { "decode-token", token }));
    }

    [Fact]
    public void Program_DecodeToken_MalformedTokenExitsFour()
    {
        Assert.Equal(ExitCodes.InvalidToken, Program.Main(new[] { "decode-token", "garbage-token" }));
    }

    [Fact]
    public void Program_UnknownCommandExitsUsage()
    {
        Assert.Equal(ExitCodes.Usage, Program.Main(new[] { "no-such-command" }));
    }

    [Fact]
    public void Program_ValidateToken_MissingJwksExitsUsage()
    {
        var (token, _) = JwtFactory.CreateSignedToken();

        Assert.Equal(ExitCodes.Usage, Program.Main(new[] { "validate-token", token }));
    }
}
