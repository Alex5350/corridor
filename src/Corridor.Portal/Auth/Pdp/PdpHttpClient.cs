using System.Xml;
using Microsoft.Extensions.Logging;

namespace Corridor.Portal.Auth.Pdp;

/// <summary>
/// Policy enforcement point client. Posts an XACML 2.0 request context (the dialect
/// okta-sim's PdpEngine matches on: Subject/Resource/Action categories with the standard
/// role, resource-id, and action-id attribute URIs) to POST /pdp/decide and reads the
/// Decision out of the Response document. Real decisions are cached 15 minutes per
/// (role, resource, action) triple on the same TimeProvider pattern the legacy JWKS
/// provider uses. Fail closed is non-negotiable: an unreachable PDP, an HTTP error, or
/// an unparseable Decision becomes a Deny with exactly one warning logged, and synthetic
/// denials are never cached, so a recovered PDP is consulted on the next call.
/// </summary>
public sealed class PdpHttpClient : IPdpClient
{
    public const string ClientName = "pdp";

    private const string ContextNamespace = "urn:oasis:names:tc:xacml:2.0:context:schema:os";
    private const string RoleAttributeId = "urn:oasis:names:tc:xacml:2.0:subject:role";
    private const string ResourceAttributeId = "urn:oasis:names:tc:xacml:1.0:resource:resource-id";
    private const string ActionAttributeId = "urn:oasis:names:tc:xacml:1.0:action:action-id";
    private const string StringDataType = "http://www.w3.org/2001/XMLSchema#string";

    private static readonly TimeSpan CacheTimeToLive = TimeSpan.FromMinutes(15);
    private static readonly XmlReaderSettings SafeReader = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null
    };

    private readonly HttpClient _httpClient;
    private readonly Uri _decideUri;
    private readonly TimeProvider _clock;
    private readonly ILogger<PdpHttpClient> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<(string Role, string Resource, string Action), (PdpDecision Decision, DateTimeOffset CachedAt)> _cache = new();

    public PdpHttpClient(HttpClient httpClient, TimeProvider clock, ILogger<PdpHttpClient> logger)
    {
        _httpClient = httpClient;
        var baseAddress = httpClient.BaseAddress ?? new Uri("http://localhost:8080");
        _decideUri = new Uri(baseAddress, "pdp/decide");
        _clock = clock;
        _logger = logger;
    }

    public async Task<PdpDecision> DecideAsync(string role, string resource, string action, CancellationToken cancellationToken = default)
    {
        var key = (role, resource, action);
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached) && _clock.GetUtcNow() - cached.CachedAt < CacheTimeToLive)
            {
                return cached.Decision;
            }
        }

        PdpDecision decision;
        bool decidedByPdp;
        try
        {
            (decision, decidedByPdp) = await RequestDecisionAsync(role, resource, action, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // the caller gave up, the PDP did not fail
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "The PDP call for role {Role}, resource {Resource}, action {Action} failed ({Message}); failing closed to Deny.",
                role, resource, action, exception.Message);
            return FailClosedDecision();
        }

        if (decidedByPdp)
        {
            lock (_gate)
            {
                _cache[key] = (decision, _clock.GetUtcNow());
            }
        }
        return decision;
    }

    private async Task<(PdpDecision Decision, bool DecidedByPdp)> RequestDecisionAsync(string role, string resource, string action, CancellationToken cancellationToken)
    {
        var requestXml = BuildRequestContext(role, resource, action);
        using var response = await _httpClient.PostAsync(
            _decideUri,
            new StringContent(requestXml, System.Text.Encoding.UTF8, "application/xacml+xml"),
            cancellationToken);
        var responseXml = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("The PDP answered HTTP {StatusCode}; failing closed to Deny.", (int)response.StatusCode);
            return (FailClosedDecision(), false);
        }
        return ParseDecision(responseXml);
    }

    /// <summary>Reads the first Decision and StatusMessage out of a Response document with hardened XmlReader settings.</summary>
    private (PdpDecision Decision, bool DecidedByPdp) ParseDecision(string responseXml)
    {
        try
        {
            string? decision = null;
            string? statusMessage = null;
            using var reader = XmlReader.Create(new StringReader(responseXml), SafeReader);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }
                if (reader.LocalName == "Decision" && decision is null)
                {
                    decision = reader.ReadElementContentAsString();
                }
                else if (reader.LocalName == "StatusMessage" && statusMessage is null)
                {
                    statusMessage = reader.ReadElementContentAsString();
                }
            }
            if (decision == "Permit")
            {
                return (new PdpDecision(true, statusMessage ?? string.Empty), true);
            }
            if (decision == "Deny")
            {
                return (new PdpDecision(false, statusMessage ?? string.Empty), true);
            }
            _logger.LogWarning(
                "The PDP response carried Decision '{Decision}' instead of Permit or Deny; failing closed to Deny.",
                decision ?? "(absent)");
            return (FailClosedDecision(), false);
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException)
        {
            _logger.LogWarning("The PDP response could not be parsed ({Message}); failing closed to Deny.", exception.Message);
            return (FailClosedDecision(), false);
        }
    }

    private static PdpDecision FailClosedDecision()
        => new(false, "The policy decision point did not return a usable decision; the request was denied (fail closed).");

    /// <summary>
    /// Builds the XACML 2.0 request context with XmlWriter only: every dynamic value (the
    /// role claim above all) lands in attribute or element text, never concatenated into
    /// markup, so no input can break out of the document.
    /// </summary>
    private static string BuildRequestContext(string role, string resource, string action)
    {
        using var buffer = new StringWriter();
        using (var writer = XmlWriter.Create(buffer, new XmlWriterSettings { Indent = false, OmitXmlDeclaration = true }))
        {
            writer.WriteStartElement("Request", ContextNamespace);
            WriteCategory(writer, "Subject", RoleAttributeId, role);
            WriteCategory(writer, "Resource", ResourceAttributeId, resource);
            WriteCategory(writer, "Action", ActionAttributeId, action);
            writer.WriteEndElement(); // Request
        }
        return buffer.ToString();

        static void WriteCategory(XmlWriter writer, string category, string attributeId, string value)
        {
            writer.WriteStartElement(category);
            writer.WriteStartElement("Attribute");
            writer.WriteAttributeString("AttributeId", attributeId);
            writer.WriteAttributeString("DataType", StringDataType);
            writer.WriteElementString("AttributeValue", value);
            writer.WriteEndElement(); // Attribute
            writer.WriteEndElement(); // category
        }
    }
}
