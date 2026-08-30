using Corridor.Portal.Data.Memory;
using Corridor.Portal.Models;
using Corridor.Portal.Services;

namespace Corridor.Portal.Tests;

public class TrustModeServiceTests
{
    [Theory]
    [InlineData(TrustMode.Adfs, TrustMode.Dual)]
    [InlineData(TrustMode.Dual, TrustMode.Okta)]
    [InlineData(TrustMode.Okta, TrustMode.Adfs)]
    public void NextMode_CyclesThroughTheMigrationPath(TrustMode current, TrustMode expected)
    {
        Assert.Equal(expected, TrustModeService.NextMode(current));
    }

    [Fact]
    public async Task FlipAsync_WritesMigrationAppRowAndAuditEvent()
    {
        var apps = new InMemoryMigrationAppRepository();
        var audit = new InMemoryAuditEventRepository();
        var service = new TrustModeService(apps, audit);

        var next = await service.FlipAsync("portal", "admin@corridor.example");

        Assert.Equal(TrustMode.Dual, next);
        var stored = await apps.GetAsync("portal");
        Assert.NotNull(stored);
        Assert.Equal(TrustMode.Dual, stored.TrustMode);
        Assert.Equal("admin@corridor.example", stored.FlippedBy);
        Assert.NotNull(stored.LastFlippedAt);

        var events = await audit.ListRecentAsync(10);
        var auditEvent = Assert.Single(events);
        Assert.Equal("TrustModeChanged", auditEvent.Event);
        Assert.Equal("portal", auditEvent.AppKey);
        Assert.Equal("admin@corridor.example", auditEvent.Actor);
        Assert.Equal("Adfs -> Dual", auditEvent.Detail);
    }

    [Fact]
    public async Task FlipAsync_RejectsUnknownAppKey()
    {
        var service = new TrustModeService(
            new InMemoryMigrationAppRepository(),
            new InMemoryAuditEventRepository());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.FlipAsync("unknown", "admin@corridor.example"));
    }
}
