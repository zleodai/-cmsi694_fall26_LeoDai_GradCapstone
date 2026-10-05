using System.Data.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PlaytestOps.Web.Data;

namespace PlaytestOps.Web.Pages;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class RunsModel(ApplicationDbContext database, ILogger<RunsModel> logger) : PageModel
{
    public RunDetailsViewModel Details { get; private set; } = new(null, []);

    public async Task<IActionResult> OnGetAsync(string id, string? level, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(id, "N", out _)) return NotFound();
        Details = await LoadAsync(id, level, cancellationToken);
        if (Details.Run is null && !Details.LoadFailed) return NotFound();
        return Page();
    }

    public async Task<IActionResult> OnGetDetailsAsync(string id, string? level, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(id, "N", out _)) return NotFound();
        Details = await LoadAsync(id, level, cancellationToken);
        if (Details.Run is null && !Details.LoadFailed) return NotFound();
        Response.Headers["X-PlaytestOps-Fragment"] = "run-details";
        return new PartialViewResult
        {
            ViewName = "_RunDetails",
            ViewData = new Microsoft.AspNetCore.Mvc.ViewFeatures.ViewDataDictionary<RunDetailsViewModel>(ViewData, Details),
            StatusCode = Details.LoadFailed ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status200OK
        };
    }

    private async Task<RunDetailsViewModel> LoadAsync(string id, string? level, CancellationToken cancellationToken)
    {
        var selectedLevel = level?.ToLowerInvariant() is "debug" or "warning" or "error" ? level.ToLowerInvariant() : "all";
        try
        {
            var run = await database.PlaytestRuns.AsNoTracking().Include(run => run.Playtest).ThenInclude(test => test.Project)
                .SingleOrDefaultAsync(run => run.Id == id, cancellationToken);
            var logs = run?.Logs.Where(log => selectedLevel switch
            {
                "debug" => log.Level == "Debug",
                "warning" => log.Level == "Warning",
                "error" => log.Level is "Error" or "Assert" or "Exception",
                _ => true
            }).OrderBy(log => log.Sequence).ToList() ?? [];
            return new(run, logs, selectedLevel);
        }
        catch (DbException exception)
        {
            logger.LogError(exception, "Could not load playtest run {RunId}.", id);
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return new(null, [], selectedLevel, LoadFailed: true);
        }
    }
}
