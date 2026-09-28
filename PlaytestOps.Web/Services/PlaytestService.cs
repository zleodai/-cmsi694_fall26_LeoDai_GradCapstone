using Microsoft.EntityFrameworkCore;
using PlaytestOps.Web.Data;
using PlaytestOps.Web.Models;

namespace PlaytestOps.Web.Services;

public interface IPlaytestService
{
    Task<IReadOnlyList<Playtest>> GetTestsAsync(CancellationToken cancellationToken);
}

public sealed class PlaytestService(ApplicationDbContext database) : IPlaytestService
{
    public async Task<IReadOnlyList<Playtest>> GetTestsAsync(CancellationToken cancellationToken)
    {
        var tests = await database.Playtests.AsNoTracking().Include(test => test.Project)
            .Include(test => test.Runs.OrderByDescending(run => run.RequestedAt).Take(5))
            .OrderBy(test => test.Id).ToListAsync(cancellationToken);
        // One live attempt per test means the latest five always include its queued attempt.
        foreach (var project in tests.GroupBy(test => test.ProjectId))
        {
            var position = 0;
            foreach (var run in project.SelectMany(test => test.Runs).Where(run => run.State == "Queued")
                .OrderBy(run => run.RequestedAt).ThenBy(run => run.Id)) run.QueuePosition = ++position;
        }
        return tests;
    }
}
