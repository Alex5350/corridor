using Corridor.Portal.Auth;
using Corridor.Portal.Data;
using Corridor.Portal.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Corridor.Portal.Pages.Admin;

[Authorize(Roles = "Admin")]
public class MigrationModel(IMigrationAppRepository apps, TrustModeService trustModes) : PageModel
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

    public static string NextModeLabel(Models.TrustMode current)
    {
        return Models.TrustModes.Label(TrustModeService.NextMode(current));
    }
}