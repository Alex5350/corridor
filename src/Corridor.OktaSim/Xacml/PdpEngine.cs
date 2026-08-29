using System.Xml;
using Corridor.OktaSim.Models;
using Corridor.OktaSim.Saml;

namespace Corridor.OktaSim.Xacml;

/// <summary>
/// The attribute triple the demo policies decide on, extracted from an XACML
/// 2.0 or 3.0 context Request. Null means the attribute was not presented.
/// </summary>
public sealed record XacmlRequest(string? Role, string? ResourceId, string? ActionId);

/// <summary>
/// A simplified XACML rule: string-equality match sets per category; a null set
/// is a wildcard. Deliberate subset of XACML (single-valued string attributes,
/// string-equal matching, first-applicable combining) documented as such: it is
/// enough to express the three demo policies honestly.
/// </summary>
public sealed record XacmlRule(string RuleId, string Effect, HashSet<string>? Roles, HashSet<string>? Resources, HashSet<string>? Actions)
{
    public bool AppliesTo(XacmlRequest request) =>
        (Roles is null || (request.Role is not null && Roles.Contains(request.Role)))
        && (Resources is null || (request.ResourceId is not null && Resources.Contains(request.ResourceId)))
        && (Actions is null || (request.ActionId is not null && Actions.Contains(request.ActionId)));
}

public sealed record XacmlPolicy(string PolicyId, IReadOnlyList<XacmlRule> Rules);

/// <summary>
/// Policy decision point. Loads policies from the repo's policies/ directory at
/// startup (filename order matters: the deny-all sorts last) and falls back to an
/// in-code copy of the same three policies so tests and stripped deployments
/// still have a working PDP. Malformed requests always get a real XACML Response
/// with a Deny and a StatusMessage, never a naked 500.
/// </summary>
public sealed class PdpEngine
{
    public const string RoleAttributeId = "urn:oasis:names:tc:xacml:2.0:subject:role";
    public const string ResourceAttributeId = "urn:oasis:names:tc:xacml:1.0:resource:resource-id";
    public const string ActionAttributeId = "urn:oasis:names:tc:xacml:1.0:action:action-id";

    private static readonly string ContextNs2 = "urn:oasis:names:tc:xacml:2.0:context:schema:os";
    private static readonly string ContextNs3 = "urn:oasis:names:tc:xacml:3.0:context:schema:os";

    private readonly IReadOnlyList<XacmlPolicy> _policies;
    public int PolicyCount => _policies.Count;
    public string SourceDescription { get; }

    public PdpEngine(IWebHostEnvironment env, IConfiguration config, ILogger<PdpEngine> logger)
    {
        (_policies, SourceDescription) = LoadFromDisk(env, config, logger);
    }

    public PdpEngine(IReadOnlyList<XacmlPolicy> policies, string sourceDescription = "in-code fallback")
    {
        _policies = policies;
        SourceDescription = sourceDescription;
    }

    private static (IReadOnlyList<XacmlPolicy>, string) LoadFromDisk(
        IWebHostEnvironment env, IConfiguration config, ILogger<PdpEngine> logger)
    {
        var files = Services.ContentPaths.ListFiles(
            env, config, "OktaSim:PoliciesDir", Path.Combine("..", "..", "policies"), "*.xacml.xml");
        if (files.Count == 0)
        {
            logger.LogWarning("No policy files found under policies/; using the in-code fallback set");
            return (FallbackPolicies(), "in-code fallback");
        }
        var loaded = new List<XacmlPolicy>();
        foreach (var file in files)
        {
            loaded.Add(XacmlPolicyParser.Parse(File.ReadAllText(file)));
        }
        logger.LogInformation("Loaded {Count} XACML policies from {Directory}", loaded.Count, Path.GetDirectoryName(files[0]));
        return (loaded, $"{files.Count} file(s) from policies/");
    }

    /// <summary>Decide a raw request body into a complete XACML 2.0 Response document.</summary>
    public string Decide(string requestBody)
    {
        if (string.IsNullOrWhiteSpace(requestBody))
        {
            return WriteResponse("Deny", errorStatus: "urn:oasis:names:tc:xacml:1.0:status:syntax-error",
                message: "Malformed XACML request: body is empty.");
        }

        XacmlRequest request;
        try
        {
            request = ParseRequest(requestBody);
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException or FormatException)
        {
            return WriteResponse("Deny", errorStatus: "urn:oasis:names:tc:xacml:1.0:status:syntax-error",
                message: $"Malformed XACML request: {ex.Message}");
        }

        foreach (var policy in _policies)
        {
            foreach (var rule in policy.Rules)
            {
                if (rule.AppliesTo(request))
                {
                    return WriteResponse(rule.Effect, policyId: policy.PolicyId);
                }
            }
        }

        // Nothing matched at all (possible when the deny-all file is missing):
        // XACML semantics say a request with no applicable policy is NotApplicable,
        // and the safe encoding of that for this PDP is Deny.
        return WriteResponse("Deny");
    }

