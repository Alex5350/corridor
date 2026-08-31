using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.IdentityModel.Tokens;

namespace Corridor.OktaSim.Services;

/// <summary>
/// Resolves repo-relative demo assets (certs/, policies/) from any working
/// directory: configured path first, then content-root-relative, then a walk up
/// from the assembly location looking for the repo layout. Keeps tests and the
/// service loading the SAME committed files without copying them.
/// </summary>
public static class ContentPaths
{
    public static string? Locate(IWebHostEnvironment env, IConfiguration config, string configKey, string repoRelative)
    {
        var configured = config[configKey];
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var candidates = new List<string>();
        try
        {
            candidates.Add(Path.GetFullPath(Path.Combine(env.ContentRootPath, repoRelative)));
        }
        catch (ArgumentException)
        {
            // Content root unusable for path joins; fall through to assembly walk.
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var node = dir; node is not null; node = node.Parent)
        {
            candidates.Add(Path.Combine(node.FullName, repoRelative));
            if (node.FullName.Length <= 3)
            {
                break;
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    public static IReadOnlyList<string> ListFiles(IWebHostEnvironment env, IConfiguration config, string configKey, string repoRelative, string pattern)
    {
        var configured = config[configKey];
        string? dir = null;
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
        {
            dir = configured;
        }
        else
        {
            var probe = new[]
            {
                TrySafeCombine(env.ContentRootPath, repoRelative),
                null,
            };
            var assemblyWalk = AncestorDirectories(AppContext.BaseDirectory)
                .Select(d => TrySafeCombine(d, repoRelative))
                .OfType<string>();
            dir = probe.Concat(assemblyWalk).FirstOrDefault(Directory.Exists);
        }

        if (dir is null)
        {
            return [];
        }
        return Directory.EnumerateFiles(dir, pattern).OrderBy(p => p, StringComparer.Ordinal).ToArray();
    }

    private static string? TrySafeCombine(string root, string relative)
    {
        try
        {
            return Path.GetFullPath(Path.Combine(root, relative));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static IEnumerable<string> AncestorDirectories(string start)
    {
        var node = new DirectoryInfo(start);
        while (node is not null)
        {
            yield return node.FullName;
            if (node.FullName.Length <= 3)
            {
                yield break;
            }
            node = node.Parent;
        }
    }
}

/// <summary>
/// RS256 signing material. The current key is the committed demo PEM (kid
/// okta-sim-2026-08); a second generated key is published as a retired kid so the
/// JWKS demonstrates rotation. Also mints the self-signed dev certificate used for
/// SAML assertion signing, derived from the same RSA key at startup.
/// </summary>
public sealed class SigningKeys : IDisposable
{
    public const string CurrentKid = "okta-sim-2026-08";
    public const string RetiredKid = "okta-sim-2026-02";
    public const string SamlCertificateDistinguishedName = "CN=okta-sim.corridor.local";

    public RsaSecurityKey Current { get; }
    public RsaSecurityKey Retired { get; }
    public X509Certificate2 SamlCertificate { get; }
    public string? LoadedFromPath { get; }

    private readonly RSA _currentRsa;
    private readonly RSA _retiredRsa;

    public SigningKeys(IWebHostEnvironment env, IConfiguration config, ILogger<SigningKeys> logger)
    {
        var path = ContentPaths.Locate(env, config, "OktaSim:SigningKeyPem", Path.Combine("..", "..", "certs", "okta-sim-signing-key.pem"));
        _currentRsa = RSA.Create();
        if (path is not null)
        {
            _currentRsa.ImportFromPem(File.ReadAllText(path));
            LoadedFromPath = path;
        }
        else
        {
            logger.LogWarning("Signing PEM not found; generated an ephemeral key instead (tokens still verify against this instance's JWKS)");
            _currentRsa.KeySize = 2048;
        }

        Current = new RsaSecurityKey(_currentRsa) { KeyId = CurrentKid };

        _retiredRsa = RSA.Create(2048);
        Retired = new RsaSecurityKey(_retiredRsa) { KeyId = RetiredKid };

        SamlCertificate = CreateDevelopmentCertificate(_currentRsa);
        logger.LogInformation(
            "Signing keys ready: current kid {CurrentKid} from {Source}, retired kid {RetiredKid} (generated), SAML cert {Subject}",
            CurrentKid, path is null ? "ephemeral generation" : path, RetiredKid, SamlCertificate.Subject);
    }

    public IReadOnlyList<JsonWebKey> ExportJwks() => [ToJwk(Current, CurrentKid), ToJwk(Retired, RetiredKid)];

    private static JsonWebKey ToJwk(RsaSecurityKey key, string kid)
    {
        var rsa = key.Rsa ?? throw new InvalidOperationException("RSA instance missing");
        var parameters = rsa.ExportParameters(includePrivateParameters: false);
        return new JsonWebKey
        {
            Kty = "RSA",
            Kid = kid,
            Use = "sig",
            Alg = SecurityAlgorithms.RsaSha256,
            N = Base64UrlEncoder.Encode(parameters.Modulus),
            E = Base64UrlEncoder.Encode(parameters.Exponent),
        };
    }

    private static X509Certificate2 CreateDevelopmentCertificate(RSA rsa)
    {
        // Self-signed, in-memory only, never persisted: pure demo material for
        // SAML metadata and assertion signatures.
        var request = new CertificateRequest(
            SamlCertificateDistinguishedName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = DateTimeOffset.UtcNow.AddYears(2);
        return request.CreateSelfSigned(notBefore, notAfter);
    }

    public void Dispose()
    {
        // Deliberately does NOT dispose the RSA keys or the derived certificate.
        // This object is a process lifetime singleton: parallel test hosts load the
        // SAME committed PEM, and the token handler's shared crypto provider cache
        // binds signature providers to the first loaded RSA. Disposing it when one
        // host shuts down breaks signing for every other host still running (seen
        // live as ObjectDisposedException from TrySignHash under parallel suites).
        // The OS reclaims the handles at process exit; nothing is leaked while alive.
    }
}
