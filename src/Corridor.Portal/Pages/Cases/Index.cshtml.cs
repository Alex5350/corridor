using System.ComponentModel.DataAnnotations;
using Corridor.Portal.Auth;
using Corridor.Portal.Services.TraceLink;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace Corridor.Portal.Pages.Cases;

[Authorize(Policy = "AnyRole")]
public class IndexModel(ITraceLinkClient traceLink) : PageModel
{
    public static readonly string[] StatusOptions = ["Received", "UnderReview", "Traced", "Closed", "Rejected"];

    public static IEnumerable<SelectListItem> StatusSelectList =>
        StatusOptions.Select(s => new SelectListItem(s, s));

    public IReadOnlyList<Models.TraceCase> Cases { get; private set; } = [];

    [BindProperty(SupportsGet = true)]
    public string? StatusFilter { get; set; }

    // Display models only: binding happens through handler parameters so each form
    // validates on its own. [BindProperty] here would validate BOTH forms on every
    // POST and silently reject whichever form was not submitted.
    public CreateInput Create { get; set; } = new();

    public StatusUpdateInput StatusUpdate { get; set; } = new();

    public string? StatusMessage { get; private set; }
    public string? ErrorMessage { get; private set; }

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostCreateAsync(CreateInput create)
    {
        Create = create;
        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }
        try
        {
            var caseNumber = await traceLink.CreateTraceRequestAsync(new Models.TraceRequestCreate(
                create.LicenseeName!.Trim(),
                create.ItemDescription!.Trim(),
                create.Serial!.Trim(),
                PortalClaims.ReadUpn(User) ?? "unknown"));
            StatusMessage = $"Trace request recorded as case {caseNumber}.";
        }
        catch (TraceLinkFaultException fault)
        {
            ErrorMessage = $"The trace service rejected the request: {fault.Message} ({fault.Subcode})";
        }
        catch (HttpRequestException)
        {
            ErrorMessage = "The trace service is unreachable. Nothing was created.";
        }
        // HttpClient timeouts surface as TaskCanceledException (an OperationCanceledException):
        // the filter keeps a genuine client disconnect propagating instead of masking it as a timeout.
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested is false)
        {
            ErrorMessage = "The trace service timed out. Nothing was created.";
        }
        Create = new CreateInput();
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostUpdateStatusAsync(StatusUpdateInput statusUpdate)
    {
        StatusUpdate = statusUpdate;
        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }
        try
        {
            var updated = await traceLink.UpdateStatusAsync(
                statusUpdate.CaseNumber!.Trim(),
                statusUpdate.NewStatus!,
                PortalClaims.ReadUpn(User) ?? "unknown");
            StatusMessage = updated
                ? $"Case {statusUpdate.CaseNumber} moved to {statusUpdate.NewStatus}."
                : $"Case {statusUpdate.CaseNumber} was not updated. Check the current status.";
        }
        catch (TraceLinkFaultException fault)
        {
            ErrorMessage = $"The trace service rejected the update: {fault.Message} ({fault.Subcode})";
        }
        catch (HttpRequestException)
        {
            ErrorMessage = "The trace service is unreachable. Nothing was updated.";
        }
        // HttpClient timeouts surface as TaskCanceledException (an OperationCanceledException):
        // the filter keeps a genuine client disconnect propagating instead of masking it as a timeout.
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested is false)
        {
            ErrorMessage = "The trace service timed out. Nothing was updated.";
        }
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        var filter = StatusOptions.Contains(StatusFilter, StringComparer.Ordinal) ? StatusFilter : null;
        var requester = PortalClaims.ReadUpn(User) ?? "unknown";
        // The case list is a read, not the page's reason for being: when the trace client
        // fails here the page still renders with an inline error panel and an empty table,
        // so the create and status forms stay usable while the legacy service is down.
        try
        {
            Cases = await traceLink.SearchCasesAsync(requester, filter, 50);
        }
        catch (TraceLinkFaultException fault)
        {
            Cases = [];
            ErrorMessage = $"The trace service failed while loading cases: {fault.Message} ({fault.Subcode})";
        }
        catch (HttpRequestException)
        {
            Cases = [];
            ErrorMessage = "The trace service is unreachable. The case list below is empty.";
        }
        // HttpClient timeouts surface as TaskCanceledException (an OperationCanceledException):
        // the filter keeps a genuine client disconnect propagating instead of masking it as a timeout.
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested is false)
        {
            Cases = [];
            ErrorMessage = "The trace service timed out. The case list below is empty.";
        }
    }

    public sealed class CreateInput
    {
        [Required, StringLength(160, MinimumLength = 2)]
        public string? LicenseeName { get; set; }

        [Required, StringLength(200, MinimumLength = 3)]
        public string? ItemDescription { get; set; }

        [Required, StringLength(32, MinimumLength = 3)]
        public string? Serial { get; set; }
    }

    public sealed class StatusUpdateInput
    {
        [Required, StringLength(16, MinimumLength = 8)]
        public string? CaseNumber { get; set; }

        [Required]
        public string? NewStatus { get; set; }
    }
}
