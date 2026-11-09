using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlaytestOps.Web.Bridge;

namespace PlaytestOps.Web.Pages;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class StreamingModel : PageModel
{
    public IActionResult OnGet()
    {
        if (!BridgeRegistry.IsLocalOperator(HttpContext)) return StatusCode(403);
        return Page();
    }
}
