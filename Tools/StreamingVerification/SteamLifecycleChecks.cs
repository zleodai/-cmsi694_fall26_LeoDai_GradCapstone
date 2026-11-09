using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PlaytestOps.Web.Data;
using PlaytestOps.Web.Models;
using PlaytestOps.Web.Streaming;

/// <summary>Real loopback signaling with simulated owned processes; never launches Steam or an executable.</summary>
internal static class SteamLifecycleChecks
{
    private const string Lobby = "109775241000000001";
    private const string HostAccount = "76561197960265729";

    public static async Task Run(Action<bool, string> check)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "PlaytestOps-steam-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var provider = new FakeLauncher();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = scratch });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(connection));
        builder.Services.AddAntiforgery();
        builder.Services.AddBuildStreaming();
        builder.Services.AddSingleton<ISteamStreamingLauncher>(provider);
        await using var app = builder.Build();
        app.UseWebSockets();
        app.MapBuildStreaming();
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.EnsureCreatedAsync();
        var build = await CreateBuildAsync(app.Services, scratch);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var supervisor = app.Services.GetRequiredService<StreamingSupervisor>();
        var peers = new List<RuntimePeer>();

        async Task Start(int count = 2, Mode mode = Mode.Normal)
        {
            await Until(() => !supervisor.IsBuildInUse(build.Id), "previous group releases approved artifacts");
            foreach (var peer in peers) await peer.DisposeAsync();
            peers.Clear();
            provider.Reset(mode);
            var boxes = Enumerable.Range(1, count - 1).Select(index => "FixtureBox" + index).ToArray();
            var result = await supervisor.StartSessionAsync(new(build.Id, 640, 360, false, count, "arena-steam", boxes), address, CancellationToken.None);
            // A retained-cleanup error can be available synchronously and legitimately return 409.
            if (mode != Mode.RetainCleanupFailure && result.Error is not null)
                throw new InvalidOperationException("Fixture group did not start: " + result.Error);
            await Until(() => provider.Calls.Count >= 1, "host launch provider entry");
        }

        async Task<RuntimePeer> Connect(LaunchCall call)
        {
            var peer = await RuntimePeer.ConnectAsync(call);
            peers.Add(peer);
            return peer;
        }

        async Task<RuntimePeer> ReadyHost()
        {
            var peer = await Connect(provider.Calls[0]);
            await peer.SendReadyAsync(Lobby, HostAccount);
            await Until(() => supervisor.Snapshots()[0].GameReady, "host game readiness");
            return peer;
        }

        async Task ReadyClients(int count)
        {
            await Until(() => provider.Calls.Count == count, "joining players launch after host readiness");
            for (var index = 1; index < count; index++)
            {
                var peer = await Connect(provider.Calls[index]);
                await peer.SendReadyAsync(Lobby, (76561197960265729UL + (ulong)index).ToString());
            }
            await Until(() => supervisor.Snapshots().Count == count && supervisor.Snapshots().All(state => state.GameReady && state.RuntimeConnected), "all Steam fixture players ready");
        }

