using Microsoft.EntityFrameworkCore;
using PlaytestOps.Web.Data;
using PlaytestOps.Web.Models;

namespace PlaytestOps.Web.Bridge;

public sealed record RunUpdate(string Type, string RunId, int Sequence, string State, string? Outcome,
    double DurationSeconds, string? Message, string? StackTrace, string? Output);
public sealed record RunRequestResult(string? RunId, string? Error, int? QueuePosition = null);

public sealed class RunService(ApplicationDbContext db, BridgeRegistry registry, RunMonitor monitor)
{
    public async Task<RunRequestResult> RequestAsync(int testId, CancellationToken ct)
    {
        await registry.CatalogGate.WaitAsync(ct);
        try
        {
            var test = await db.Playtests.FindAsync([testId], ct);
            if (test is null) return new(null, "Test not found.");
            if (!test.IsAvailable || test.ProjectId is null || test.RunState is not ("Runnable" or "Explicit"))
                return new(null, "This test is unavailable or cannot run. Refresh discovery in Unity.");
            var session = registry.ProjectSession(test.ProjectId);
            if (session is null) return new(null, "Connect the project's Unity Editor before queueing a test.");
            var existing = await db.PlaytestRuns.FirstOrDefaultAsync(x => x.PlaytestId == testId &&
                (x.State == "Queued" || x.State == "Pending" || x.State == "Running"), ct);
            if (existing is not null) return new(existing.Id, "This test is already queued or running.");
            var queued = await db.PlaytestRuns.CountAsync(x => x.Playtest.ProjectId == test.ProjectId && x.State == "Queued", ct);
            if (queued >= 100) return new(null, "This project's queue is full (100 waiting tests). Wait for a test to finish.");
            var run = new PlaytestRun { Playtest = test, SessionId = session.Id };
            db.PlaytestRuns.Add(run);
            await db.SaveChangesAsync(ct);
            monitor.Wake();
            return new(run.Id, null, queued + 1);
        }
        finally { registry.CatalogGate.Release(); }
    }

    // Caller holds CatalogGate, shared with requests and catalog writes.
    public async Task<string?> UpdateAsync(EditorSession session, RunUpdate update, CancellationToken ct)
    {
        if (!Guid.TryParseExact(update.RunId, "N", out _) || update.Sequence < 1 ||
            update.State is not ("Running" or "Passed" or "Failed") || !double.IsFinite(update.DurationSeconds) || update.DurationSeconds < 0 ||
            (update.Outcome?.Length ?? 0) > 128 || (update.Message?.Length ?? 0) > 16000 ||
            (update.StackTrace?.Length ?? 0) > 32000 || (update.Output?.Length ?? 0) > 64000)
            return "Invalid or oversized run update.";
        if (update.State == "Passed" && update.Outcome != "Passed") return "Passed requires a Passed Unity outcome.";
        var run = await db.PlaytestRuns.Include(x => x.Playtest).SingleOrDefaultAsync(x => x.Id == update.RunId, ct);
        if (run is null || run.SessionId != session.Id || run.Playtest.ProjectId != session.ProjectId)
            return "Run does not belong to this Editor session.";
        if (run.State == "Queued") return "Run has not been dispatched to Unity.";
        // Retried, out-of-order, or late callbacks cannot overwrite a final result or a newer attempt.
        if (run.State is "Passed" or "Failed" || update.Sequence <= run.Sequence) return null;
        run.Sequence = update.Sequence;
        run.State = update.State;
        if (update.State == "Running" || update.Outcome is not ("Rejected" or "Interrupted")) run.StartedAt ??= DateTime.UtcNow;
        run.Outcome = update.Outcome ?? "";
        run.DurationSeconds = update.DurationSeconds;
        run.Message = update.Message ?? "";
        run.StackTrace = update.StackTrace ?? "";
        run.Output = update.Output ?? "";
        run.Playtest.Status = update.State switch { "Passed" => TestStatus.Passed, "Failed" => TestStatus.Failed, _ => TestStatus.Running };
        if (update.State != "Running") run.FinishedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        monitor.Wake();
        return null;
    }

    internal static void Fail(PlaytestRun run, string outcome, string message)
    {
        run.State = "Failed"; run.Outcome = outcome; run.Message = message; run.FinishedAt = DateTime.UtcNow;
        run.Playtest.Status = TestStatus.Failed;
    }
}
