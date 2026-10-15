using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PlaytestOps.Web.Bridge;
using PlaytestOps.Web.Data;
using PlaytestOps.Web.Models;

namespace PlaytestOps.Web.Pages;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class BuildDetailsModel(ApplicationDbContext db, BuildExecutionService builds) : PageModel
{
    public UnityBuild Build { get; private set; } = null!;
    public IReadOnlyList<UnityBuildLog> Logs { get; private set; } = [];
    public bool HasMore => (Logs.LastOrDefault()?.Sequence ?? 0) < Build.LastLogSequence;
    public async Task<IActionResult> OnGetAsync(string id, CancellationToken cancellationToken)
    {
        if (!BridgeRegistry.IsLocalOperator(HttpContext)) return StatusCode(403);
        if (!Guid.TryParseExact(id, "N", out _)) return NotFound();
        var build = await db.UnityBuilds.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (build is null) return NotFound();
        Build = build; Logs = await db.UnityBuildLogs.AsNoTracking().Where(item => item.BuildId == id).OrderBy(item => item.Sequence).Take(200).ToArrayAsync(cancellationToken);
        return Page();
    }
    public async Task<IActionResult> OnGetStatusAsync(string id, int after, CancellationToken cancellationToken)
    {
        if (!BridgeRegistry.IsLocalOperator(HttpContext)) return StatusCode(403);
        if (!Guid.TryParseExact(id, "N", out _) || !ModelState.IsValid || after < 0 || after > BuildExecutionService.MaximumLogs) return BadRequest();
        var build = await db.UnityBuilds.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (build is null) return NotFound();
        var logs = await db.UnityBuildLogs.AsNoTracking().Where(item => item.BuildId == id && item.Sequence > after).OrderBy(item => item.Sequence).Take(200).ToArrayAsync(cancellationToken);
        return new JsonResult(new { build, logs, active = BuildExecutionService.Polls(build.State), hasMore = (logs.LastOrDefault()?.Sequence ?? after) < build.LastLogSequence });
    }
    public async Task<IActionResult> OnGetLogFileAsync(string id, CancellationToken cancellationToken)
    {
        if (!BridgeRegistry.IsLocalOperator(HttpContext)) return StatusCode(403);
        if (!Guid.TryParseExact(id, "N", out _)) return NotFound();
        var build = await db.UnityBuilds.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (build is null) return NotFound();
        var text = new StringBuilder($"PlaytestOps build {id}\nState: {build.State}; phase: {build.Phase}\nCapture truncated: {build.LogsTruncated}; dropped entries/parts: {build.DroppedLogCount}\n\n");
        await foreach (var log in db.UnityBuildLogs.AsNoTracking().Where(item => item.BuildId == id).OrderBy(item => item.Sequence).AsAsyncEnumerable().WithCancellation(cancellationToken))
            text.Append('[').Append(log.Sequence).Append("] ").Append(log.TimestampUtc).Append(' ').Append(log.Level).Append(log.Truncated ? " [TRUNCATED]" : "").AppendLine().AppendLine(log.Message).AppendLine(log.StackTrace).AppendLine();
        return File(Encoding.UTF8.GetBytes(text.ToString()), "text/plain; charset=utf-8", "PlaytestOps-build-" + id + ".log");
    }
    public async Task<IActionResult> OnPostResolveUnknownAsync(string id, CancellationToken cancellationToken)
    {
        if (!BridgeRegistry.IsLocalOperator(HttpContext)) return StatusCode(403);
        if (!Guid.TryParseExact(id, "N", out _)) return BadRequest();
        return await builds.ResolveUnknownAsync(id, cancellationToken) ? RedirectToPage("/BuildDetails", new { id }) : StatusCode(409);
    }
}
