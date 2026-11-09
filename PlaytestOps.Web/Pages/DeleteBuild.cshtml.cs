using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlaytestOps.Web.Bridge;

namespace PlaytestOps.Web.Pages;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[RequestSizeLimit(16384)]
public sealed class DeleteBuildModel(BuildDeletionService deletion) : PageModel
{
    public BuildDeletionPreview Preview { get; private set; } = null!;
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(string id, CancellationToken cancellationToken)
    {
        if (!BridgeRegistry.IsLocalOperator(HttpContext)) return StatusCode(403);
        if (!Guid.TryParseExact(id, "N", out _)) return NotFound();
        var preview = await deletion.PreviewAsync(id, cancellationToken);
        if (preview is null) return NotFound();
        Preview = preview;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string id, bool confirmed, bool deleteFiles, CancellationToken cancellationToken)
    {
        if (!BuildDeletionService.IsLocalDeleteRequest(HttpContext)) return StatusCode(403);
        if (!Guid.TryParseExact(id, "N", out _)) return BadRequest();
        var preview = await deletion.PreviewAsync(id, cancellationToken);
        if (preview is null) return NotFound();
        Preview = preview;
        if (!ModelState.IsValid || !confirmed)
        {
            Error = "Confirm deletion of this exact build's history and captured logs before continuing.";
            Response.StatusCode = 400;
            return Page();
        }

        var result = await deletion.DeleteAsync(id, deleteFiles, cancellationToken);
        if (result.Deleted)
        {
            TempData["BuildDeletionNotice"] = deleteFiles
                ? "Build deleted. Saved history, captured build logs and local output were removed."
                : "Build deleted. Saved history and captured build logs were removed; local output files were not deleted.";
            return RedirectToPage("/Builds", new { projectId = preview.Build.ProjectId });
        }
        if (result.StatusCode == 404) return NotFound();
        var refreshed = await deletion.PreviewAsync(id, cancellationToken);
        if (refreshed is null) return NotFound();
        Preview = refreshed;
        Error = result.Error ?? "The build could not be deleted. Review its current state and try again.";
        Response.StatusCode = result.StatusCode;
        return Page();
    }
}
