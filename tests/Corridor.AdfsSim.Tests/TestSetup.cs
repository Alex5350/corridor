using System.IO.Compression;
using System.Text;
using Corridor.AdfsSim;
using Corridor.AdfsSim.Saml;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Corridor.AdfsSim.Tests;

/// <summary>Shared plumbing for the unit tests: repo paths, the dev signing certificate,
/// default options, and AuthnRequest construction helpers.</summary>
public static class TestSetup
{
    public const string PortalIssuer = "http://localhost:5200/saml";

    public const string PortalAcs = "http://localhost:5200/saml/acs";

    public const string IdpEntityId = "http://localhost:8090/adfs/services/trust";

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "certs", "adfs-sim-cert.pem")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("Could not locate the repo root (certs/adfs-sim-cert.pem not found above the test output directory).");
    }

    public static SigningCertificate CreateSigningCertificate()
    {
        var repoRoot = RepoRoot();
        return new SigningCertificate(
            Options.Create(new AdfsSimOptions
            {
                CertificatePath = Path.Combine(repoRoot, "certs", "adfs-sim-cert.pem"),
                KeyPath = Path.Combine(repoRoot, "certs", "adfs-sim-key.pem"),
            }),
            new StubHostEnvironment(repoRoot));
    }

    public static AdfsSimOptions DefaultOptions() => new()
    {
        BaseUrl = "http://localhost:8090",
        EntityId = IdpEntityId,
        SsoPath = "/adfs/ls",
        AssertionLifetimeMinutes = 60,
        NotBeforeSkewMinutes = 5,
        RelyingParties =
        [
            new RelyingPartyOptions
            {
                Name = "PermitPortal",
                Issuer = PortalIssuer,
                AcsUrl = PortalAcs,
            },
        ],
    };

    public static RegisteredRelyingParty PortalParty() =>
        new("PermitPortal", PortalIssuer, PortalAcs, PortalIssuer);

    public static AuthnRequestData PortalRequest(string id = "_portal-req-1") =>
        new(id, PortalIssuer, PortalAcs, "http://localhost:8090/adfs/ls");

    /// <summary>Builds an AuthnRequest XML document the way a relying party would.</summary>
    public static string BuildAuthnRequestXml(string id, string issuer, string acs) => $"""
        <samlp:AuthnRequest xmlns:samlp="urn:oasis:names:tc:SAML:2.0:protocol" xmlns:saml="urn:oasis:names:tc:SAML:2.0:assertion"
            ID="{id}" Version="2.0" IssueInstant="2026-09-01T12:00:00Z"
            Destination="http://localhost:8090/adfs/ls"
            AssertionConsumerServiceURL="{acs}">
          <saml:Issuer>{issuer}</saml:Issuer>
        </samlp:AuthnRequest>
        """;

    /// <summary>Base64 of the raw-deflated XML, the HTTP-POST binding encoding.</summary>
    public static string DeflatedBase64(string xml)
    {
        using var output = new MemoryStream();
        using (var compressor = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            compressor.Write(Encoding.UTF8.GetBytes(xml), 0, Encoding.UTF8.GetByteCount(xml));
        }

        return Convert.ToBase64String(output.ToArray());
    }

    public static string PlainBase64(string xml) => Convert.ToBase64String(Encoding.UTF8.GetBytes(xml));

    private sealed class StubHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "Corridor.AdfsSim.Tests";

        public string EnvironmentName { get; set; } = "Testing";

        public string ContentRootPath { get; set; } = contentRootPath;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
