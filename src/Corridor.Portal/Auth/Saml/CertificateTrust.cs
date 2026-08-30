using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using Microsoft.Extensions.Options;

namespace Corridor.Portal.Auth.Saml;

/// <summary>
/// Loads the ADFS simulation signing certificate from the shared dev material in certs/.
/// The private key is only needed to mint the portal's service assertion for the SOAP hop.
/// </summary>
public sealed class AdfsCertificateStore(IOptions<AdfsOptions> options)
{
    private readonly object _gate = new();
    private X509Certificate2? _certificateWithKey;
    private X509Certificate2? _certificateOnly;

    public X509Certificate2 LoadCertificate()
    {
        lock (_gate)
        {
            // The certificate file holds the public certificate only; the single-argument
            // CreateFromPemFile requires a private key in the same file, so read the PEM directly.
            return _certificateOnly ??= X509Certificate2.CreateFromPem(File.ReadAllText(options.Value.CertificatePath));
        }
    }

    public X509Certificate2 LoadCertificateWithPrivateKey()
    {
        lock (_gate)
        {
            return _certificateWithKey ??= X509Certificate2.CreateFromPemFile(options.Value.CertificatePath, options.Value.PrivateKeyPath);
        }
    }
}

/// <summary>Fetches the okta-sim SAML IdP signing certificate from its metadata document, cached 15 minutes.</summary>
public sealed class OktaSamlMetadataClient(HttpClient httpClient, IOptions<OktaOptions> options)
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(15);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private X509Certificate2? _cached;
    private DateTimeOffset _fetchedAt;

    public async Task<X509Certificate2?> GetSigningCertificateAsync(CancellationToken ct = default)
    {
        if (_cached is not null && DateTimeOffset.UtcNow - _fetchedAt < CacheLifetime)
        {
            return _cached;
        }
        await _gate.WaitAsync(ct);
        try
        {
            if (_cached is not null && DateTimeOffset.UtcNow - _fetchedAt < CacheLifetime)
            {
                return _cached;
            }
            var metadataUrl = options.Value.Authority.TrimEnd('/') + options.Value.SamlMetadataPath;
            using var response = await httpClient.GetAsync(metadataUrl, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            var xml = await response.Content.ReadAsStringAsync(ct);
            var certificateText = XDocument.Parse(xml)
                .Descendants()
                .Where(e => e.Name.LocalName == "X509Certificate")
                .Select(e => e.Value.Trim())
                .FirstOrDefault();
            if (certificateText is null)
            {
                return null;
            }
            _cached = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(certificateText));
            _fetchedAt = DateTimeOffset.UtcNow;
            return _cached;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Xml.XmlException or FormatException)
        {
            // Metadata is unreachable or unparsable: dual trust continues with the ADFS certificate only.
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// Resolves which IdP signing certificates the ACS should trust for the portal's CURRENT trust
/// mode: Adfs trusts only the ADFS certificate, Dual trusts both providers, Okta trusts none
/// (the portal has cut over to OIDC and no longer accepts SAML).
/// </summary>
public sealed class TrustedCertificateProvider(AdfsCertificateStore adfs, OktaSamlMetadataClient okta) : ITrustedCertificateProvider
{
    public async Task<IReadOnlyList<TrustedCertificate>> GetTrustedAsync(Models.TrustMode mode, CancellationToken ct = default)
    {
        if (mode == Models.TrustMode.Okta)
        {
            return [];
        }
        var trusted = new List<TrustedCertificate>
        {
            new("adfs", adfs.LoadCertificate())
        };
        if (mode == Models.TrustMode.Dual)
        {
            var oktaCertificate = await okta.GetSigningCertificateAsync(ct);
            if (oktaCertificate is not null)
            {
                trusted.Add(new TrustedCertificate("okta-saml", oktaCertificate));
            }
        }
        return trusted;
    }
}

public interface ITrustedCertificateProvider
{
    Task<IReadOnlyList<TrustedCertificate>> GetTrustedAsync(Models.TrustMode mode, CancellationToken ct = default);
}
