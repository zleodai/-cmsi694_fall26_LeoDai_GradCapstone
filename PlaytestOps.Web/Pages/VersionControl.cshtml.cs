using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlaytestOps.Web.Bridge;

namespace PlaytestOps.Web.Pages;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class VersionControlModel(BridgeRegistry registry, IProjectVersionControlProvider versionControl) : PageModel
{
    public IReadOnlyList<SessionView> Projects { get; private set; } = [];
    public SessionView? SelectedProject { get; private set; }
    public string? SelectedProjectId { get; private set; }
    public int Offset { get; private set; }
    public string Scope { get; private set; } = "repository";
    public VcsSnapshot? Snapshot { get; private set; }
    public string? Error { get; private set; }
    public bool ProjectUnavailable { get; private set; }
    public DateTimeOffset? ReceivedAt { get; private set; }
    public int PreviousOffset => Math.Max(0, Offset - VersionControlService.PageSize);
    public int NextOffset => Offset + VersionControlService.PageSize;
    public bool CanViewNext => Snapshot?.History is { State: "available", HasMore: true } && NextOffset <= VersionControlService.MaximumOffset;

    public async Task<IActionResult> OnGetAsync(string? projectId, int offset, string? scope, CancellationToken cancellationToken)
    {
        // No dashboard account authentication exists yet. Repository metadata can be
        // sensitive and is available only to the localhost operator, just like source.
        if (!BridgeRegistry.IsLocalOperator(HttpContext)) return StatusCode(StatusCodes.Status403Forbidden);
        Projects = registry.List().Where(project => project.Connected)
            .OrderBy(project => project.ProjectName, StringComparer.OrdinalIgnoreCase).ThenBy(project => project.ProjectId).ToList();
        SelectedProjectId = string.IsNullOrEmpty(projectId) && Projects.Count == 1 ? Projects[0].ProjectId : projectId;
        if (!ModelState.IsValid || !VersionControlService.IsPageRequest(offset, scope ?? "repository"))
        {
            Error = "Choose repository or branch history with an offset between 0 and 100,000.";
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return Page();
        }
        Offset = offset;
        Scope = scope ?? "repository";
        if (string.IsNullOrEmpty(SelectedProjectId)) return Page();
        SelectedProject = Projects.FirstOrDefault(project => project.ProjectId == SelectedProjectId);
        if (SelectedProject is null)
        {
            ProjectUnavailable = true;
            return Page();
        }
        var result = await versionControl.SnapshotAsync(SelectedProject.ProjectId, Offset, Scope, cancellationToken);
        if (registry.ConnectedProject(SelectedProject.ProjectId)?.Id != SelectedProject.SessionId)
        {
            ProjectUnavailable = true;
            SelectedProject = null;
            return Page();
        }
        Error = result.Error;
        Snapshot = result.Snapshot;
        if (Snapshot is not null) ReceivedAt = DateTimeOffset.UtcNow;
        return Page();
    }

    public static string Utc(string timestamp) => DateTimeOffset.ParseExact(timestamp,
        ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:ss'Z'"], CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal).ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    public static string ChangesetNumber(long number) => number < 0 ? "Unavailable" : number.ToString(CultureInfo.InvariantCulture);
    public static string Display(string text) => string.IsNullOrEmpty(text) ? "Unavailable" : text;
    public static string StateMessage(VcsSnapshot snapshot) => snapshot.State switch
    {
        "notUvcs" => "This Unity project is not in a UVCS workspace.",
        "missingClient" => VersionControlService.ErrorMessage("missingClient"),
        "busy" => VersionControlService.ErrorMessage("busy"),
        _ => VersionControlService.ErrorMessage(snapshot.ErrorCode)
    };
}
