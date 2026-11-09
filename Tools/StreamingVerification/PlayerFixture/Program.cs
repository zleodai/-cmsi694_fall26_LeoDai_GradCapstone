using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

// Disposable native-process signaling fixture. It has no Unity, WebRTC or input implementation.
// The production supervisor still validates and launches its pinned temporary build normally.
using var config = JsonDocument.Parse(Environment.GetEnvironmentVariable("PLAYTESTOPS_STREAM_CONFIG") ?? throw new Exception("Missing config"));
var stream = config.RootElement;
var sessionId = stream.GetProperty("sessionId").GetString()!;
var instanceId = stream.GetProperty("instanceId").GetString()!;
var arenaText = Environment.GetEnvironmentVariable("PLAYTESTOPS_ARENA_CONFIG");
using var arenaDocument = string.IsNullOrEmpty(arenaText) ? null : JsonDocument.Parse(arenaText);
var arena = arenaDocument?.RootElement;
var role = arena?.GetProperty("role").GetString() ?? "single";
var storage = arena?.GetProperty("storagePath").GetString();
var launchedUtc = DateTimeOffset.UtcNow;
using var socket = new ClientWebSocket();
socket.Options.SetRequestHeader("Authorization", "Bearer " + stream.GetProperty("token").GetString());
var origin = new Uri(stream.GetProperty("origin").GetString()!);
var uri = new UriBuilder(origin) { Scheme = origin.Scheme == "https" ? "wss" : "ws", Path = $"/api/streaming/runtime/{sessionId}/{instanceId}" }.Uri;
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
using var sendGate = new SemaphoreSlim(1, 1);
async Task Send(string type, Dictionary<string, object?>? fields = null)
{
    fields ??= [];
    fields["type"] = type; fields["sessionId"] = sessionId; fields["instanceId"] = instanceId;
    await sendGate.WaitAsync(deadline.Token);
    try { await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fields)), WebSocketMessageType.Text, true, deadline.Token); }
    finally { sendGate.Release(); }
}
await socket.ConnectAsync(uri, deadline.Token);
await Send("runtime.ready", new() { ["width"] = stream.GetProperty("width").GetInt32(), ["height"] = stream.GetProperty("height").GetInt32() });
await Send("runtime.cursor", new() { ["mouseLockRequested"] = role == "host" });
if (arena is { } game)
{
    if (role == "host") await Task.Delay(400, deadline.Token);
    var readyUtc = DateTimeOffset.UtcNow;
    File.WriteAllText(Path.Combine(storage!, "fixture.json"), JsonSerializer.Serialize(new { pid = Environment.ProcessId, sessionId, instanceId, role,
        index = game.GetProperty("index").GetInt32(), playerCount = game.GetProperty("playerCount").GetInt32(),
        port = game.GetProperty("port").GetInt32(), address = game.GetProperty("address").GetString(),
        launchedUtc, readyUtc, storagePath = storage }));
    await Send("runtime.game-ready", new() { ["role"] = role });
}
var heartbeat = Task.Run(async () =>
{
    try
    {
        while (!deadline.IsCancellationRequested)
        {
            await Task.Delay(500, deadline.Token);
            if (storage is not null && File.Exists(Path.Combine(storage, "exit-now"))) Environment.Exit(17);
            if (storage is not null && File.Exists(Path.Combine(storage, "cursor-command.json")))
            {
                var path = Path.Combine(storage, "cursor-command.json");
                using var command = JsonDocument.Parse(await File.ReadAllTextAsync(path, deadline.Token));
                File.Delete(path);
                await Send("runtime.cursor", command.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone()));
            }
            await Send("runtime.heartbeat");
        }
    }
    catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
});
try
{
    var bytes = new byte[65536];
    while (!deadline.IsCancellationRequested)
    {
        using var payload = new MemoryStream();
        WebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(new ArraySegment<byte>(bytes), deadline.Token);
            if (received.MessageType == WebSocketMessageType.Close) return;
            payload.Write(bytes, 0, received.Count);
        } while (!received.EndOfMessage);
        using var document = JsonDocument.Parse(payload.ToArray());
        var message = document.RootElement;
        switch (message.GetProperty("type").GetString())
        {
            case "viewer.ready": await Send("signal.offer", new() { ["sdp"] = "fixture-offer:" + role + ":" + instanceId }); break;
            case "signal.answer": await Send("runtime.log", new() { ["level"] = "info", ["message"] = "fixture-answer:" + message.GetProperty("sdp").GetString() }); break;
            case "control.granted": await Send("control.ready", new() { ["leaseId"] = message.GetProperty("leaseId").GetString() }); break;
            case "control.revoked":
                var lease = message.GetProperty("leaseId").GetString();
                if (storage is not null)
                {
                    File.WriteAllText(Path.Combine(storage, "revoked.json"), JsonSerializer.Serialize(new { leaseId = lease }));
                    while (File.Exists(Path.Combine(storage, "hold-release"))) await Task.Delay(20, deadline.Token);
                }
                await Send("control.released", new() { ["leaseId"] = lease }); break;
            case "session.stop": return;
        }
    }
}
finally
{
    deadline.Cancel();
    await heartbeat;
}
