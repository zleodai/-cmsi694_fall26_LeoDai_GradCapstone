using Microsoft.EntityFrameworkCore;
using PlaytestOps.Web.Data;
using PlaytestOps.Web.Models;
using System.Net.WebSockets;

namespace PlaytestOps.Web.Bridge;

public sealed record RunUpdate(string Type, string RunId, int Sequence, string State, string? Outcome,
    double DurationSeconds, string? Message, string? StackTrace, string? Output);
public sealed record RunRequestResult(string? RunId, string? Error);

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
            var session = registry.ConnectedProject(test.ProjectId);
            if (session?.Socket is not { } socket) return new(null, "Connect the project's Unity Editor before running a test.");
            if (await db.PlaytestRuns.AnyAsync(x => x.Playtest.ProjectId == test.ProjectId && (x.State == "Pending" || x.State == "Running"), ct))
                return new(null, "This project's Editor already has a requested or running test. Wait for it to finish.");
            var run = new PlaytestRun { Playtest = test, SessionId = session.Id };
            db.PlaytestRuns.Add(run);
            await db.SaveChangesAsync(ct);
            monitor.Wake();
            // Persist before sending. Never resend an execution command after an uncertain delivery.
            using var delivery = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await registry.SendAsync(session, socket, new { type = "run.request", runId = run.Id,
                    mode = test.Mode, uniqueName = test.UniqueName }, delivery.Token);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                Fail(run, "DeliveryUnknown", "Could not confirm delivery to Unity. Check the Editor before trying again.");
                await db.SaveChangesAsync(CancellationToken.None);
                return new(run.Id, run.Message);
            }
            return new(run.Id, null);
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
        return null;
    }

    internal static void Fail(PlaytestRun run, string outcome, string message)
    {
        run.State = "Failed"; run.Outcome = outcome; run.Message = message; run.FinishedAt = DateTime.UtcNow;
        run.Playtest.Status = TestStatus.Failed;
    }
}
