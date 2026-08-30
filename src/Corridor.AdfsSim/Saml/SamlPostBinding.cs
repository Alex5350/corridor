namespace Corridor.AdfsSim.Saml;

/// <summary>Renders the auto-submitting HTML form that carries a SAMLResponse back to the
/// relying party ACS (HTTP-POST binding).</summary>
public static class SamlPostBinding
{
    public static string BuildAutoSubmitForm(string actionUrl, string samlResponseBase64, string? relayState)
    {
        var action = System.Web.HttpUtility.HtmlAttributeEncode(actionUrl);
        var response = System.Web.HttpUtility.HtmlAttributeEncode(samlResponseBase64);
        var relayStateField = string.IsNullOrWhiteSpace(relayState)
            ? string.Empty
            : $"        <input type=\"hidden\" name=\"RelayState\" value=\"{System.Web.HttpUtility.HtmlAttributeEncode(relayState)}\"/>";

        return $"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
                <meta charset="utf-8"/>
                <title>Returning to the application</title>
            </head>
            <body onload="document.getElementById('saml-form').submit();">
                <noscript>
                    <p>JavaScript is disabled. Select Continue to return to the application.</p>
                </noscript>
                <form id="saml-form" method="post" action="{action}">
                    <input type="hidden" name="SAMLResponse" value="{response}"/>
            {relayStateField}
                    <noscript>
                        <button type="submit">Continue</button>
                    </noscript>
                </form>
            </body>
            </html>
            """;
    }
}
