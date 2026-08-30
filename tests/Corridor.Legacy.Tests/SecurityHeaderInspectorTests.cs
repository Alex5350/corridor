using System.Security.Cryptography.X509Certificates;
using System.Xml;
using Corridor.Legacy.Security;
using Corridor.Legacy.Tests.TestDoubles;
using CoreWCF.Channels;

namespace Corridor.Legacy.Tests;

// The cor:Security dispatch inspector over real CoreWCF Message objects:
// header parsing, identity attachment, and the fault subcodes on rejection.

public class SecurityHeaderInspectorTests : IDisposable
{
    private const string Audience = "http://localhost:8000/TraceLink.svc";

    private readonly X509Certificate2 _signingCertificate = TestSaml.CreateSigningCertificate();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero));

    private CorridorSecurityMessageInspector CreateInspector(TrustMode mode)
    {
        var migrationState = new Corridor.Legacy.DataAccess.InMemoryMigrationState(mode);
        ITokenValidationStrategy[] strategies =
        {
            new SamlTokenValidator(_signingCertificate, Audience, _clock),
            new StubTokenStrategy(IdentityTokenKind.Jwt)
        };
        return new CorridorSecurityMessageInspector(new TokenValidator(migrationState, strategies, "legacy"));
    }

    private string BuildValidAssertion() => TestSaml.BuildAssertion(
        Audience,
        "officer@corridor.example",
        _clock.GetUtcNow().UtcDateTime.AddMinutes(-5),
        _clock.GetUtcNow().UtcDateTime.AddMinutes(55),
        sign: true,
        signingCertificate: _signingCertificate);

    [Fact]
    public void Valid_saml_header_attaches_the_identity_to_the_message()
    {
        CorridorSecurityMessageInspector inspector = CreateInspector(TrustMode.Adfs);
        using Message message = BuildSoapMessage(BuildValidAssertion(), jwt: null);
        Message mutable = message;

        inspector.AfterReceiveRequest(ref mutable, null!, null!);

        var identity = Assert.IsType<ValidatedIdentity>(mutable.Properties[CorridorSecurityMessageInspector.IdentityPropertyName]);
        Assert.Equal("officer@corridor.example", identity.Upn);
    }

    [Fact]
    public void Jwt_header_in_okta_mode_attaches_the_identity_to_the_message()
    {
        CorridorSecurityMessageInspector inspector = CreateInspector(TrustMode.Okta);
        using Message message = BuildSoapMessage(null, jwt: "header.payload.signature");
        Message mutable = message;

        inspector.AfterReceiveRequest(ref mutable, null!, null!);

        var identity = Assert.IsType<ValidatedIdentity>(mutable.Properties[CorridorSecurityMessageInspector.IdentityPropertyName]);
        Assert.Equal(IdentityTokenKind.Jwt, identity.Kind);
    }

    [Fact]
    public void Jwt_header_in_adfs_mode_faults_with_cor_InvalidIdentityMode()
    {
        CorridorSecurityMessageInspector inspector = CreateInspector(TrustMode.Adfs);
        using Message message = BuildSoapMessage(null, jwt: "header.payload.signature");
        Message mutable = message;

        CoreWCF.FaultException fault = Assert.Throws<CoreWCF.FaultException>(() => inspector.AfterReceiveRequest(ref mutable, null!, null!));

        Assert.Equal("InvalidIdentityMode", fault.Code.SubCode!.Name);
        Assert.Equal(CorridorSecurityNamespaces.Security, fault.Code.SubCode.Namespace);
    }

    [Fact]
    public void Missing_security_header_faults_with_cor_MissingSecurityHeader()
    {
        CorridorSecurityMessageInspector inspector = CreateInspector(TrustMode.Dual);
        using Message message = ParseSoap(
            """<soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"><soap:Body /></soap:Envelope>""");
        Message mutable = message;

        CoreWCF.FaultException fault = Assert.Throws<CoreWCF.FaultException>(() => inspector.AfterReceiveRequest(ref mutable, null!, null!));

        Assert.Equal("MissingSecurityHeader", fault.Code.SubCode!.Name);
    }

    [Fact]
    public void Header_with_no_token_inside_faults_with_cor_InvalidTokenFormat()
    {
        CorridorSecurityMessageInspector inspector = CreateInspector(TrustMode.Dual);
        using Message message = ParseSoap(
            """<soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"><soap:Header><cor:Security xmlns:cor="http://corridor.example/security"><nothing-useful /></cor:Security></soap:Header><soap:Body /></soap:Envelope>""");
        Message mutable = message;

        CoreWCF.FaultException fault = Assert.Throws<CoreWCF.FaultException>(() => inspector.AfterReceiveRequest(ref mutable, null!, null!));

        Assert.Equal("InvalidTokenFormat", fault.Code.SubCode!.Name);
    }

    private static Message BuildSoapMessage(string? assertionXml, string? jwt)
    {
        string content = assertionXml is not null
            ? assertionXml
            : $"<jwt>{jwt}</jwt>";
        return ParseSoap(
            $$"""<soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"><soap:Header><cor:Security xmlns:cor="http://corridor.example/security">{{content}}</cor:Security></soap:Header><soap:Body /></soap:Envelope>""");
    }

    private static Message ParseSoap(string soapXml)
    {
        using var stringReader = new StringReader(soapXml);
        using var xmlReader = XmlReader.Create(stringReader);
        return Message.CreateMessage(xmlReader, int.MaxValue, MessageVersion.Soap11);
    }

    public void Dispose() => _signingCertificate.Dispose();
}
