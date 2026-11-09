using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using PlaytestOps.Web.Data;
using PlaytestOps.Web.Models;
using PlaytestOps.Web.Streaming;

internal static class MultistreamChecks
{
    public static async Task Run(HttpClient http, string address, string token, IServiceProvider services,
        string scratch, string executable, Action<bool, string> check)
    {
        var supervisor = services.GetRequiredService<StreamingSupervisor>();
        async Task<HttpResponseMessage> Post(string path, object body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
            request.Headers.Add("Origin", address);
            request.Headers.Add(StreamingProtocol.AntiforgeryHeader, token);
            return await http.SendAsync(request);
        }
        foreach (var invalid in new[] { new { instanceCount = 0, launchProfile = "single" }, new { instanceCount = 5, launchProfile = "arena-kcp" },
            new { instanceCount = 1, launchProfile = "arena-kcp" }, new { instanceCount = 2, launchProfile = "single" }, new { instanceCount = 2, launchProfile = "shell" } })
            check((await Post("/api/streaming/start", new { buildId = Guid.NewGuid().ToString("N"), invalid.instanceCount, invalid.launchProfile })).StatusCode == HttpStatusCode.BadRequest,
                $"typed profile rejects {invalid.launchProfile}/{invalid.instanceCount} before launching");
        check((await Post("/api/streaming/start", new { buildId = Guid.NewGuid().ToString("N"), instanceCount = 4, launchProfile = "arena-kcp", arguments = "-batchmode" })).StatusCode == HttpStatusCode.BadRequest,
            "multi-instance start rejects arbitrary command-line fields");
        check((await http.PostAsJsonAsync("/api/streaming/stop", new { })).StatusCode == HttpStatusCode.Forbidden, "group stop requires Origin and antiforgery");

        var build = new UnityBuild { Id = Guid.NewGuid().ToString("N"), RequestKey = Guid.NewGuid().ToString("N"), ProjectId = "multistream-fixture",
            ProjectName = "Signaling process fixture", State = "Succeeded", Revision = "fixture", ActiveTarget = "StandaloneWindows64", FinishedAt = DateTime.UtcNow };
        build.OutputPath = Path.Combine(scratch, "Builds", "PlaytestOps", build.Id);
        build.ExecutablePath = Path.Combine(build.OutputPath, "Player.exe"); build.ManifestPath = Path.Combine(build.OutputPath, "build-manifest.json");
        Directory.CreateDirectory(build.OutputPath);
        foreach (var source in Directory.GetFiles(Path.GetDirectoryName(executable)!))
            File.Copy(source, Path.Combine(build.OutputPath, Path.GetFullPath(source) == Path.GetFullPath(executable) ? "Player.exe" : Path.GetFileName(source)));
        var buildGuid = Guid.NewGuid().ToString("N");
        async Task Marker(bool arena)
        {
            await File.WriteAllTextAsync(Path.Combine(build.OutputPath, StreamingProtocol.CapabilityFile), JsonSerializer.Serialize(new {
                protocolVersion = 1, packageVersion = "0.1.0", buildGuid, capture = "framebuffer", input = "input-system", transport = "webrtc",
                launchProfiles = arena ? new[] { "arena-kcp" } : Array.Empty<string>() }));
            var entries = Directory.GetFiles(build.OutputPath).Where(path => path != build.ManifestPath).Select(path => new {
                path = Path.GetFileName(path), bytes = new FileInfo(path).Length, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) }).ToArray();
            await File.WriteAllTextAsync(build.ManifestPath, JsonSerializer.Serialize(new { protocolVersion = 1, buildId = build.Id, result = "Succeeded", totalErrors = 0,
                artifactListTruncated = false, activeTarget = build.ActiveTarget, revision = build.Revision, buildGuid, artifacts = entries }));
        }
        await Marker(false);
        await using (var scope = services.CreateAsyncScope())
        { var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(); db.UnityBuilds.Add(build); await db.SaveChangesAsync(); }
        object Launch(int count) => new { buildId = build.Id, instanceCount = count, launchProfile = count == 1 ? "single" : "arena-kcp", width = 640, height = 360 };
        check((await Post("/api/streaming/start", Launch(4))).StatusCode == HttpStatusCode.BadRequest, "multiplayer requires the hash-approved Arena capability");
        check((await Post("/api/streaming/start", Launch(1))).IsSuccessStatusCode, "legacy single launch succeeds without Arena capability");
        await Until(() => supervisor.Snapshot()?.RuntimeConnected == true, "single readiness");
        check(supervisor.Snapshots().Count == 1 && supervisor.Snapshot()?.Role == "single", "legacy session aliases the single child");
        check(!supervisor.Snapshot()!.MouseLockRequested, "single runtime defaults to unlocked cursor intent");
        check(supervisor.LatencySnapshot() is null && (await Post($"/api/streaming/groups/{supervisor.Snapshot()!.GroupId}/latency",
            new { enabled = true, minRttMs = 50, maxRttMs = 100 })).StatusCode == HttpStatusCode.Conflict,
            "Single-player groups reject KCP latency controls");
        check((await Post($"/api/streaming/{supervisor.Snapshot()!.SessionId}/stop", new { })).IsSuccessStatusCode, "legacy child stop remains available");
        await Until(() => !supervisor.IsBuildInUse(build.Id), "single pin cleanup");
        await Marker(true);

