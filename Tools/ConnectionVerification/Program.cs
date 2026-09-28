using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

var root = Path.GetFullPath(args[0]);
var appRoot = args[1];
var db = Path.Combine(root, "bridge-check-" + Guid.NewGuid().ToString("N") + ".db");
var address = "http://127.0.0.1:5291";
var logs = new List<string>();
Process? server = null;
var checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
async Task Start()
{
    var start = new ProcessStartInfo("dotnet") { WorkingDirectory = appRoot, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add(Path.Combine(root, "build/PlaytestOps.Web.dll")); start.ArgumentList.Add("--urls"); start.ArgumentList.Add(address);
    start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
    start.Environment["ConnectionStrings__DefaultConnection"] = "Data Source=" + db;
    server = Process.Start(start)!;
    server.OutputDataReceived += (_, e) => { if (e.Data != null) lock(logs) logs.Add(e.Data); };
    server.ErrorDataReceived += (_, e) => { if (e.Data != null) lock(logs) logs.Add(e.Data); };
    server.BeginOutputReadLine(); server.BeginErrorReadLine();
    using var probe = new HttpClient();
    for (var i = 0; i < 100; i++)
    {
        if (server.HasExited) throw new Exception("Server exited during startup.");
        try { if ((await probe.GetAsync(address)).IsSuccessStatusCode) return; } catch (HttpRequestException) { }
        await Task.Delay(100);
    }
    throw new Exception("Server did not start.");
}
void Stop() { if (server is { HasExited: false }) { server.Kill(true); server.WaitForExit(); } server?.Dispose(); }
async Task<JsonElement> Receive(ClientWebSocket socket)
{
    using var timeout = new CancellationTokenSource(10000);
    using var stream = new MemoryStream(); var bytes = new byte[8192]; WebSocketReceiveResult result;
    do { result = await socket.ReceiveAsync(new ArraySegment<byte>(bytes), timeout.Token); stream.Write(bytes, 0, result.Count); } while (!result.EndOfMessage);
    return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
}
async Task<JsonElement> Send(ClientWebSocket socket, object payload)
{
    await socket.SendAsync(new ArraySegment<byte>(JsonSerializer.SerializeToUtf8Bytes(payload)), WebSocketMessageType.Text, true, CancellationToken.None);
    return await Receive(socket);
}
long Scalar(string sql)
{
    using var connection = new SqliteConnection("Data Source=" + db); connection.Open();
    using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar());
}
try
{
    await Start();
    using var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(address) };
    var html = await client.GetStringAsync("/Editors");
    var anti = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
    Check(anti.Length > 20, "pairing page supplies antiforgery token");
    Check((await client.PostAsync("/Editors?handler=PairingCode", new FormUrlEncodedContent([]))).StatusCode == HttpStatusCode.BadRequest, "pairing-code generation rejects missing antiforgery token");
    var generated = await client.PostAsync("/Editors?handler=PairingCode", new FormUrlEncodedContent(new Dictionary<string,string>{{"__RequestVerificationToken", anti}}));
    var code = Regex.Match(await generated.Content.ReadAsStringAsync(), "id=\"pairing-code\">([^<]+)").Groups[1].Value;
    Check(code.Length == 10, "local form creates a pairing code");
    var projectId = Guid.NewGuid().ToString("N");
    object Pair(string value) => new { code = value, projectId, projectName = "Connection checks", unityVersion = "6000.6.0f1" };
    Check((await client.PostAsJsonAsync("/api/editor/pair", Pair("invalid"))).StatusCode == HttpStatusCode.Unauthorized, "invalid code rejected");
    var paired = await client.PostAsJsonAsync("/api/editor/pair", Pair(code));
    Check(paired.IsSuccessStatusCode, "valid pairing accepted");
    var credential = JsonDocument.Parse(await paired.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
    Check((await client.PostAsJsonAsync("/api/editor/pair", Pair(code))).StatusCode == HttpStatusCode.Unauthorized, "pairing code cannot be reused");
    Check((await client.GetAsync("/api/editor/connect")).StatusCode == HttpStatusCode.Unauthorized, "socket endpoint requires credentials");
    using var initialSocket = new ClientWebSocket(); var socket = initialSocket; socket.Options.SetRequestHeader("Authorization", "Bearer " + credential);
    await socket.ConnectAsync(new Uri("ws://127.0.0.1:5291/api/editor/connect"), CancellationToken.None);
    Check((await Receive(socket)).GetProperty("type").GetString() == "session.ready", "authenticated socket receives ready handshake");
    var cases = new[] { new { uniqueName = "Checks/One", fullName = "Checks.One", name = "One", description = "Placeholder check", assembly = "Checks", mode = "EditMode", runState = "Runnable", skipReason = "" },
        new { uniqueName = "Checks/Two", fullName = "Checks.Two", name = "Two", description = "Placeholder check", assembly = "Checks", mode = "PlayMode", runState = "Explicit", skipReason = "Selected only" } };
    object Catalog(object tests) => new { type = "catalog.replace", requestId = "check", tests };
    Check((await Send(socket, Catalog(cases))).GetProperty("count").GetInt32() == 2, "catalog acknowledged");
    var id = Scalar("SELECT Id FROM Playtests WHERE UniqueName='Checks/One'");
    Scalar("UPDATE Playtests SET Status=3 WHERE UniqueName='Checks/One'; SELECT COUNT(*) FROM Playtests;");
    await Send(socket, Catalog(cases));
    Check(Scalar("SELECT COUNT(*) FROM Playtests") == 2, "repeated sync contains only Unity tests and creates no duplicates");
    Check(Scalar("SELECT Id FROM Playtests WHERE UniqueName='Checks/One'") == id && Scalar("SELECT Status FROM Playtests WHERE Id=" + id) == 3, "sync preserves internal IDs and previous status");
    Check((await Send(socket, Catalog(new[] { cases[0], cases[0] }))).GetProperty("type").GetString() == "error", "duplicate identities rejected");
    Check(Scalar("SELECT COUNT(*) FROM Playtests WHERE ProjectId IS NOT NULL AND IsAvailable=1") == 2, "invalid catalog does not mutate saved records");
    Check((await Send(socket, Catalog(Array.Empty<object>()))).GetProperty("unavailableCount").GetInt32() == 2, "empty successful discovery archives missing tests");
    await Send(socket, Catalog(cases));
    Check(Scalar("SELECT COUNT(*) FROM Playtests WHERE ProjectId IS NOT NULL AND IsAvailable=1") == 2, "returning tests restore availability");
    Check((await client.GetStringAsync("/")).Contains("Connection checks"), "dashboard renders SQLite project metadata");
    Check((await client.GetStringAsync("/Editors")).Contains("<strong>Connected</strong>"), "dashboard shows live connected state");
    // Exercise execution through the same antiforgery-protected form used by the browser.
    html = await client.GetStringAsync("/");
    anti = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
    async Task<HttpResponseMessage> Run(long testId, string? token = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/?handler=Run");
        request.Headers.Add("HX-Request", "true");
        request.Content = new FormUrlEncodedContent(new Dictionary<string,string> { ["testId"] = testId.ToString(), ["__RequestVerificationToken"] = token ?? anti });
        return await client.SendAsync(request);
    }
    object Update(string runId, int sequence, string state, string outcome = "", string message = "") => new {
        type = "run.update", runId, sequence, state, outcome, durationSeconds = 1.25, message, stackTrace = "at Checks.One()", output = "<script>alert('test')</script>" };
    Check((await Run(id, "invalid")).StatusCode == HttpStatusCode.BadRequest, "run form requires valid antiforgery token");
    await Run(id);
    async Task<JsonElement> NextCommand(ClientWebSocket target)
    {
        var probe = await Receive(target);
        Check(probe.GetProperty("type").GetString() == "run.probe", "dispatch asks Unity to confirm readiness");
        await target.SendAsync(new ArraySegment<byte>(JsonSerializer.SerializeToUtf8Bytes(new { type = "run.ready", requestId = probe.GetProperty("requestId").GetString() })), WebSocketMessageType.Text, true, CancellationToken.None);
        return await Receive(target);
    }
    var command = await NextCommand(socket);
    var runId = command.GetProperty("runId").GetString()!;
    Check(command.GetProperty("type").GetString() == "run.request" && command.GetProperty("uniqueName").GetString() == "Checks/One", "run request selects one exact case");
    Check((await (await Run(id)).Content.ReadAsStringAsync()).Contains("already queued or running"), "duplicate queued/running test request rejected");
    Check(Scalar("SELECT COUNT(*) FROM PlaytestRuns") == 1, "busy request does not create another attempt");
    Check((await Send(socket, Update(Guid.NewGuid().ToString("N"), 1, "Passed", "Passed"))).GetProperty("type").GetString() == "error", "unknown run result rejected");
    var otherCodePage = await client.PostAsync("/Editors?handler=PairingCode", new FormUrlEncodedContent(new Dictionary<string,string>{{"__RequestVerificationToken", anti}}));
    var otherCode = Regex.Match(await otherCodePage.Content.ReadAsStringAsync(), "id=\"pairing-code\">([^<]+)").Groups[1].Value;
    var otherPair = await client.PostAsJsonAsync("/api/editor/pair", new { code = otherCode, projectId = Guid.NewGuid().ToString("N"), projectName = "Other project", unityVersion = "6000.6.0f1" });
    var otherCredential = JsonDocument.Parse(await otherPair.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
    using (var otherSocket = new ClientWebSocket())
    {
        otherSocket.Options.SetRequestHeader("Authorization", "Bearer " + otherCredential);
        await otherSocket.ConnectAsync(new Uri("ws://127.0.0.1:5291/api/editor/connect"), CancellationToken.None);
        await Receive(otherSocket);
        Check((await Send(otherSocket, Update(runId, 1, "Passed", "Passed"))).GetProperty("type").GetString() == "error", "a different paired project cannot report this run");
        otherSocket.Abort();
    }
    Check((await Send(socket, Update(runId, 1, "Failed", "Failed", new string('x', 16001)))).GetProperty("type").GetString() == "error", "oversized result rejected without mutation");
    using (var remoteHost = new HttpRequestMessage(HttpMethod.Post, "/?handler=Run"))
    {
        remoteHost.Headers.Host = "example.com";
        remoteHost.Content = new FormUrlEncodedContent(new Dictionary<string,string> { ["testId"] = id.ToString(), ["__RequestVerificationToken"] = anti });
        Check((await client.SendAsync(remoteHost)).StatusCode == HttpStatusCode.Forbidden, "run request rejects non-local Host");
    }
    Check((await Send(socket, Update(runId, 1, "Running"))).GetProperty("type").GetString() == "run.accepted", "running event acknowledged");
    Check(Scalar("SELECT Status FROM Playtests WHERE Id=" + id) == 1, "Running persists to SQLite");
    Check((await client.GetStringAsync("/")).Contains("Latest attempt: Running"), "dashboard renders active run");
    Check((await Send(socket, Update(runId, 2, "Passed", "Failed"))).GetProperty("type").GetString() == "error", "contradictory Passed outcome rejected");
    await Send(socket, Update(runId, 3, "Passed", "Passed", "Success"));
    Check(Scalar("SELECT Status FROM Playtests WHERE Id=" + id) == 3, "Passed result persists");
    await Send(socket, Update(runId, 2, "Running"));
    await Send(socket, Update(runId, 4, "Failed", "Failed", "late"));
    Check(Scalar("SELECT Status FROM Playtests WHERE Id=" + id) == 3, "late callbacks cannot overwrite terminal result");
    html = await client.GetStringAsync("/");
    Check(html.Contains("&lt;script&gt;") && !html.Contains("<script>alert('test')</script>"), "test output is HTML encoded");
    await Run(id);
    runId = (await NextCommand(socket)).GetProperty("runId").GetString()!;
    await Send(socket, Update(runId, 1, "Failed", "Failed", "Expected 1 but was 0"));
    Check(Scalar("SELECT Status FROM Playtests WHERE Id=" + id) == 2 && Scalar("SELECT COUNT(*) FROM PlaytestRuns WHERE State='Passed'") == 1,
        "failed retry preserves previous passing attempt");
    html = await client.GetStringAsync("/");
    Check(html.Contains("Expected 1 but was 0") && html.Contains("at Checks.One()") && html.Contains("Previous attempts"), "dashboard renders result, stack trace, and history");
    await Send(socket, Catalog(cases));
    Check(Scalar("SELECT Status FROM Playtests WHERE Id=" + id) == 2, "discovery preserves remote result status");
    await Run(id);
    runId = (await NextCommand(socket)).GetProperty("runId").GetString()!;
    Scalar("UPDATE PlaytestRuns SET DispatchedAt='2000-01-01' WHERE Id='" + runId + "'; SELECT 1;");
    for (var wait = 0; wait < 30 && Scalar("SELECT COUNT(*) FROM PlaytestRuns WHERE State='Pending'") > 0; wait++) await Task.Delay(100);
    Check(Scalar("SELECT COUNT(*) FROM PlaytestRuns WHERE Outcome='TimedOut'") == 1, "unacknowledged request times out without replay");
    await Run(id);
    runId = (await NextCommand(socket)).GetProperty("runId").GetString()!;
    await Send(socket, Update(runId, 1, "Running"));
    using var resumedSocket = new ClientWebSocket();
    resumedSocket.Options.SetRequestHeader("Authorization", "Bearer " + credential);
    socket.Abort();
    await resumedSocket.ConnectAsync(new Uri("ws://127.0.0.1:5291/api/editor/connect"), CancellationToken.None);
    await Receive(resumedSocket);
    socket = resumedSocket;
    Check((await Send(socket, Update(runId, 2, "Passed", "Passed"))).GetProperty("type").GetString() == "run.accepted", "reconnected Editor can complete its active run");
    // Hold the readiness reply to exercise queue order and prove no eager dispatch.
    var secondId = Scalar("SELECT Id FROM Playtests WHERE UniqueName='Checks/Two'");
    var queuedPage = await (await Run(id)).Content.ReadAsStringAsync();
    Check(queuedPage.Contains("Added to queue") && queuedPage.Contains("Queued #1"), "enqueue response announces position one");
    var queueProbe = await Receive(socket);
    Check(queueProbe.GetProperty("type").GetString() == "run.probe", "queued run waits on a readiness probe");
    queuedPage = await (await Run(secondId)).Content.ReadAsStringAsync();
    Check(queuedPage.Contains("Queued #2") && Scalar("SELECT COUNT(*) FROM PlaytestRuns WHERE State='Queued'") == 2, "distinct test queues behind the first with position two");
    var beforeDuplicate = Scalar("SELECT COUNT(*) FROM PlaytestRuns");
    await Task.WhenAll(Run(secondId), Run(secondId));
    Check(Scalar("SELECT COUNT(*) FROM PlaytestRuns") == beforeDuplicate, "concurrent duplicate clicks create no duplicate attempts");
    var queuedRunId = "";
    using (var sql = new SqliteConnection("Data Source=" + db))
    {
        sql.Open(); using var select = sql.CreateCommand(); select.CommandText = "SELECT Id FROM PlaytestRuns WHERE State='Queued' AND PlaytestId=" + secondId;
        queuedRunId = (string)select.ExecuteScalar()!;
    }
    Check((await Send(socket, Update(queuedRunId, 1, "Passed", "Passed"))).GetProperty("type").GetString() == "error", "undispatched queued attempts cannot accept run results");
    Scalar("UPDATE PlaytestRuns SET RequestedAt='2000-01-01' WHERE State='Queued' AND PlaytestId=" + id + "; SELECT 1;");
    await socket.SendAsync(new ArraySegment<byte>(JsonSerializer.SerializeToUtf8Bytes(new { type = "run.ready", requestId = Guid.NewGuid().ToString("N") })), WebSocketMessageType.Text, true, CancellationToken.None);
    await Task.Delay(2200);
    Check(Scalar("SELECT COUNT(*) FROM PlaytestRuns WHERE State='Queued'") == 2, "stale readiness is ignored and queue wait does not consume execution timeout");
    await socket.SendAsync(new ArraySegment<byte>(JsonSerializer.SerializeToUtf8Bytes(new { type = "run.ready", requestId = queueProbe.GetProperty("requestId").GetString() })), WebSocketMessageType.Text, true, CancellationToken.None);
    var firstQueued = await Receive(socket);
    Check(firstQueued.GetProperty("uniqueName").GetString() == "Checks/One", "FIFO dispatches the oldest queued test first");
    var firstQueuedId = firstQueued.GetProperty("runId").GetString()!;
    await Send(socket, Update(firstQueuedId, 1, "Running"));
    Check(Scalar("SELECT COUNT(*) FROM PlaytestRuns WHERE State='Running'") == 1 && Scalar("SELECT COUNT(*) FROM PlaytestRuns WHERE State='Queued'") == 1, "one run executes while another waits");
    Check((await client.GetStringAsync("/")).Contains("Queued #1"), "waiting position advances after dispatch");
    await Send(socket, Update(firstQueuedId, 2, "Failed", "Failed", "Intentional queue check"));
    var nextProbe = await Receive(socket);
    Check(nextProbe.GetProperty("type").GetString() == "run.probe" && Scalar("SELECT COUNT(*) FROM PlaytestRuns WHERE State='Queued'") == 1, "failure advances queue but waits for fresh Unity readiness");
    // Replace the socket while waiting: an old probe must not authorize execution.
    using var queueSocket = new ClientWebSocket(); queueSocket.Options.SetRequestHeader("Authorization", "Bearer " + credential);
    socket.Abort();
    await queueSocket.ConnectAsync(new Uri("ws://127.0.0.1:5291/api/editor/connect"), CancellationToken.None);
    Check((await Receive(queueSocket)).GetProperty("type").GetString() == "session.ready", "reconnection always sends the handshake before queue messages");
    socket = queueSocket;
    var freshProbe = await Receive(socket);
    await socket.SendAsync(new ArraySegment<byte>(JsonSerializer.SerializeToUtf8Bytes(new { type = "run.ready", requestId = nextProbe.GetProperty("requestId").GetString() })), WebSocketMessageType.Text, true, CancellationToken.None);
    await Task.Delay(2200);
    Check(Scalar("SELECT COUNT(*) FROM PlaytestRuns WHERE State='Queued'") == 1, "reconnection invalidates the previous socket readiness");
    await socket.SendAsync(new ArraySegment<byte>(JsonSerializer.SerializeToUtf8Bytes(new { type = "run.ready", requestId = freshProbe.GetProperty("requestId").GetString() })), WebSocketMessageType.Text, true, CancellationToken.None);
    var secondQueued = await Receive(socket);
    Check(secondQueued.GetProperty("runId").GetString() == queuedRunId, "reconnection preserves the queued attempt without duplication");
    await Send(socket, Update(queuedRunId, 1, "Passed", "Passed"));
    Check(!(await client.GetStringAsync("/")).Contains("hx-trigger=\"every 3s\""), "dashboard polling stops when queue drains");
    // Missing tests are skipped without sending an execution request.
    await Run(secondId);
    await Receive(socket); // hold readiness, then remove this case from discovery
    await Send(socket, Catalog(new[] { cases[0] }));
    for (var wait = 0; wait < 40 && Scalar("SELECT COUNT(*) FROM PlaytestRuns WHERE State='Queued'") > 0; wait++) await Task.Delay(100);
    Check(Scalar("SELECT COUNT(*) FROM PlaytestRuns WHERE Outcome='Unavailable'") == 1, "unavailable queued tests fail clearly without execution");
    await Send(socket, Catalog(cases));
    await Run(id);
    runId = (await NextCommand(socket)).GetProperty("runId").GetString()!;
    await Send(socket, Update(runId, 1, "Running"));
    await Run(secondId);
    Check(Scalar("SELECT COUNT(*) FROM PlaytestRuns WHERE State='Queued'") == 1, "queue accepts another test while Unity is running");
    using var disconnect = new HttpRequestMessage(HttpMethod.Delete, "/api/editor/session"); disconnect.Headers.Authorization = new("Bearer", credential);
    Check((await client.SendAsync(disconnect)).StatusCode == HttpStatusCode.NoContent, "disconnect revokes session");
    using var revoked = new HttpRequestMessage(HttpMethod.Get, "/api/editor/connect"); revoked.Headers.Authorization = new("Bearer", credential);
    Check((await client.SendAsync(revoked)).StatusCode == HttpStatusCode.Unauthorized, "revoked credentials cannot reconnect");
    Check(WebUtility.HtmlDecode(await (await Run(id)).Content.ReadAsStringAsync()).Contains("Connect the project's Unity Editor"), "offline run request is rejected");
    Stop(); await Start();
    Check(Scalar("SELECT COUNT(*) FROM PlaytestRuns WHERE State IN ('Queued','Pending','Running')") == 0 && Scalar("SELECT COUNT(*) FROM PlaytestRuns WHERE Outcome='Interrupted'") >= 2,
        "server restart closes interrupted runs and preserves history");
    Check(Scalar("SELECT COUNT(*) FROM Playtests") == 2 && (await client.GetStringAsync("/")).Contains("Connection checks"), "database survives server restart without reseeding");
    Check((await client.GetStringAsync("/Editors")).Contains("No paired Editors yet."), "server restart clears ephemeral pairing sessions");
    Console.WriteLine($"SUCCESS: {checks} checks");
}
finally
{
    Stop();
    File.WriteAllLines(Path.Combine(root, "bridge-check.log"), logs);
}
