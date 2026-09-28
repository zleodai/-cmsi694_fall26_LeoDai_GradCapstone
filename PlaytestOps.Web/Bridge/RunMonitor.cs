using System.Net.WebSockets;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using PlaytestOps.Web.Data;
using PlaytestOps.Web.Models;

namespace PlaytestOps.Web.Bridge;

// No SQLite polling when there are no queued or active runs.
public sealed class RunMonitor(IServiceScopeFactory scopes, BridgeRegistry registry, ILogger<RunMonitor> logger) : BackgroundService
{
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(1);
    private readonly Dictionary<string, DateTime> disconnected = new();
    public void Wake() => wake.Writer.TryWrite(true);

    public override async Task StartAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var abandoned = await db.PlaytestRuns.Include(x => x.Playtest)
            .Where(x => x.State == "Queued" || x.State == "Pending" || x.State == "Running").ToListAsync(ct);
        foreach (var run in abandoned) RunService.Fail(run, "Interrupted", run.State == "Queued"
            ? "PlaytestOps restarted. This queued test was not dispatched; pair Unity and queue it again."
            : "PlaytestOps restarted before receiving a final result. Check Unity before running again.");
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
                    catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Could not process test queue."); }
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
            var runs = await db.PlaytestRuns.Include(x => x.Playtest)
                .Where(x => x.State == "Queued" || x.State == "Pending" || x.State == "Running")
                .OrderBy(x => x.RequestedAt).ThenBy(x => x.Id).ToListAsync(ct);
            var now = DateTime.UtcNow;
            foreach (var run in runs)
            {
                if (registry.IsConnected(run.SessionId)) disconnected.Remove(run.Id);
                else disconnected.TryAdd(run.Id, now);
                if (registry.ProjectSession(run.Playtest.ProjectId!)?.Id != run.SessionId)
                    RunService.Fail(run, "Interrupted", "The paired Editor session ended or was replaced. Queue again after connecting Unity.");
                else if (disconnected.TryGetValue(run.Id, out var since) && now - since > TimeSpan.FromMinutes(2))
                    RunService.Fail(run, "Interrupted", run.State == "Queued" ? "The Editor was disconnected for two minutes. This queued test was not dispatched."
                        : "The Editor was disconnected for two minutes. Execution may still be active in Unity; no final result was received.");
                else if (run.State == "Pending" && now - (run.DispatchedAt ?? run.RequestedAt) > TimeSpan.FromMinutes(2))
                    RunService.Fail(run, "TimedOut", "Unity did not acknowledge the request within two minutes. Check the Editor before retrying.");
                else if (run.State != "Queued" && now - (run.DispatchedAt ?? run.RequestedAt) > TimeSpan.FromMinutes(30))
                    RunService.Fail(run, "TimedOut", "No final result arrived within 30 minutes. This does not stop Unity; check the Editor before retrying.");
            }
            await db.SaveChangesAsync(ct);
            foreach (var project in runs.GroupBy(x => x.Playtest.ProjectId))
            {
                if (project.Any(x => x.State is "Pending" or "Running")) continue;
                var next = project.FirstOrDefault(x => x.State == "Queued");
                if (next is null)
                {
                    var idleSession = registry.ProjectSession(project.Key!);
                    if (idleSession is not null) { idleSession.RunProbeId = null; idleSession.RunReady = false; }
                    continue;
                }
                var session = registry.ConnectedProject(project.Key!);
                if (session?.Socket is not { } socket || session.Id != next.SessionId) continue;
                if (!next.Playtest.IsAvailable || next.Playtest.RunState is not ("Runnable" or "Explicit"))
                {
                    session.RunProbeId = null; session.RunReady = false;
                    RunService.Fail(next, "Unavailable", "This queued test is no longer available to run. Refresh discovery in Unity.");
                    await db.SaveChangesAsync(ct);
                    continue;
                }
                using var delivery = CancellationTokenSource.CreateLinkedTokenSource(ct);
                delivery.CancelAfter(TimeSpan.FromSeconds(10));
                if (!session.RunReady)
                {
                    if (session.RunProbeId is not null) continue;
                    session.RunProbeId = Guid.NewGuid().ToString("N");
                    try { await registry.SendAsync(session, socket, new { type = "run.probe", requestId = session.RunProbeId }, delivery.Token); }
                    catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
                    { session.RunProbeId = null; socket.Abort(); }
                    continue;
                }
                // Consume a readiness reply once. Reconnecting invalidates it; each new run requires a fresh probe.
                session.RunReady = false;
                session.RunProbeId = null;
                next.State = "Pending";
                next.DispatchedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                try
                {
                    await registry.SendAsync(session, socket, new { type = "run.request", runId = next.Id,
                        mode = next.Playtest.Mode, uniqueName = next.Playtest.UniqueName }, delivery.Token);
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
                {
                    // Never resend an execution command whose delivery is uncertain.
                    RunService.Fail(next, "DeliveryUnknown", "Could not confirm delivery to Unity. Check the Editor before trying again.");
                    await db.SaveChangesAsync(CancellationToken.None);
                    socket.Abort();
                }
            }
            foreach (var key in disconnected.Keys.Where(key => !runs.Any(x => x.Id == key && x.State is "Queued" or "Pending" or "Running")).ToArray()) disconnected.Remove(key);
            return runs.Any(x => x.State is "Queued" or "Pending" or "Running");
        }
        finally { registry.CatalogGate.Release(); }
    }
}