        check((await Post("/api/streaming/start", Launch(4))).IsSuccessStatusCode, "four-child group launch is accepted");
        await Until(() => supervisor.Snapshots().Count == 4 && supervisor.Snapshots().All(s => s.RuntimeConnected && s.GameReady), "four fixture runtimes ready");
        await Until(() => supervisor.LatencySnapshot() is not null, "KCP latency startup barrier");
        var states = supervisor.Snapshots();
        var groupId = states[0].GroupId!;
        string Storage(StreamingState state) => Path.Combine(scratch, "App_Data", "Streaming", groupId, state.InstanceId!);
        check(states.Select(s => s.SessionId).Distinct().Count() == 4 && states.Select(s => s.InstanceId).Distinct().Count() == 4 && states.All(s => s.GroupId == groupId),
            "four children have distinct session/instance identities within one group");
        check(states.Select(s => s.Role).SequenceEqual(new[] { "host", "client", "client", "client" }) && states.Select(s => s.Index).SequenceEqual(new[] { 0, 1, 2, 3 }),
            "group roles and stable zero-based indices are correct");
        check(states.Select(s => s.Label).SequenceEqual(new[] { "Host", "Client 1", "Client 2", "Client 3" }), "tile labels identify all four players");
        check(states[0].MouseLockRequested && states.Skip(1).All(s => !s.MouseLockRequested), "runtime cursor intent is cached independently for each child");
        var receipts = states.Select(s => JsonDocument.Parse(File.ReadAllText(Path.Combine(Storage(s), "fixture.json")))).ToArray();
        try
        {
            var hostReady = receipts[0].RootElement.GetProperty("readyUtc").GetDateTimeOffset();
            check(receipts.Skip(1).All(r => r.RootElement.GetProperty("launchedUtc").GetDateTimeOffset() >= hostReady), "clients start only after game-owned host readiness");
            check(receipts.Select(r => r.RootElement.GetProperty("port").GetInt32()).Distinct().Count() == 4 &&
                receipts.All(r => r.RootElement.GetProperty("playerCount").GetInt32() == 4 && r.RootElement.GetProperty("address").GetString() == "127.0.0.1"),
                "typed Arena config assigns a private host port and distinct per-client relay ports");
            check(receipts.Select(r => r.RootElement.GetProperty("storagePath").GetString()).Distinct().Count() == 4, "each child has isolated storage");
        }
        finally { foreach (var receipt in receipts) receipt.Dispose(); }
        var envelope = await http.GetFromJsonAsync<JsonElement>("/api/streaming/state");
        check(envelope.GetProperty("sessions").GetArrayLength() == 4 && envelope.GetProperty("session").GetProperty("sessionId").GetString() == states[0].SessionId,
            "HTTP state exposes all children and preserves first-child legacy alias");
        check(envelope.GetProperty("sessions")[0].GetProperty("mouseLockRequested").GetBoolean() &&
            !envelope.GetProperty("sessions")[1].GetProperty("mouseLockRequested").GetBoolean(), "HTTP snapshots expose camel-case boolean cursor intent");
        check((await Post("/api/streaming/start", Launch(2))).StatusCode == HttpStatusCode.Conflict, "an active group prevents over-capacity or concurrent launches");
        check(supervisor.IsBuildInUse(build.Id) && await supervisor.AcquireBuildDeletionLeaseAsync(build.Id, CancellationToken.None) is null, "all group artifact pins prevent build deletion");

