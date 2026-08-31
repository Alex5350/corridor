using System.Security.Claims;
using Corridor.Portal.Data;
using Corridor.Portal.Data.Memory;
using Corridor.Portal.Models;
using Corridor.Portal.Pages.Admin;
using Corridor.Portal.Services;
using Corridor.Portal.Services.Scim;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Corridor.Portal.Tests;

/// <summary>In-memory SCIM endpoint for provisioning tests: records calls, hands back ids, can throw.</summary>
public sealed class FakeScimProvisioner : IScimProvisioner
{
    public List<ScimUser> Created { get; } = [];
    public List<(string ExternalId, ScimUser User)> Replaced { get; } = [];
    public List<string> Deactivated { get; } = [];

    /// <summary>Staged failure for the next call of that kind, used for the error paths.</summary>
    public Exception? ThrowOnNextCall { get; set; }

    private void ThrowIfScripted()
    {
        if (ThrowOnNextCall is { } failure)
        {
            ThrowOnNextCall = null;
            throw failure;
        }
    }

    public Task<string> CreateAsync(ScimUser user, CancellationToken ct = default)
    {
        ThrowIfScripted();
        Created.Add(user);
        return Task.FromResult($"scim-{Created.Count:000}");
    }

    public Task ReplaceAsync(string externalId, ScimUser user, CancellationToken ct = default)
    {
        ThrowIfScripted();
        Replaced.Add((externalId, user));
        return Task.CompletedTask;
    }

    public Task DeactivateAsync(string externalId, CancellationToken ct = default)
    {
        ThrowIfScripted();
        Deactivated.Add(externalId);
        return Task.CompletedTask;
    }
}

public class DirectoryProvisionerTests
{
    private static DirectoryUserAccount Account(
        string upn, string? externalId, bool active = true, string role = "Officer") =>
        new(1, upn, "Demo User", role, externalId, active);

    [Theory]
    [InlineData(true, null, ProvisionAction.Create)]
    [InlineData(true, "scim-001", ProvisionAction.Replace)]
    [InlineData(false, "scim-001", ProvisionAction.Deactivate)]
    [InlineData(false, null, ProvisionAction.Skip)]
    public void Decide_PicksTheScimOperationFromExternalIdAndActiveFlag(
        bool active, string? externalId, ProvisionAction expected)
    {
        Assert.Equal(expected, ProvisionPlan.Decide(Account("officer@corridor.example", externalId, active)));
    }

    [Fact]
    public async Task ProvisionAsync_CreatesReplacesAndDeactivates_AndWritesOneAuditRow()
    {
        var accounts = new InMemoryDirectoryUserRepository(new List<DirectoryUserAccount>
        {
            Account("admin@corridor.example", externalId: null, role: "Admin"),
            Account("officer@corridor.example", externalId: "scim-001"),
            Account("clerk@corridor.example", externalId: "scim-002", active: false, role: "Clerk")
        });
        var audit = new InMemoryAuditEventRepository();
        var scim = new FakeScimProvisioner();
        var provisioner = new DirectoryProvisioner(accounts, scim, audit);

        var summary = await provisioner.ProvisionAsync("admin@corridor.example");

        Assert.Equal(new ProvisioningSummary(1, 1, 1), summary);
        Assert.Equal("created 1, updated 1, deactivated 1", summary.Describe());

        // The created account's returned id lands in the directory row, so the next run updates it.
        Assert.Equal("scim-001", (await accounts.ListAsync()).Single(a => a.Upn == "admin@corridor.example").ScimExternalId);
        Assert.Equal(("scim-001", new ScimUser("officer@corridor.example", "Demo User", "Officer")), scim.Replaced.Single());
        Assert.Equal("scim-002", scim.Deactivated.Single());

        var auditEvent = Assert.Single(await audit.ListRecentAsync(10));
        Assert.Equal("oktasim", auditEvent.AppKey);
        Assert.Equal("DirectoryProvisioned", auditEvent.Event);
        Assert.Equal("admin@corridor.example", auditEvent.Actor);
        Assert.Equal("created 1, updated 1, deactivated 1", auditEvent.Detail);
    }

    [Fact]
    public async Task ProvisionAsync_WhenTheProvisionerThrows_WritesNoAuditRow()
    {
        var accounts = new InMemoryDirectoryUserRepository(new List<DirectoryUserAccount>
        {
            Account("officer@corridor.example", externalId: null)
        });
        var audit = new InMemoryAuditEventRepository();
        var scim = new FakeScimProvisioner
        {
            ThrowOnNextCall = new ScimProvisioningException("SCIM create failed with 400: userName taken.")
        };
        var provisioner = new DirectoryProvisioner(accounts, scim, audit);

        await Assert.ThrowsAsync<ScimProvisioningException>(
            () => provisioner.ProvisionAsync("admin@corridor.example"));

        Assert.Empty(await audit.ListRecentAsync(10));
    }

    [Fact]
    public async Task Handler_ShowsTheRunSummaryOnTheDashboard()
    {
        var accounts = new InMemoryDirectoryUserRepository(new List<DirectoryUserAccount>
        {
            Account("officer@corridor.example", externalId: "scim-001")
        });
        var audit = new InMemoryAuditEventRepository();
        var model = NewMigrationModel(new DirectoryProvisioner(accounts, new FakeScimProvisioner(), audit));

        await model.OnPostProvisionAsync();

        Assert.Equal("Directory provisioned into okta-sim: created 0, updated 1, deactivated 0.", model.StatusMessage);
        Assert.Null(model.ErrorMessage);
        Assert.Single(await audit.ListRecentAsync(10));
    }

    [Fact]
    public async Task Handler_WhenProvisioningFails_ShowsTheErrorInlineAndClaimsNoSuccess()
    {
        var audit = new InMemoryAuditEventRepository();
        var model = NewMigrationModel(new DirectoryProvisioner(
            new InMemoryDirectoryUserRepository(),
            new FakeScimProvisioner
            {
                ThrowOnNextCall = new HttpRequestException("connection refused")
            },
            audit));

        await model.OnPostProvisionAsync();

        Assert.Contains("Directory provisioning failed", model.ErrorMessage);
        Assert.Contains("connection refused", model.ErrorMessage);
        Assert.Null(model.StatusMessage);
        Assert.Empty(await audit.ListRecentAsync(10));
    }

    /// <summary>The dashboard page model with a signed-in admin, as the Razor handler would see it.</summary>
    private static MigrationModel NewMigrationModel(DirectoryProvisioner provisioning)
    {
        var model = new MigrationModel(
            new InMemoryMigrationAppRepository(),
            new TrustModeService(new InMemoryMigrationAppRepository(), new InMemoryAuditEventRepository()),
            provisioning);
        var claims = new List<Claim>
        {
            new("upn", "admin@corridor.example"),
            new(ClaimTypes.Role, "Admin")
        };
        model.PageContext = new PageContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestScheme"))
            }
        };
        return model;
    }
}
