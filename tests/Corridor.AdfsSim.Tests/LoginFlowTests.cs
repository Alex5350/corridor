using System.Net;
using Corridor.AdfsSim.Saml;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Corridor.AdfsSim.Tests;

/// <summary>Drives the login flow over the real pipeline (TestServer, no network, no
/// database: the in-memory demo user store answers credential checks).</summary>
public sealed class LoginFlowTests : IClassFixture<AdfsSimFactory>
{
    private readonly AdfsSimFactory _factory;
    private readonly HttpClient _client;

    public LoginFlowTests(AdfsSimFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
    }

    private static FormUrlEncodedContent LoginPost(
        string samlRequest, string? relayState, string? userName, string? password)
    {
        var fields = new Dictionary<string, string>
        {
            ["SAMLRequest"] = samlRequest,
            ["UserName"] = userName ?? string.Empty,
            ["Password"] = password ?? string.Empty,
        };

        if (relayState is not null)
        {
            fields["RelayState"] = relayState;
        }

        return new FormUrlEncodedContent(fields);
    }

    private static string PortalSamlRequest() => TestSetup.DeflatedBase64(
        TestSetup.BuildAuthnRequestXml("_flow-req-1", TestSetup.PortalIssuer, TestSetup.PortalAcs));

    [Fact]
    public async Task Healthz_ReturnsAnonymousOkJson()
    {
        var response = await _client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("""{"status":"ok"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task LoginPage_ShowsAdfsChrome_AndPostsToAdfsLs_WithHiddenRequestContext()
    {
        var samlRequest = PortalSamlRequest();

        var response = await _client.GetAsync($"/?SAMLRequest={Uri.EscapeDataString(samlRequest)}&RelayState=rs-login-1");

        response.EnsureSuccessStatusCode();
        // Attribute values are HTML-encoded on the page; decode before comparing.
        var html = System.Net.WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Contains("adfs-sim.corridor.local", html);
        Assert.Contains("action=\"/adfs/ls\"", html);
        Assert.Contains("name=\"SAMLRequest\"", html);
        Assert.Contains(samlRequest, html);
        Assert.Contains("name=\"RelayState\"", html);
        Assert.Contains("value=\"rs-login-1\"", html);
        Assert.Contains("Demo1234!", html);
    }

    [Fact]
    public async Task AdfsLs_Get_RedirectsToTheLoginPage_KeepingRequestContext()
    {
        var samlRequest = PortalSamlRequest();

        var response = await _client.GetAsync(
            $"/adfs/ls?SAMLRequest={Uri.EscapeDataString(samlRequest)}&RelayState=rs-get-1");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.StartsWith("/", location);
        Assert.Contains("SAMLRequest=", location);
        Assert.Contains("RelayState=rs-get-1", location);
    }

    [Fact]
    public async Task BadPassword_ReRendersLoginWithError_AndNoAssertionIsIssued()
    {
        var response = await _client.PostAsync("/adfs/ls", LoginPost(PortalSamlRequest(), "rs-bad", "admin@corridor.example", "wrong-password"));

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("The user name or password is incorrect", html);
        Assert.DoesNotContain("SAMLResponse", html);
        // The request context survives the retry.
        Assert.Contains("name=\"RelayState\"", html);
        Assert.Contains("value=\"rs-bad\"", html);
    }

    [Fact]
    public async Task UnknownRelyingParty_IsRefusedWithError()
    {
        var stranger = TestSetup.DeflatedBase64(
            TestSetup.BuildAuthnRequestXml("_flow-req-2", "http://unregistered.example/metadata", "http://unregistered.example/acs"));

        var response = await _client.PostAsync("/adfs/ls", LoginPost(stranger, null, "admin@corridor.example", "Demo1234!"));

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("not registered", html);
        Assert.DoesNotContain("SAMLResponse", html);
    }

    [Fact]
    public async Task ValidCredentials_PostSignedResponseToThePortalAcs_WithRelayState()
    {
        var response = await _client.PostAsync(
            "/adfs/ls",
            LoginPost(PortalSamlRequest(), "rs-ok-42", "admin@corridor.example", "Demo1234!"));

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // Auto-submitting form aimed at the registered ACS, carrying RelayState.
        Assert.Contains("action=\"http://localhost:5200/saml/acs\"", html);
        Assert.Contains("name=\"SAMLResponse\"", html);
        Assert.Contains("value=\"rs-ok-42\"", html);
        Assert.Contains("saml-form", html);

        var payload = SamlTestParsing.HiddenFieldValue(html, "SAMLResponse");
        Assert.NotNull(payload);

        var assertionXml = SamlTestParsing.ExtractAssertionXml(SamlTestParsing.Decode(payload));
        var trusted = _factory.Services.GetRequiredService<SigningCertificate>().Certificate;
        var result = SamlValidator.ValidateAssertion(assertionXml, TestSetup.PortalIssuer, DateTime.UtcNow, trusted);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Equal("admin@corridor.example", result.NameId);
        Assert.Equal("_flow-req-1", result.InResponseTo);

        // The role claim rides along in the attribute statement.
        Assert.Contains(SamlResponseBuilder.RoleClaim, assertionXml);
        Assert.Contains("Admin", assertionXml);
    }

    [Fact]
    public async Task AcsMismatchBetweenIssuerAndRequest_IsRefused()
    {
        // Registered issuer, but the request points the ACS somewhere else.
        var tamperedAcs = TestSetup.DeflatedBase64(
            TestSetup.BuildAuthnRequestXml("_flow-req-3", TestSetup.PortalIssuer, "http://evil.example/acs"));

        var response = await _client.PostAsync("/adfs/ls", LoginPost(tamperedAcs, null, "admin@corridor.example", "Demo1234!"));

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("not registered", html);
        Assert.DoesNotContain("SAMLResponse", html);
    }
}
