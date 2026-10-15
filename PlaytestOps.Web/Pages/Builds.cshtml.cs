using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PlaytestOps.Web.Bridge;
using PlaytestOps.Web.Data;
using PlaytestOps.Web.Models;

namespace PlaytestOps.Web.Pages;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[RequestSizeLimit(16384)]
public sealed class BuildsModel(BridgeRegistry registry, IProjectBuildConfigurationProvider configuration, BuildExecutionService builds, ApplicationDbContext db) : PageModel
{
    public IReadOnlyList<SessionView> Projects { get; private set; } = [];
    public SessionView? SelectedProject { get; private set; }
    public string? SelectedProjectId { get; private set; }
    public BuildConfigurationSnapshot? Configuration { get; private set; }
    public IReadOnlyList<UnityBuild> History { get; private set; } = [];
    public string RequestKey { get; private set; } = "";
    public string? Error { get; private set; }
    public bool Held { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? projectId, CancellationToken cancellationToken)
    {
        if (!BridgeRegistry.IsLocalOperator(HttpContext)) return StatusCode(403);
        SelectProject(projectId);
        History = await db.UnityBuilds.AsNoTracking().Where(item => SelectedProjectId == null || item.ProjectId == SelectedProjectId).OrderByDescending(item => item.RequestedAt).Take(50).ToArrayAsync(cancellationToken);
        if (SelectedProject is not null)
        {
            var result = await configuration.GetAsync(SelectedProject.ProjectId, cancellationToken);
            Configuration = result.Snapshot; Error = result.Error;
            Held = await db.UnityBuilds.AnyAsync(item => item.ProjectId == SelectedProject.ProjectId && (item.State == "Pending" || item.State == "Running" || item.State == "Unknown"), cancellationToken);
            if (Configuration is { State: "available", BuildExecutionSupported: true } snapshot && snapshot.Fields.Any(field => field.Editable)) RequestKey = builds.IssueRequestKey(SelectedProject.ProjectId, SelectedProject.SessionId, snapshot.Revision);
        }
        return Page();
    }

    public async Task<IActionResult> OnPostStartAsync(string? projectId, string? editorSessionId, string? revision, string? requestKey, CancellationToken cancellationToken)
    {
        if (!BridgeRegistry.IsLocalOperator(HttpContext)) return StatusCode(403);
        if (!ModelState.IsValid || projectId is null || editorSessionId is null || revision is null || requestKey is null) return BadRequest();
        var result = await builds.RequestAsync(projectId, editorSessionId, revision, requestKey, cancellationToken);
        if (result.BuildId is not null) return RedirectToPage("/BuildDetails", new { id = result.BuildId });
        SelectProject(projectId); Error = result.Error;
        History = await db.UnityBuilds.AsNoTracking().Where(item => item.ProjectId == projectId).OrderByDescending(item => item.RequestedAt).Take(50).ToArrayAsync(cancellationToken);
        Response.StatusCode = 409;
        return Page();
    }
    private void SelectProject(string? projectId)
    {
        Projects = registry.List().Where(item => item.Connected).OrderBy(item => item.ProjectName).ThenBy(item => item.ProjectId).ToArray();
        SelectedProjectId = string.IsNullOrEmpty(projectId) && Projects.Count == 1 ? Projects[0].ProjectId : projectId;
        SelectedProject = Projects.FirstOrDefault(item => item.ProjectId == SelectedProjectId);
    }
}
