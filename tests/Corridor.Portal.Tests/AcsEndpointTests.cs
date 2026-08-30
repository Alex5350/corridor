using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Corridor.Portal.Auth.Saml;
using Corridor.Portal.Data;
using Corridor.Portal.Data.Memory;
using Corridor.Portal.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Corridor.Portal.Tests;

public class AcsEndpointTests : IClassFixture<AcsPortalFactory>
{
    private readonly AcsPortalFactory _factory;

    public AcsEndpointTests(AcsPortalFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Acs_AcceptsSignedResponseAndIssuesAppCookie()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
            AllowAutoRedirect = false
        });
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["SAMLResponse"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(_factory.BuildSignedResponse())),
            ["RelayState"] = "/Permits"
        });

        var response = await client.PostAsync("/saml/acs", content);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Permits", response.Headers.Location?.ToString());
        Assert.Contains(response.Headers, header => header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Acs_RefusesSamlAfterCutoverToOkta()
    {
        await _factory.MigrationApps.UpdateTrustModeAsync("portal", TrustMode.Okta,
            "admin@corridor.example", DateTime.UtcNow);
        try
        {
            using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["SAMLResponse"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(_factory.BuildSignedResponse()))
            });

            var response = await client.PostAsync("/saml/acs", content);

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("/Login?error=", response.Headers.Location?.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(response.Headers, header => header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await _factory.MigrationApps.UpdateTrustModeAsync("portal", TrustMode.Adfs,
                "admin@corridor.example", DateTime.UtcNow);
        }
    }

    [Fact]
    public async Task Acs_RejectsTamperedResponseWithoutCookie()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var tampered = _factory.BuildSignedResponse()
            .Replace("inspector@corridor.example", "admin@corridor.example", StringComparison.Ordinal);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["SAMLResponse"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(tampered))
        });

        var response = await client.PostAsync("/saml/acs", content);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Login?error=", response.Headers.Location?.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(response.Headers, header => header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Portal factory whose SAML trust list is a test generated certificate.</summary>
public sealed class AcsPortalFactory : PortalFactory
{
    private readonly X509Certificate2 _signingCertificate = CreateSigningCertificate();

    public InMemoryMigrationAppRepository MigrationApps { get; } = new();

    public string BuildSignedResponse()
    {
        var now = DateTime.UtcNow;
        var spec = new SignedAssertionSpec(
            "http://acs-test-idp/adfs/services/trust",
            "http://localhost:5200/saml",
            "inspector@corridor.example",
            "inspector@corridor.example",
            ["Inspector"],
            now.AddMinutes(-1),
            now.AddMinutes(10));
        var factory = new SamlAssertionFactory();
        return factory.WrapInResponse(factory.BuildSignedAssertion(spec, _signingCertificate));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IMigrationAppRepository>();
            services.AddSingleton<IMigrationAppRepository>(MigrationApps);
            services.RemoveAll<ITrustedCertificateProvider>();
            services.AddSingleton<ITrustedCertificateProvider>(new FixedCertificateProvider(_signingCertificate));
        });
    }

    private static X509Certificate2 CreateSigningCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=acs-test-idp", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private sealed class FixedCertificateProvider(X509Certificate2 certificate) : ITrustedCertificateProvider
    {
        public Task<IReadOnlyList<TrustedCertificate>> GetTrustedAsync(TrustMode mode, CancellationToken ct = default)
        {
            IReadOnlyList<TrustedCertificate> trusted = mode == TrustMode.Okta
                ? []
                : [new TrustedCertificate("adfs", certificate)];
            return Task.FromResult(trusted);
        }
    }
}
