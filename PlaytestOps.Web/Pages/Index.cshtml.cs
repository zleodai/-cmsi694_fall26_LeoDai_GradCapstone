using System.Data.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlaytestOps.Web.Services;
using PlaytestOps.Web.Bridge;

namespace PlaytestOps.Web.Pages;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class IndexModel(IPlaytestService tests, BridgeRegistry registry, RunService runs, ILogger<IndexModel> logger) : PageModel
{
    public TestListViewModel TestList { get; private set; } = new([]);

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        TestList = await LoadTestsAsync(cancellationToken);

    public async Task<IActionResult> OnGetTestsAsync(CancellationToken cancellationToken)
    {
        var model = await LoadTestsAsync(cancellationToken);
        Response.Headers["X-PlaytestOps-Fragment"] = "tests";
        return new PartialViewResult
        {
            ViewName = "_TestList",
            ViewData = new Microsoft.AspNetCore.Mvc.ViewFeatures.ViewDataDictionary<TestListViewModel>(ViewData, model),
            StatusCode = model.LoadFailed ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status200OK
        };
    }

    public async Task<IActionResult> OnPostRunAsync(int testId, CancellationToken cancellationToken)
    {
        // Account authentication is not present yet; execution is restricted to the local operator.
        if (!BridgeRegistry.IsLocalOperator(HttpContext)) return StatusCode(403);
        string notice;
        try
        {
            var requested = await runs.RequestAsync(testId, cancellationToken);
            notice = requested.Error ?? "Run requested. Refresh tests to see progress and results.";
        }
        catch (Exception ex) when (ex is DbException or Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            logger.LogError(ex, "Could not request a test run.");
            notice = "Could not save the run request. Check the Editor before trying again.";
        }
        TestList = (await LoadTestsAsync(cancellationToken)) with { Notice = notice };
        if (Request.Headers["HX-Request"] == "true")
        {
            Response.Headers["X-PlaytestOps-Fragment"] = "tests";
            return new PartialViewResult { ViewName = "_TestList", ViewData = new Microsoft.AspNetCore.Mvc.ViewFeatures.ViewDataDictionary<TestListViewModel>(ViewData, TestList), StatusCode = TestList.LoadFailed ? 503 : 200 };
        }
        TempData["RunNotice"] = notice;
        return RedirectToPage();
    }

    private async Task<TestListViewModel> LoadTestsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return new(await tests.GetTestsAsync(cancellationToken), ConnectedProjects: registry.List().Where(x => x.Connected).Select(x => x.ProjectId).ToHashSet(), Notice: TempData["RunNotice"] as string, CanRun: BridgeRegistry.IsLocalOperator(HttpContext));
        }
        catch (DbException exception)
        {
            logger.LogError(exception, "Could not load the playtest dashboard.");
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return new([], LoadFailed: true);
        }
    }
}
