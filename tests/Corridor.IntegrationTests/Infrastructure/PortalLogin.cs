using System.Net;

namespace Corridor.IntegrationTests.Infrastructure;

/// <summary>
/// Drives a real portal sign-in: /Login?provider=Okta challenges okta-sim, the
/// credentials are POSTed to its login form, and the callback sets the portal cookie.
/// Requires the portal to be in Dual or Okta trust mode (the caller sets it).
/// </summary>
public static class PortalLogin
{
    public static async Task<HttpClient> SignInViaOktaAsync(
        Uri portalBase,
        Uri oktaBase,
        string username,
        string password)
    {
        var client = new HttpClient(new BrowserLikeCookieHandler())
        {
            BaseAddress = portalBase,
        };

        // The redirect chain parks on the okta-sim login form.
        using var challenge = await client.GetAsync("/Login?provider=Okta&returnUrl=%2F");
        var loginHtml = await challenge.Content.ReadAsStringAsync();
        Assert.Contains("name=\"username\"", loginHtml, StringComparison.Ordinal);

        var form = HtmlForms.ParseHiddenFields(loginHtml);
        form["username"] = username;
        form["password"] = password;
        using var post = new HttpRequestMessage(HttpMethod.Post, new Uri(oktaBase, "/authorize"))
        {
            Content = new FormUrlEncodedContent(form),
        };
        using var callback = await client.SendAsync(post);
        Assert.True(callback.IsSuccessStatusCode,
            $"Portal OIDC callback failed HTTP {(int)callback.StatusCode} at {callback.RequestMessage?.RequestUri}");

        // Prove the cookie jar now authenticates against the portal.
        await OpenMigrationDashboardAsync(client);
        return client;
    }

    /// <summary>Reads the migration dashboard, which only an authenticated Admin can open.</summary>
    public static async Task<string> OpenMigrationDashboardAsync(HttpClient portalClient)
    {
        using var dashboard = await portalClient.GetAsync("/Admin/Migration");
        Assert.True(dashboard.IsSuccessStatusCode,
            $"The portal cookie did not authenticate: HTTP {(int)dashboard.StatusCode}");
        return await dashboard.Content.ReadAsStringAsync();
    }

    /// <summary>POSTs the flip handler with the Razor antiforgery token.</summary>
    public static async Task<string> FlipTrustModeAsync(HttpClient portalClient, string appKey)
    {
        var dashboard = await OpenMigrationDashboardAsync(portalClient);
        var token = HtmlForms.AntiforgeryToken(dashboard);
        using var response = await portalClient.PostAsync(
            "/Admin/Migration?handler=Flip",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["appKey"] = appKey,
            }));
        Assert.True(response.IsSuccessStatusCode,
            $"The flip POST failed HTTP {(int)response.StatusCode}");
        return await response.Content.ReadAsStringAsync();
    }

    private static string Trim(string value) => value.Length <= 400 ? value : value[..400];
}