    /// <summary>
    /// Parses XACML 2.0 (Subject/Resource/Action elements) and 3.0 (Attributes
    /// with Category) request contexts with hardened XmlReader settings.
    /// </summary>
    public static XacmlRequest ParseRequest(string xml)
    {
        string? role = null, resource = null, action = null;
        using var reader = XmlReader.Create(new StringReader(xml), SafeXml.ReaderSettings);
        var currentCategory = (Category?)null;

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }
            switch (reader.LocalName)
            {
                case "Request":
                {
                    var ns = reader.NamespaceURI;
                    if (ns != ContextNs2 && ns != ContextNs3)
                    {
                        throw new InvalidOperationException($"Request namespace {ns} is not an XACML 2.0/3.0 context schema.");
                    }
                    break;
                }
                case "Subject":
                case "Resource":
                case "Action":
                    currentCategory = reader.LocalName switch
                    {
                        "Subject" => Category.Subject,
                        "Resource" => Category.Resource,
                        _ => Category.Action,
                    };
                    break;
                case "Attributes" when reader.NamespaceURI == ContextNs3:
                {
                    var category = reader.GetAttribute("Category") ?? string.Empty;
                    currentCategory = category.Contains("subject", StringComparison.OrdinalIgnoreCase)
                        ? Category.Subject
                        : category.Contains("resource", StringComparison.OrdinalIgnoreCase)
                            ? Category.Resource
                            : category.Contains("action", StringComparison.OrdinalIgnoreCase)
                                ? Category.Action
                                : null;
                    break;
                }
                case "Attribute":
                {
                    if (currentCategory is not null)
                    {
                        var pair = ReadAttribute(reader);
                        if (pair is not null)
                        {
                            var (attributeId, value) = pair.Value;
                            switch (attributeId)
                            {
                                case RoleAttributeId when currentCategory == Category.Subject:
                                    role ??= value;
                                    break;
                                case ResourceAttributeId when currentCategory == Category.Resource:
                                    resource ??= value;
                                    break;
                                case ActionAttributeId when currentCategory == Category.Action:
                                    action ??= value;
                                    break;
                            }
                        }
                    }
                    break;
                }
                default:
                    break;
            }
        }

        if (role is null && resource is null && action is null)
        {
            throw new InvalidOperationException("No recognized attributes (role, resource-id, action-id) in the request.");
        }
        return new XacmlRequest(role, resource, action);

        static (string AttributeId, string Value)? ReadAttribute(XmlReader reader)
        {
            var attributeId = reader.GetAttribute("AttributeId");
            if (string.IsNullOrEmpty(attributeId))
            {
                return null;
            }
            var subtree = reader.ReadSubtree();
            while (subtree.Read())
            {
                if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "AttributeValue")
                {
                    return (attributeId, subtree.ReadElementContentAsString());
                }
            }
            return null;
        }
    }

    /// <summary>Hand-builds the XACML 2.0 Response document (XmlWriter, no LINQ).</summary>
    public static string WriteResponse(
        string decision,
        string? policyId = null,
        string? errorStatus = null,
        string? message = null)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            OmitXmlDeclaration = false,
            Encoding = System.Text.Encoding.UTF8,
        };
        using var buffer = new StringWriter();
        using (var writer = XmlWriter.Create(buffer, settings))
        {
            writer.WriteStartElement("Response", ContextNs2);
            writer.WriteStartElement("Result");
            writer.WriteElementString("Decision", decision);
            writer.WriteStartElement("Status");
            var statusCode = errorStatus ?? "urn:oasis:names:tc:xacml:1.0:status:ok";
            writer.WriteStartElement("StatusCode");
            writer.WriteAttributeString("Value", statusCode);
            writer.WriteEndElement(); // StatusCode
            if (message is not null)
            {
                writer.WriteElementString("StatusMessage", message);
            }
            writer.WriteEndElement(); // Status
            if (errorStatus is not null)
            {
                writer.WriteStartElement("Obligation");
                writer.WriteAttributeString("ObligationId", "corridor:obligation:pdp-error");
                writer.WriteStartElement("AttributeAssignment");
                writer.WriteAttributeString("AttributeId", "corridor:obligation:detail");
                writer.WriteAttributeString("DataType", "http://www.w3.org/2001/XMLSchema#string");
                writer.WriteString(message ?? "request rejected");
                writer.WriteEndElement(); // AttributeAssignment
                writer.WriteEndElement(); // Obligation
            }
            else if (policyId is not null)
            {
                writer.WriteElementString("PolicyIdReference", policyId);
            }
            writer.WriteEndElement(); // Result
            writer.WriteEndElement(); // Response
        }
        return buffer.ToString();
    }

    private enum Category
    {
        Subject,
        Resource,
        Action,
    }

    /// <summary>In-code copy of the three policies committed under policies/.</summary>
    public static IReadOnlyList<XacmlPolicy> FallbackPolicies() =>
    [
        new XacmlPolicy(
            "corridor:policy:trace-read:1",
            [
                new XacmlRule(
                    "corridor:rule:trace-read:permit",
                    "Permit",
                    Roles: [DirectoryRoles.Officer, DirectoryRoles.Admin],
                    Resources: ["trace-cases"],
                    Actions: ["read"]),
            ]),
        new XacmlPolicy(
            "corridor:policy:assignments-write:1",
            [
                new XacmlRule(
                    "corridor:rule:assignments-write:permit",
                    "Permit",
                    Roles: [DirectoryRoles.Inspector],
                    Resources: ["assignments"],
                    Actions: ["write"]),
            ]),
        new XacmlPolicy(
            "corridor:policy:deny-all:1",
            [
                new XacmlRule("corridor:rule:deny-all", "Deny", Roles: null, Resources: null, Actions: null),
            ]),
    ];
}

