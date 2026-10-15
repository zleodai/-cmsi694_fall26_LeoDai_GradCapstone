using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using PlaytestOps.Web.Data;
using PlaytestOps.Web.Models;

namespace PlaytestOps.Web.Bridge;

public sealed record BuildLogUpdate(int Sequence, string TimestampUtc, string Level, string Message, string StackTrace, bool Truncated = false);
public sealed record BuildUpdate(string BuildId, string BuildToken, int Sequence, string State, string Phase, string Message,
    string OutputPath, string ExecutablePath, string ManifestPath, string ProfilePath, string ActiveTarget, string UnityVersion,
    double DurationSeconds, long TotalBytes, int TotalWarnings, int TotalErrors, bool LogsTruncated, int DroppedLogCount, BuildLogUpdate[] Logs);
public sealed record BuildRequestResult(string? BuildId, string? Error);
public sealed record BuildAcknowledgement(string Type, string BuildId, int Sequence, int LogSequence, string Error = "", string State = "");

public sealed class BuildExecutionService(IServiceScopeFactory scopes, BridgeRegistry registry, RunMonitor runs) : BackgroundService
{
    public const int MaximumLogs = 10000;
    public const int MaximumLogCharacters = 8 * 1024 * 1024;
    public const int MaximumBatchCharacters = 128000;
    private readonly object gate = new();
    private readonly Dictionary<string, Form> forms = new();
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(1);
    private void Wake() => wake.Writer.TryWrite(true);
    private sealed record Form(string ProjectId, string SessionId, string Revision, DateTime ExpiresAt);
    public static bool HoldsProject(string state) => state is "Pending" or "Running" or "Unknown";
    public static bool Polls(string state) => state is "Pending" or "Running";

    public string IssueRequestKey(string projectId, string sessionId, string revision)
    {
        lock (gate)
        {
            foreach (var expiredKey in forms.Where(item => item.Value.ExpiresAt <= DateTime.UtcNow).Select(item => item.Key).ToArray()) forms.Remove(expiredKey);
            if (forms.Count >= 100) forms.Remove(forms.Keys.First());
            var key = Guid.NewGuid().ToString("N");
            forms[key] = new(projectId, sessionId, revision, DateTime.UtcNow.AddMinutes(5));
            return key;
        }
    }

