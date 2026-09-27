using Microsoft.EntityFrameworkCore;
using PlaytestOps.Web.Data;
using PlaytestOps.Web.Models;

namespace PlaytestOps.Web.Bridge;

public sealed record CatalogTest(string UniqueName, string FullName, string Name, string Description, string Assembly, string Mode, string RunState, string SkipReason);
public sealed record CatalogMessage(string Type, string RequestId, CatalogTest[] Tests);

public sealed class CatalogService(ApplicationDbContext db)
{
    public static string? Validate(CatalogMessage message)
    {
        if (message.Type != "catalog.replace") return "Unsupported message type.";
        if (string.IsNullOrWhiteSpace(message.RequestId) || message.RequestId.Length > 64) return "requestId is required (max 64 characters).";
        if (message.Tests is null || message.Tests.Length > 5000) return "tests must be an array of at most 5000 cases.";
        var keys = new HashSet<(string, string)>();
        foreach (var test in message.Tests)
        {
            if (test is null || !Valid(test.UniqueName, 4096) || !Valid(test.FullName, 4096) || !Valid(test.Name, 160) ||
                !Valid(test.Assembly, 256) || test.Description is null || test.Description.Length > 2000 ||
                test.SkipReason is null || test.SkipReason.Length > 2000 || test.Mode is not ("EditMode" or "PlayMode") ||
                test.RunState is not ("Runnable" or "Explicit" or "Skipped" or "Ignored" or "NotRunnable"))
                return "A test has missing, invalid, or oversized metadata.";
            if (!keys.Add((test.Mode, test.UniqueName))) return "Duplicate test identity in catalog.";
        }
        return null;
    }
    private static bool Valid(string? text, int length) => !string.IsNullOrWhiteSpace(text) && text.Length <= length;

    public async Task<int> ReplaceAsync(EditorSession session, CatalogMessage message, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var project = await db.UnityProjects.FindAsync([session.ProjectId], ct);
        if (project is null) { project = new UnityProject { Id = session.ProjectId }; db.UnityProjects.Add(project); }
        project.Name = session.ProjectName;
        project.UnityVersion = session.UnityVersion;
        project.LastSyncedAt = DateTimeOffset.UtcNow;
        var saved = await db.Playtests.Where(x => x.ProjectId == session.ProjectId).ToListAsync(ct);
        var byKey = saved.ToDictionary(x => (x.Mode!, x.UniqueName!));
        foreach (var row in saved) row.IsAvailable = false;
        foreach (var item in message.Tests)
        {
            if (!byKey.TryGetValue((item.Mode, item.UniqueName), out var row))
            {
                row = new Playtest { ProjectId = session.ProjectId, Mode = item.Mode, UniqueName = item.UniqueName };
                db.Playtests.Add(row);
            }
            row.Name = item.Name;
            row.Description = item.Description;
            row.FullName = item.FullName;
            row.Assembly = item.Assembly;
            row.RunState = item.RunState;
            row.SkipReason = item.SkipReason;
            row.IsAvailable = true;
            // Discovery never resets a saved test outcome.
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return saved.Count(x => !x.IsAvailable);
    }
}