        string latencyPath = $"/api/streaming/groups/{groupId}/latency";
        check(envelope.GetProperty("networkLatency").GetProperty("links").GetArrayLength() == 3 &&
            !supervisor.LatencySnapshot()!.Enabled && supervisor.LatencySnapshot()!.Links.All(link => link.GameRttMs is null),
            "Ready KCP groups expose disabled remote links without inventing game RTT samples");
        check((await http.PostAsJsonAsync(latencyPath, new { enabled = true, minRttMs = 50, maxRttMs = 100 })).StatusCode == HttpStatusCode.Forbidden,
            "Live KCP latency writes require Origin and antiforgery");
        using (var missingToken = new HttpRequestMessage(HttpMethod.Post, latencyPath) { Content = JsonContent.Create(new { enabled = true }) })
        {
            missingToken.Headers.Add("Origin", address);
            check((await http.SendAsync(missingToken)).StatusCode == HttpStatusCode.Forbidden, "Live KCP latency rejects a missing antiforgery token");
        }
        foreach (string invalid in new[] { "null", "[]", "{", "{\"enabled\":\"yes\"}", "{\"enabled\":true,\"minRttMs\":-1,\"maxRttMs\":100}",
            "{\"enabled\":true,\"minRttMs\":100,\"maxRttMs\":50}", "{\"enabled\":true,\"maxRttMs\":1001}", "{\"enabled\":true,\"shell\":\"ignored\"}", new string(' ', 1025) })
        {
            using var invalidRequest = new HttpRequestMessage(HttpMethod.Post, latencyPath) { Content = new StringContent(invalid, Encoding.UTF8, "application/json") };
            invalidRequest.Headers.Add("Origin", address); invalidRequest.Headers.Add(StreamingProtocol.AntiforgeryHeader, token);
            check((await http.SendAsync(invalidRequest)).StatusCode == HttpStatusCode.BadRequest && supervisor.LatencySnapshot()!.Revision == 0,
                "Malformed or invalid KCP latency requests cannot change applied settings");
        }
        check((await Post($"/api/streaming/groups/{Guid.NewGuid():N}/latency", new { enabled = true, minRttMs = 50, maxRttMs = 100 })).StatusCode == HttpStatusCode.Conflict,
            "A stale group identity cannot configure the active KCP relays");

