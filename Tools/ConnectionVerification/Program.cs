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
string TextScalar(string sql)
{
    using var connection = new SqliteConnection("Data Source=" + db); connection.Open();
    using var command = connection.CreateCommand(); command.CommandText = sql; return (string)command.ExecuteScalar()!;
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
    // Structured log snapshots use a separate attempt and only this disposable database.
    await Run(id);
    var loggedRunId = (await NextCommand(socket)).GetProperty("runId").GetString()!;
    object LogEntry(int sequence, string level, string message, string stackTrace = "", string timestampUtc = "2026-10-04T20:00:00.0000000Z") =>
        new { sequence, timestampUtc, level, message, stackTrace };
    object LogUpdate(int sequence, object? entries, string state = "Running", string outcome = "", bool logsTruncated = false, int droppedLogCount = 0) =>
        new { type = "run.update", runId = loggedRunId, sequence, state, outcome, durationSeconds = 1.25,
            message = "Log verification", stackTrace = "", output = "Legacy output remains available", logs = entries, logsTruncated, droppedLogCount };
    var initialLogs = new[] {
        LogEntry(1, "Debug", "LOG-DEBUG <script>alert('log')</script> & debug", "<debug-stack>"),
        LogEntry(2, "Warning", "LOG-WARNING connection warning"),
        LogEntry(3, "Error", "LOG-ERROR expected smoke error", "at <error-stack>") };
    using (var otherSocket = new ClientWebSocket())
    {
        otherSocket.Options.SetRequestHeader("Authorization", "Bearer " + otherCredential);
        await otherSocket.ConnectAsync(new Uri("ws://127.0.0.1:5291/api/editor/connect"), CancellationToken.None);
        await Receive(otherSocket);
        Check((await Send(otherSocket, LogUpdate(1, initialLogs))).GetProperty("type").GetString() == "error" &&
            Scalar("SELECT Sequence FROM PlaytestRuns WHERE Id='" + loggedRunId + "'") == 0,
            "foreign Editor cannot inject structured logs into another project's attempt");
        otherSocket.Abort();
    }
    Check((await Send(socket, LogUpdate(1, initialLogs))).GetProperty("type").GetString() == "run.accepted", "structured debug, warning, and error snapshot acknowledged");
    var initialLogJson = TextScalar("SELECT LogsJson FROM PlaytestRuns WHERE Id='" + loggedRunId + "'");
    using (var persisted = JsonDocument.Parse(initialLogJson))
    {
        Check(persisted.RootElement.GetArrayLength() == 3 && persisted.RootElement[0].GetProperty("level").GetString() == "Debug" &&
            persisted.RootElement[1].GetProperty("level").GetString() == "Warning" && persisted.RootElement[2].GetProperty("level").GetString() == "Error",
            "SQLite persists ordered structured severity entries");
        Check(persisted.RootElement[0].GetProperty("message").GetString() == "LOG-DEBUG <script>alert('log')</script> & debug" &&
            persisted.RootElement[2].GetProperty("stackTrace").GetString() == "at <error-stack>", "SQLite preserves full log text and stack traces");
    }
    var logRoute = "/Runs/" + loggedRunId;
    html = await client.GetStringAsync(logRoute);
    Check(html.Contains("LOG-DEBUG") && html.Contains("LOG-WARNING") && html.Contains("LOG-ERROR") &&
        html.IndexOf("LOG-DEBUG", StringComparison.Ordinal) < html.IndexOf("LOG-WARNING", StringComparison.Ordinal) &&
        html.IndexOf("LOG-WARNING", StringComparison.Ordinal) < html.IndexOf("LOG-ERROR", StringComparison.Ordinal), "run detail page renders logs chronologically");
    Check(html.Contains("&lt;script&gt;") && html.Contains("&lt;error-stack&gt;") && !html.Contains("<script>alert('log')</script>"), "run detail page HTML-encodes log messages and stack traces");
    Check(html.Contains("hx-trigger=\"every 3s\""), "active run detail page polls for new log snapshots");
    var partial = await client.GetStringAsync(logRoute + "?handler=Details&level=all");
    Check(partial.Contains("LOG-DEBUG") && partial.Contains("LOG-WARNING") && partial.Contains("LOG-ERROR"), "detail polling handler returns the full current log snapshot");
    var debugPage = await client.GetStringAsync(logRoute + "?level=debug");
    Check(debugPage.Contains("LOG-DEBUG") && !debugPage.Contains("LOG-WARNING") && !debugPage.Contains("LOG-ERROR"), "debug filter excludes warning and error entries");
    var warningPage = await client.GetStringAsync(logRoute + "?level=warning");
    Check(!warningPage.Contains("LOG-DEBUG") && warningPage.Contains("LOG-WARNING") && !warningPage.Contains("LOG-ERROR"), "warning filter excludes other severity entries");
    Check((await client.GetAsync("/Runs/" + Guid.NewGuid().ToString("N"))).StatusCode == HttpStatusCode.NotFound &&
        (await client.GetAsync("/Runs/not-a-run-id")).StatusCode == HttpStatusCode.NotFound, "unknown and malformed run detail routes return 404");
    async Task InvalidLogSnapshot(object? entries, string reason, bool truncated = false, int dropped = 0)
    {
        Check((await Send(socket, LogUpdate(2, entries, logsTruncated: truncated, droppedLogCount: dropped))).GetProperty("type").GetString() == "error" &&
            Scalar("SELECT Sequence FROM PlaytestRuns WHERE Id='" + loggedRunId + "'") == 1 &&
            TextScalar("SELECT LogsJson FROM PlaytestRuns WHERE Id='" + loggedRunId + "'") == initialLogJson,
            reason + " rejected without mutating accepted logs or run sequence");
    }
    await InvalidLogSnapshot(new[] { LogEntry(1, "Debug", "Changed prefix"), initialLogs[1], initialLogs[2] }, "rewritten immutable log prefix");
    await InvalidLogSnapshot(initialLogs.Take(2).ToArray(), "shrinking log snapshot");
    await InvalidLogSnapshot(initialLogs.Concat(new[] { LogEntry(5, "Debug", "Gap") }).ToArray(), "non-contiguous log sequence");
    await InvalidLogSnapshot(initialLogs.Concat(new[] { LogEntry(4, "Info", "Invalid severity") }).ToArray(), "unknown log severity");
    await InvalidLogSnapshot(initialLogs.Concat(new object[] { null! }).ToArray(), "null log entry");
    await InvalidLogSnapshot(initialLogs.Concat(new[] { LogEntry(4, "Debug", "Invalid time", timestampUtc: "not-a-timestamp") }).ToArray(), "malformed UTC timestamp");
    await InvalidLogSnapshot(initialLogs.Concat(new[] { LogEntry(4, "Debug", "Non-UTC time", timestampUtc: "2026-10-04T20:00:00+01:00") }).ToArray(), "non-UTC timestamp");
    await InvalidLogSnapshot(initialLogs.Concat(new[] { LogEntry(4, "Debug", new string('m', 16001)) }).ToArray(), "oversized log message");
    await InvalidLogSnapshot(initialLogs.Concat(new[] { LogEntry(4, "Debug", "Oversized stack", new string('s', 16001)) }).ToArray(), "oversized log stack trace");
    await InvalidLogSnapshot(initialLogs.Concat(Enumerable.Range(4, 13).Select(n => LogEntry(n, "Debug", new string('a', 15000)))).ToArray(), "oversized aggregate log text");
    await InvalidLogSnapshot(initialLogs.Concat(Enumerable.Range(4, 998).Select(n => LogEntry(n, "Debug", "entry"))).ToArray(), "more than 1000 log entries");
    await InvalidLogSnapshot(initialLogs, "dropped count without truncation flag", dropped: 1);
    await InvalidLogSnapshot(initialLogs, "negative dropped count", truncated: true, dropped: -1);
    await InvalidLogSnapshot(null, "omitted logs with non-default truncation metadata", truncated: true);
    await Send(socket, LogUpdate(1, new[] { LogEntry(1, "Debug", "Invalid stale rewrite") }));
    Check(TextScalar("SELECT LogsJson FROM PlaytestRuns WHERE Id='" + loggedRunId + "'") == initialLogJson &&
        Scalar("SELECT Sequence FROM PlaytestRuns WHERE Id='" + loggedRunId + "'") == 1, "replayed log update cannot overwrite accepted snapshot");
    await Send(socket, Update(loggedRunId, 2, "Running"));
    Check(TextScalar("SELECT LogsJson FROM PlaytestRuns WHERE Id='" + loggedRunId + "'") == initialLogJson &&
        Scalar("SELECT Sequence FROM PlaytestRuns WHERE Id='" + loggedRunId + "'") == 2, "legacy update omitting structured logs preserves the accepted snapshot");
    var completeLogs = initialLogs.Concat(new[] {
        LogEntry(4, "Assert", "LOG-ASSERT assertion diagnostic"), LogEntry(5, "Exception", "LOG-EXCEPTION exception diagnostic", "at exception stack") }).ToArray();
    Check((await Send(socket, LogUpdate(3, completeLogs, logsTruncated: true, droppedLogCount: 7))).GetProperty("type").GetString() == "run.accepted" &&
        Scalar("SELECT LogsTruncated FROM PlaytestRuns WHERE Id='" + loggedRunId + "'") == 1 &&
        Scalar("SELECT DroppedLogCount FROM PlaytestRuns WHERE Id='" + loggedRunId + "'") == 7, "extended snapshot persists assert, exception, and truncation metadata");
    var completeLogJson = TextScalar("SELECT LogsJson FROM PlaytestRuns WHERE Id='" + loggedRunId + "'");
    Check((await Send(socket, LogUpdate(4, completeLogs))).GetProperty("type").GetString() == "error" &&
        Scalar("SELECT Sequence FROM PlaytestRuns WHERE Id='" + loggedRunId + "'") == 3 &&
        TextScalar("SELECT LogsJson FROM PlaytestRuns WHERE Id='" + loggedRunId + "'") == completeLogJson,
        "increasing update cannot clear a previously recorded capture truncation");
    Check((await Send(socket, LogUpdate(4, completeLogs, logsTruncated: true, droppedLogCount: 6))).GetProperty("type").GetString() == "error" &&
        Scalar("SELECT DroppedLogCount FROM PlaytestRuns WHERE Id='" + loggedRunId + "'") == 7 &&
        Scalar("SELECT Sequence FROM PlaytestRuns WHERE Id='" + loggedRunId + "'") == 3,
        "increasing update cannot decrease previously reported omitted messages");
    var errorPage = await client.GetStringAsync(logRoute + "?level=error");
    Check(!errorPage.Contains("LOG-DEBUG") && !errorPage.Contains("LOG-WARNING") && errorPage.Contains("LOG-ERROR") &&
        errorPage.Contains("LOG-ASSERT") && errorPage.Contains("LOG-EXCEPTION"), "error filter includes Error, Assert, and Exception entries");
    Check(errorPage.Contains("7 messages were omitted") && errorPage.Contains("stored log below is incomplete"), "detail page warns that retained logs are truncated");
    await Send(socket, LogUpdate(2, initialLogs));
    Check(TextScalar("SELECT LogsJson FROM PlaytestRuns WHERE Id='" + loggedRunId + "'") == completeLogJson &&
        Scalar("SELECT DroppedLogCount FROM PlaytestRuns WHERE Id='" + loggedRunId + "'") == 7, "stale smaller snapshot cannot erase logs or truncation metadata");
    Check((await Send(socket, LogUpdate(4, completeLogs, state: "Passed", outcome: "Passed", logsTruncated: true, droppedLogCount: 7))).GetProperty("type").GetString() == "run.accepted",
        "terminal result retains its full structured log snapshot");
    await Send(socket, LogUpdate(5, Array.Empty<object>(), state: "Failed", outcome: "Failed"));
    Check(TextScalar("SELECT LogsJson FROM PlaytestRuns WHERE Id='" + loggedRunId + "'") == completeLogJson &&
        TextScalar("SELECT State FROM PlaytestRuns WHERE Id='" + loggedRunId + "'") == "Passed",
        "late terminal callback cannot overwrite captured logs or passing outcome");
    html = await client.GetStringAsync(logRoute);
    Check(html.Contains("LOG-EXCEPTION") && !html.Contains("hx-trigger=\"every 3s\""), "terminal run detail page keeps logs and stops polling");
    partial = await client.GetStringAsync(logRoute + "?handler=Details&level=warning");
    Check(partial.Contains("LOG-WARNING") && !partial.Contains("LOG-DEBUG") && !partial.Contains("LOG-ERROR") &&
        !partial.Contains("hx-trigger=\"every 3s\""), "terminal detail handler respects filters without continued polling");
    html = await client.GetStringAsync("/");
    Check(html.Contains(logRoute), "dashboard latest attempt links to its full log page");
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
    Check((await client.GetStringAsync("/")).Contains(logRoute), "dashboard previous-attempt history links to persisted full logs");
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
    html = await client.GetStringAsync("/");
    Check(!html.Contains("data-test-id=\"" + id + "\"") && !html.Contains("data-test-id=\"" + secondId + "\"") &&
        Scalar("SELECT COUNT(*) FROM Playtests") == 2, "revocation hides the disconnected catalog without deleting its saved tests");
    Stop(); await Start();
    Check(Scalar("SELECT COUNT(*) FROM PlaytestRuns WHERE State IN ('Queued','Pending','Running')") == 0 && Scalar("SELECT COUNT(*) FROM PlaytestRuns WHERE Outcome='Interrupted'") >= 2,
        "server restart closes interrupted runs and preserves history");
    Check(Scalar("SELECT COUNT(*) FROM Playtests") == 2 && !(await client.GetStringAsync("/")).Contains("Connection checks"), "database survives server restart without reseeding while disconnected catalogs remain hidden");
    Check((await client.GetStringAsync("/Editors")).Contains("No paired Editors yet."), "server restart clears ephemeral pairing sessions");
    Check(TextScalar("SELECT LogsJson FROM PlaytestRuns WHERE Id='" + loggedRunId + "'") == completeLogJson &&
        Scalar("SELECT LogsTruncated FROM PlaytestRuns WHERE Id='" + loggedRunId + "'") == 1 &&
        Scalar("SELECT DroppedLogCount FROM PlaytestRuns WHERE Id='" + loggedRunId + "'") == 7, "structured logs and truncation metadata survive server restart");
    html = await client.GetStringAsync(logRoute);
    Check(html.Contains("LOG-DEBUG") && html.Contains("LOG-WARNING") && html.Contains("LOG-EXCEPTION") &&
        !html.Contains("hx-trigger=\"every 3s\""), "historical full log page remains readable after restart");
    Check(!(await client.GetStringAsync("/")).Contains(logRoute), "dashboard hides previous-attempt links until their Editor reconnects");
    // Visibility depends on an open socket, not merely a pairing or saved project.
    string RowMarker(long testId) => "data-test-id=\"" + testId + "\"";
    async Task<string> PairVisibilityProject(string visibilityProjectId, string projectName)
    {
        var editors = await client.GetStringAsync("/Editors");
        var visibilityAnti = Regex.Match(editors, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var generatedPage = await client.PostAsync("/Editors?handler=PairingCode", new FormUrlEncodedContent(new Dictionary<string,string>{{"__RequestVerificationToken", visibilityAnti}}));
        generatedPage.EnsureSuccessStatusCode();
        var visibilityCode = Regex.Match(await generatedPage.Content.ReadAsStringAsync(), "id=\"pairing-code\">([^<]+)").Groups[1].Value;
        var result = await client.PostAsJsonAsync("/api/editor/pair", new { code = visibilityCode, projectId = visibilityProjectId, projectName, unityVersion = "6000.6.0f1" });
        result.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await result.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
    }
    async Task<ClientWebSocket> ConnectVisibilityProject(string visibilityCredential)
    {
        var visibilitySocket = new ClientWebSocket();
        visibilitySocket.Options.SetRequestHeader("Authorization", "Bearer " + visibilityCredential);
        await visibilitySocket.ConnectAsync(new Uri("ws://127.0.0.1:5291/api/editor/connect"), CancellationToken.None);
        var ready = await Receive(visibilitySocket);
        if (ready.GetProperty("type").GetString() != "session.ready") throw new Exception("Visibility fixture socket did not receive its handshake.");
        return visibilitySocket;
    }
    async Task<string> WaitForView(Func<string, bool> expected)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var view = await client.GetStringAsync("/");
            if (expected(view)) return view;
            await Task.Delay(100);
        }
        throw new Exception("Dashboard did not reflect the socket visibility change.");
    }
    html = await client.GetStringAsync("/");
    Check(html.Contains("No Unity Editor connected.") && !html.Contains("No tests discovered."), "no connected Editor has a distinct dashboard empty state");
    var visibilityCredential = await PairVisibilityProject(projectId, "Connection checks");
    Check((await client.GetStringAsync("/Editors")).Contains("<strong>Disconnected</strong>") &&
        !(await client.GetStringAsync("/")).Contains(RowMarker(id)), "paired Editor without an active socket does not expose its saved catalog");
    var storedStatus = Scalar("SELECT Status FROM Playtests WHERE Id=" + id);
    var storedRunCount = Scalar("SELECT COUNT(*) FROM PlaytestRuns");
    using var visibilitySocket = await ConnectVisibilityProject(visibilityCredential);
    html = await client.GetStringAsync("/");
    Check(html.Contains(RowMarker(id)) && html.Contains(RowMarker(secondId)) && html.Contains(logRoute) &&
        Scalar("SELECT Status FROM Playtests WHERE Id=" + id) == storedStatus && Scalar("SELECT COUNT(*) FROM PlaytestRuns") == storedRunCount,
        "reconnecting restores saved tests and history without rediscovery or changing outcomes");
    using (var fragment = await client.GetAsync("/?handler=Tests"))
    {
        var fragmentHtml = await fragment.Content.ReadAsStringAsync();
        Check(fragment.IsSuccessStatusCode && fragment.Headers.GetValues("X-PlaytestOps-Fragment").Single() == "tests" &&
            fragmentHtml.Contains(RowMarker(id)) && fragmentHtml.Contains(RowMarker(secondId)), "tests fragment applies the same connected-project visibility as the full dashboard");
    }
    Scalar("INSERT INTO Playtests (Name,Description,Status,IsAvailable) VALUES ('Unlinked visibility fixture','Isolated verifier only',0,1); SELECT 1;");
    var unlinkedId = Scalar("SELECT Id FROM Playtests WHERE Name='Unlinked visibility fixture' AND ProjectId IS NULL");
    html = await client.GetStringAsync("/");
    Check(!html.Contains(RowMarker(unlinkedId)) && !html.Contains("Unlinked visibility fixture") && html.Contains(RowMarker(id)),
        "unlinked null-project placeholders are excluded even while a Unity Editor is connected");
    Scalar("DELETE FROM Playtests WHERE Id=" + unlinkedId + "; SELECT 1;");
    visibilitySocket.Abort();
    html = await WaitForView(view => !view.Contains(RowMarker(id)));
    Check(html.Contains("No Unity Editor connected.") && (await client.GetStringAsync("/Editors")).Contains("<strong>Disconnected</strong>"),
        "socket disconnect hides rows while the paired session remains recorded");
    Check(Scalar("SELECT COUNT(*) FROM Playtests") == 2 && Scalar("SELECT COUNT(*) FROM PlaytestRuns") == storedRunCount &&
        (await client.GetStringAsync(logRoute)).Contains("LOG-EXCEPTION"), "socket disconnect preserves saved tests, attempts, and direct historical log access");
    var alternateProjectId = Guid.NewGuid().ToString("N");
    var alternateCredential = await PairVisibilityProject(alternateProjectId, "Alternate visibility project");
    using var alternateSocket = await ConnectVisibilityProject(alternateCredential);
    await Send(alternateSocket, Catalog(new[] { new { uniqueName = "Visibility/Alternate", fullName = "Visibility.Alternate", name = "Alternate connected test",
        description = "Isolated visibility fixture", assembly = "Visibility", mode = "EditMode", runState = "Runnable", skipReason = "" } }));
    var alternateId = Scalar("SELECT Id FROM Playtests WHERE UniqueName='Visibility/Alternate'");
    html = await client.GetStringAsync("/");
    Check(html.Contains(RowMarker(alternateId)) && !html.Contains(RowMarker(id)) && !html.Contains(RowMarker(secondId)),
        "a different connected project's catalog does not reveal a paired-disconnected project's tests");
    using var reconnectedVisibilitySocket = await ConnectVisibilityProject(visibilityCredential);
    html = await client.GetStringAsync("/");
    Check(html.Contains(RowMarker(id)) && html.Contains(RowMarker(secondId)) && html.Contains(RowMarker(alternateId)),
        "multiple active Unity Editors display the union of their project catalogs");
    alternateSocket.Abort();
    html = await WaitForView(view => !view.Contains(RowMarker(alternateId)));
    Check(html.Contains(RowMarker(id)) && html.Contains(RowMarker(secondId)) && Scalar("SELECT COUNT(*) FROM Playtests WHERE Id=" + alternateId) == 1,
        "disconnecting one Editor hides only its rows and preserves the other connected catalog");
    using (var fragment = await client.GetAsync("/?handler=Tests"))
    {
        var fragmentHtml = await fragment.Content.ReadAsStringAsync();
        Check(fragmentHtml.Contains(RowMarker(id)) && !fragmentHtml.Contains(RowMarker(alternateId)), "refresh fragment excludes disconnected project rows");
    }
    // A queued attempt retains refresh polling during a recoverable socket drop.
    await Run(id);
    var visibilityProbe = await Receive(reconnectedVisibilitySocket);
    Check(visibilityProbe.GetProperty("type").GetString() == "run.probe", "visibility fixture queues work before its connection drops");
    reconnectedVisibilitySocket.Abort();
    html = await WaitForView(view => !view.Contains(RowMarker(id)));
    Check(html.Contains("No Unity Editor connected.") && html.Contains("hx-trigger=\"every 3s\""),
        "hidden queued work keeps the tests fragment polling while its Editor is disconnected");
    using var queueVisibilitySocket = await ConnectVisibilityProject(visibilityCredential);
    var visibilityCommand = await NextCommand(queueVisibilitySocket);
    Check(visibilityCommand.GetProperty("uniqueName").GetString() == "Checks/One", "reconnected Editor resumes its saved hidden queue without rediscovery");
    await Send(queueVisibilitySocket, Update(visibilityCommand.GetProperty("runId").GetString()!, 1, "Passed", "Passed"));
    Check(!(await client.GetStringAsync("/")).Contains("hx-trigger=\"every 3s\""), "visibility polling stops after reconnected queued work finishes");
    queueVisibilitySocket.Abort();
    await WaitForView(view => !view.Contains(RowMarker(id)));
    var emptyCredential = await PairVisibilityProject(Guid.NewGuid().ToString("N"), "Empty connected project");
    using var emptySocket = await ConnectVisibilityProject(emptyCredential);
    await Send(emptySocket, Catalog(Array.Empty<object>()));
    html = await client.GetStringAsync("/");
    Check(html.Contains("No tests discovered.") && !html.Contains("No Unity Editor connected.") && !html.Contains("data-test-id="),
        "connected empty catalog differs from the no-connected-Editor empty state");
    Check(Scalar("SELECT COUNT(*) FROM Playtests") == 3 && Scalar("SELECT COUNT(*) FROM PlaytestRuns") == storedRunCount + 1 &&
        TextScalar("SELECT LogsJson FROM PlaytestRuns WHERE Id='" + loggedRunId + "'") == completeLogJson,
        "visibility changes leave stored disconnected catalogs and full log history intact");
    Console.WriteLine($"SUCCESS: {checks} checks");
}
finally
{
    Stop();
    File.WriteAllLines(Path.Combine(root, "bridge-check.log"), logs);
}
