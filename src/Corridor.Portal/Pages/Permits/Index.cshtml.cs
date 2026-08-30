using System.ComponentModel.DataAnnotations;
using Corridor.Portal.Auth;
using Corridor.Portal.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Corridor.Portal.Pages.Permits;

[Authorize(Policy = "AnyRole")]
public class IndexModel(IPermitRepository permits) : PageModel
{
    public static readonly string[] StatusOptions = ["UnderReview", "Approved", "Rejected"];

    public IReadOnlyList<Models.Permit> Permits { get; private set; } = [];

    [BindProperty(SupportsGet = true)]
    public string? StatusFilter { get; set; }

    [BindProperty]
    public ApplyInput Apply { get; set; } = new();

    public string? StatusMessage { get; private set; }
    public string? ErrorMessage { get; private set; }

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostApplyAsync()
    {
        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }
        try
        {
            var created = await permits.CreateAsync(new Models.NewPermit(
                Apply.LicenseeName!.Trim(),
                Apply.ItemDescription!.Trim(),
                Apply.Quantity,
                Apply.Purpose!.Trim(),
                PortalClaims.ReadUpn(User) ?? "unknown"));
            StatusMessage = $"Application {created.PermitNumber} recorded in UnderReview status.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.SqlClient.SqlException)
        {
            ErrorMessage = "The permit store is not reachable. The application was not recorded.";
        }
        await LoadAsync();
        Apply = new ApplyInput();
        return Page();
    }

    private async Task LoadAsync()
    {
        var filter = StatusOptions.Contains(StatusFilter, StringComparer.Ordinal) ? StatusFilter : null;
        Permits = await permits.ListAsync(filter);
    }

    public sealed class ApplyInput
    {
        [Required, StringLength(160, MinimumLength = 2)]
        public string? LicenseeName { get; set; }

        [Required, StringLength(200, MinimumLength = 3)]
        public string? ItemDescription { get; set; }

        [Required, Range(1, 100000)]
        public int Quantity { get; set; }

        [Required, StringLength(300, MinimumLength = 3)]
        public string? Purpose { get; set; }
    }
}