        var measuredClient = states[1];
        string samplePath = Path.Combine(Storage(measuredClient), "arena-state.json");
        Dictionary<string, object?> Sample() => new() {
            ["groupId"] = groupId, ["sessionId"] = measuredClient.SessionId, ["instanceId"] = measuredClient.InstanceId,
            ["utc"] = DateTimeOffset.UtcNow, ["hasGameNetworkRtt"] = true, ["gameNetworkRttMs"] = 82.5, ["gameNetworkRttStdDevMs"] = 7.25 };
        await File.WriteAllTextAsync(samplePath, JsonSerializer.Serialize(Sample()));
        var measured = await http.GetFromJsonAsync<JsonElement>("/api/streaming/state");
        var measuredLink = measured.GetProperty("networkLatency").GetProperty("links")[0];
        check(measuredLink.GetProperty("gameRttMs").GetDouble() == 82.5 && measuredLink.GetProperty("gameRttStdDevMs").GetDouble() == 7.25 &&
            measuredLink.GetProperty("gameRttSampleUtc").ValueKind == JsonValueKind.String && supervisor.LatencySnapshot()!.Links.Skip(1).All(link => link.GameRttMs is null),
            "HTTP latency state reports only fresh game RTT matching the exact client identity");
        foreach (var invalidSample in new (string Field, object? Value)[] {
            ("groupId", Guid.NewGuid().ToString("N")), ("sessionId", states[0].SessionId), ("instanceId", states[2].InstanceId),
            ("utc", DateTimeOffset.UtcNow.AddSeconds(-10)), ("utc", DateTimeOffset.UtcNow.AddSeconds(10)),
            ("hasGameNetworkRtt", false), ("gameNetworkRttMs", -1), ("gameNetworkRttStdDevMs", -1), ("gameNetworkRttMs", 20001), ("gameNetworkRttMs", "NaN") })
        {
            var sample = Sample(); sample[invalidSample.Field] = invalidSample.Value;
            await File.WriteAllTextAsync(samplePath, JsonSerializer.Serialize(sample));
            var link = supervisor.LatencySnapshot()!.Links[0];
            check(link.GameRttMs is null && link.GameRttStdDevMs is null && link.GameRttSampleUtc is null,
                "Invalid, stale, or mismatched game RTT is unavailable: " + invalidSample.Field);
        }
        await File.WriteAllTextAsync(samplePath, JsonSerializer.Serialize(Sample()).Replace("82.5", "1e999", StringComparison.Ordinal));
        check(supervisor.LatencySnapshot()!.Links[0].GameRttMs is null, "Nonfinite game RTT cannot enter HTTP latency state");
        await File.WriteAllTextAsync(samplePath, "{}");
        check(supervisor.LatencySnapshot()!.Links[0].GameRttMs is null, "Incomplete game RTT snapshots remain unavailable");
        File.Delete(samplePath);
        check(supervisor.LatencySnapshot()!.Links[0].GameRttMs is null, "Older builds without game RTT snapshots remain supported");

