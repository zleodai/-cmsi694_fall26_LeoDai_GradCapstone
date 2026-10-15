using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlaytestOps.Web.Bridge;

namespace PlaytestOps.Web.Pages;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[RequestSizeLimit(32768)]
public sealed class BuildSettingsModel(BridgeRegistry registry, IProjectBuildConfigurationProvider configuration) : PageModel
{
    public IReadOnlyList<SessionView> Projects { get; private set; } = [];
    public SessionView? SelectedProject { get; private set; }
    public string? SelectedProjectId { get; private set; }
    public BuildConfigurationSnapshot? Snapshot { get; private set; }
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? projectId, CancellationToken cancellationToken)
    {
        if (!BridgeRegistry.IsLocalOperator(HttpContext)) return StatusCode(403);
        SelectProject(projectId);
        if (!ModelState.IsValid) { Response.StatusCode = 400; Error = "Invalid project selection."; return Page(); }
        if (SelectedProject is not null) Accept(await configuration.GetAsync(SelectedProject.ProjectId, cancellationToken));
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? projectId, string? editorSessionId, string? revision, string? settingId, string? value, CancellationToken cancellationToken)
    {
        if (!BridgeRegistry.IsLocalOperator(HttpContext)) return StatusCode(403);
        SelectProject(projectId);
        if (value is null && Request.HasFormContentType && Request.Form.ContainsKey("value")) value = Request.Form["value"].ToString();
        if (!ModelState.IsValid || SelectedProject is null || string.IsNullOrEmpty(editorSessionId) || editorSessionId != SelectedProject.SessionId ||
            !BuildConfigurationService.Revision(revision) || !BuildConfigurationService.Identifier(settingId) || value is null || value.Length > BuildConfigurationService.MaximumValueLength)
        { Response.StatusCode = 400; Error = "The settings form is invalid or its Editor session changed. Refresh before editing."; return Page(); }
        Accept(await configuration.SetAsync(SelectedProject.ProjectId, editorSessionId, revision!, settingId!, value, cancellationToken));
        return Page();
    }

    private void SelectProject(string? projectId)
    {
        Projects = registry.List().Where(item => item.Connected).OrderBy(item => item.ProjectName, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.ProjectId).ToArray();
        SelectedProjectId = string.IsNullOrEmpty(projectId) && Projects.Count == 1 ? Projects[0].ProjectId : projectId;
        SelectedProject = Projects.FirstOrDefault(item => item.ProjectId == SelectedProjectId);
        if (!string.IsNullOrEmpty(SelectedProjectId) && SelectedProject is null) Error = "The selected Unity Editor is no longer connected. Connect it and refresh.";
    }
    private void Accept(BuildConfigurationResult result)
    {
        if (SelectedProject is null || registry.ConnectedProject(SelectedProject.ProjectId)?.Id != SelectedProject.SessionId)
        { Error = "The selected Editor changed during the request. An edit may have been applied; refresh to confirm before retrying."; Snapshot = null; return; }
        Snapshot = result.Snapshot; Error = result.Error;
    }
}
