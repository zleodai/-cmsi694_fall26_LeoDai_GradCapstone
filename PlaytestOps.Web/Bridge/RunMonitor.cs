using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using PlaytestOps.Web.Data;

namespace PlaytestOps.Web.Bridge;

// Sleeps without querying SQLite when there are no active runs.
public sealed class RunMonitor(IServiceScopeFactory scopes, BridgeRegistry registry, ILogger<RunMonitor> logger) : BackgroundService
{
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(1);
    private readonly Dictionary<string, DateTime> disconnected = new();
    public void Wake() => wake.Writer.TryWrite(true);

    public override async Task StartAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var abandoned = await db.PlaytestRuns.Include(x => x.Playtest).Where(x => x.State == "Pending" || x.State == "Running").ToListAsync(ct);
        foreach (var run in abandoned) RunService.Fail(run, "Interrupted", "PlaytestOps restarted before receiving a final result. Check Unity before running again.");
        await db.SaveChangesAsync(ct);
        await base.StartAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            while (await wake.Reader.WaitToReadAsync(ct))
            {
                while (wake.Reader.TryRead(out _)) { }
                var active = true;
                while (active)
                {
                    try { active = await CheckAsync(ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Could not check active runs."); }
                    if (active) await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task<bool> CheckAsync(CancellationToken ct)
    {
        await registry.CatalogGate.WaitAsync(ct);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var runs = await db.PlaytestRuns.Include(x => x.Playtest).Where(x => x.State == "Pending" || x.State == "Running").ToListAsync(ct);
            var now = DateTime.UtcNow;
            foreach (var run in runs)
            {
                if (registry.IsConnected(run.SessionId)) disconnected.Remove(run.Id);
                else disconnected.TryAdd(run.Id, now);
                if (run.State == "Pending" && now - run.RequestedAt > TimeSpan.FromMinutes(2))
                    RunService.Fail(run, "TimedOut", "Unity did not acknowledge the request within two minutes. Check the Editor before retrying.");
                else if (disconnected.TryGetValue(run.Id, out var since) && now - since > TimeSpan.FromMinutes(2))
                    RunService.Fail(run, "Interrupted", "The Editor was disconnected for two minutes. Execution may still be active in Unity; no final result was received.");
                else if (now - run.RequestedAt > TimeSpan.FromMinutes(30))
                    RunService.Fail(run, "TimedOut", "No final result arrived within 30 minutes. This does not stop Unity; check the Editor before retrying.");
            }
            await db.SaveChangesAsync(ct);
            foreach (var key in disconnected.Keys.Where(key => !runs.Any(x => x.Id == key && (x.State == "Pending" || x.State == "Running"))).ToArray()) disconnected.Remove(key);
            return runs.Any(x => x.State is "Pending" or "Running");
        }
        finally { registry.CatalogGate.Release(); }
    }
}
