using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PlaytestOps.Web.Data;
using PlaytestOps.Web.Models;

namespace PlaytestOps.Web.Bridge;

public sealed record RunUpdate(string Type, string RunId, int Sequence, string State, string? Outcome,
    double DurationSeconds, string? Message, string? StackTrace, string? Output,
    RunLogEntry[]? Logs = null, bool LogsTruncated = false, int DroppedLogCount = 0);
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
        var logFailure = ValidateLogs(run, update);
        if (logFailure is not null) return logFailure;
        // Missing logs mean a legacy client; do not erase a snapshot already received.
        if (update.Logs is not null)
        {
            run.LogsJson = JsonSerializer.Serialize(update.Logs, PlaytestRun.LogJsonOptions);
            run.LogsTruncated = update.LogsTruncated;
            run.DroppedLogCount = update.DroppedLogCount;
        }
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

    private static string? ValidateLogs(PlaytestRun run, RunUpdate update)
    {
        if (update.Logs is null)
            return update.LogsTruncated || update.DroppedLogCount != 0 ? "Log capture metadata requires a log snapshot." : null;
        if (update.Logs.Length > 1000 || update.DroppedLogCount < 0 ||
            (update.DroppedLogCount > 0 && !update.LogsTruncated))
            return "Invalid or oversized log snapshot.";
        var totalCharacters = 0;
        for (var index = 0; index < update.Logs.Length; index++)
        {
            var entry = update.Logs[index];
            if (entry is null || entry.Sequence != index + 1 ||
                entry.Level is not ("Debug" or "Warning" or "Error" or "Assert" or "Exception") ||
                entry.Message is null || entry.StackTrace is null || entry.Message.Length > 16000 || entry.StackTrace.Length > 16000 ||
                entry.TimestampUtc is null || entry.TimestampUtc.Length > 64 ||
                !DateTimeOffset.TryParseExact(entry.TimestampUtc, TimestampFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var timestamp) || timestamp.Offset != TimeSpan.Zero)
                return "Invalid or oversized log entry.";
            totalCharacters += entry.Message.Length + entry.StackTrace.Length;
            if (totalCharacters > 192000) return "Invalid or oversized log snapshot.";
        }
        var previous = run.Logs;
        if (update.Logs.Length < previous.Count || (run.LogsTruncated && !update.LogsTruncated) ||
            update.DroppedLogCount < run.DroppedLogCount)
            return "A log snapshot cannot remove previous capture data.";
        for (var index = 0; index < previous.Count; index++)
            if (previous[index] != update.Logs[index]) return "A log snapshot cannot change previous entries.";
        return null;
    }

    private static readonly string[] TimestampFormats = [
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd'T'HH:mm:sszzz"
    ];

    internal static void Fail(PlaytestRun run, string outcome, string message)
    {
        run.State = "Failed"; run.Outcome = outcome; run.Message = message; run.FinishedAt = DateTime.UtcNow;
        run.Playtest.Status = TestStatus.Failed;
    }
}
