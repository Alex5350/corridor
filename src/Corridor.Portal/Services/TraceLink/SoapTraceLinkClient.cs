using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Xml.Linq;
using Corridor.Portal.Auth;
using Corridor.Portal.Auth.Saml;
using Corridor.Portal.Data;
using Corridor.Portal.Models;
using Microsoft.Extensions.Options;

namespace Corridor.Portal.Services.TraceLink;

/// <summary>
/// Obtains client credentials tokens from okta-sim for the legacy client, cached 10 minutes.
/// </summary>
public sealed class OktaServiceTokenClient(HttpClient httpClient, IOptions<OktaOptions> okta, IOptions<LegacyOptions> legacy)
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _expiresAt;

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (_accessToken is not null && DateTimeOffset.UtcNow < _expiresAt)
        {
            return _accessToken;
        }
        await _gate.WaitAsync(ct);
        try
        {
            if (_accessToken is not null && DateTimeOffset.UtcNow < _expiresAt)
            {
                return _accessToken;
            }
            var tokenEndpoint = okta.Value.Authority.TrimEnd('/') + "/token";
            using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint);
            var basic = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(
                $"{legacy.Value.OktaClientId}:{legacy.Value.OktaClientSecret ?? string.Empty}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials"
            });
            using var response = await httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(ct);
            _accessToken = payload?.AccessToken
                ?? throw new InvalidOperationException("okta-sim returned no access token for the legacy client.");
            _expiresAt = DateTimeOffset.UtcNow + CacheLifetime;
            return _accessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record TokenResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("access_token")] string? AccessToken);
}

/// <summary>
/// Chooses the SOAP security credential for the legacy hop based on the LEGACY app's current
/// TrustMode: ADFS mode mints a service SAML assertion signed with the ADFS dev certificate;
/// Dual and Okta modes use an okta-sim client credentials JWT.
/// </summary>
public sealed class LegacyCredentialFactory(
    IMigrationAppRepository apps,
    AdfsCertificateStore adfsCertificates,
    SamlAssertionFactory assertions,
    OktaServiceTokenClient tokens,
    IOptions<AdfsOptions> adfs,
    IOptions<LegacyOptions> legacy)
{
    public async Task<SoapCredential> GetCredentialAsync(CancellationToken ct = default)
    {
        var app = await apps.GetAsync("legacy", ct);
        var mode = app?.TrustMode ?? TrustMode.Adfs;
        if (mode == TrustMode.Adfs)
        {
            var now = DateTime.UtcNow;
            var spec = new SignedAssertionSpec(
                adfs.Value.Issuer,
                legacy.Value.ServiceUrl,
                legacy.Value.ServiceUpn,
                legacy.Value.ServiceUpn,
                [],
                now.AddMinutes(-1),
                now.AddMinutes(5));
            return new SoapCredential(true, assertions.BuildSignedAssertion(spec, adfsCertificates.LoadCertificateWithPrivateKey()));
        }
        var token = await tokens.GetAccessTokenAsync(ct);
        return new SoapCredential(false, token);
    }
}

