using System.Text;
using System.Text.RegularExpressions;

namespace Corridor.IntegrationTests.Infrastructure;

/// <summary>Builds XACML 2.0 context requests and reads the PDP decision out of the response.</summary>
public static class Xacml
{
    public const string RoleId = "urn:oasis:names:tc:xacml:2.0:subject:role";
    public const string ResourceId = "urn:oasis:names:tc:xacml:1.0:resource:resource-id";
    public const string ActionId = "urn:oasis:names:tc:xacml:1.0:action:action-id";
    public const string ContextNs = "urn:oasis:names:tc:xacml:2.0:context:schema:os";

    public static string Request(string role, string resource, string action) =>
        $$"""
        <Request xmlns="{{ContextNs}}"><Subject><Attribute AttributeId="{{RoleId}}"><AttributeValue>{{role}}</AttributeValue></Attribute></Subject><Resource><Attribute AttributeId="{{ResourceId}}"><AttributeValue>{{resource}}</AttributeValue></Attribute></Resource><Action><Attribute AttributeId="{{ActionId}}"><AttributeValue>{{action}}</AttributeValue></Attribute></Action></Request>
        """;

    public static async Task<XacmlResponse> DecideAsync(HttpClient http, Uri oktaBase, string requestBody)
    {
        using var response = await http.PostAsync(
            new Uri(oktaBase, "/pdp/decide"),
            new StringContent(requestBody, Encoding.UTF8, "application/xacml+xml"));
        var xml = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"PDP answered HTTP {(int)response.StatusCode}");
        Assert.Contains("<Decision>", xml, StringComparison.Ordinal);
        var decision = ExtractElement(xml, "Decision");
        var statusCode = ExtractAttribute(xml, "StatusCode", "Value");
        var statusMessage = ExtractElement(xml, "StatusMessage");
        return new XacmlResponse(decision, statusCode, statusMessage, xml);
    }

    public sealed record XacmlResponse(string Decision, string StatusCode, string StatusMessage, string RawXml);

    private static string ExtractElement(string xml, string localName)
    {
        var opening = $"<{localName}";
        var start = xml.IndexOf(opening, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }
        var valueStart = xml.IndexOf('>', start) + 1;
        var end = xml.IndexOf($"</{localName}>", valueStart, StringComparison.Ordinal);
        return end < 0 ? string.Empty : xml[valueStart..end].Trim();
    }

    /// <summary>Reads an attribute off an element, for the empty StatusCode element.</summary>
    private static string ExtractAttribute(string xml, string localName, string attributeName)
    {
        var opening = $"<{localName}";
        var start = xml.IndexOf(opening, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }
        var match = Regex.Match(xml[start..], $"{attributeName}=\"(?<value>[^\"]+)\"");
        return match.Success ? match.Groups["value"].Value : string.Empty;
    }
}