        var viewers = new List<FixtureViewer>();
        try
        {
            foreach (var state in states)
            {
                using var grantResponse = await Post($"/api/streaming/{state.SessionId}/viewer-grant", new { });
                var grant = (await grantResponse.Content.ReadFromJsonAsync<JsonElement>());
                var viewer = await FixtureViewer.Connect(address, grant); viewers.Add(viewer);
                await viewer.Next("viewer.ready");
                var cursor = await viewer.Next("runtime.cursor");
                check(cursor.GetProperty("mouseLockRequested").GetBoolean() == state.MouseLockRequested &&
                    cursor.GetProperty("sessionId").GetString() == state.SessionId && cursor.GetProperty("instanceId").GetString() == state.InstanceId,
                    "viewer attach sends cached cursor intent with exact child identity " + state.Index);
                var offer = await viewer.Next("signal.offer");
                check(offer.GetProperty("sdp").GetString() == $"fixture-offer:{state.Role}:{state.InstanceId}", "viewer offer is isolated to child " + state.Index);
                await viewer.Send("signal.answer", new() { ["sdp"] = "answer-for-" + state.Index });
            }
            await Until(() => supervisor.Snapshots().All(s => s.Logs.Any(log => log.Message == "fixture-answer:answer-for-" + s.Index)), "per-child answer routing");
            check(supervisor.Snapshots().All(s => s.Logs.Count(log => log.Message.StartsWith("fixture-answer:")) == 1), "answers never leak to another runtime");
            check((await Post($"/api/streaming/{states[0].SessionId}/viewer-grant", new { })).StatusCode == HttpStatusCode.Conflict, "an existing viewer cannot be displaced by a second grant");

            await File.WriteAllTextAsync(Path.Combine(Storage(states[0]), "cursor-command.json"), "{\"mouseLockRequested\":false}");
            var unlocked = await viewers[0].Next("runtime.cursor");
            check(!unlocked.GetProperty("mouseLockRequested").GetBoolean() && !supervisor.Snapshots()[0].MouseLockRequested,
                "authenticated runtime cursor unlock is relayed live and stored");
            await File.WriteAllTextAsync(Path.Combine(Storage(states[1]), "cursor-command.json"), "{\"mouseLockRequested\":true}");
            var locked = await viewers[1].Next("runtime.cursor");
            check(locked.GetProperty("mouseLockRequested").GetBoolean() && supervisor.Snapshots()[1].MouseLockRequested &&
                !supervisor.Snapshots()[0].MouseLockRequested && !supervisor.Snapshots()[2].MouseLockRequested,
                "runtime cursor lock changes only the addressed child's intent");
            check(supervisor.Snapshots().All(s => !s.ControllerActive), "cursor intent does not acquire or modify input leases");
            await viewers[0].DisposeAsync();
            await Until(() => !supervisor.Snapshots()[0].ViewerConnected, "cursor reconnect viewer detach");
            using (var response = await Post($"/api/streaming/{states[0].SessionId}/viewer-grant", new { }))
                viewers[0] = await FixtureViewer.Connect(address, await response.Content.ReadFromJsonAsync<JsonElement>());
            await viewers[0].Next("viewer.ready");
            var cached = await viewers[0].Next("runtime.cursor");
            check(!cached.GetProperty("mouseLockRequested").GetBoolean(), "replacement viewer receives the latest cached cursor intent");
            await viewers[0].Next("signal.offer");

            var firstRequest = Guid.NewGuid().ToString("N");
            await viewers[0].Send("control.request", new() { ["requestId"] = firstRequest });
            var firstGrant = await viewers[0].Next("control.granted");
            check(firstGrant.GetProperty("requestId").GetString() == firstRequest, "control grant echoes the exact focus request identity");
            var firstLease = firstGrant.GetProperty("leaseId").GetString();
            var processIdentities = states.Select(state => JsonDocument.Parse(File.ReadAllText(Path.Combine(Storage(state), "fixture.json")))).ToArray();
            try
            {
                foreach (var setting in new[] { new KcpLatencySettings(true, 50, 100), new(true, 80, 160), new(false, 80, 160) })
                {
                    long previousRevision = supervisor.LatencySnapshot()!.Revision;
                    using var response = await Post(latencyPath, setting);
                    var applied = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("networkLatency");
                    check(response.IsSuccessStatusCode && applied.GetProperty("enabled").GetBoolean() == setting.Enabled &&
                        applied.GetProperty("minRttMs").GetInt32() == setting.MinRttMs && applied.GetProperty("maxRttMs").GetInt32() == setting.MaxRttMs &&
                        applied.GetProperty("revision").GetInt64() == previousRevision + 1,
                        "Live HTTP KCP latency apply/change/reset returns the authoritative applied revision");
                    check(supervisor.Snapshots().Select(state => state.SessionId).SequenceEqual(states.Select(state => state.SessionId)) &&
                        supervisor.Snapshots().All(state => state.RuntimeConnected && state.ViewerConnected && !state.Exited) &&
                        supervisor.Snapshots()[0].ControllerActive && supervisor.Snapshots().Count(state => state.ControllerActive) == 1 &&
                        processIdentities.All(receipt => ProcessAlive(receipt.RootElement.GetProperty("pid").GetInt32())),
                        "Live latency updates preserve owned players, viewer sockets, and the active control lease");
                }
                await viewers[0].Send("control.heartbeat", new() { ["leaseId"] = firstLease });
                var renewed = await viewers[0].Next("control.renewed");
                check(renewed.GetProperty("leaseId").GetString() == firstLease, "The exact preexisting control lease survives latency reset");
            }
            finally { foreach (var receipt in processIdentities) receipt.Dispose(); }
            await File.WriteAllTextAsync(Path.Combine(Storage(states[0]), "hold-release"), "hold");
            await viewers[1].Send("control.request");
            var revoked = await viewers[0].Next("control.revoked");
            check(revoked.GetProperty("leaseId").GetString() == firstLease, "focus change revokes the exact old lease");
            check(!await viewers[1].Has("control.granted", 250), "next child receives no grant before old runtime release ACK");
            check(supervisor.Snapshots().All(s => !s.ControllerActive), "input is inactive throughout the release barrier");
            File.Delete(Path.Combine(Storage(states[0]), "hold-release"));
            var released = await viewers[0].Next("control.released");
            var secondGrant = await viewers[1].Next("control.granted");
            check(released.GetProperty("leaseId").GetString() == firstLease && secondGrant.GetProperty("leaseId").GetString() != firstLease, "neutral-release ACK precedes a fresh child lease");
            check(supervisor.Snapshots().Count(s => s.ControllerActive) == 1 && supervisor.Snapshots()[1].ControllerActive, "exactly one child controls input after focus switches");
            await viewers[1].Send("control.release", new() { ["leaseId"] = firstLease });
            await viewers[1].Send("control.heartbeat", new() { ["leaseId"] = firstLease });
            await Task.Delay(100);
            check(supervisor.Snapshots()[1].ControllerActive, "stale release and heartbeat cannot revoke a newer lease");

            await File.WriteAllTextAsync(Path.Combine(Storage(states[1]), "hold-release"), "hold");
            var deniedRequest = Guid.NewGuid().ToString("N");
            await viewers[2].Send("control.request", new() { ["requestId"] = deniedRequest });
            await viewers[1].Next("control.revoked");
            var denied = await viewers[2].Next("control.denied", 5000);
            check(denied.GetProperty("requestId").GetString() == deniedRequest, "control denial echoes the blocked focus request identity");
            check(supervisor.Snapshots().All(s => !s.ControllerActive), "missing neutral-release ACK denies focus and fails closed");
            File.Delete(Path.Combine(Storage(states[1]), "hold-release"));
            await viewers[1].Next("control.released");
            await viewers[2].Send("control.request"); await viewers[2].Next("control.granted");
            check(supervisor.Snapshots()[2].ControllerActive && supervisor.Snapshots().Count(s => s.ControllerActive) == 1, "a matching late ACK permits a subsequent explicit control request");

            await File.WriteAllTextAsync(Path.Combine(Storage(states[2]), "hold-release"), "hold");
            await viewers[2].DisposeAsync();
            await Until(() => !supervisor.Snapshots()[2].ViewerConnected, "viewer disconnect revocation");
            await Until(() => File.Exists(Path.Combine(Storage(states[2]), "revoked.json")), "disconnected viewer runtime revoke");
            await viewers[3].Send("control.request");
            check(!await viewers[3].Has("control.granted", 250), "viewer disconnect cannot bypass the old runtime neutral-release barrier");
            check(supervisor.Snapshots().All(s => !s.ControllerActive), "disconnected controller and pending target both remain inactive");
            File.Delete(Path.Combine(Storage(states[2]), "hold-release"));
            await viewers[3].Next("control.granted");
            check(supervisor.Snapshots()[3].ControllerActive && supervisor.Snapshots().Count(s => s.ControllerActive) == 1,
                "another viewer gains exclusive control only after the disconnected runtime release ACK");
            check((await Post("/api/streaming/stop", new { })).IsSuccessStatusCode, "group stop succeeds with viewers and active input");
        }
        finally
        {
            foreach (var viewer in viewers) await viewer.DisposeAsync();
            await supervisor.StopGroupAsync(CancellationToken.None);
        }
        await Until(() => !supervisor.IsBuildInUse(build.Id), "all group children exited and pins released");
        check(supervisor.Snapshots().All(s => s.Exited && !s.RuntimeConnected && !s.ViewerConnected && !s.ControllerActive), "group stop closes all four owned processes, sockets, and leases");
        check(supervisor.Snapshots().All(s => !s.MouseLockRequested), "group stop resets every cursor intent");
        check(supervisor.LatencySnapshot() is null && (await Post(latencyPath, new { enabled = true, minRttMs = 50, maxRttMs = 100 })).StatusCode == HttpStatusCode.Conflict,
            "Stopped KCP groups remove latency state and reject stale live changes");
        using (var file = new FileStream(build.ExecutablePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            check(file.CanWrite, "approved artifact becomes writable only after group cleanup");
        using (var lease = await supervisor.AcquireBuildDeletionLeaseAsync(build.Id, CancellationToken.None))
            check(lease is not null, "build deletion is allowed after every child releases ownership");
        foreach (var count in new[] { 2, 3 })
        {
            check((await Post("/api/streaming/start", Launch(count))).IsSuccessStatusCode, "typed Arena launch accepts " + count + " children");
            await Until(() => supervisor.Snapshots().Count == count && supervisor.Snapshots().All(s => s.GameReady), "group " + count + " readiness");
            await supervisor.StopGroupAsync(CancellationToken.None);
            await Until(() => !supervisor.IsBuildInUse(build.Id), "group " + count + " cleanup");
            check(supervisor.Snapshots().All(s => s.Exited), "group " + count + " cleanup completes");
        }
        await Post("/api/streaming/start", Launch(4));
        var reserved = supervisor.Snapshots();
        check(reserved.Skip(1).All(s => s.State == "Waiting" && !s.RuntimeConnected), "clients remain reserved while the host prepares");
        await supervisor.StopGroupAsync(CancellationToken.None);
        await Until(() => !supervisor.IsBuildInUse(build.Id), "cancelled startup cleanup");
        check(supervisor.Snapshots().All(s => s.Exited), "stop during host readiness cancels pending clients and releases all reservations");

        foreach (var invalidCursor in new[] { "{}", "{\"mouseLockRequested\":\"true\"}", "{\"mouseLockRequested\":1}", "{\"mouseLockRequested\":null}" })
        {
            await Post("/api/streaming/start", Launch(2));
            await Until(() => supervisor.Snapshots().All(s => s.GameReady), "invalid cursor fixture readiness");
            var host = supervisor.Snapshots()[0];
            await File.WriteAllTextAsync(Path.Combine(scratch, "App_Data", "Streaming", host.GroupId!, host.InstanceId!, "cursor-command.json"), invalidCursor);
            await Until(() => !supervisor.IsBuildInUse(build.Id), "invalid cursor runtime disconnect cleanup");
            check(supervisor.Snapshots().All(s => s.Exited && !s.MouseLockRequested), "invalid cursor intent is rejected and reset: " + invalidCursor);
        }

        await Post("/api/streaming/start", Launch(4));
        await Until(() => supervisor.Snapshots().All(s => s.GameReady), "failure fixture readiness");
        var failedPeer = supervisor.Snapshots()[2];
        await File.WriteAllTextAsync(Path.Combine(scratch, "App_Data", "Streaming", failedPeer.GroupId!, failedPeer.InstanceId!, "exit-now"), "exit");
        await Until(() => !supervisor.IsBuildInUse(build.Id), "unexpected peer exit cleanup");
        check(supervisor.Snapshots().All(s => s.Exited), "an unexpected Arena peer exit stops the entire owned group");

        await Post("/api/streaming/start", Launch(4));
        await Until(() => supervisor.Snapshots().All(s => s.GameReady), "shutdown fixture readiness");
        await supervisor.StopAsync(CancellationToken.None);
        await Until(() => !supervisor.IsBuildInUse(build.Id), "supervisor shutdown cleanup");
        check(supervisor.Snapshots().All(s => s.Exited), "backend shutdown stops every owned child and releases shared pins");
        check((await Post("/api/streaming/start", Launch(1))).StatusCode == HttpStatusCode.Conflict, "shutdown supervisor rejects further launches");
    }

    private static async Task Until(Func<bool> condition, string phase)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition()) { if (timeout.Elapsed > TimeSpan.FromSeconds(15)) throw new Exception("Fixture timeout: " + phase); await Task.Delay(25); }
    }

    private static bool ProcessAlive(int processId)
    {
        using var process = Process.GetProcessById(processId);
        return !process.HasExited;
    }
}

