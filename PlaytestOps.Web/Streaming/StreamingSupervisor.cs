using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PlaytestOps.Web.Streaming;

// Each child has a process identity held by its direct or Sandboxie launch provider.
public sealed class StreamingSupervisor(StreamingArtifactValidator artifacts, IWebHostEnvironment environment,
    ISteamStreamingLauncher steam) : BackgroundService
{
    private readonly object gate = new();
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private Group? group;
    private Session? current => group?.Sessions.FirstOrDefault();
    private Session? pendingController;
    private bool shuttingDown;
    private sealed class Group
    {
        public required string Id, Profile;
        public required StreamingArtifact Artifact;
        public required int Port;
        public string? SteamLobbyId, HostSteamId;
        public KcpLatencySettings LatencySettings = new();
        public long LatencyRevision;
        public List<Session> Sessions = [];
        public CancellationTokenSource StartupCancellation = new();
        public Task Startup = Task.CompletedTask, Completion = Task.CompletedTask;
        public bool Stopping;
    }
    private sealed class Session
    {
        public required string Id, InstanceId, BuildId, Origin, Token, ProjectName;
        public IStreamingOwnedProcess? Child;
        public KcpLatencyRelay? LatencyRelay;
        public Task Launch = Task.CompletedTask;
        public string? SandboxName, SteamId;
        public required Group Group;
        public required string Role, StoragePath;
        public required int Index;
        public StreamingAudit? Audit;
        public required int Width, Height;
        public bool Diagnostics, Started, Exited, Ready, Stopping;
        public string State = "Starting", Message = "Waiting for the owned player runtime.";
        public DateTimeOffset StartedAt = DateTimeOffset.UtcNow;
        public DateTimeOffset? LastHeartbeat;
        public int? ExitCode;
        public StreamingPeer? Runtime, Viewer;
        public string? GrantHash;
        public DateTimeOffset GrantExpires, ViewerLastSeen;
        public string? LeaseId, LeaseToken, ControlRequestId;
        public bool LeaseReady;
        public DateTimeOffset LeaseExpires;
        public string? ReleasingLeaseId;
        public DateTimeOffset ReleaseDeadline;
        public bool GameReady, MouseLockRequested;
        public TaskCompletionSource Readiness = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Queue<StreamingLog> Logs = new();
        public long LogSequence;
        public Task Completion = Task.CompletedTask;
    }

    public StreamingState? Snapshot() { lock (gate) return current is null ? null : View(current); }
    public IReadOnlyList<StreamingState> Snapshots() { lock (gate) return group?.Sessions.Select(View).ToArray() ?? []; }
    public StreamingLatencyState? LatencySnapshot() { lock (gate) return LatencyView(group); }

    public async Task<StreamingLatencyResult> ConfigureLatencyAsync(string groupId, KcpLatencySettings settings, CancellationToken ct)
    {
        try { KcpLatencySettings.Validate(settings); }
        catch (ArgumentException) { return new(null, "Use whole milliseconds from 0 to 1000, with minimum no greater than maximum.", 400); }
        await lifecycle.WaitAsync(ct);
        Group? target = null;
        try
        {
            lock (gate)
            {
                if (shuttingDown || group is not { } active || active.Id != groupId)
                    return new(null, "This playtest group is no longer active. Refresh the session.", 409);
                if (LatencyView(active) is null)
                    return new(null, "Latency controls require a ready local KCP multiplayer group.", 409);
                target = active;
                foreach (var session in active.Sessions.Skip(1)) session.LatencyRelay!.Configure(settings);
                active.LatencySettings = settings;
                active.LatencyRevision++;
                Log(active.Sessions[0], "Info", settings.Enabled
                    ? $"KCP network latency enabled: {settings.MinRttMs}–{settings.MaxRttMs} ms added RTT on each remote-client link."
                    : "KCP network latency disabled; pending datagrams flushed.");
                var applied = LatencyView(active) ?? throw new InvalidOperationException("The KCP relay stopped during configuration.");
                return new(applied, null);
            }
        }
        catch (InvalidOperationException)
        {
            if (target is not null) await StopChildrenAsync(target, "The owned KCP relays could not apply the requested network settings.");
            return new(null, "The KCP network link stopped while applying latency. The owned group is being stopped.", 409);
        }
        finally { lifecycle.Release(); }
    }

    private static StreamingLatencyState? LatencyView(Group? active)
    {
        if (active is null || active.Profile != "arena-kcp" || active.Stopping ||
            !active.Startup.IsCompletedSuccessfully || active.Sessions.Any(session => !session.Ready || !session.GameReady || session.Exited || session.Stopping))
            return null;
        var links = new List<StreamingLatencyLinkState>();
        foreach (var session in active.Sessions.Skip(1))
        {
            if (session.LatencyRelay is null) return null;
            var relay = session.LatencyRelay.Snapshot;
            if (relay.Stopped) return null;
            var sample = ReadGameRtt(session);
            links.Add(new(session.InstanceId, session.Index, $"Client {session.Index}",
                relay.QueuedPackets, relay.QueuedBytes, relay.DroppedPackets, relay.ForwardedPackets,
                sample.Rtt, sample.Deviation, sample.Utc));
        }
        return new(active.Id, active.LatencySettings.Enabled, active.LatencySettings.MinRttMs,
            active.LatencySettings.MaxRttMs, active.LatencyRevision, links);
    }

    private static (double? Rtt, double? Deviation, DateTimeOffset? Utc) ReadGameRtt(Session session)
    {
        // This optional game-owned observation is independent of WebRTC/input RTT.
        // Older approved arena builds can use the relay but report no game sample.
        try
        {
            var path = Path.Combine(session.StoragePath, "arena-state.json");
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return default;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length is <= 0 or > 262144) return default;
            using var document = JsonDocument.Parse(file, new JsonDocumentOptions { MaxDepth = 8 });
            var value = document.RootElement;
            if (value.GetProperty("groupId").GetString() != session.Group.Id ||
                value.GetProperty("sessionId").GetString() != session.Id ||
                value.GetProperty("instanceId").GetString() != session.InstanceId ||
                !value.GetProperty("hasGameNetworkRtt").GetBoolean() ||
                !value.GetProperty("utc").TryGetDateTimeOffset(out var utc) ||
                DateTimeOffset.UtcNow - utc > TimeSpan.FromSeconds(3) || utc - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(1))
                return default;
            var roundTrip = value.GetProperty("gameNetworkRttMs").GetDouble();
            var deviation = value.GetProperty("gameNetworkRttStdDevMs").GetDouble();
            if (!double.IsFinite(roundTrip) || !double.IsFinite(deviation) || roundTrip < 0 || roundTrip > 20000 || deviation < 0 || deviation > 20000)
                return default;
            return (roundTrip, deviation, utc);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
            InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException) { return default; }
    }
    public bool IsBuildInUse(string buildId)
    {
        lock (gate) return group is { } g && g.Sessions.Any(s => s.BuildId == buildId) && !g.Completion.IsCompletedSuccessfully;
    }

    // Keep validation, file removal and record deletion in the same critical
    // section as player launch. An Exited flag alone precedes pin disposal.
    public async Task<IDisposable?> AcquireBuildDeletionLeaseAsync(string buildId, CancellationToken ct)
    {
        await lifecycle.WaitAsync(ct);
        lock (gate)
        {
            if (shuttingDown || (group is { } g && g.Sessions.Any(s => s.BuildId == buildId) && !g.Completion.IsCompletedSuccessfully))
            { lifecycle.Release(); return null; }
        }
        return new LifecycleLease(lifecycle);
    }

    private sealed class LifecycleLease(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? owner = semaphore;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release();
    }
    private static StreamingState View(Session? s) => s is null
        ? new(null, null, null, null, "Idle", "Select a successful build to start streaming.", 1280, 720, 30, 6000, 0, 0, false, false, false, null, null, null, [], null, false, true)
        : new(s.Id, s.InstanceId, s.BuildId, s.ProjectName, s.State, s.Message, s.Width, s.Height, 30, 6000,
            s.Ready ? s.Width : 0, s.Ready ? s.Height : 0, s.Runtime is not null, s.Viewer is not null, s.LeaseId is not null && s.LeaseReady,
            s.StartedAt, s.LastHeartbeat, s.ExitCode, s.Logs.ToArray(), s.State == "Failed" ? s.Message : null, s.Diagnostics, s.Exited,
            s.Group.Id, s.Index, s.Role, s.Role == "client" ? $"Client {s.Index}" : s.Role == "host" ? "Host" : "Single player",
            s.Group.Profile, s.GameReady, s.MouseLockRequested);

    public async Task<StreamingResult> StartSessionAsync(StreamingStartRequest request, string origin, CancellationToken ct)
    {
        if (!StreamingProtocol.ValidDimensions(request.Width, request.Height))
            return new(null, "Resolution must be even: width 320–1920 and height 180–1080.", 400);
        if (!StreamingProtocol.ValidLaunchProfile(request.LaunchProfile, request.InstanceCount))
            return new(null, "Select single with one player, or a multiplayer profile with two to four players.", 400);
        var sandboxes = request.SteamSandboxes ?? [];
        if (request.LaunchProfile == "arena-steam" ? sandboxes.Length != request.InstanceCount - 1 : sandboxes.Length != 0)
            return new(null, "Select one distinct Steam sandbox for each joining client.", 400);
        await lifecycle.WaitAsync(ct);
        try
        {
            lock (gate)
            {
                if (shuttingDown) return new(null, "The streaming supervisor is shutting down.", 409);
                if (group is { } previous && !previous.Completion.IsCompletedSuccessfully)
                    return new(Snapshot(), "A streaming group is active or still releasing its files. Stop it and wait for cleanup before starting another.", 409);
            }
            StreamingArtifact artifact;
            try { artifact = await artifacts.ValidateAsync(request.BuildId, ct); }
            catch (InvalidDataException ex) { return new(null, ex.Message, 400); }
            if (request.LaunchProfile == "arena-kcp" && !artifact.SupportsArenaKcp)
            { artifact.Dispose(); return new(null, "This approved build does not declare the arena-kcp launch profile. Rebuild with the Arena adapter.", 400); }
            if (request.LaunchProfile == "arena-steam")
            {
                if (!artifact.SupportsArenaSteam)
                { artifact.Dispose(); return new(null, "Rebuild the Steam arena with its streaming adapter before starting this profile.", 400); }
                try { await steam.ValidateEnvironmentsAsync(sandboxes, ct); }
                catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                { artifact.Dispose(); return new(null, exception.Message, 409); }
                catch { artifact.Dispose(); throw; }
            }
            var g = new Group { Id = Guid.NewGuid().ToString("N"), Profile = request.LaunchProfile,
                Artifact = artifact, Port = 0 };
            try
            {
                if (g.Profile == "arena-kcp") g.Port = AllocatePort();
                for (var index = 0; index < request.InstanceCount; index++)
                {
                    var s = new Session { Id = Guid.NewGuid().ToString("N"), InstanceId = Guid.NewGuid().ToString("N"),
                        BuildId = request.BuildId, ProjectName = artifact.ProjectName, Origin = origin, Token = NewToken(),
                        Width = request.Width, Height = request.Height, Diagnostics = request.Diagnostics, Group = g,
                        Role = g.Profile == "single" ? "single" : index == 0 ? "host" : "client", Index = index,
                        SandboxName = request.LaunchProfile == "arena-steam" && index > 0 ? sandboxes[index - 1] : null,
                        StoragePath = "" };
                    g.Sessions.Add(s);
                    if (g.Profile == "arena-kcp" && index > 0) s.LatencyRelay = new KcpLatencyRelay(g.Port);
                    s.StoragePath = Path.Combine(environment.ContentRootPath, "App_Data", "Streaming", g.Id, s.InstanceId);
                    CreateStorage(s.StoragePath);
                    s.Audit = new StreamingAudit(environment.ContentRootPath, s.Id, s.InstanceId, s.BuildId, s.Width, s.Height);
                    Log(s, "Info", $"Reserved group {g.Id}, instance {s.InstanceId}, role {s.Role}, index {index}; private storage {s.StoragePath}.");
                    if (index > 0) { s.State = "Waiting"; s.Message = "Waiting for game-owned host readiness before joining."; }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or SocketException or InvalidOperationException)
            {
                foreach (var s in g.Sessions) { s.LatencyRelay?.Dispose(); s.Child?.Dispose(); s.Audit?.Dispose(); }
                artifact.Dispose(); g.StartupCancellation.Dispose();
                return new(null, "Streaming ownership history could not be created. No player was started.", 409);
            }
            lock (gate)
            {
                group = g; pendingController = null;
                if (g.Profile == "arena-steam") g.Sessions[0].Launch = LaunchSteamChildAsync(g.Sessions[0]);
                else LaunchChild(g.Sessions[0]);
                g.Startup = LaunchRemainingAsync(g);
                g.Completion = CompleteGroupAsync(g);
                var first = g.Sessions[0];
                return first.State == "Failed" ? new(View(first), first.Message, 409) : new(View(first), null);
            }
        }
        finally { lifecycle.Release(); }
    }

    private static int AllocatePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static void CreateStorage(string root)
    {
        for (var path = root; !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Streaming storage must use ordinary local directories.");
        Directory.CreateDirectory(root);
    }

    // Called under gate. Only fixed Unity options and the typed profile enter the child.
    private void LaunchChild(Session s)
    {
        if (s.Group.Stopping) return;
        var artifact = s.Group.Artifact;
        var info = new ProcessStartInfo(artifact.ExecutablePath) { WorkingDirectory = Path.GetDirectoryName(artifact.ExecutablePath)!,
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Normal,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-screen-fullscreen", "0", "-screen-width", s.Width.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-screen-height", s.Height.ToString(System.Globalization.CultureInfo.InvariantCulture), "-logFile", "-" }) info.ArgumentList.Add(argument);
        info.Environment["PLAYTESTOPS_STREAM_CONFIG"] = JsonSerializer.Serialize(new { sessionId = s.Id, instanceId = s.InstanceId,
            origin = s.Origin, token = s.Token, width = s.Width, height = s.Height, frameRate = 30, maxBitrateKbps = 6000, diagnostics = s.Diagnostics });
        // Prevent inherited profile state from changing a legacy single-player launch.
        info.Environment.Remove("PLAYTESTOPS_ARENA_CONFIG");
        if (s.Group.Profile == "arena-kcp")
            info.Environment["PLAYTESTOPS_ARENA_CONFIG"] = JsonSerializer.Serialize(new { protocolVersion = 1,
                groupId = s.Group.Id, sessionId = s.Id, instanceId = s.InstanceId, role = s.Role, index = s.Index,
                playerCount = s.Group.Sessions.Count, address = "127.0.0.1", port = s.LatencyRelay?.LocalPort ?? s.Group.Port, storagePath = s.StoragePath });
        info.Environment.Remove("PLAYTESTOPS_STEAM_CONFIG");
        try
        {
            s.Child = StreamingOwnedProcess.Start(info);
            s.Started = true;
            s.StartedAt = DateTimeOffset.UtcNow; s.State = "Starting"; s.Message = "Waiting for the owned player runtime.";
            Log(s, "Info", "Started the approved player; waiting for runtime readiness.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            s.State = "Failed"; s.Message = "Windows could not start the approved player."; s.Exited = true;
            Log(s, "Error", s.Message); s.Audit?.Dispose(); s.Child?.Dispose(); s.Token = "";
            s.Readiness.TrySetException(new InvalidOperationException(s.Message));
            return;
        }
        s.Completion = ObserveExitAsync(s);
    }

    private async Task LaunchSteamChildAsync(Session s)
    {
        lock (gate)
        {
            if (s.Group.Stopping) return;
            s.Started = true; s.StartedAt = DateTimeOffset.UtcNow;
            s.State = "Starting";
            s.Message = "Starting the approved player in its selected Steam environment.";
        }
        try
        {
            var streamConfig = JsonSerializer.Serialize(new { sessionId = s.Id, instanceId = s.InstanceId,
                origin = s.Origin, token = s.Token, width = s.Width, height = s.Height,
                frameRate = 30, maxBitrateKbps = 6000, diagnostics = s.Diagnostics });
            var steamConfig = JsonSerializer.Serialize(new { protocolVersion = 1, groupId = s.Group.Id,
                sessionId = s.Id, instanceId = s.InstanceId, role = s.Role, index = s.Index,
                playerCount = s.Group.Sessions.Count, appId = 480,
                lobbyId = s.Index == 0 ? "" : s.Group.SteamLobbyId,
                hostSteamId = s.Index == 0 ? "" : s.Group.HostSteamId });
            var arguments = new[] { "-screen-fullscreen", "0", "-screen-width", s.Width.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-screen-height", s.Height.ToString(System.Globalization.CultureInfo.InvariantCulture), "-logFile", "-" };
            var child = await steam.LaunchAsync(s.Group.Artifact.ExecutablePath,
                Path.GetDirectoryName(s.Group.Artifact.ExecutablePath)!, s.StoragePath, s.SandboxName,
                arguments, streamConfig, steamConfig, s.Group.StartupCancellation.Token);
            lock (gate)
            {
                s.Child = child;
                s.Completion = ObserveExitAsync(s);
                Log(s, "Info", "Started the approved Steam player in " + (s.SandboxName ?? "normal Steam") + ".");
            }
        }
        catch (SteamLaunchCleanupException exception)
        {
            lock (gate)
            {
                s.Child = exception.OwnedProcess;
                s.State = "Failed"; s.Message = exception.Message;
                s.Readiness.TrySetException(new InvalidOperationException(s.Message));
                Log(s, "Error", s.Message);
                s.Completion = ObserveExitAsync(s);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException or
            ArgumentException or OperationCanceledException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            lock (gate)
            {
                s.Exited = true; s.State = s.Group.Stopping ? "Stopped" : "Failed";
                s.Message = s.Group.Stopping ? "Steam startup cancelled." : "Steam environment launch failed: " + exception.Message;
                s.Readiness.TrySetException(new InvalidOperationException(s.Message));
                Log(s, "Error", s.Message); s.Runtime?.Abort(); s.Viewer?.Abort();
                s.Runtime = null; s.Viewer = null; s.Token = "";
                s.Audit?.Dispose();
            }
        }
    }

    private async Task LaunchRemainingAsync(Group g)
    {
        if (g.Profile == "single") return;
        try
        {
            await g.Sessions[0].Launch;
            await g.Sessions[0].Readiness.Task.WaitAsync(TimeSpan.FromSeconds(45), g.StartupCancellation.Token);
            lock (gate)
            {
                if (g.Stopping) return;
                var host = g.Sessions[0];
                if (host.Child is null || host.Exited || host.Stopping || host.State == "Failed" || !host.Ready || !host.GameReady)
                    throw new InvalidOperationException("The host exited or failed before the joining players could start.");
                foreach (var s in g.Sessions.Skip(1))
                    if (g.Profile == "arena-steam") s.Launch = LaunchSteamChildAsync(s);
                    else LaunchChild(s);
            }
            await Task.WhenAll(g.Sessions.Select(s => s.Launch));
            await Task.WhenAll(g.Sessions.Select(s => s.Readiness.Task)).WaitAsync(TimeSpan.FromSeconds(45), g.StartupCancellation.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or InvalidOperationException)
        {
            bool cancelled; lock (gate) cancelled = g.Stopping;
            if (!cancelled) await StopChildrenAsync(g, "Arena group readiness failed; all owned players are being stopped.");
        }
    }

    private async Task CompleteGroupAsync(Group g)
    {
        try
        {
            await g.Startup;
            await Task.WhenAll(g.Sessions.Select(s => s.Launch));
            await Task.WhenAll(g.Sessions.Select(s => s.Completion));
        }
        finally
        {
            foreach (var session in g.Sessions)
                if (session.LatencyRelay is not null) await session.LatencyRelay.DisposeAsync();
        }
        g.Artifact.Dispose(); g.StartupCancellation.Dispose();
    }

    public async Task<StreamingResult> StopGroupAsync(CancellationToken ct)
    {
        await lifecycle.WaitAsync(ct);
        try
        {
            Group? g; lock (gate) g = group;
            if (g is null) return new(null, "Streaming group not found.", 404);
            await StopChildrenAsync(g, null);
            // The startup task cannot launch a child after Stopping is set.
            await g.Startup;
            lock (gate) return new(View(g.Sessions[0]), null);
        }
        finally { lifecycle.Release(); }
    }

    private async Task StopChildrenAsync(Group g, string? failure)
    {
        lock (gate)
        {
            g.Stopping = true;
            if (g.Sessions.Any(s => !s.Exited) && !g.StartupCancellation.IsCancellationRequested) g.StartupCancellation.Cancel();
            pendingController = null;
        }
        await Task.WhenAll(g.Sessions.Select(s => StopOwnedAsync(s, failure)));
    }

    public async Task<StreamingResult> StopSessionAsync(string sessionId, CancellationToken ct)
    {
        await lifecycle.WaitAsync(ct);
        try
        {
            Session? s;
            lock (gate)
            {
                s = group?.Sessions.FirstOrDefault(s => s.Id == sessionId);
                if (s is null) return new(null, "Streaming session not found.", 404);
                if (s.Exited) return new(View(s), null);
            }
            // Losing a peer during initial readiness invalidates the launch as a group.
            if (!s.Group.Startup.IsCompleted) { await StopChildrenAsync(s.Group, null); await s.Group.Startup; }
            else await StopOwnedAsync(s, null);
            lock (gate) return new(View(s), null);
        }
        finally { lifecycle.Release(); }
    }

    public StreamingBrowserGrant? IssueViewerGrant(string sessionId)
    {
        lock (gate)
        {
            var s = group?.Sessions.FirstOrDefault(s => s.Id == sessionId);
            if (s is null || s.Exited || s.Stopping || !s.Ready || s.Viewer is not null ||
                (s.GrantHash is not null && s.GrantExpires > DateTimeOffset.UtcNow)) return null;
            var token = NewToken(); s.GrantHash = Hash(token); s.GrantExpires = DateTimeOffset.UtcNow.AddSeconds(15);
            return new(s.Id, s.InstanceId, token, $"/api/streaming/viewer/{s.Id}/{s.InstanceId}", s.GrantExpires, []);
        }
    }

    public bool CanAcceptViewer(string sessionId, string instanceId)
    {
        lock (gate)
        {
            var s = Find(sessionId, instanceId);
            return s is { Exited: false, Stopping: false, Ready: true, Viewer: null, GrantHash: not null } && s.GrantExpires > DateTimeOffset.UtcNow;
        }
    }

    public bool AuthenticateRuntime(string sessionId, string instanceId, string authorization, string origin)
    {
        lock (gate)
        {
            var s = Find(sessionId, instanceId);
            return s is { Started: true, Exited: false, Stopping: false, Runtime: null } && s.Origin == origin &&
                authorization.StartsWith("Bearer ", StringComparison.Ordinal) && Matches(s.Token, authorization[7..]);
        }
    }

    public async Task RuntimeSocketAsync(string sessionId, string instanceId, WebSocket socket, CancellationToken ct)
    {
        await using var peer = new StreamingPeer(socket);
        Session? s;
        lock (gate)
        {
            s = Find(sessionId, instanceId);
            if (s is null || s.Exited || s.Stopping || s.Runtime is not null) { peer.Abort(); return; }
            s.MouseLockRequested = false;
            s.Runtime = peer;
        }
        peer.Start();
        try { await ReadMessagesAsync(s, peer, true, ct); }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(s.Runtime, peer))
                {
                    Revoke(s, "Runtime disconnected."); s.Runtime = null; s.Ready = false; s.MouseLockRequested = false;
                    s.Viewer?.Abort(); s.Viewer = null; s.GrantHash = null;
                    if (!s.Exited && !s.Stopping && s.State != "Failed") { s.State = "Failed"; s.Message = "The owned runtime disconnected."; }
                }
            }
        }
    }

    public async Task ViewerSocketAsync(string sessionId, string instanceId, WebSocket socket, CancellationToken ct)
    {
        await using var peer = new StreamingPeer(socket);
        Session? s = null;
        try
        {
            using var authDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            authDeadline.CancelAfter(TimeSpan.FromSeconds(5));
            using var auth = await StreamingPeer.ReceiveAsync(socket, authDeadline.Token);
            if (auth is null) return;
            var message = auth.RootElement;
            if (Text(message, "type", 32) != "viewer.auth" || Text(message, "sessionId", 32) != sessionId || Text(message, "instanceId", 32) != instanceId) return;
            var token = Text(message, "token", 64);
            lock (gate)
            {
                s = Find(sessionId, instanceId);
                if (s is null || s.Exited || s.Stopping || !s.Ready || s.Viewer is not null ||
                    s.GrantExpires <= DateTimeOffset.UtcNow || s.GrantHash is null || token is null || !MatchesHash(s.GrantHash, token)) return;
                s.GrantHash = null; s.Viewer = peer; s.ViewerLastSeen = DateTimeOffset.UtcNow;
                peer.Start();
                var ready = new { type = "viewer.ready", sessionId = s.Id, instanceId = s.InstanceId };
                peer.Send(ready);
                peer.Send(new { type = "runtime.cursor", sessionId = s.Id, instanceId = s.InstanceId, mouseLockRequested = s.MouseLockRequested });
                s.Runtime?.Send(ready);
            }
            await ReadMessagesAsync(s, peer, false, ct);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or JsonException or InvalidDataException or ObjectDisposedException) { }
        finally
        {
            lock (gate)
            {
                if (s is not null && ReferenceEquals(s.Viewer, peer))
                {
                    Revoke(s, "Viewer disconnected."); s.Viewer = null;
                    s.Runtime?.Send(new { type = "viewer.closed", sessionId = s.Id, instanceId = s.InstanceId });
                }
            }
        }
    }

    private async Task ReadMessagesAsync(Session s, StreamingPeer peer, bool runtime, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, peer.Closed);
        var window = DateTimeOffset.UtcNow; var count = 0;
        try
        {
            while (!linked.IsCancellationRequested)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(20));
                using var document = await StreamingPeer.ReceiveAsync(peer.Socket, deadline.Token);
                if (document is null) break;
                var now = DateTimeOffset.UtcNow;
                if (now - window >= TimeSpan.FromSeconds(1)) { window = now; count = 0; }
                if (++count > 120) throw new InvalidDataException("Signaling rate exceeded.");
                lock (gate)
                {
                    if (!ReferenceEquals(group, s.Group) || s.Exited || (runtime ? s.Runtime != peer : s.Viewer != peer)) break;
                    var message = document.RootElement;
                    if (Text(message, "sessionId", 32) != s.Id || Text(message, "instanceId", 32) != s.InstanceId)
                        throw new InvalidDataException("Stale streaming identity.");
                    if (!runtime) s.ViewerLastSeen = now;
                    Handle(s, runtime, message);
                }
            }
        }
        catch (InvalidDataException exception) when (runtime)
        {
            lock (gate)
            {
                s.State = "Failed"; s.Message = Scrub(s, exception.Message);
                s.Readiness.TrySetException(new InvalidOperationException(s.Message));
                Log(s, "Error", s.Message);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or JsonException or InvalidDataException or ObjectDisposedException) { }
    }

    private void Handle(Session s, bool runtime, JsonElement m)
    {
        var type = Text(m, "type", 32) ?? throw new InvalidDataException("Missing message type.");
        object Envelope(string kind) => new { type = kind, sessionId = s.Id, instanceId = s.InstanceId };
        if (runtime && type == "runtime.cursor")
        {
            if (!m.TryGetProperty("mouseLockRequested", out var cursor) || cursor.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("Cursor intent requires a boolean mouseLockRequested field.");
            s.MouseLockRequested = cursor.GetBoolean();
            s.Viewer?.Send(new { type, sessionId = s.Id, instanceId = s.InstanceId, mouseLockRequested = s.MouseLockRequested });
            return;
        }
        if (runtime && type == "runtime.ready")
        {
            if (Number(m, "width", 320, 1920) != s.Width || Number(m, "height", 180, 1080) != s.Height)
                throw new InvalidDataException("The capture resolution does not match the requested player size.");
            s.Ready = true; s.LastHeartbeat = DateTimeOffset.UtcNow; s.State = "Ready";
            s.Message = "Player runtime is ready for a local viewer.";
            CompleteReadiness(s);
            Log(s, "Info", s.Message); return;
        }
        if (runtime && type == "runtime.game-ready")
        {
            if (s.Group.Profile is not ("arena-kcp" or "arena-steam") || Text(m, "role", 16) != s.Role)
                throw new InvalidDataException("Unexpected game readiness identity.");
            if (s.Group.Profile == "arena-steam")
            {
                var lobbyId = SteamIdentity(m, "lobbyId");
                var steamId = SteamIdentity(m, "steamId");
                if (s.SteamId is not null && s.SteamId != steamId ||
                    s.Group.Sessions.Any(other => other != s && other.SteamId == steamId))
                    throw new InvalidDataException("Every streamed Steam player must use a distinct signed-in account.");
                if (s.Index == 0)
                {
                    if (s.Group.SteamLobbyId is not null && s.Group.SteamLobbyId != lobbyId)
                        throw new InvalidDataException("The host Steam lobby changed during a stream.");
                    s.Group.SteamLobbyId = lobbyId; s.Group.HostSteamId = steamId;
                }
                else if (s.Group.SteamLobbyId != lobbyId || s.Group.HostSteamId == steamId)
                    throw new InvalidDataException("The Steam client did not join the owned host lobby.");
                s.SteamId = steamId;
            }
            s.GameReady = true; CompleteReadiness(s);
            Log(s, "Info", "Game-owned " + s.Role + " readiness confirmed."); return;
        }
        if (runtime && type == "runtime.heartbeat") { s.LastHeartbeat = DateTimeOffset.UtcNow; return; }
        if (runtime && type == "runtime.log")
        {
            var level = Text(m, "level", 16);
            if (level?.ToLowerInvariant() is not ("info" or "warning" or "error" or "debug" or "exception" or "assert" or "log")) throw new InvalidDataException("Invalid log level.");
            Log(s, level, Text(m, "message", 2000) ?? ""); return;
        }
        if (runtime && type == "stream.error")
        {
            s.State = "Failed"; s.Message = Scrub(s, Text(m, "message", 2000) ?? "Runtime streaming failed.");
            Log(s, "Error", s.Message); s.Viewer?.Send(new { type, sessionId = s.Id, instanceId = s.InstanceId, message = s.Message }); return;
        }
        if (!runtime && type == "viewer.heartbeat") { s.Viewer?.Send(Envelope("viewer.heartbeat")); return; }
        if (!runtime && type == "control.request")
        {
            if (!s.Ready || s.Stopping || s.LeaseId is not null) return;
            var requestId = Text(m, "requestId", 32);
            if (requestId is not null && !Guid.TryParseExact(requestId, "N", out _)) throw new InvalidDataException("Invalid control request identity.");
            if (pendingController is { } previous)
                previous.Viewer?.Send(new { type = "control.denied", sessionId = previous.Id, instanceId = previous.InstanceId,
                    requestId = previous.ControlRequestId, reason = "A newer control focus request replaced this request." });
            s.ControlRequestId = requestId;
            pendingController = s;
            foreach (var other in s.Group.Sessions.Where(other => other != s)) Revoke(other, "Control focus changed.");
            TryGrantPending(); return;
        }
        if (runtime && type == "control.released")
        {
            var lease = Text(m, "leaseId", 32);
            if (lease is null || lease != s.ReleasingLeaseId) return;
            s.ReleasingLeaseId = null;
            s.Viewer?.Send(new { type, sessionId = s.Id, instanceId = s.InstanceId, leaseId = lease });
            Log(s, "Info", "Runtime confirmed neutral input after control release.");
            TryGrantPending(); return;
        }
        if (runtime && type == "control.ready")
        {
            // This acknowledgement comes from the current authenticated runtime,
            // after its main thread has installed the grant. Only then may the
            // browser learn the credentials and send input on the data channel.
            if (s.LeaseId is null || s.LeaseReady || !s.Ready || s.Stopping ||
                Text(m, "leaseId", 32) != s.LeaseId) return;
            if (s.LeaseExpires <= DateTimeOffset.UtcNow) { Revoke(s, "Controller readiness timed out."); return; }
            s.LeaseReady = true;
            Log(s, "Info", "Manual control acquired by the connected viewer.");
            s.Viewer?.Send(ControlGrant(s)); return;
        }
        if (!runtime && type == "control.heartbeat")
        {
            // A delayed renewal from the same viewer must not revoke a newer lease.
            if (Text(m, "leaseId", 32) != s.LeaseId) return;
            if (s.LeaseId is null || !s.LeaseReady || s.LeaseExpires <= DateTimeOffset.UtcNow)
            { Revoke(s, "Controller lease expired or changed."); return; }
            s.LeaseExpires = DateTimeOffset.UtcNow.AddSeconds(StreamingProtocol.ControllerLeaseSeconds);
            var renewed = new { type = "control.renewed", sessionId = s.Id, instanceId = s.InstanceId, leaseId = s.LeaseId, expiresUtc = s.LeaseExpires };
            s.Runtime?.Send(renewed); s.Viewer?.Send(renewed); return;
        }
        if (!runtime && type == "control.release")
        {
            // Legacy release-without-ID cancels a pending request. An identified
            // release can only affect that exact live lease.
            var lease = Text(m, "leaseId", 32);
            if (lease is not null && lease != s.LeaseId) return;
            if (pendingController == s) pendingController = null;
            Revoke(s, "Controller released."); return;
        }
        if (!s.Ready || s.Stopping) return;
        var recipient = runtime ? s.Viewer : s.Runtime;
        if (type == "signal.ice")
        {
            var candidate = Text(m, "candidate", 4096);
            var mid = Text(m, "sdpMid", 256, true);
            var index = OptionalNumber(m, "sdpMLineIndex", 0, 32);
            if (candidate is null) throw new InvalidDataException("Invalid ICE candidate.");
            recipient?.Send(new { type, sessionId = s.Id, instanceId = s.InstanceId, candidate, sdpMid = mid, sdpMLineIndex = index }); return;
        }
        if ((runtime && type == "signal.offer") || (!runtime && type == "signal.answer"))
        {
            var sdp = Text(m, "sdp", 48000) ?? throw new InvalidDataException("Invalid SDP.");
            recipient?.Send(new { type, sessionId = s.Id, instanceId = s.InstanceId, sdp }); return;
        }
        throw new InvalidDataException("Unsupported signaling message.");
    }

    private static void CompleteReadiness(Session s)
    {
        if (s.Ready && (s.Group.Profile == "single" || s.GameReady)) s.Readiness.TrySetResult();
    }
    private void TryGrantPending()
    {
        if (pendingController is not { } s || group != s.Group || s.Group.Stopping || s.Stopping || s.Exited ||
            !s.Ready || s.Viewer is null || s.Runtime is null) { pendingController = null; return; }
        // A revoke ACK is a barrier even when the old viewer has disappeared.
        if (s.Group.Sessions.Any(other => other.ReleasingLeaseId is not null || other.LeaseId is not null)) return;
        pendingController = null;
        s.LeaseId = Guid.NewGuid().ToString("N"); s.LeaseToken = NewToken(); Grant(s);
    }
    private static void Grant(Session s)
    {
        s.LeaseReady = false;
        s.LeaseExpires = DateTimeOffset.UtcNow.AddSeconds(StreamingProtocol.ControllerLeaseSeconds);
        s.Runtime?.Send(ControlGrant(s));
    }
    private static object ControlGrant(Session s) => new { type = "control.granted", sessionId = s.Id, instanceId = s.InstanceId,
        requestId = s.ControlRequestId, leaseId = s.LeaseId, leaseToken = s.LeaseToken, expiresUtc = s.LeaseExpires, timeoutMs = StreamingProtocol.ControllerTimeoutMs };
    private void Revoke(Session s, string reason)
    {
        if (s.LeaseId is null) return;
        Log(s, "Info", "Manual control revoked: " + reason);
        var leaseId = s.LeaseId;
        if (!s.Exited)
        {
            s.ReleasingLeaseId = leaseId;
            s.ReleaseDeadline = DateTimeOffset.UtcNow.AddSeconds(3);
        }
        s.LeaseId = null; s.LeaseToken = null; s.LeaseReady = false;
        var revoked = new { type = "control.revoked", sessionId = s.Id, instanceId = s.InstanceId, leaseId, reason };
        s.Runtime?.Send(revoked); s.Viewer?.Send(revoked);
    }

    private async Task ObserveExitAsync(Session s)
    {
        var child = s.Child!;
        var output = CaptureAsync(s, child.StandardOutput, "Info");
        var error = CaptureAsync(s, child.StandardError, "Error");
        await child.WaitForExitAsync();
        lock (gate)
        {
            s.ExitCode = child.ExitCode; s.Exited = true; s.Ready = false; s.MouseLockRequested = false;
            s.Readiness.TrySetException(new InvalidOperationException("Owned player exited before group readiness completed."));
            if (s.State != "Failed")
            {
                s.State = s.Stopping || s.ExitCode == 0 ? "Stopped" : "Failed";
                s.Message = $"Owned player exited with code {s.ExitCode}.";
            }
            Log(s, "Info", $"Owned player exited with code {s.ExitCode}.");
            Revoke(s, "Player exited."); s.Runtime?.Abort(); s.Viewer?.Abort();
            s.Runtime = null; s.Viewer = null; s.GrantHash = null;
            s.ReleasingLeaseId = null; TryGrantPending();
        }
        // A grandchild retaining a pipe must not block releasing the owned process.
        try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException) { }
        child.Dispose(); s.Audit?.Dispose();
        bool failedGroup;
        lock (gate)
        {
            s.Token = "";
            failedGroup = s.Group.Profile is "arena-kcp" or "arena-steam" && !s.Group.Stopping && !s.Stopping;
        }
        if (failedGroup) await StopChildrenAsync(s.Group, "An Arena peer exited unexpectedly; all owned players are being stopped.");
    }

    private async Task CaptureAsync(Session s, StreamReader? reader, string level)
    {
        if (reader is null) return;
        var buffer = new char[512]; var line = new StringBuilder(1000);
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer)) > 0)
                foreach (var character in buffer.AsSpan(0, read))
                {
                    if (character == '\n') { lock (gate) Log(s, level, line.ToString()); line.Clear(); }
                    else if (line.Length < 2000 && character != '\r') line.Append(character);
                }
            if (line.Length > 0) { lock (gate) Log(s, level, line.ToString()); }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private async Task StopOwnedAsync(Session s, string? failure)
    {
        await s.Launch;
        lock (gate)
        {
            if (s.Exited) return;
            if (pendingController == s) pendingController = null;
            s.Stopping = true; s.MouseLockRequested = false; s.GrantHash = null; Revoke(s, failure ?? "Session stopped.");
            s.State = failure is null ? "Stopping" : "Failed";
            s.Message = failure ?? "Waiting for the owned player to quit.";
            s.Runtime?.Send(new { type = "session.stop", sessionId = s.Id, instanceId = s.InstanceId });
            s.Viewer?.Abort(); s.Viewer = null;
            Log(s, failure is null ? "Info" : "Error", s.Message);
            if (!s.Started)
            {
                s.Exited = true; s.State = failure is null ? "Stopped" : "Failed";
                s.Readiness.TrySetCanceled(); s.Child?.Dispose(); s.Audit?.Dispose(); s.Token = "";
                return;
            }
        }
        try { await s.Completion.WaitAsync(TimeSpan.FromSeconds(3)); return; }
        catch (TimeoutException) { }
        lock (gate)
        {
            if (!s.Exited)
                try { s.Child?.Kill(); Log(s, "Warning", "Graceful stop timed out; terminated the exact owned player process."); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { Log(s, "Error", "The owned process could not be terminated; its reservation is retained until it exits."); }
        }
        try { await s.Completion.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (TimeoutException) { }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(200, ct);
                var stops = new List<(Session Session, string Failure)>();
                lock (gate)
                {
                    var now = DateTimeOffset.UtcNow;
                    foreach (var s in group?.Sessions ?? [])
                    {
                        if (s.Exited || s.Stopping || !s.Started) continue;
                        if (s.LeaseId is not null && s.LeaseExpires <= now) Revoke(s, "Controller lease expired.");
                        if (s.ReleasingLeaseId is not null && s.ReleaseDeadline <= now && pendingController is { } pending)
                        {
                            pending.Viewer?.Send(new { type = "control.denied", sessionId = pending.Id, instanceId = pending.InstanceId,
                                requestId = pending.ControlRequestId,
                                reason = "The previous runtime has not confirmed neutral input. Control remains released." });
                            pendingController = null;
                        }
                        if (s.Viewer is not null && now - s.ViewerLastSeen > TimeSpan.FromSeconds(15))
                        {
                            Revoke(s, "Viewer heartbeat expired."); s.Viewer.Abort(); s.Viewer = null;
                            s.Runtime?.Send(new { type = "viewer.closed", sessionId = s.Id, instanceId = s.InstanceId });
                        }
                        string? failure = null;
                        if (s.LatencyRelay?.Snapshot.Stopped == true) failure = "The owned KCP network relay stopped unexpectedly.";
                        else if (s.State == "Failed") failure = s.Message;
                        else if (!s.Ready && now - s.StartedAt > TimeSpan.FromSeconds(45)) failure = "Runtime readiness timed out. Rebuild with streaming support and inspect player logs.";
                        else if (s.Ready && s.LastHeartbeat is { } heartbeat && now - heartbeat > TimeSpan.FromSeconds(10)) failure = "Runtime heartbeat timed out.";
                        else if (now - s.StartedAt > TimeSpan.FromHours(4)) failure = "The four-hour streaming session limit was reached.";
                        if (failure is not null) stops.Add((s, failure));
                    }
                }
                if (stops.Count > 0)
                {
                    await lifecycle.WaitAsync(ct);
                    try
                    {
                        var g = stops[0].Session.Group;
                        // Startup is atomic as a group; established sessions may fail independently.
                        if (g.Profile is "arena-kcp" or "arena-steam" || !g.Startup.IsCompleted) await StopChildrenAsync(g, stops[0].Failure);
                        else await Task.WhenAll(stops.Select(stop => StopOwnedAsync(stop.Session, stop.Failure)));
                    }
                    finally { lifecycle.Release(); }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        lock (gate) shuttingDown = true;
        await lifecycle.WaitAsync(CancellationToken.None);
        try { Group? g; lock (gate) g = group; if (g is not null) { await StopChildrenAsync(g, null); await g.Startup; } }
        finally { lifecycle.Release(); }
        await base.StopAsync(ct);
    }

    private Session? Find(string sessionId, string instanceId) => group?.Sessions.FirstOrDefault(s => s.Id == sessionId && s.InstanceId == instanceId);
    private static string SteamIdentity(JsonElement message, string field)
    {
        var value = Text(message, field, 20);
        if (value is null || value.Length < 1 || !value.All(char.IsAsciiDigit) || !ulong.TryParse(value, out var parsed) || parsed == 0)
            throw new InvalidDataException("Steam readiness requires numeric lobby and account identities.");
        if (parsed.ToString(System.Globalization.CultureInfo.InvariantCulture) != value)
            throw new InvalidDataException("Steam readiness requires canonical Steam identities.");
        return value;
    }
    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool Matches(string expected, string value) => value.Length == 64 && expected.Length == 64 &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(value));
    private static bool MatchesHash(string expected, string value) => value.Length == 64 && Matches(expected, Hash(value));
    private static void Log(Session s, string level, string message)
    {
        if (s.Logs.Count >= 200) s.Logs.Dequeue();
        var entry = new StreamingLog(++s.LogSequence, DateTimeOffset.UtcNow, level, Scrub(s, message));
        s.Logs.Enqueue(entry); s.Audit?.Append(entry);
    }
    private static string Scrub(Session s, string message)
    {
        if (s.Token.Length > 0) message = message.Replace(s.Token, "[redacted]", StringComparison.OrdinalIgnoreCase);
        if (s.LeaseToken is { } token) message = message.Replace(token, "[redacted]", StringComparison.OrdinalIgnoreCase);
        return new string(message.Take(2000).Select(c => char.IsControl(c) && c != '\t' ? ' ' : c).ToArray());
    }
    private static string? Text(JsonElement m, string name, int maximum, bool nullable = false)
    {
        if (!m.TryGetProperty(name, out var value) || (nullable && value.ValueKind == JsonValueKind.Null)) return null;
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || text.Length > maximum || text.Contains('\0'))
            throw new InvalidDataException("Invalid signaling field.");
        return text;
    }
    private static int Number(JsonElement m, string name, int minimum, int maximum) => OptionalNumber(m, name, minimum, maximum) ?? throw new InvalidDataException("Missing numeric field.");
    private static int? OptionalNumber(JsonElement m, string name, int minimum, int maximum)
    {
        if (!m.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (!value.TryGetInt32(out var number) || number < minimum || number > maximum) throw new InvalidDataException("Invalid numeric field.");
        return number;
    }
}