    public async Task<BuildRequestResult> RequestAsync(string projectId, string sessionId, string revision, string requestKey, CancellationToken ct)
    {
        if (!Guid.TryParseExact(projectId, "N", out _) || !Guid.TryParseExact(sessionId, "N", out _) || !Guid.TryParseExact(requestKey, "N", out _) || !BuildConfigurationService.Revision(revision))
            return new(null, "Invalid build request. Refresh before starting a build.");
        await registry.CatalogGate.WaitAsync(ct);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var existing = await db.UnityBuilds.AsNoTracking().SingleOrDefaultAsync(item => item.RequestKey == requestKey, ct);
            if (existing is not null) return existing.ProjectId == projectId && existing.SessionId == sessionId ? new(existing.Id, null) : new(null, "Invalid build request.");
            lock (gate)
            {
                if (!forms.TryGetValue(requestKey, out var form) || form.ExpiresAt <= DateTime.UtcNow || form.ProjectId != projectId || form.SessionId != sessionId || form.Revision != revision)
                    return new(null, "The build form expired or changed. Refresh and review the active configuration.");
            }
            var session = registry.ConnectedProject(projectId);
            if (session?.Socket is not { State: WebSocketState.Open } socket || session.Id != sessionId) return new(null, "The selected Unity Editor disconnected or changed. Refresh.");
            if (await db.UnityBuilds.AnyAsync(item => item.ProjectId == projectId && (item.State == "Pending" || item.State == "Running" || item.State == "Unknown"), ct) ||
                await db.PlaytestRuns.AnyAsync(item => item.Playtest.ProjectId == projectId && (item.State == "Queued" || item.State == "Pending" || item.State == "Running"), ct))
                return new(null, "This project has active/queued tests or an active/unknown build. Finish or resolve that work before building.");
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var build = new UnityBuild { RequestKey = requestKey, ProjectId = projectId, ProjectName = session.ProjectName,
                SessionId = session.Id, TokenHash = Hash(token), Revision = revision, UnityVersion = session.UnityVersion };
            db.UnityBuilds.Add(build);
            await db.SaveChangesAsync(ct); // Persist ownership before an execution command can leave the server.
            Wake();
            session.RunProbeId = null; session.RunReady = false;
            using var delivery = CancellationTokenSource.CreateLinkedTokenSource(ct);
            delivery.CancelAfter(TimeSpan.FromSeconds(10));
            try { await registry.SendAsync(session, socket, new { type = "build.request", buildId = build.Id, buildToken = token, revision }, delivery.Token); }
            catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                build.State = "Unknown"; build.Phase = "Delivery uncertain";
                build.Message = "Delivery could not be confirmed. Unity may still build. No retry or cancellation was sent; reconnect and check the Editor.";
                await db.SaveChangesAsync(CancellationToken.None);
            }
            return new(build.Id, null);
        }
        finally { registry.CatalogGate.Release(); }
    }

    public async Task<BuildAcknowledgement> UpdateAsync(EditorSession session, WebSocket socket, BuildUpdate update, CancellationToken ct)
    {
        var invalid = Validate(update);
        if (invalid is not null) return new("build.accepted", update.BuildId ?? "", 0, 0, invalid);
        await registry.CatalogGate.WaitAsync(ct);
        try
        {
            if (!registry.IsCurrent(session, socket)) return new("build.accepted", update.BuildId, 0, 0, "Editor connection changed.");
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var build = await db.UnityBuilds.SingleOrDefaultAsync(item => item.Id == update.BuildId, ct);
            if (build is null || build.ProjectId != session.ProjectId || !TokenMatches(build.TokenHash, update.BuildToken))
                return new("build.accepted", update.BuildId, 0, 0, "Build does not belong to this Editor.");
            BuildAcknowledgement Ack(string error = "") => new("build.accepted", build.Id, build.Sequence, build.LastLogSequence, error, build.State);
            if (build.State is "Succeeded" or "Failed" or "Rejected" or "Interrupted" || update.Sequence <= build.Sequence) return Ack();
            if (build.OutputPath.Length > 0 && (build.OutputPath != update.OutputPath || build.ExecutablePath != update.ExecutablePath || build.ManifestPath != update.ManifestPath || build.ProfilePath != update.ProfilePath || build.ActiveTarget != update.ActiveTarget))
                return Ack("Build artifact/configuration identity cannot change.");
            var newLogs = update.Logs.Where(log => log.Sequence > build.LastLogSequence).ToArray();
            if (newLogs.Length > 0 && (newLogs[0].Sequence != build.LastLogSequence + 1 || newLogs.Last().Sequence != build.LastLogSequence + newLogs.Length)) return Ack("Log sequence gap.");
            var characters = newLogs.Sum(log => log.Message.Length + log.StackTrace.Length);
            if (build.LastLogSequence + newLogs.Length > MaximumLogs || build.TotalLogCharacters + characters > MaximumLogCharacters ||
                (build.LogsTruncated && !update.LogsTruncated) || update.DroppedLogCount < build.DroppedLogCount) return Ack("Log capture limits or metadata are invalid.");
            foreach (var log in newLogs) db.UnityBuildLogs.Add(new UnityBuildLog { BuildId = build.Id, Sequence = log.Sequence, TimestampUtc = log.TimestampUtc, Level = log.Level, Message = log.Message, StackTrace = log.StackTrace, Truncated = log.Truncated });
            build.LastLogSequence += newLogs.Length; build.TotalLogCharacters += characters;
            build.Sequence = update.Sequence; build.State = update.State; build.Phase = update.Phase; build.Message = update.Message;
            build.OutputPath = update.OutputPath; build.ExecutablePath = update.ExecutablePath; build.ManifestPath = update.ManifestPath;
            build.ProfilePath = update.ProfilePath; build.ActiveTarget = update.ActiveTarget; build.UnityVersion = update.UnityVersion;
            build.DurationSeconds = update.DurationSeconds; build.TotalBytes = update.TotalBytes; build.TotalWarnings = update.TotalWarnings; build.TotalErrors = update.TotalErrors;
            build.LogsTruncated = update.LogsTruncated; build.DroppedLogCount = update.DroppedLogCount; build.LastUpdateAt = DateTime.UtcNow;
            if (build.State is "Succeeded" or "Failed" or "Rejected") build.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            Wake();
            runs.Wake();
            return Ack();
        }
        finally { registry.CatalogGate.Release(); }
    }

    private static string? Validate(BuildUpdate update)
    {
        if (update is null || !Guid.TryParseExact(update.BuildId, "N", out _) || update.BuildToken is not { Length: 64 } || !update.BuildToken.All(Uri.IsHexDigit) || update.Sequence < 1 ||
            update.State is not ("Running" or "Succeeded" or "Failed" or "Rejected" or "Unknown") || !Metadata(update.Phase, 160) || !Metadata(update.Message, 2000) ||
            !Metadata(update.OutputPath, 2048) || !Metadata(update.ExecutablePath, 2048) || !Metadata(update.ManifestPath, 2048) || !Metadata(update.ProfilePath, 1024) || !Metadata(update.UnityVersion, 64) ||
            update.ActiveTarget is not ("StandaloneWindows" or "StandaloneWindows64" or "StandaloneWindowsArm64" or "") || !double.IsFinite(update.DurationSeconds) || update.DurationSeconds < 0 ||
            update.TotalBytes < 0 || update.TotalWarnings < 0 || update.TotalErrors < 0 || update.DroppedLogCount < 0 || (update.DroppedLogCount > 0 && !update.LogsTruncated) ||
            update.Logs is null || update.Logs.Length > 64) return "Invalid build update.";
        if (update.OutputPath.Length != 0 && !ArtifactPaths(update)) return "Invalid build artifact paths.";
        if (update.State == "Succeeded" && (update.OutputPath.Length == 0 || update.ExecutablePath.Length == 0 || update.ManifestPath.Length == 0 || update.TotalBytes == 0 || update.TotalErrors != 0)) return "A successful build requires its immutable artifact identity and zero native BuildReport errors.";
        var total = 0; var previous = -1;
        foreach (var log in update.Logs)
        {
            if (log is null || log.Sequence < 1 || (previous != -1 && log.Sequence != previous + 1) || !VersionControlService.IsUtcTimestamp(log.TimestampUtc) ||
                log.Level is not ("Debug" or "Warning" or "Error" or "Assert" or "Exception") || log.Message is null || log.StackTrace is null ||
                log.Message.Length > 16000 || log.StackTrace.Length > 16000 || log.Message.Contains('\0') || log.StackTrace.Contains('\0')) return "Invalid build log batch.";
            total += log.Message.Length + log.StackTrace.Length; previous = log.Sequence;
        }
        return total > MaximumBatchCharacters ? "Build log batch is oversized." : null;
    }
    private static bool ArtifactPaths(BuildUpdate update)
    {
        var path = update.OutputPath.Replace('\\', '/').TrimEnd('/');
        return path.Length > 0 && !path.Split('/').Any(part => part is "." or "..") && path.EndsWith("/Builds/PlaytestOps/" + update.BuildId, StringComparison.Ordinal) &&
            update.ExecutablePath.Replace('\\', '/') == path + "/Player.exe" && update.ManifestPath.Replace('\\', '/') == path + "/build-manifest.json";
    }
    private static bool Metadata(string? text, int maximum) => text is not null && text.Length <= maximum && !text.Any(char.IsControl);
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static bool TokenMatches(string expected, string token) => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(Hash(token)));

    public async Task<bool> ResolveUnknownAsync(string id, CancellationToken ct)
    {
        await registry.CatalogGate.WaitAsync(ct);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var build = await db.UnityBuilds.SingleOrDefaultAsync(item => item.Id == id, ct);
            if (build?.State != "Unknown") return false;
            build.State = "Interrupted"; build.Phase = "Operator acknowledged unknown result"; build.FinishedAt = DateTime.UtcNow;
            build.Message = "The operator released the unknown-build reservation. No Unity cancellation was sent; existing output is preserved.";
            await db.SaveChangesAsync(ct); runs.Wake(); return true;
        }
        finally { registry.CatalogGate.Release(); }
    }

    public override async Task StartAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var abandoned = await db.UnityBuilds.Where(item => item.State == "Pending" || item.State == "Running").ToArrayAsync(ct);
        foreach (var build in abandoned) { build.State = "Unknown"; build.Phase = "Dashboard restarted"; build.Message = "Unity may still be building. Re-pair the same project to reconcile its saved result; no execution command was replayed."; }
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
                await registry.CatalogGate.WaitAsync(ct);
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var stale = await db.UnityBuilds.Where(item => (item.State == "Pending" || item.State == "Running") && item.LastUpdateAt < DateTime.UtcNow.AddMinutes(-2)).ToArrayAsync(ct);
                    foreach (var build in stale) { build.State = "Unknown"; build.Phase = "Reporting interrupted"; build.Message = "No build update arrived for two minutes. Unity may still be building; no cancellation or replay was sent."; }
                    if (stale.Length > 0) await db.SaveChangesAsync(ct);
                    active = await db.UnityBuilds.AnyAsync(item => item.State == "Pending" || item.State == "Running", ct);
                }
                finally { registry.CatalogGate.Release(); }
                if (active) await Task.Delay(TimeSpan.FromSeconds(5), ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
