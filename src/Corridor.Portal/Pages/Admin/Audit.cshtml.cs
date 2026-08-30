using Corridor.Portal.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Corridor.Portal.Pages.Admin;

[Authorize(Roles = "Admin")]
public class AuditModel(IAuditEventRepository audit) : PageModel
{
    public IReadOnlyList<Models.AuditEvent> Events { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Events = await audit.ListRecentAsync(50);
    }
}
