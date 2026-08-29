using System.Net;
using System.Text;
using Corridor.OktaSim.Models;
using Corridor.OktaSim.Stores;

namespace Corridor.OktaSim.Endpoints;

/// <summary>
/// Read-only "Okta-style" admin console at the root: directory users, registered
/// applications, and each app's identity provider assignment. Server-rendered
/// plain HTML with inline CSS; no client framework, no scripts.
/// </summary>
public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", async (IUserStore users, ClientRegistry clients) =>
        {
            var html = await RenderAsync(users, clients);
            return Results.Content(html, "text/html; charset=utf-8");
        }).ExcludeFromDescription();
        return app;
    }

    public static async Task<string> RenderAsync(IUserStore users, ClientRegistry clients)
    {
        var directory = await users.ListAsync();

        var body = new StringBuilder();
        body.Append("""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Corridor Okta simulation: admin console</title>
            <style>
              :root { color-scheme: light; }
              body { font-family: system-ui, -apple-system, "Segoe UI", sans-serif; margin: 0; background: #f3f4f6; color: #1f2937; }
              header { background: #1a3d6d; color: #fff; padding: 1rem 1.5rem; }
              header h1 { font-size: 1.15rem; margin: 0; }
              header p { margin: .25rem 0 0; font-size: .8rem; color: #c7d5ee; }
              main { padding: 1.5rem; max-width: 62rem; margin: 0 auto; }
              h2 { font-size: 1rem; margin: 1.5rem 0 .5rem; }
              table { border-collapse: collapse; width: 100%; background: #fff; font-size: .85rem; box-shadow: 0 1px 2px rgba(16,24,40,.08); }
              th, td { text-align: left; padding: .5rem .75rem; border-bottom: 1px solid #e5e7eb; vertical-align: top; }
              th { background: #f9fafb; font-weight: 600; color: #4b5563; }
              .pill { display: inline-block; padding: .1rem .5rem; border-radius: 999px; font-size: .72rem; }
              .pill.ok { background: #dcfce7; color: #166534; }
              .pill.off { background: #fee2e2; color: #991b1b; }
              .pill.mode { background: #dbeafe; color: #1e40af; }
              footer { padding: 1.5rem; font-size: .75rem; color: #6b7280; text-align: center; }
            </style>
            </head>
            <body>
            <header>
              <h1>Corridor Okta simulation: admin console</h1>
              <p>Target identity provider for the ADFS-to-Okta migration demo. Read-only view; synthetic data only.</p>
            </header>
            <main>
            """);

        body.Append("<h2>Directory: users</h2>");
        body.Append("<table><tr><th>Login (upn)</th><th>Display name</th><th>Role</th><th>Status</th><th>Groups</th><th>SCIM id</th></tr>");
        foreach (var user in directory)
        {
            var status = user.Active
                ? "<span class=\"pill ok\">Active</span>"
                : "<span class=\"pill off\">Deactivated</span>";
            body.Append($"<tr><td>{WebUtility.HtmlEncode(user.UserName)}</td>"
                + $"<td>{WebUtility.HtmlEncode(user.DisplayName)}</td>"
                + $"<td>{WebUtility.HtmlEncode(user.Role)}</td>"
                + $"<td>{status}</td>"
                + $"<td>{WebUtility.HtmlEncode(string.Join(", ", user.Groups))}</td>"
                + $"<td><code>{WebUtility.HtmlEncode(user.Id)}</code></td></tr>");
        }
        body.Append("</table>");

        body.Append("<h2>Applications</h2>");
        body.Append("<table><tr><th>App</th><th>Client type</th><th>Redirect / grant</th><th>IdP assignment</th></tr>");
        foreach (var client in clients.All())
        {
            var clientType = client.IsPublic
                ? "Public (PKCE S256 required)"
                : client.AllowsGrant("client_credentials")
                    ? "Confidential (service)"
                    : "Confidential";
            var redirect = client.RedirectUris.Count > 0
                ? string.Join(", ", client.RedirectUris)
                : "client_credentials (no redirect)";
            var assignment = AssignmentFor(client);
            body.Append($"<tr><td>{WebUtility.HtmlEncode(client.Description)}</td>"
                + $"<td>{WebUtility.HtmlEncode(clientType)}</td>"
                + $"<td>{WebUtility.HtmlEncode(redirect)}</td>"
                + $"<td><span class=\"pill mode\">{WebUtility.HtmlEncode(assignment)}</span></td></tr>");
        }
        body.Append("""
            </table>
            <p style="font-size:.78rem;color:#6b7280">Protocol surfaces: OIDC discovery at <code>/.well-known/openid-configuration</code>,
            SAML IdP metadata at <code>/saml/metadata</code>, SCIM provisioning at <code>/scim/v2/Users</code>,
            XACML decisions at <code>/pdp/decide</code>, liveness at <code>/healthz</code>.</p>
            </main>
            <footer>Corridor portfolio simulation. Every account, token, and certificate here is synthetic demo material.</footer>
            </body>
            </html>
            """);

        return body.ToString();
    }

    /// <summary>
    /// IdP assignment shown per app: this simulated org is the Okta target; the
    /// portal additionally carries the ADFS SAML trust during dual-trust cutover.
    /// </summary>
    private static string AssignmentFor(OAuthClient client) => client.ClientId switch
    {
        ClientRegistry.PortalClientId => "Okta (OIDC) + ADFS SAML during dual trust",
        ClientRegistry.SpaClientId => "Okta (OIDC + PKCE)",
        ClientRegistry.LegacyClientId => "Okta (service token, client credentials)",
        _ => "Okta",
    };
}
