using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlaytestOps.Web.Bridge;

namespace PlaytestOps.Web.Pages;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class ScriptsModel(BridgeRegistry registry, IProjectSourceProvider sources) : PageModel
{
    public const long MaximumFileBytes = SourceService.MaxFileBytes;
    public IReadOnlyList<SessionView> Projects { get; private set; } = [];
    public SessionView? SelectedProject { get; private set; }
    public string? SelectedProjectId { get; private set; }
    public string? SelectedPath { get; private set; }
    public string Search { get; private set; } = "";
    public SourceListResult? Listing { get; private set; }
    public SourceReadResult? SelectedScript { get; private set; }
    public IReadOnlyList<SourceFileEntry> Files { get; private set; } = [];
    public string? Error { get; private set; }
    public string? ReadError { get; private set; }
    public bool ProjectUnavailable { get; private set; }
    public DateTimeOffset? ReadAt { get; private set; }
    public IReadOnlyList<string> Roots => Listing?.Roots ?? ["Assets/Scripts", "Assets/Tests"];

    public async Task<IActionResult> OnGetAsync(string? projectId, string? path, string? q, CancellationToken cancellationToken)
    {
        // Project source is sensitive: account authentication is not present yet.
        // Only the local operator can request a list or read saved source from Unity.
        if (!BridgeRegistry.IsLocalOperator(HttpContext)) return StatusCode(StatusCodes.Status403Forbidden);
        Projects = registry.List().Where(project => project.Connected)
            .OrderBy(project => project.ProjectName, StringComparer.OrdinalIgnoreCase).ThenBy(project => project.ProjectId).ToList();
        Search = (q ?? "").Trim();
        if (Search.Length > 200)
        {
            Search = Search[..200];
            Error = "Search is limited to 200 characters. Shorten the path or filename and try again.";
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return Page();
        }
        SelectedProjectId = string.IsNullOrEmpty(projectId) && Projects.Count == 1 ? Projects[0].ProjectId : projectId;
        if (string.IsNullOrEmpty(SelectedProjectId)) return Page();
        SelectedProject = Projects.FirstOrDefault(project => project.ProjectId == SelectedProjectId);
        if (SelectedProject is null)
        {
            ProjectUnavailable = true;
            return Page();
        }
        Listing = await sources.ListAsync(SelectedProject.ProjectId, cancellationToken);
        if (ClearIfDisconnected()) return Page();
        if (Listing.Error is not null)
        {
            Error = Listing.Error;
            return Page();
        }
        Files = Listing.Files.Where(file => file.Path.Contains(Search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToList();
        if (string.IsNullOrEmpty(path)) return Page();
        var selected = Listing.Files.FirstOrDefault(file => string.Equals(file.Path, path, StringComparison.Ordinal));
        if (selected is null)
        {
            ReadError = "The selected script is no longer available within the configured reader folders. Choose a script from the current list.";
            return Page();
        }
        SelectedPath = selected.Path;
        if (selected.ByteLength > MaximumFileBytes)
        {
            ReadError = "This script is larger than the 128 KiB reader limit. Its contents were not requested from Unity.";
            return Page();
        }
        var read = await sources.ReadAsync(SelectedProject.ProjectId, selected.Path, cancellationToken);
        if (ClearIfDisconnected()) return Page();
        if (read.Error is not null) ReadError = read.Error;
        else
        {
            SelectedScript = read;
            ReadAt = DateTimeOffset.UtcNow;
        }
        return Page();
    }

    private bool ClearIfDisconnected()
    {
        if (SelectedProject is not null && registry.ConnectedProject(SelectedProject.ProjectId)?.Id == SelectedProject.SessionId) return false;
        ProjectUnavailable = true;
        SelectedProject = null;
        Listing = null;
        SelectedScript = null;
        Files = [];
        return true;
    }
}
