using System.Text;
using System.Xml.Linq;

namespace Corridor.IntegrationTests.Infrastructure;

/// <summary>The outcome of one raw SOAP 1.1 call to TraceLink.</summary>
public sealed record SoapResult(int StatusCode, bool IsFault, string? Subcode, string? FaultString, XDocument Document)
{
    public XElement Body => Document.Root?.Element(TraceLinkSoap.NsSoap + "Body")
        ?? throw new InvalidOperationException("The SOAP response has no body.");
}

/// <summary>
/// Hand-built SOAP 1.1 envelopes for the TraceLink service: contract namespace for
/// operations, cor:Security (security namespace) header carrying either an unprefixed
/// jwt element or a saml:Assertion, exactly the wire profile the dispatch inspector
/// enforces.
/// </summary>
public static class TraceLinkSoap
{
    public const string ContractNs = "http://corridor.example/tracelink/2026/08";
    public const string SecurityNs = "http://corridor.example/security";
    public const string SamlNs = "urn:oasis:names:tc:SAML:2.0:assertion";
    public const string ServiceUrlPath = "/TraceLink.svc";

    public static readonly XNamespace NsSoap = "http://schemas.xmlsoap.org/soap/envelope/";
    public static readonly XNamespace NsCor = ContractNs;

    public static string SearchCasesBody(string requester, string? statusFilter, int maxRows) =>
        $"""
            <SearchCases xmlns="{ContractNs}"><requester>{Encode(requester)}</requester><statusFilter>{Encode(statusFilter ?? string.Empty)}</statusFilter><maxRows>{maxRows}</maxRows></SearchCases>
            """;

    public static string GetCaseBody(string caseNumber) =>
        $"""<GetCase xmlns="{ContractNs}"><caseNumber>{Encode(caseNumber)}</caseNumber></GetCase>""";

    /// <summary>DataContract member order matters: ItemDescription, LicenseeName, RequesterUpn, Serial.</summary>
    public static string CreateTraceRequestBody(string licenseeName, string itemDescription, string serial, string requesterUpn) =>
        $"""<CreateTraceRequest xmlns="{ContractNs}"><request><ItemDescription>{Encode(itemDescription)}</ItemDescription><LicenseeName>{Encode(licenseeName)}</LicenseeName><RequesterUpn>{Encode(requesterUpn)}</RequesterUpn><Serial>{Encode(serial)}</Serial></request></CreateTraceRequest>""";

    public static string UpdateStatusBody(string caseNumber, string newStatus, string actor) =>
        $"""<UpdateStatus xmlns="{ContractNs}"><caseNumber>{Encode(caseNumber)}</caseNumber><newStatus>{Encode(newStatus)}</newStatus><actor>{Encode(actor)}</actor></UpdateStatus>""";

    public static string BuildJwtEnvelope(string operationBodyXml, string jwt)
    {
        var envelope = new XElement(NsSoap + "Envelope",
            new XAttribute(XNamespace.Xmlns + "cor", SecurityNs),
            new XElement(NsSoap + "Header",
                new XElement(XNamespace.Get(SecurityNs) + "Security",
                    new XElement("jwt", jwt))),
            new XElement(NsSoap + "Body", XElement.Parse(operationBodyXml)));
        return envelope.ToString(SaveOptions.DisableFormatting);
    }

    public static string BuildSamlEnvelope(string operationBodyXml, string assertionXml)
    {
        var envelope = new XElement(NsSoap + "Envelope",
            new XAttribute(XNamespace.Xmlns + "cor", SecurityNs),
            new XAttribute(XNamespace.Xmlns + "saml", SamlNs),
            new XElement(NsSoap + "Header",
                new XElement(XNamespace.Get(SecurityNs) + "Security",
                    XElement.Parse(assertionXml))),
            new XElement(NsSoap + "Body", XElement.Parse(operationBodyXml)));
        return envelope.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>An envelope with no cor:Security header at all, for the negative path.</summary>
    public static string BuildUnsecuredEnvelope(string operationBodyXml)
    {
        var envelope = new XElement(NsSoap + "Envelope",
            new XElement(NsSoap + "Body", XElement.Parse(operationBodyXml)));
        return envelope.ToString(SaveOptions.DisableFormatting);
    }

    public static async Task<SoapResult> CallAsync(HttpClient http, Uri legacyBase, string operation, string envelope)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(legacyBase, ServiceUrlPath));
        request.Content = new StringContent(envelope, Encoding.UTF8, "text/xml");
        request.Headers.Add("SOAPAction", $"\"{ContractNs}/TraceLinkService/{operation}\"");
        using var response = await http.SendAsync(request);
        var xml = await response.Content.ReadAsStringAsync();
        var document = XDocument.Parse(xml);
        var fault = document.Root?.Element(NsSoap + "Body")?.Element(NsSoap + "Fault");
        if (fault is null)
        {
            return new SoapResult((int)response.StatusCode, IsFault: false, Subcode: null, FaultString: null, Document: document);
        }

        // SOAP 1.1 faultcode is an unqualified element; CoreWCF writes it as
        // <faultcode xmlns:a="...">a:Subcode</faultcode>, so match by local name.
        var faultCode = fault.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "faultcode")?.Value ?? string.Empty;
        var localName = faultCode.Contains(':') ? faultCode.Split(':', 2)[1] : faultCode;
        var faultString = fault.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "faultstring")?.Value.Trim();
        return new SoapResult((int)response.StatusCode, IsFault: true, Subcode: localName, FaultString: faultString, Document: document);
    }

    /// <summary>All TraceCase entries in a SearchCases response.</summary>
    public static List<(string CaseNumber, string Status)> ReadCases(SoapResult result)
    {
        Assert.False(result.IsFault, $"SearchCases faulted: {result.Subcode} {result.FaultString}");
        return result.Body.Descendants(NsCor + "TraceCase")
            .Select(c => (
                c.Element(NsCor + "CaseNumber")?.Value ?? string.Empty,
                c.Element(NsCor + "Status")?.Value ?? string.Empty))
            .ToList();
    }

    public static string ReadCreatedCaseNumber(SoapResult result)
    {
        Assert.False(result.IsFault, $"CreateTraceRequest faulted: {result.Subcode} {result.FaultString}");
        var value = result.Body.Descendants(NsCor + "CreateTraceRequestResult").FirstOrDefault()?.Value.Trim();
        Assert.False(string.IsNullOrEmpty(value), "CreateTraceRequest returned no case number.");
        return value!;
    }

    public static bool ReadUpdateStatusResult(SoapResult result)
    {
        Assert.False(result.IsFault, $"UpdateStatus faulted: {result.Subcode} {result.FaultString}");
        var value = result.Body.Descendants(NsCor + "UpdateStatusResult").FirstOrDefault()?.Value.Trim();
        return bool.TryParse(value, out var updated) && updated;
    }

    private static string Encode(string value) => System.Security.SecurityElement.Escape(value) ?? value;
}
