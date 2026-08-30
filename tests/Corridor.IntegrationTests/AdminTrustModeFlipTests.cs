using System.Net;
using Corridor.IntegrationTests.Infrastructure;

namespace Corridor.IntegrationTests;

/// <summary>
/// The portal admin flip: driving the migration dashboard through a real OIDC login
/// flips idn.MigrationApps and records the TrustModeChanged audit event.
/// </summary>
[Collection(CorridorStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AdminTrustModeFlipTests(CorridorStackFixture fixture)
{
    [Fact]
    public async Task AdminFlip_ViaPortalDashboard_FlipsTheDbRowAndWritesAnAuditEvent()
    {
        // OIDC sign-in is only routed when the portal is in Dual or Okta mode.
        await Sql.SetTrustModeAsync(fixture.CorridorConnectionString, "portal", "Dual");
        await Sql.SetTrustModeAsync(fixture.CorridorConnectionString, "spa", "Adfs");
        try
        {
            var portal = await PortalLogin.SignInViaOktaAsync(
                fixture.PortalBase, fixture.OktaBase, "admin@corridor.example", Oidc.DemoPassword);

            var before = await Sql.GetTrustModeAsync(fixture.CorridorConnectionString, "spa");
            Assert.Equal("Adfs", before);

            var page = await PortalLogin.FlipTrustModeAsync(portal, "spa");
            Assert.Contains("spa now trusts Dual", page, StringComparison.Ordinal);

            var after = await Sql.GetTrustModeAsync(fixture.CorridorConnectionString, "spa");
            Assert.Equal("Dual", after);

            var audit = await Sql.RowAsync(fixture.CorridorConnectionString,
                """
                SELECT TOP 1 Actor, Event, Detail
                FROM idn.AuditEvents
                WHERE AppKey = 'spa'
                ORDER BY Id DESC
                """);
            Assert.NotEmpty(audit);
            Assert.Equal("admin@corridor.example", audit[0]);
            Assert.Equal("TrustModeChanged", audit[1]);
            Assert.Equal("Adfs -> Dual", audit[2]);

            var flipped = await Sql.RowAsync(fixture.CorridorConnectionString,
                "SELECT FlippedBy FROM idn.MigrationApps WHERE AppKey = 'spa'");
            Assert.Equal("admin@corridor.example", flipped[0]);
        }
        finally
        {
            await Sql.SetTrustModeAsync(fixture.CorridorConnectionString, "spa", "Adfs");
            await Sql.SetTrustModeAsync(fixture.CorridorConnectionString, "portal", "Adfs");
        }
    }

    [Fact]
    public async Task AdminFlip_NonAdminAccount_CannotOpenTheDashboard()
    {
        await Sql.SetTrustModeAsync(fixture.CorridorConnectionString, "portal", "Dual");
        try
        {
            var portal = await PortalLogin.SignInViaOktaAsync(
                fixture.PortalBase, fixture.OktaBase, "clerk@corridor.example", Oidc.DemoPassword);

            using var dashboard = await portal.GetAsync("/Admin/Migration");
            // The redirect-aware client lands on the access denied page.
            Assert.True(dashboard.IsSuccessStatusCode);
            Assert.Contains("/AccessDenied", dashboard.RequestMessage?.RequestUri?.ToString() ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            await Sql.SetTrustModeAsync(fixture.CorridorConnectionString, "portal", "Adfs");
        }
    }
}