        async Task StopAndRelease()
        {
            await supervisor.StopGroupAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(12));
            await Until(() => !supervisor.IsBuildInUse(build.Id), "group releases its build pins");
        }

        try
        {
            await Start(4);
            await Task.Delay(100);
            check(provider.Calls.Count == 1, "Steam clients do not launch before host game readiness");
            await ReadyHost();
            await ReadyClients(4);
            check(supervisor.LatencySnapshot() is null && (await supervisor.ConfigureLatencyAsync(
                supervisor.Snapshots()[0].GroupId!, new(true, 50, 100), CancellationToken.None)).StatusCode == 409,
                "Steam groups reject KCP relay latency controls");
            var calls = provider.Calls;
            check(calls.Select(call => call.Stream.GetProperty("sessionId").GetString()).Distinct().Count() == 4,
                "Steam group assigns four distinct authenticated runtime identities");
            check(calls[0].Sandbox is null && calls.Skip(1).Select(call => call.Sandbox).SequenceEqual(new[] { "FixtureBox1", "FixtureBox2", "FixtureBox3" }),
                "Steam host uses normal environment and each joining player uses its selected sandbox");
            check(calls.Skip(1).All(call => call.Steam.GetProperty("lobbyId").GetString() == Lobby && call.Steam.GetProperty("hostSteamId").GetString() == HostAccount),
                "Steam clients receive the authenticated host lobby and account identities");
            await StopAndRelease();
            check(calls.All(call => call.Process.HasExited && call.Process.Disposed), "Steam group stop confirms all owned exits and disposes handles");
            using (File.Open(build.ExecutablePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            check(true, "Steam approved artifact can be opened exclusively after complete cleanup");

            await Start(mode: Mode.BlockLaunch);
            var pending = provider.Calls[0];
            await StopAndRelease();
            check(pending.CancellationObserved && provider.Calls.Count == 1, "Stop cancels pending Steam launch without launching clients");

            await Start(mode: Mode.ReadyThenFail);
            await ReadyHost();
            var failedHost = provider.Calls[0];
            check(provider.Calls.Count == 1, "Steam provider ownership must complete even after host runtime reports readiness");
            failedHost.Process.Exit(1);
            failedHost.LaunchDecision.TrySetException(new InvalidOperationException("Fixture ownership adoption failed."));
            await Until(() => !supervisor.IsBuildInUse(build.Id), "failed ready host cleanup");
            check(provider.Calls.Count == 1, "A ready host that fails process adoption never launches joining players");

            foreach (var invalid in new[]
            {
                (LobbyId: Lobby, SteamId: HostAccount, Error: "distinct"),
                (LobbyId: "109775241000000002", SteamId: "76561197960265730", Error: "lobby"),
                (LobbyId: Lobby, SteamId: "0" + HostAccount, Error: "identit")
            })
            {
                await Start();
                await ReadyHost();
                await Until(() => provider.Calls.Count == 2, "invalid readiness client launch");
                var invalidPeer = await Connect(provider.Calls[1]);
                await invalidPeer.SendReadyAsync(invalid.LobbyId, invalid.SteamId);
                await Until(() => !supervisor.IsBuildInUse(build.Id), "invalid Steam readiness stops the group");
                var client = supervisor.Snapshots()[1];
                check(!client.GameReady && client.Logs.Any(log => log.Message.Contains(invalid.Error, StringComparison.OrdinalIgnoreCase)),
                    "Invalid Steam readiness retains its actionable " + invalid.Error + " reason");
                check(provider.Calls.All(call => call.Process.HasExited), "Invalid Steam readiness cleans up every owned player");
            }

            await Start(3);
            await ReadyHost();
            await ReadyClients(3);
            provider.Calls[0].Process.Exit(9);
            await Until(() => !supervisor.IsBuildInUse(build.Id), "unexpected host exit stops peers without observer self-await");
            check(provider.Calls.All(call => call.Process.HasExited && call.Process.Disposed),
                "Unexpected Steam host exit completes all observers without self-await deadlock");

            await Start(mode: Mode.RetainCleanupFailure);
            var retained = provider.Calls[0];
            await Until(() => retained.Process.KillAttempts > 0, "retained process cleanup attempt");
            check(supervisor.IsBuildInUse(build.Id) && !retained.Process.Disposed,
                "Unconfirmed Steam launch cleanup retains the process and build reservation");
            bool writeBlocked = false;
            try { using var held = File.Open(build.ExecutablePath, FileMode.Open, FileAccess.Write, FileShare.None); }
            catch (IOException) { writeBlocked = true; }
            check(writeBlocked, "Unconfirmed Steam launch cleanup keeps approved executable pinned");
            var retry = await supervisor.StartSessionAsync(new(build.Id, 640, 360, false, 2, "arena-steam", ["FixtureBox1"]), address, CancellationToken.None);
            check(retry.StatusCode == 409 && provider.Calls.Count == 1, "A retained Steam process prevents another group from starting");
            retained.Process.Exit(0);
            await Until(() => !supervisor.IsBuildInUse(build.Id), "retained process eventually exits");
            check(retained.Process.Disposed, "Confirmed exit finally disposes the retained Steam process");
        }
        finally
        {
            foreach (var call in provider.Calls)
            {
                call.Process.Exit(0);
                call.LaunchDecision.TrySetCanceled();
            }
            foreach (var peer in peers) await peer.DisposeAsync();
            await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
            // The scratch tree is fixture-owned and uniquely created above.
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static async Task<UnityBuild> CreateBuildAsync(IServiceProvider services, string scratch)
    {
        var build = new UnityBuild { RequestKey = Guid.NewGuid().ToString("N"), ProjectId = "steam-lifecycle-fixture",
            ProjectName = "Steam lifecycle fixture", State = "Succeeded", Revision = "fixture", ActiveTarget = "StandaloneWindows64" };
        build.OutputPath = Path.Combine(scratch, "Builds", "PlaytestOps", build.Id);
        build.ExecutablePath = Path.Combine(build.OutputPath, "Player.exe");
        build.ManifestPath = Path.Combine(build.OutputPath, "build-manifest.json");
        Directory.CreateDirectory(Path.Combine(build.OutputPath, "Player_Data", "Plugins", "x86_64"));
        await File.WriteAllTextAsync(build.ExecutablePath, "Approved simulated executable; never run.");
        await File.WriteAllTextAsync(Path.Combine(build.OutputPath, "steam_appid.txt"), "480");
        await File.WriteAllTextAsync(Path.Combine(build.OutputPath, "Player_Data", "Plugins", "x86_64", "steam_api64.dll"), "Approved simulated native library; never loaded.");
        var buildGuid = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(Path.Combine(build.OutputPath, StreamingProtocol.CapabilityFile), JsonSerializer.Serialize(new {
            protocolVersion = 1, packageVersion = "0.1.0", buildGuid, capture = "framebuffer", input = "input-system", transport = "webrtc",
            launchProfiles = new[] { "arena-steam" }, steamAppId = 480, steamAdapter = "steam-arena-v1" }));
        var artifacts = Directory.GetFiles(build.OutputPath, "*", SearchOption.AllDirectories).Select(path => new {
            path = Path.GetRelativePath(build.OutputPath, path).Replace('\\', '/'), bytes = new FileInfo(path).Length,
            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) }).ToArray();
        await File.WriteAllTextAsync(build.ManifestPath, JsonSerializer.Serialize(new { protocolVersion = 1, buildId = build.Id,
            result = "Succeeded", totalErrors = 0, artifactListTruncated = false, activeTarget = build.ActiveTarget,
            revision = build.Revision, buildGuid, artifacts }));
        await using var scope = services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        database.UnityBuilds.Add(build);
        await database.SaveChangesAsync();
        return build;
    }

    private static async Task Until(Func<bool> predicate, string description)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { while (!predicate()) await Task.Delay(20, timeout.Token); }
        catch (OperationCanceledException) { throw new TimeoutException(description); }
    }

    private enum Mode { Normal, BlockLaunch, ReadyThenFail, RetainCleanupFailure }

    /// <summary>Deterministic process-adoption seam with explicit failure/cancellation scenarios.</summary>
    private sealed class FakeLauncher : ISteamStreamingLauncher
    {
        private readonly ConcurrentQueue<LaunchCall> calls = new();
        private Mode mode;
        public IReadOnlyList<LaunchCall> Calls => calls.ToArray();
        public void Reset(Mode nextMode) { calls.Clear(); mode = nextMode; }
        public SteamEnvironmentInspection InspectEnvironments() => new([new("FixtureBox1", true)], true);
        public Task ValidateEnvironmentsAsync(IReadOnlyList<string> sandboxNames, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }

        public async Task<IStreamingOwnedProcess> LaunchAsync(string executablePath, string workingDirectory,
            string storagePath, string? sandboxName, IReadOnlyList<string> arguments,
            string streamConfigJson, string steamConfigJson, CancellationToken cancellationToken)
        {
            using var streamDocument = JsonDocument.Parse(streamConfigJson);
            using var steamDocument = JsonDocument.Parse(steamConfigJson);
            var call = new LaunchCall(streamDocument.RootElement.Clone(), steamDocument.RootElement.Clone(), sandboxName);
            calls.Enqueue(call);
            if (mode == Mode.RetainCleanupFailure)
            {
                call.Process.RefuseKill = true;
                throw new SteamLaunchCleanupException("Fixture startup cleanup could not confirm exit.", call.Process, new IOException("fixture cleanup failure"));
            }
            if (mode is Mode.BlockLaunch or Mode.ReadyThenFail)
            {
                try { return await call.LaunchDecision.Task.WaitAsync(cancellationToken); }
                catch (OperationCanceledException)
                {
                    call.CancellationObserved = true;
                    call.Process.Exit(0);
                    call.Process.Dispose();
                    throw;
                }
                catch { call.Process.Dispose(); throw; }
            }
            return call.Process;
        }
    }

    /// <summary>Captured immutable launch identities and the fake process owned by that launch.</summary>
    private sealed class LaunchCall(JsonElement stream, JsonElement steam, string? sandbox)
    {
        public JsonElement Stream { get; } = stream;
        public JsonElement Steam { get; } = steam;
        public string? Sandbox { get; } = sandbox;
        public FakeProcess Process { get; } = new();
        public TaskCompletionSource<IStreamingOwnedProcess> LaunchDecision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool CancellationObserved;
    }

    /// <summary>Process lifetime simulation; the only exit sources are fixture events and stop messages.</summary>
    private sealed class FakeProcess : IStreamingOwnedProcess
    {
        private static int nextId = 900000;
        private readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int exitCode;
        private int exitPublished;
        public int Id { get; } = Interlocked.Increment(ref nextId);
        public bool HasExited => exited.Task.IsCompleted;
        public int? ExitCode => HasExited ? exitCode : null;
        public StreamReader? StandardOutput => null;
        public StreamReader? StandardError => null;
        public volatile bool Disposed;
        public volatile bool RefuseKill;
        public int KillAttempts;
        public Task WaitForExitAsync(CancellationToken cancellationToken = default) => exited.Task.WaitAsync(cancellationToken);
        public void Exit(int code)
        {
            if (Interlocked.Exchange(ref exitPublished, 1) != 0) return;
            exitCode = code;
            exited.TrySetResult();
        }
        public void Kill()
        {
            Interlocked.Increment(ref KillAttempts);
            if (RefuseKill) throw new InvalidOperationException("Fixture termination denied.");
            Exit(137);
        }
        public void Dispose() => Disposed = true;
    }

    /// <summary>Authenticated runtime peer; responds to real supervisor stop signaling with a fake process exit.</summary>
    private sealed class RuntimePeer(LaunchCall call, ClientWebSocket socket) : IAsyncDisposable
    {
        private readonly CancellationTokenSource lifetime = new();
        private Task receive = Task.CompletedTask;
        public static async Task<RuntimePeer> ConnectAsync(LaunchCall call)
        {
            var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", "Bearer " + call.Stream.GetProperty("token").GetString());
            var uri = new UriBuilder(call.Stream.GetProperty("origin").GetString()!) { Scheme = "ws",
                Path = "/api/streaming/runtime/" + call.Stream.GetProperty("sessionId").GetString() + "/" + call.Stream.GetProperty("instanceId").GetString() };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await socket.ConnectAsync(uri.Uri, timeout.Token);
            var peer = new RuntimePeer(call, socket);
            peer.receive = peer.ReceiveAsync();
            return peer;
        }
        public async Task SendReadyAsync(string lobbyId, string steamId)
        {
            await SendAsync(new { type = "runtime.ready", sessionId = call.Stream.GetProperty("sessionId").GetString(),
                instanceId = call.Stream.GetProperty("instanceId").GetString(), width = 640, height = 360 });
            await SendAsync(new { type = "runtime.game-ready", sessionId = call.Stream.GetProperty("sessionId").GetString(),
                instanceId = call.Stream.GetProperty("instanceId").GetString(), role = call.Steam.GetProperty("role").GetString(), lobbyId, steamId });
        }
        private async Task SendAsync(object value) => await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)), WebSocketMessageType.Text, true, lifetime.Token);
        private async Task ReceiveAsync()
        {
            var buffer = new byte[8192];
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    using var message = new MemoryStream();
                    WebSocketReceiveResult received;
                    do
                    {
                        received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), lifetime.Token);
                        if (received.MessageType == WebSocketMessageType.Close) return;
                        message.Write(buffer, 0, received.Count);
                    } while (!received.EndOfMessage);
                    using var document = JsonDocument.Parse(message.ToArray());
                    if (document.RootElement.GetProperty("type").GetString() == "session.stop") call.Process.Exit(0);
                }
            }
            catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
            finally { call.Process.Exit(2); }
        }
        public async ValueTask DisposeAsync()
        {
            lifetime.Cancel();
            socket.Abort();
            await receive;
            socket.Dispose();
            lifetime.Dispose();
        }
    }
}
