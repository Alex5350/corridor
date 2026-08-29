using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Corridor.OktaSim.Tests;

/// <summary>
/// Boots the real app in memory. No connection string is configured, so the
/// directory runs on the in-memory seeded store: unit tests never need SQL
/// Server or the network. Keys and policies resolve from the committed repo
/// layout via the app's own walk-up path resolution.
/// </summary>
public sealed class OktaSimFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Corridor", string.Empty);
        builder.UseSetting("OktaSim:Issuer", "http://localhost:8080");
    }

    /// <summary>
    /// Client that does not follow redirects: the OAuth endpoints answer with
    /// 302s whose Location we must inspect, not chase (the redirect targets are
    /// the portal and SPA ports, which the test server does not host).
    /// </summary>
    public HttpClient CreateNoRedirectClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
}
