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
    public string? Error { get; private set; }
    public SessionView? ConnectedEditor => Sessions.FirstOrDefault(session => session.Connected);
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

    public IActionResult OnPostDisconnect(string? sessionId)
    {
        if (!CanPair) return StatusCode(403);
        var editor = registry.ConnectedEditor();
        if (!ModelState.IsValid || editor is null || editor.Id != sessionId)
        {
            Response.StatusCode = StatusCodes.Status409Conflict;
            Error = "The Editor connection changed. Refresh before disconnecting; no other Editor was disconnected.";
            Sessions = registry.List();
            return Page();
        }
        registry.Revoke(editor);
        return RedirectToPage();
    }
}
