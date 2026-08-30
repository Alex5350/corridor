using System.Security.Cryptography;
using System.Text;

namespace Corridor.Ops.Tool.Tests;

/// <summary>
/// Test material helpers: builds tokens and JWKS documents with a generated
/// RSA key, plus small utilities for temp files and fake HTTP transport.
/// </summary>
internal static class JwtFactory
{
    public const string DefaultIssuer = "http://localhost:8080";
    public const string DefaultAudience = "legacy";
    public const string DefaultKid = "ops-test-key-1";

    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static byte[] FromBase64Url(string text)
    {
        var normalized = text.Replace('-', '+').Replace('_', '/');
        normalized += (normalized.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            _ => "",
        };
        return Convert.FromBase64String(normalized);
    }

    /// <summary>An unsigned two segment token: header plus payload.</summary>
    public static string BuildToken(string payloadJson) =>
        Base64Url(Encoding.ASCII.GetBytes("""{"alg":"RS256","typ":"JWT"}""")) + "." +
        Base64Url(Encoding.UTF8.GetBytes(payloadJson));

    /// <summary>An RS256 signed token plus the matching JWKS document.</summary>
    public static (string Token, string Jwks) CreateSignedToken(
        string? kid = null,
        string? issuer = null,
        string? audience = null,
        bool expired = false)
    {
        kid ??= DefaultKid;
        issuer ??= DefaultIssuer;
        audience ??= DefaultAudience;
        using var rsaKey = RSA.Create(2048);

        var exp = DateTimeOffset.UtcNow.AddDays(expired ? -1 : 1).ToUnixTimeSeconds();
        var nbf = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
        var header = $$"""{"alg":"RS256","typ":"JWT","kid":"{{kid}}"}""";
        var payload = $$"""{"iss":"{{issuer}}","aud":"{{audience}}","sub":"inspector@corridor.example","upn":"inspector@corridor.example","role":["Inspector","FieldOps"],"exp":{{exp}},"nbf":{{nbf}}}""";

        var headerSegment = Base64Url(Encoding.ASCII.GetBytes(header));
        var payloadSegment = Base64Url(Encoding.UTF8.GetBytes(payload));
        var signatureSegment = Base64Url(rsaKey.SignData(
            Encoding.ASCII.GetBytes(headerSegment + "." + payloadSegment),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1));

        var parameters = rsaKey.ExportParameters(includePrivateParameters: false);
        var jwks = $$"""{"keys":[{"kty":"RSA","use":"sig","kid":"{{kid}}","alg":"RS256","n":"{{Base64Url(parameters.Modulus!)}}","e":"{{Base64Url(parameters.Exponent!)}}"}]}""";
        return (headerSegment + "." + payloadSegment + "." + signatureSegment, jwks);
    }

    /// <summary>Rebuilds the token with a modified payload but the original
    /// signature, the classic tamper attempt.</summary>
    public static string TamperPayload(string token)
    {
        var parts = token.Split('.');
        var payload = Encoding.UTF8.GetString(FromBase64Url(parts[1]));
        payload = payload.Replace("inspector", "mallory");
        return parts[0] + "." + Base64Url(Encoding.UTF8.GetBytes(payload)) + "." + parts[2];
    }
}

/// <summary>A temp file that is deleted when the test finishes.</summary>
internal sealed class TempFile : IDisposable
{
    public string Path { get; }

    private TempFile(string path) => Path = path;

    public static TempFile Write(string content)
    {
        var path = System.IO.Path.GetTempFileName();
        File.WriteAllText(path, content);
        return new TempFile(path);
    }

    public void Dispose()
    {
        try
        {
            File.Delete(Path);
        }
        catch (IOException)
        {
            // Best effort cleanup of temp artifacts.
        }
    }
}

/// <summary>An HttpClientHandler that always returns a canned response and
/// records the request it saw.</summary>
internal sealed class FakeHandler : HttpClientHandler
{
    private readonly HttpResponseMessage _response;

    internal FakeHandler(HttpResponseMessage response) => _response = response;

    internal HttpRequestMessage? LastRequest { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        LastRequest = request;
        return Task.FromResult(_response);
    }
}
