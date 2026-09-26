using System.Data.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlaytestOps.Web.Services;

namespace PlaytestOps.Web.Pages;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class IndexModel(IPlaytestService tests, ILogger<IndexModel> logger) : PageModel
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

    private async Task<TestListViewModel> LoadTestsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return new(await tests.GetTestsAsync(cancellationToken));
        }
        catch (DbException exception)
        {
            logger.LogError(exception, "Could not load the playtest dashboard.");
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return new([], LoadFailed: true);
        }
    }
}
