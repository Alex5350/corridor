using System.Text.RegularExpressions;
using System.Xml;
using Corridor.AdfsSim.Saml;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Corridor.AdfsSim.Tests;

/// <summary>Boots the adfs-sim app with no database: the in-memory demo user store kicks
/// in because the test configuration has no Corridor connection string.</summary>
public sealed class AdfsSimFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
    }
}

public static class SamlTestParsing
{
    private static readonly Regex HiddenValue = new("name=\"(?<name>[^\"]+)\" value=\"(?<value>[^\"]*)\"", RegexOptions.Compiled);

    public static string? HiddenFieldValue(string html, string name) =>
        HiddenValue.Matches(html)
            .Where(m => m.Groups["name"].Value == name)
            .Select(m => System.Net.WebUtility.HtmlDecode(m.Groups["value"].Value))
            .FirstOrDefault();

    public static string ExtractAssertionXml(string responseXml)
    {
        var doc = SamlXml.LoadDocument(responseXml);
        var assertion = doc.GetElementsByTagName("Assertion", SamlXml.AssertionNs)[0]
            ?? throw new InvalidOperationException("The response carries no saml:Assertion.");
        return ((XmlElement)assertion).OuterXml;
    }

    public static string Decode(string base64) =>
        System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
}