internal sealed class FixtureViewer : IAsyncDisposable
{
    private readonly ClientWebSocket socket = new();
    private readonly CancellationTokenSource closed = new();
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private readonly Channel<JsonElement> messages = Channel.CreateBounded<JsonElement>(128);
    private string sessionId = "", instanceId = "";
    private bool disposed;
    private Task reading = Task.CompletedTask, heartbeat = Task.CompletedTask;
    public static async Task<FixtureViewer> Connect(string address, JsonElement grant)
    {
        var viewer = new FixtureViewer { sessionId = grant.GetProperty("sessionId").GetString()!, instanceId = grant.GetProperty("instanceId").GetString()! };
        viewer.socket.Options.SetRequestHeader("Origin", address);
        var uri = new UriBuilder(address) { Scheme = "ws", Path = grant.GetProperty("viewerWebSocketPath").GetString() }.Uri;
        await viewer.socket.ConnectAsync(uri, viewer.closed.Token);
        viewer.reading = viewer.Read();
        await viewer.Send("viewer.auth", new() { ["token"] = grant.GetProperty("token").GetString() });
        viewer.heartbeat = Task.Run(async () =>
        {
            try { while (!viewer.closed.IsCancellationRequested) { await Task.Delay(750, viewer.closed.Token); await viewer.Send("viewer.heartbeat"); } }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
        });
        return viewer;
    }
    public async Task Send(string type, Dictionary<string, object?>? message = null)
    {
        message ??= []; message["type"] = type; message["sessionId"] = sessionId; message["instanceId"] = instanceId;
        await sendGate.WaitAsync(closed.Token);
        try { await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)), WebSocketMessageType.Text, true, closed.Token); }
        finally { sendGate.Release(); }
    }
    private async Task Read()
    {
        try
        {
            var buffer = new byte[65536];
            while (!closed.IsCancellationRequested)
            {
                using var payload = new MemoryStream(); WebSocketReceiveResult received;
                do { received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), closed.Token); if (received.MessageType == WebSocketMessageType.Close) return; payload.Write(buffer, 0, received.Count); } while (!received.EndOfMessage);
                using var message = JsonDocument.Parse(payload.ToArray());
                await messages.Writer.WriteAsync(message.RootElement.Clone(), closed.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
        finally { messages.Writer.TryComplete(); }
    }
    public async Task<JsonElement> Next(string type, int milliseconds = 5000)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(closed.Token); timeout.CancelAfter(milliseconds);
        try { while (true) { var message = await messages.Reader.ReadAsync(timeout.Token); if (message.GetProperty("type").GetString() == type) return message; } }
        catch (OperationCanceledException) { throw new TimeoutException("No fixture viewer message: " + type); }
    }
    public async Task<bool> Has(string type, int milliseconds)
    { try { await Next(type, milliseconds); return true; } catch (TimeoutException) { return false; } }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        closed.Cancel(); socket.Abort(); await Task.WhenAll(reading, heartbeat); socket.Dispose(); sendGate.Dispose(); closed.Dispose();
    }
}
