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
    public async Task<IReadOnlyList<Playtest>> GetTestsAsync(CancellationToken cancellationToken) =>
        await database.Playtests.AsNoTracking().OrderBy(test => test.Id)
            .ToListAsync(cancellationToken);
}
