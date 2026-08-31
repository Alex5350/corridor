using Corridor.Portal.Auth;
using Corridor.Portal.Data;
using Corridor.Portal.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Corridor.Portal.Pages.Admin;

[Authorize(Roles = "Admin")]
public class MigrationModel(
    IMigrationAppRepository apps,
    TrustModeService trustModes,
    DirectoryProvisioner provisioning) : PageModel
{
    public IReadOnlyList<Models.MigrationApp> Apps { get; private set; } = [];

    public string? StatusMessage { get; private set; }
    public string? ErrorMessage { get; private set; }

    public async Task OnGetAsync()
    {
        Apps = await apps.ListAsync();
    }

    public async Task<IActionResult> OnPostFlipAsync(string appKey)
    {
        if (string.IsNullOrWhiteSpace(appKey))
        {
            ErrorMessage = "No application key was supplied.";
            Apps = await apps.ListAsync();
            return Page();
        }
        try
        {
            var next = await trustModes.FlipAsync(appKey, PortalClaims.ReadUpn(User) ?? "unknown");
            StatusMessage = $"{appKey} now trusts {Models.TrustModes.Label(next)}.";
        }
        catch (InvalidOperationException)
        {
            ErrorMessage = $"Unknown application key {appKey}.";
        }
        Apps = await apps.ListAsync();
        return Page();
    }

    /// <summary>
    /// Provisions idn.Users into the target directory over the SCIM bridge. A provisioning
    /// failure shows inline here; the provisioner only writes its audit event after every
    /// account succeeded, so the trail never records a run that did not complete.
    /// </summary>
    public async Task<IActionResult> OnPostProvisionAsync()
    {
        try
        {
            var summary = await provisioning.ProvisionAsync(PortalClaims.ReadUpn(User) ?? "unknown");
            StatusMessage = $"Directory provisioned into okta-sim: {summary.Describe()}.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Directory provisioning failed: {ex.Message}";
        }
        Apps = await apps.ListAsync();
        return Page();
    }

    public static string NextModeLabel(Models.TrustMode current)
    {
        return Models.TrustModes.Label(TrustModeService.NextMode(current));
    }
}
