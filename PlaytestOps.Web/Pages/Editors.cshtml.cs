using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlaytestOps.Web.Bridge;

namespace PlaytestOps.Web.Pages;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class EditorsModel(BridgeRegistry registry) : PageModel
{
    public IReadOnlyList<SessionView> Sessions { get; private set; } = [];
    public string? PairingCode { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }
    public bool CanPair => BridgeRegistry.IsLocalOperator(HttpContext);
    public void OnGet() => Sessions = registry.List();
    // Razor Pages validates the form's antiforgery token automatically.
    public IActionResult OnPostPairingCode()
    {
        if (!CanPair) return StatusCode(403);
        (PairingCode, ExpiresAt) = registry.CreateCode();
        Sessions = registry.List();
        return Page();
    }
}