/// <summary>Parses the committed policy files into the simplified rule model.</summary>
public static class XacmlPolicyParser
{
    public static XacmlPolicy Parse(string xml)
    {
        var doc = SafeXml.LoadDocument(xml);
        var root = doc.DocumentElement
            ?? throw new InvalidOperationException("Policy document has no root element.");
        if (root.LocalName != "Policy")
        {
            throw new InvalidOperationException($"Expected a Policy element, found {root.LocalName}.");
        }
        var policyId = root.GetAttribute("PolicyId");
        if (string.IsNullOrEmpty(policyId))
        {
            throw new InvalidOperationException("Policy element has no PolicyId.");
        }

        var rules = new List<XacmlRule>();
        foreach (XmlElement ruleElement in root.GetElementsByTagName("Rule", root.NamespaceURI))
        {
            var ruleId = ruleElement.GetAttribute("RuleId");
            var effect = ruleElement.GetAttribute("Effect");
            if (effect is not ("Permit" or "Deny"))
            {
                throw new InvalidOperationException($"Rule {ruleId} has an unsupported Effect '{effect}'.");
            }

            HashSet<string>? roles = null, resources = null, actions = null;
            var target = ruleElement.GetElementsByTagName("Target", root.NamespaceURI);
            if (target.Count > 0 && target[0] is XmlElement targetElement)
            {
                roles = Collect(targetElement, "Subjects", "SubjectMatch", "SubjectAttributeDesignator",
                    PdpEngine.RoleAttributeId, root.NamespaceURI);
                resources = Collect(targetElement, "Resources", "ResourceMatch", "ResourceAttributeDesignator",
                    PdpEngine.ResourceAttributeId, root.NamespaceURI);
                actions = Collect(targetElement, "Actions", "ActionMatch", "ActionAttributeDesignator",
                    PdpEngine.ActionAttributeId, root.NamespaceURI);
            }
            rules.Add(new XacmlRule(ruleId, effect, roles, resources, actions));
        }
        return new XacmlPolicy(policyId, rules);
    }

    private static HashSet<string>? Collect(
        XmlElement target,
        string container,
        string match,
        string designator,
        string attributeId,
        string ns)
    {
        var containers = target.GetElementsByTagName(container, ns);
        if (containers.Count == 0)
        {
            return null; // category absent from the target: wildcard
        }
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (XmlElement matchElement in ((XmlElement)containers[0]!).GetElementsByTagName(match, ns))
        {
            var designators = matchElement.GetElementsByTagName(designator, ns);
            foreach (XmlElement d in designators)
            {
                if (d.GetAttribute("AttributeId") != attributeId)
                {
                    continue;
                }
                var attributeValues = matchElement.GetElementsByTagName("AttributeValue", ns);
                foreach (XmlElement v in attributeValues)
                {
                    if (!string.IsNullOrEmpty(v.InnerText))
                    {
                        values.Add(v.InnerText.Trim());
                    }
                }
            }
        }
        return values.Count == 0 ? null : values;
    }
}
