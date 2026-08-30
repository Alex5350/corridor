using System.Text.RegularExpressions;

namespace Corridor.IntegrationTests.Infrastructure;

/// <summary>Minimal HTML scraping for the simulation's hand-written forms.</summary>
public static partial class HtmlForms
{
    [GeneratedRegex(@"<input[^>]*type=""hidden""[^>]*name=""(?<name>[^""]+)""[^>]*value=""(?<value>[^""]*)""[^>]*/?>")]
    private static partial Regex HiddenField();

    public static Dictionary<string, string> ParseHiddenFields(string html)
    {
        var fields = new Dictionary<string, string>();
        foreach (Match match in HiddenField().Matches(html))
        {
            fields[match.Groups["name"].Value] = match.Groups["value"].Value;
        }
        return fields;
    }

    /// <summary>The Razor antiforgery token hidden input (__RequestVerificationToken).</summary>
    public static string AntiforgeryToken(string html)
    {
        var match = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""(?<token>[^""]+)""");
        Assert.True(match.Success, "The page carries no antiforgery token.");
        return match.Groups["token"].Value;
    }
}
