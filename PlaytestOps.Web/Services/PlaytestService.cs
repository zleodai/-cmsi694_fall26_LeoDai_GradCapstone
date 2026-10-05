using Microsoft.EntityFrameworkCore;
using PlaytestOps.Web.Data;
using PlaytestOps.Web.Models;

namespace PlaytestOps.Web.Services;

public interface IPlaytestService
{
    Task<IReadOnlyList<Playtest>> GetTestsAsync(IReadOnlySet<string> connectedProjects, CancellationToken cancellationToken);
    Task<bool> HasActiveRunsAsync(IReadOnlySet<string> pairedProjects, CancellationToken cancellationToken);
}

public sealed class PlaytestService(ApplicationDbContext database) : IPlaytestService
{
    public async Task<IReadOnlyList<Playtest>> GetTestsAsync(IReadOnlySet<string> connectedProjects, CancellationToken cancellationToken)
    {
        if (connectedProjects.Count == 0) return [];
        var projectIds = connectedProjects.ToArray();
        // The dashboard needs attempt summaries; full structured logs are loaded only on /Runs/{id}.
        // An explicit projection avoids reading up to five large LogsJson snapshots for every test
        // on each active dashboard refresh.
        var tests = await database.Playtests.AsNoTracking()
            .Where(test => test.ProjectId != null && projectIds.Contains(test.ProjectId))
            .OrderBy(test => test.Id)
            .Select(test => new Playtest
            {
                Id = test.Id,
                Name = test.Name,
                Description = test.Description,
                Status = test.Status,
                ProjectId = test.ProjectId,
                Project = test.Project == null ? null : new UnityProject
                {
                    Id = test.Project.Id,
                    Name = test.Project.Name,
                    UnityVersion = test.Project.UnityVersion,
                    LastSyncedAt = test.Project.LastSyncedAt
                },
                UniqueName = test.UniqueName,
                FullName = test.FullName,
                Assembly = test.Assembly,
                Mode = test.Mode,
                RunState = test.RunState,
                SkipReason = test.SkipReason,
                IsAvailable = test.IsAvailable,
                Runs = test.Runs.OrderByDescending(run => run.RequestedAt).Take(5)
                    .Select(run => new PlaytestRun
                    {
                        Id = run.Id,
                        PlaytestId = run.PlaytestId,
                        SessionId = run.SessionId,
                        State = run.State,
                        RequestedAt = run.RequestedAt,
                        DispatchedAt = run.DispatchedAt,
                        StartedAt = run.StartedAt,
                        FinishedAt = run.FinishedAt,
                        DurationSeconds = run.DurationSeconds,
                        Outcome = run.Outcome,
                        Message = run.Message,
                        StackTrace = run.StackTrace,
                        Output = run.Output,
                        Sequence = run.Sequence
                    }).ToList()
            }).ToListAsync(cancellationToken);
        // One live attempt per test means the latest five always include its queued attempt.
        foreach (var project in tests.GroupBy(test => test.ProjectId))
        {
            var position = 0;
            foreach (var run in project.SelectMany(test => test.Runs).Where(run => run.State == "Queued")
                .OrderBy(run => run.RequestedAt).ThenBy(run => run.Id)) run.QueuePosition = ++position;
        }
        return tests;
    }

    public Task<bool> HasActiveRunsAsync(IReadOnlySet<string> pairedProjects, CancellationToken cancellationToken)
    {
        if (pairedProjects.Count == 0) return Task.FromResult(false);
        var projectIds = pairedProjects.ToArray();
        // Keep active-work refresh alive during short socket gaps (e.g. domain reload),
        // even though disconnected projects' test rows are hidden from the view.
        return database.PlaytestRuns.AsNoTracking().AnyAsync(run =>
            run.Playtest.ProjectId != null && projectIds.Contains(run.Playtest.ProjectId) &&
            (run.State == "Queued" || run.State == "Pending" || run.State == "Running"), cancellationToken);
    }
}