/// <summary>
/// Real TraceLink client: builds SOAP 1.1 envelopes with the cor:Security header, posts them
/// to the legacy service, and parses cases and faults back out. Forwards the inbound
/// traceparent header on every hop and logs the correlation id.
/// </summary>
public sealed class SoapTraceLinkClient(
    IHttpClientFactory httpClients,
    LegacyCredentialFactory credentials,
    IOptions<LegacyOptions> legacy,
    IHttpContextAccessor httpContextAccessor,
    ILogger<SoapTraceLinkClient> logger) : ITraceLinkClient
{
    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace Cor = "http://corridor.example/tracelink/2026/08";
    private static readonly XNamespace Sec = "http://corridor.example/security";
    private static readonly XNamespace SamlNs = "urn:oasis:names:tc:SAML:2.0:assertion";

    public async Task<IReadOnlyList<TraceCase>> SearchCasesAsync(string requester, string? statusFilter, int maxRows, CancellationToken ct = default)
    {
        var body = new XElement(Cor + "SearchCases",
            new XElement(Cor + "requester", requester),
            new XElement(Cor + "statusFilter", statusFilter),
            new XElement(Cor + "maxRows", maxRows));
        var responseBody = await CallAsync(TraceLinkHttpClients.Read, "SearchCases", body, ct);
        var result = FindElement(responseBody, "SearchCasesResult") ?? responseBody;
        return [.. result.Elements().Where(e => e.Name.LocalName == "TraceCase").Select(ReadCase)];
    }

    public async Task<TraceCase?> GetCaseAsync(string caseNumber, CancellationToken ct = default)
    {
        var body = new XElement(Cor + "GetCase", new XElement(Cor + "caseNumber", caseNumber));
        var responseBody = await CallAsync(TraceLinkHttpClients.Read, "GetCase", body, ct);
        var result = FindElement(responseBody, "GetCaseResult");
        return result is null || result.IsEmpty ? null : ReadCase(result);
    }

    public async Task<string> CreateTraceRequestAsync(TraceRequestCreate request, CancellationToken ct = default)
    {
        var body = new XElement(Cor + "CreateTraceRequest",
            new XElement(Cor + "request",
                new XElement(Cor + "ItemDescription", request.ItemDescription),
                new XElement(Cor + "LicenseeName", request.LicenseeName),
                new XElement(Cor + "RequesterUpn", request.RequesterUpn),
                new XElement(Cor + "Serial", request.Serial)));
        var responseBody = await CallAsync(TraceLinkHttpClients.Write, "CreateTraceRequest", body, ct);
        var result = FindElement(responseBody, "CreateTraceRequestResult")?.Value
            ?? throw new TraceLinkFaultException(TraceLinkFaults.Unavailable, "The legacy service returned no case number.");
        return result.Trim();
    }

    public async Task<bool> UpdateStatusAsync(string caseNumber, string newStatus, string actor, CancellationToken ct = default)
    {
        var body = new XElement(Cor + "UpdateStatus",
            new XElement(Cor + "caseNumber", caseNumber),
            new XElement(Cor + "newStatus", newStatus),
            new XElement(Cor + "actor", actor));
        var responseBody = await CallAsync(TraceLinkHttpClients.Write, "UpdateStatus", body, ct);
        var result = FindElement(responseBody, "UpdateStatusResult")?.Value.Trim();
        return bool.TryParse(result, out var updated) && updated;
    }

    private async Task<XElement> CallAsync(string clientName, string action, XElement operationBody, CancellationToken ct)
    {
        var credential = await credentials.GetCredentialAsync(ct);
        var security = credential.IsSamlAssertion
            ? new XElement(Sec + "Security", XElement.Parse(credential.Content))
            : new XElement(Sec + "Security", new XElement("jwt", credential.Content));
        var envelope = new XElement(Soap + "Envelope",
            new XAttribute(XNamespace.Xmlns + "cor", Cor.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "saml", SamlNs.NamespaceName),
            new XElement(Soap + "Header", security),
            new XElement(Soap + "Body", operationBody));

        using var request = new HttpRequestMessage(HttpMethod.Post, legacy.Value.ServiceUrl);
        request.Content = new StringContent(envelope.ToString(), System.Text.Encoding.UTF8, "text/xml");
        request.Headers.Add("SOAPAction", $"\"{Cor.NamespaceName}/TraceLinkService/{action}\"");
        var traceparent = httpContextAccessor.HttpContext?.Request.Headers.TraceParent.ToString();
        if (!string.IsNullOrEmpty(traceparent))
        {
            request.Headers.Add("traceparent", traceparent);
        }
        var correlationId = Activity.Current?.Id ?? traceparent ?? "none";
        logger.LogInformation("SOAP {Action} over {ClientName} to {ServiceUrl} correlation {CorrelationId}", action, clientName, legacy.Value.ServiceUrl, correlationId);

        var httpClient = httpClients.CreateClient(clientName);
        using var response = await httpClient.SendAsync(request, ct);
        var xml = await response.Content.ReadAsStringAsync(ct);
        var document = XDocument.Parse(xml);
        var body = document.Root?.Element(Soap + "Body");
        var fault = body?.Element(Soap + "Fault");
        if (fault is not null)
        {
            throw new TraceLinkFaultException(ReadSubcode(fault), ReadFaultString(fault));
        }
        if (!response.IsSuccessStatusCode || body is null)
        {
            throw new TraceLinkFaultException(TraceLinkFaults.Unavailable,
                $"The legacy trace service answered HTTP {(int)response.StatusCode} without a SOAP body.");
        }
        return body;
    }

    private static XElement? FindElement(XElement body, string localName)
    {
        return body.Descendants().FirstOrDefault(e => e.Name.LocalName == localName);
    }

    private static string Field(XElement element, string localName)
    {
        return element.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value.Trim() ?? "";
    }

    private static TraceCase ReadCase(XElement element)
    {
        return new TraceCase(
            Field(element, "CaseNumber"),
            Field(element, "LicenseeName"),
            Field(element, "ItemDescription"),
            Field(element, "Serial"),
            Field(element, "Status"),
            DateTime.TryParse(Field(element, "SubmittedAt"), out var submittedAt) ? submittedAt.ToUniversalTime() : DateTime.MinValue,
            Field(element, "SubmittedBy"),
            Field(element, "Disposition") is { Length: > 0 } disposition ? disposition : null);
    }

    private static string ReadSubcode(XElement fault)
    {
        // SOAP 1.1 has no formal subcode: honor a nested Subcode element when the service
        // provides one, otherwise fall back to the cor: token inside the faultcode, and
        // finally to well known message shapes from the stored procedures.
        var subcode = fault.Descendants()
            .Where(e => e.Name.LocalName is "Subcode" or "subcode")
            .Select(e => e.Value.Trim())
            .FirstOrDefault(v => v.Length > 0);
        if (subcode is not null)
        {
            return Normalize(subcode);
        }
        // CoreWCF serializes SOAP 1.1 faultcode/faultstring as UNQUALIFIED child elements,
        // so resolve them by local name rather than envelope-qualified lookup.
        var faultCode = fault.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "faultcode")?.Value
            ?? fault.Element(Soap + "Code")?.Value
            ?? fault.Elements().FirstOrDefault(e => e.Name.LocalName == "Code")?.Value
            ?? "";
        // The subcode namespace prefix (a:, cor:) varies on the wire: accept any prefixed token.
        var match = System.Text.RegularExpressions.Regex.Match(faultCode, @"[A-Za-z0-9]+:[A-Za-z]+");
        if (match.Success)
        {
            return Normalize(match.Value);
        }
        var message = ReadFaultString(fault);
        if (message.Contains("Illegal transition", StringComparison.OrdinalIgnoreCase))
        {
            return TraceLinkFaults.IllegalStatusTransition;
        }
        if (message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return TraceLinkFaults.CaseNotFound;
        }
        if (message.Contains("Unknown status", StringComparison.OrdinalIgnoreCase)
            || message.Contains("invalid", StringComparison.OrdinalIgnoreCase))
        {
            return TraceLinkFaults.ValidationError;
        }
        return TraceLinkFaults.Unavailable;
    }

    private static string Normalize(string subcode)
    {
        // Wire prefixes vary (a:, cor:, none): collapse any prefix onto the canonical cor: form.
        var localName = subcode.Contains(':')
            ? subcode[(subcode.LastIndexOf(':') + 1)..]
            : subcode.TrimStart(':');
        return "cor:" + localName;
    }

    private static string ReadFaultString(XElement fault)
    {
        return fault.Elements().FirstOrDefault(e => e.Name.LocalName == "faultstring")?.Value.Trim()
            ?? fault.Element(Soap + "faultstring")?.Value.Trim()
            ?? fault.Element(Soap + "Reason")?.Value.Trim()
            ?? "The legacy trace service returned a SOAP fault.";
    }
}
