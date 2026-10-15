using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

if (args.Length < 2)
{
    Console.Error.WriteLine("BUILD EXECUTION VERIFICATION FAILED: Usage: Checks <temporary-output-directory> <absolute-web-project-path>");
    Environment.ExitCode = 2;
    return;
}

var root = Path.GetFullPath(args[0]);
var appRoot = Path.GetFullPath(args[1]);
var buildDll = Path.Combine(root, "build", "PlaytestOps.Web.dll");
if (!File.Exists(buildDll))
{
    Console.Error.WriteLine("BUILD EXECUTION VERIFICATION FAILED: Build the app into <temporary-output-directory>/build first.");
    Environment.ExitCode = 2;
    return;
}
Directory.CreateDirectory(root);
var database = Path.Combine(root, "build-execution-check-" + Guid.NewGuid().ToString("N") + ".db");
var address = "http://127.0.0.1:5296";
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var serverLogs = new List<string>();
var checks = 0;
Process? server = null;
var sockets = new List<ClientWebSocket>();
var inboxes = new Dictionary<ClientWebSocket, MessageInbox>();
var projectForSocket = new Dictionary<ClientWebSocket, string>();
var passed = false;

void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException(label);
    checks++;
    Console.WriteLine("PASS " + label);
}

MessageInbox Inbox(ClientWebSocket socket)
{
    if (!inboxes.TryGetValue(socket, out var inbox)) inboxes.Add(socket, inbox = new MessageInbox(socket));
    return inbox;
}

Task<JsonElement> Receive(ClientWebSocket socket, int timeoutMilliseconds = 10000) => Inbox(socket).ReceiveAsync(timeoutMilliseconds);
Task<bool> HasMessageSoon(ClientWebSocket socket, int milliseconds = 350) => Inbox(socket).HasMessageSoonAsync(milliseconds);
Task Send(ClientWebSocket socket, object payload) => socket.SendAsync(
    new ArraySegment<byte>(JsonSerializer.SerializeToUtf8Bytes(payload, json)), WebSocketMessageType.Text, true, CancellationToken.None);

static string Antiforgery(string html) => WebUtility.HtmlDecode(
    Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);

static string Hidden(string html, string name) => WebUtility.HtmlDecode(
    Regex.Match(html, "<input[^>]*name=\"" + Regex.Escape(name) + "\"[^>]*value=\"([^\"]*)\"").Groups[1].Value);

async Task StartServer()
{
    using (var probe = new TcpListener(IPAddress.Loopback, 5296)) probe.Start();
    var start = new ProcessStartInfo("dotnet")
    {
        WorkingDirectory = appRoot,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    start.ArgumentList.Add(buildDll);
    start.ArgumentList.Add("--urls");
    start.ArgumentList.Add(address);
    start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
    start.Environment["ConnectionStrings__DefaultConnection"] = "Data Source=" + database;
    var localAppData = Path.Combine(root, "local-app-data");
    Directory.CreateDirectory(Path.Combine(localAppData, "ASP.NET", "DataProtection-Keys"));
    start.Environment["LOCALAPPDATA"] = localAppData;
    server = Process.Start(start) ?? throw new InvalidOperationException("Could not start the isolated dashboard process.");
    server.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (serverLogs) serverLogs.Add(e.Data); };
    server.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (serverLogs) serverLogs.Add(e.Data); };
    server.BeginOutputReadLine();
    server.BeginErrorReadLine();
    using var readiness = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    for (var attempt = 0; attempt < 100; attempt++)
    {
        if (server.HasExited) throw new InvalidOperationException("Isolated dashboard exited during startup; inspect the retained server log.");
        try
        {
            using var response = await readiness.GetAsync(address + "/Editors");
            if (response.IsSuccessStatusCode) return;
        }
        catch (HttpRequestException) { }
        await Task.Delay(100);
    }
    throw new TimeoutException("Isolated dashboard did not start on port 5296.");
}

void StopServer()
{
    if (server is { HasExited: false })
    {
        server.Kill(entireProcessTree: true);
        if (!server.WaitForExit(10000)) throw new TimeoutException("The verifier's own dashboard process did not stop.");
    }
    server?.Dispose();
    server = null;
}

async Task<(string SessionId, string Bearer, ClientWebSocket Socket)> Pair(HttpClient client, string projectId, string name)
{
    var page = await client.GetStringAsync("/Editors");
    var token = Antiforgery(page);
    Check(token.Length > 20, "Editor pairing page provides an antiforgery token");
    using var codeResponse = await client.PostAsync("/Editors?handler=PairingCode", new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["__RequestVerificationToken"] = token
    }));
    codeResponse.EnsureSuccessStatusCode();
    var codeHtml = await codeResponse.Content.ReadAsStringAsync();
    var code = Regex.Match(codeHtml, "id=\"pairing-code\">([^<]+)").Groups[1].Value;
    Check(code.Length == 10, "isolated dashboard creates a fixture pairing code");
    using var pairResponse = await client.PostAsync("/api/editor/pair", new StringContent(JsonSerializer.Serialize(new
    {
        code, projectId, projectName = name, unityVersion = "6000.6.0f1"
    }, json), Encoding.UTF8, "application/json"));
    pairResponse.EnsureSuccessStatusCode();
    using var paired = JsonDocument.Parse(await pairResponse.Content.ReadAsStringAsync());
    var bearer = paired.RootElement.GetProperty("token").GetString()!;
    var sessionId = paired.RootElement.GetProperty("sessionId").GetString()!;
    var socket = new ClientWebSocket();
    sockets.Add(socket);
    socket.Options.SetRequestHeader("Authorization", "Bearer " + bearer);
    await socket.ConnectAsync(new Uri(address.Replace("http://", "ws://") + "/api/editor/connect"), CancellationToken.None);
    var hello = await Receive(socket);
    Check(hello.GetProperty("type").GetString() == "session.ready" &&
        hello.GetProperty("projectId").GetString() == projectId && hello.GetProperty("sessionId").GetString() == sessionId,
        "authenticated fixture Editor owns exactly its paired project session");
    projectForSocket[socket] = projectId;
    return (sessionId, bearer, socket);
}

async Task<JsonElement> SettingsRequest(ClientWebSocket socket, string projectId)
{
    var command = await Receive(socket);
    Check(command.GetProperty("type").GetString() == "build.settings.get", "build page captures fresh Unity settings before issuing a start form");
    Check(projectForSocket.TryGetValue(socket, out var pairedProject) && pairedProject == projectId,
        "settings capture is received only on the selected project's paired Editor socket");
    return command;
}

object SettingsReply(string requestId) => new
{
    type = "build.settings.result", requestId,
    snapshot = new
    {
        protocolVersion = 1, capturedAtUtc = DateTime.UtcNow.ToString("O"), revision = new string('a', 64), state = "available", errorCode = "",
        unityVersion = "6000.6.0f1", activeTarget = "StandaloneWindows64", profileName = "Acceptance Windows", profilePath = "Assets/Settings/Windows.asset",
        scope = "Active Build Profile + effective Player Settings", buildExecutionSupported = true,
        fields = new[] { new { id = "Build.development", category = "Build", label = "Development", kind = "bool", value = "false", choices = Array.Empty<string>(), editable = true, note = "Fixture setting" } },
        limitations = Array.Empty<string>()
    }
};

async Task<(string Html, string Anti, string Revision, string RequestKey)> LoadBuildPage(HttpClient client, ClientWebSocket socket, string projectId, bool requireStartForm = true)
{
    var pageTask = client.GetAsync("/Builds?projectId=" + Uri.EscapeDataString(projectId));
    var command = await SettingsRequest(socket, projectId);
    Check(Guid.TryParseExact(command.GetProperty("requestId").GetString(), "N", out _), "build settings request has a correlation ID");
    await Send(socket, SettingsReply(command.GetProperty("requestId").GetString()!));
    using var response = await pageTask;
    var html = await response.Content.ReadAsStringAsync();
    Check(response.StatusCode == HttpStatusCode.OK, "build dashboard returns the selected project's current configuration");
    Check(response.Headers.CacheControl?.NoStore == true, "build dashboard response is not cached");
    var anti = Antiforgery(html);
    var revision = Hidden(html, "revision");
    var requestKey = Hidden(html, "requestKey");
    if (requireStartForm)
    {
        Check(anti.Length > 20 && BuildConfigurationServiceLikeRevision(revision), "build start form contains antiforgery and current revision guards");
        Check(Guid.TryParseExact(requestKey, "N", out _), "build start form uses an opaque server-issued request key");
    }
    return (html, anti, revision, requestKey);
}

static bool BuildConfigurationServiceLikeRevision(string value) => value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

async Task<JsonElement> StartBuild(HttpClient client, ClientWebSocket socket, string projectId, string sessionId,
    string anti, string revision, string requestKey, bool expectDispatch = true, bool injectUserPaths = false)
{
    var fields = new Dictionary<string, string>
    {
        ["__RequestVerificationToken"] = anti, ["projectId"] = projectId, ["editorSessionId"] = sessionId,
        ["revision"] = revision, ["requestKey"] = requestKey
    };
    if (injectUserPaths)
    {
        fields["outputPath"] = "C:\\Outside\\UserChosen";
        fields["executablePath"] = "C:\\Outside\\malware.exe";
        fields["buildId"] = Guid.NewGuid().ToString("N");
    }
    var post = client.PostAsync("/Builds?handler=Start", new FormUrlEncodedContent(fields));
    if (!expectDispatch)
    {
        using var rejected = await post;
        Check(rejected.StatusCode == HttpStatusCode.Conflict, "build request is rejected while this project has active work");
        Check(!await HasMessageSoon(socket), "rejected build request dispatches no Unity execution command");
        return default;
    }
    var command = await Receive(socket);
    Check(command.GetProperty("type").GetString() == "build.request", "start action dispatches one local Unity build request");
    var buildId = command.GetProperty("buildId").GetString()!;
    var buildToken = command.GetProperty("buildToken").GetString()!;
    Check(Guid.TryParseExact(buildId, "N", out _) && buildToken.Length == 64 && buildToken.All(Uri.IsHexDigit),
        "build identity and 256-bit capability are generated server-side");
    Check(command.GetProperty("revision").GetString() == revision, "Editor receives the exact reviewed configuration revision");
    Check(!command.TryGetProperty("outputPath", out _) && !command.TryGetProperty("executablePath", out _) && !command.TryGetProperty("executableName", out _),
        "browser cannot supply a build path or executable name to Unity");
    using var response = await post;
    Check(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.SeeOther && response.Headers.Location?.ToString().Contains(buildId, StringComparison.Ordinal) == true,
        "accepted build redirects to its immutable history record");
    var body = await response.Content.ReadAsStringAsync();
    Check(!body.Contains(buildToken, StringComparison.Ordinal), "secret build token is never rendered into browser HTML");
    return command;
}

static string OutputPath(string project, string buildId) => "C:/Fixture/" + project + "/Builds/PlaytestOps/" + buildId;
static string ExecutablePath(string outputPath) => outputPath + "/Player.exe";
static string ManifestPath(string outputPath) => outputPath + "/build-manifest.json";
static string Utc() => DateTime.UtcNow.ToString("O");

object BuildUpdate(string buildId, string token, int sequence, string state, string projectName,
    IEnumerable<object>? logs = null, string? message = null, string? outputPath = null, string? executablePath = null,
    string? manifestPath = null, string? profilePath = null, string activeTarget = "StandaloneWindows64",
    long totalBytes = 0, int totalWarnings = 0, int totalErrors = 0, double duration = 1.25,
    bool logsTruncated = false, int droppedLogCount = 0, string phase = "Building")
{
    var output = outputPath ?? OutputPath(projectName, buildId);
    return new
    {
        type = "build.update", buildId, buildToken = token, sequence, state, phase, message = message ?? (state == "Failed" ? "Fixture build failed" : "Fixture build state"),
        outputPath = output, executablePath = executablePath ?? ExecutablePath(output), manifestPath = manifestPath ?? ManifestPath(output),
        profilePath = profilePath ?? "Assets/Settings/Build Profiles/Acceptance Windows.asset", activeTarget, unityVersion = "6000.6.0f1",
        durationSeconds = duration, totalBytes, totalWarnings, totalErrors, logsTruncated, droppedLogCount,
        logs = logs?.ToArray() ?? Array.Empty<object>()
    };
}

object Log(int sequence, string message, string level = "Debug", string? stack = "") => new
{
    sequence, timestampUtc = Utc(), level, message, stackTrace = stack ?? "", truncated = false
};

async Task<JsonElement> SendUpdate(ClientWebSocket socket, object update, string label, bool expectError = false)
{
    await Send(socket, update);
    var ack = await Receive(socket);
    Check(ack.GetProperty("type").GetString() == "build.accepted", label + " receives correlated build acknowledgement");
    var error = ack.GetProperty("error").GetString() ?? "";
    Check(expectError ? error.Length > 0 : error.Length == 0, label + (expectError ? " is rejected without acceptance" : " is accepted"));
    return ack;
}

async Task<JsonElement> Status(HttpClient client, string buildId, int after = 0)
{
    using var response = await client.GetAsync($"/Builds/{buildId}?handler=Status&after={after}");
    Check(response.StatusCode == HttpStatusCode.OK, "build status and log page is readable after=" + after);
    Check(response.Headers.CacheControl?.NoStore == true, "build status response is not cached");
    return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
}

async Task<(long TestId, string Anti)> PublishTest(HttpClient client, ClientWebSocket socket)
{
    var tests = new[] { new { uniqueName = "BuildChecks/One", fullName = "BuildChecks.One", name = "One", description = "Build mutex fixture",
        assembly = "BuildChecks", mode = "EditMode", runState = "Runnable", skipReason = "" } };
    await Send(socket, new { type = "catalog.replace", requestId = Guid.NewGuid().ToString("N"), tests });
    var catalog = await Receive(socket);
    Check(catalog.GetProperty("type").GetString() == "catalog.accepted" && catalog.GetProperty("count").GetInt32() == 1,
        "fixture test is available for build/test exclusion checks");
    var index = await client.GetStringAsync("/");
    var anti = Antiforgery(index);
    Check(anti.Length > 20, "test request uses the current dashboard antiforgery token");
    var row = Regex.Match(index, "data-test-id=\"(\\d+)\".*?BuildChecks/One", RegexOptions.Singleline);
    if (!row.Success) row = Regex.Match(index, "data-test-id=\"(\\d+)\"", RegexOptions.Singleline);
    Check(row.Success && long.TryParse(row.Groups[1].Value, out _), "test catalog renders a requestable fixture ID");
    return (long.Parse(row.Groups[1].Value), anti);
}

async Task<HttpResponseMessage> RequestTest(HttpClient client, long testId, string anti) => await client.PostAsync("/?handler=Run",
    new FormUrlEncodedContent(new Dictionary<string, string> { ["testId"] = testId.ToString(), ["__RequestVerificationToken"] = anti }));

object RunUpdate(string runId, int sequence, string state, string outcome = "", string message = "") => new
{
    type = "run.update", runId, sequence, state, outcome, durationSeconds = 0.25, message, stackTrace = "", output = "fixture"
};

void SaveServerLog()
{
    try { File.WriteAllLines(Path.Combine(root, "build-execution-server.log"), serverLogs); }
    catch (Exception exception) { Console.Error.WriteLine("Could not retain isolated server log: " + exception.Message); Environment.ExitCode = 1; }
}

try
{
    await StartServer();
    using var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false })
    { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(40) };

    var projectA = Guid.NewGuid().ToString("N");
    var projectB = Guid.NewGuid().ToString("N");
    var editorA = await Pair(client, projectA, "Build fixture A");
    var editorB = await Pair(client, projectB, "Build fixture B");

    using (var remote = new HttpRequestMessage(HttpMethod.Get, "/Builds?projectId=" + projectA))
    {
        remote.Headers.Host = "attacker.example";
        using var response = await client.SendAsync(remote);
        Check(response.StatusCode == HttpStatusCode.Forbidden, "build dashboard rejects non-loopback Host headers");
        Check(!await HasMessageSoon(editorA.Socket), "rejected remote Host cannot query a connected Editor");
    }

    var initial = await LoadBuildPage(client, editorA.Socket, projectA);
    Check(initial.Html.Contains("Build Windows player", StringComparison.Ordinal) && initial.Html.Contains("Local build only", StringComparison.Ordinal),
        "dashboard exposes local Windows build and its no-run/no-user-path boundary");
    Check(!initial.Html.Contains("BuildToken", StringComparison.OrdinalIgnoreCase), "start page contains no build capability token");
    Check(!initial.Html.Contains("name=\"outputPath\"", StringComparison.OrdinalIgnoreCase) && !initial.Html.Contains("name=\"executablePath\"", StringComparison.OrdinalIgnoreCase),
        "browser form has no output or executable path input");

    using (var missingAnti = await client.PostAsync("/Builds?handler=Start", new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["projectId"] = projectA, ["editorSessionId"] = editorA.SessionId, ["revision"] = initial.Revision, ["requestKey"] = initial.RequestKey
    })))
    {
        Check(missingAnti.StatusCode == HttpStatusCode.BadRequest, "build start rejects a missing antiforgery token");
        Check(!await HasMessageSoon(editorA.Socket), "antiforgery rejection cannot dispatch a Unity build");
    }
    using (var wrongSession = await client.PostAsync("/Builds?handler=Start", new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["__RequestVerificationToken"] = initial.Anti, ["projectId"] = projectA, ["editorSessionId"] = Guid.NewGuid().ToString("N"),
        ["revision"] = initial.Revision, ["requestKey"] = initial.RequestKey
    })))
    {
        Check(wrongSession.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.BadRequest, "build start rejects a forged Editor session");
        Check(!await HasMessageSoon(editorA.Socket), "forged session cannot dispatch to the real Editor");
    }
    using (var wrongProject = await client.PostAsync("/Builds?handler=Start", new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["__RequestVerificationToken"] = initial.Anti, ["projectId"] = projectB, ["editorSessionId"] = editorA.SessionId,
        ["revision"] = initial.Revision, ["requestKey"] = initial.RequestKey
    })))
    {
        Check(wrongProject.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.BadRequest, "build request key cannot be replayed against another project");
        Check(!await HasMessageSoon(editorA.Socket) && !await HasMessageSoon(editorB.Socket), "cross-project form replay dispatches to neither Editor");
    }

    var fixture = await PublishTest(client, editorA.Socket);
    using (var queued = await RequestTest(client, fixture.TestId, fixture.Anti))
        Check(queued.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.SeeOther,
            "test can be queued before a build starts (HTTP " + (int)queued.StatusCode + ")");
    var probe = await Receive(editorA.Socket);
    Check(probe.GetProperty("type").GetString() == "run.probe", "run monitor confirms Editor readiness before test dispatch");
    var queuedBuild = await StartBuild(client, editorA.Socket, projectA, editorA.SessionId, initial.Anti, initial.Revision, initial.RequestKey, expectDispatch: false);
    _ = queuedBuild;

    await Send(editorA.Socket, new { type = "run.ready", requestId = probe.GetProperty("requestId").GetString() });
    var test = await Receive(editorA.Socket);
    Check(test.GetProperty("type").GetString() == "run.request", "ready Editor receives the queued test");
    var runId = test.GetProperty("runId").GetString()!;
    using (var pendingBuild = await client.PostAsync("/Builds?handler=Start", new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["__RequestVerificationToken"] = initial.Anti, ["projectId"] = projectA, ["editorSessionId"] = editorA.SessionId,
        ["revision"] = initial.Revision, ["requestKey"] = initial.RequestKey
    })))
    {
        Check(pendingBuild.StatusCode == HttpStatusCode.Conflict, "pending/running test excludes build creation");
        Check(!await HasMessageSoon(editorA.Socket), "test/build mutual exclusion prevents build dispatch");
    }
    await Send(editorA.Socket, RunUpdate(runId, 1, "Running"));
    Check((await Receive(editorA.Socket)).GetProperty("type").GetString() == "run.accepted", "fixture test Running state is recorded");
    using (var runningBuild = await client.PostAsync("/Builds?handler=Start", new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["__RequestVerificationToken"] = initial.Anti, ["projectId"] = projectA, ["editorSessionId"] = editorA.SessionId,
        ["revision"] = initial.Revision, ["requestKey"] = initial.RequestKey
    })))
    {
        Check(runningBuild.StatusCode == HttpStatusCode.Conflict, "running test continues to exclude build creation");
        Check(!await HasMessageSoon(editorA.Socket), "running test causes no build execution command");
    }
    await Send(editorA.Socket, RunUpdate(runId, 2, "Passed", "Passed", "Fixture complete"));
    Check((await Receive(editorA.Socket)).GetProperty("type").GetString() == "run.accepted", "test terminal result releases project build lock");

    var buildCommandA = await StartBuild(client, editorA.Socket, projectA, editorA.SessionId, initial.Anti, initial.Revision, initial.RequestKey, injectUserPaths: true);
    var buildIdA = buildCommandA.GetProperty("buildId").GetString()!;
    var buildTokenA = buildCommandA.GetProperty("buildToken").GetString()!;
    Check(!await HasMessageSoon(editorB.Socket), "selected project's build command is not broadcast to another connected Editor");
    var outA = OutputPath("A", buildIdA);
    await SendUpdate(editorA.Socket, BuildUpdate(buildIdA, buildTokenA, 1, "Running", "A", phase: "Preparing output"), "first build update");

    // Exact request-key replay must resolve to the first record without a second execution.
    using (var replay = await client.PostAsync("/Builds?handler=Start", new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["__RequestVerificationToken"] = initial.Anti, ["projectId"] = projectA, ["editorSessionId"] = editorA.SessionId,
        ["revision"] = initial.Revision, ["requestKey"] = initial.RequestKey
    })))
    {
        Check(replay.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.SeeOther && replay.Headers.Location?.ToString().Contains(buildIdA, StringComparison.Ordinal) == true,
            "replayed start key resolves to the original build record");
        Check(!await HasMessageSoon(editorA.Socket), "replayed start key never sends a second Unity build request");
    }

    var badToken = await SendUpdate(editorA.Socket, BuildUpdate(buildIdA, new string('0', 64), 2, "Failed", "A", message: "foreign"), "wrong build token", expectError: true);
    Check(badToken.GetProperty("buildId").GetString() == buildIdA, "wrong token rejection remains bound to the requested build");
    await SendUpdate(editorB.Socket, BuildUpdate(buildIdA, buildTokenA, 2, "Failed", "B", message: "cross-project"), "cross-project build update", expectError: true);

    await SendUpdate(editorA.Socket, BuildUpdate(buildIdA, buildTokenA, 2, "Running", "A", outputPath: "C:/Fixture/Alternate/Builds/PlaytestOps/" + buildIdA,
        executablePath: "C:/Fixture/Alternate/Builds/PlaytestOps/" + buildIdA + "/Player.exe", manifestPath: "C:/Fixture/Alternate/Builds/PlaytestOps/" + buildIdA + "/build-manifest.json"),
        "immutable output folder cannot be switched mid-build", expectError: true);
    await SendUpdate(editorA.Socket, BuildUpdate(buildIdA, buildTokenA, 2, "Running", "A", outputPath: "C:/Fixture/A/../Outside/Builds/PlaytestOps/" + buildIdA,
        executablePath: "C:/Fixture/A/../Outside/Builds/PlaytestOps/" + buildIdA + "/Player.exe", manifestPath: "C:/Fixture/A/../Outside/Builds/PlaytestOps/" + buildIdA + "/build-manifest.json"),
        "traversing artifact path", expectError: true);

    var firstLogs = Enumerable.Range(1, 64).Select(i => Log(i, i == 1 ? "<script>BUILD-LOG-XSS</script>" : "Fixture build log " + i,
        i == 2 ? "Warning" : "Debug", "stack line")).ToArray();
    var runningAck = await SendUpdate(editorA.Socket, BuildUpdate(buildIdA, buildTokenA, 2, "Running", "A", firstLogs,
        outputPath: outA, phase: "Compiling scripts"), "64-entry build log batch");
    Check(runningAck.GetProperty("logSequence").GetInt32() == 64, "build acknowledgment tracks the contiguous log sequence");

    var foreignBuildToken = await SendUpdate(editorA.Socket, BuildUpdate(buildIdA, buildTokenA, 3, "Running", "A", [Log(65, "duplicate identity")],
        outputPath: outA, profilePath: "Assets/Settings/Other.asset"), "immutable profile identity change", expectError: true);
    Check(foreignBuildToken.GetProperty("sequence").GetInt32() == 2, "rejected immutable identity update does not advance status sequence");
    await SendUpdate(editorA.Socket, BuildUpdate(buildIdA, buildTokenA, 3, "Running", "A", [Log(66, "gap")], outputPath: outA), "log sequence gap", expectError: true);
    await SendUpdate(editorA.Socket, BuildUpdate(buildIdA, buildTokenA, 3, "Running", "A", Enumerable.Range(65, 65).Select(i => Log(i, "too many logs")), outputPath: outA),
        "65-entry oversized batch", expectError: true);
    var oversized = Enumerable.Range(1, 4).Select(i => Log(i, new string('x', 16000), "Debug", new string('y', 16000)))
        .Append(Log(5, "x")).ToArray();
    await SendUpdate(editorA.Socket, BuildUpdate(buildIdA, buildTokenA, 3, "Running", "A", oversized, outputPath: outA), "128001-character oversized batch", expectError: true);
    await SendUpdate(editorA.Socket, BuildUpdate(buildIdA, buildTokenA, 3, "Running", "A", [Log(65, new string('x', 16001))], outputPath: outA),
        "oversized individual log message", expectError: true);
    await SendUpdate(editorA.Socket, BuildUpdate(buildIdA, buildTokenA, 3, "Running", "A", [new { sequence = 65, timestampUtc = "not-a-date", level = "Debug", message = "bad time", stackTrace = "", truncated = false }], outputPath: outA),
        "invalid log timestamp", expectError: true);
    await SendUpdate(editorA.Socket, BuildUpdate(buildIdA, buildTokenA, 3, "Running", "A", [Log(65, "bad level", "Trace")], outputPath: outA),
        "unsupported log level", expectError: true);

    // Persist 201 entries over four bounded batches and prove the detail endpoint can page all of them.
    for (var batch = 0; batch < 2; batch++)
    {
        var logs = Enumerable.Range(65 + batch * 64, 64).Select(i => Log(i, "Persisted full-history log " + i,
            i % 17 == 0 ? "Error" : "Debug", "stack " + i)).ToArray();
        var ack = await SendUpdate(editorA.Socket, BuildUpdate(buildIdA, buildTokenA, 3 + batch, "Running", "A", logs,
            outputPath: outA, phase: batch == 2 ? "Writing player" : "Building player"), "bounded log page " + (batch + 2));
        Check(ack.GetProperty("logSequence").GetInt32() == 64 * (batch + 2), "log page acknowledgment advances exactly 64 entries");
    }
    var lastLogs = new[] { Log(193, "Persisted full-history log 193"), Log(194, "Persisted full-history log 194"), Log(195, "Persisted full-history log 195"),
        Log(196, "Persisted full-history log 196"), Log(197, "Persisted full-history log 197"), Log(198, "Persisted full-history log 198"), Log(199, "Persisted full-history log 199"),
        Log(200, "Persisted full-history log 200"), Log(201, "Persisted full-history log 201") };
    await SendUpdate(editorA.Socket, BuildUpdate(buildIdA, buildTokenA, 5, "Running", "A", lastLogs, outputPath: outA), "final partial log page");
    var duplicate = await SendUpdate(editorA.Socket, BuildUpdate(buildIdA, buildTokenA, 5, "Failed", "A", [Log(201, "duplicate must not replace")],
        message: "late duplicate", outputPath: outA), "duplicate update sequence");
    Check(duplicate.GetProperty("sequence").GetInt32() == 5 && duplicate.GetProperty("logSequence").GetInt32() == 201,
        "duplicate sequence is idempotent and cannot overwrite newer state/logs");

    // Project B can build concurrently while A is active; project ownership must not bleed between sessions.
    var initialB = await LoadBuildPage(client, editorB.Socket, projectB);
    var buildCommandB = await StartBuild(client, editorB.Socket, projectB, editorB.SessionId, initialB.Anti, initialB.Revision, initialB.RequestKey);
    var buildIdB = buildCommandB.GetProperty("buildId").GetString()!;
    var buildTokenB = buildCommandB.GetProperty("buildToken").GetString()!;
    var outB = OutputPath("B", buildIdB);
    await SendUpdate(editorB.Socket, BuildUpdate(buildIdB, buildTokenB, 1, "Running", "B", outputPath: outB), "second project's independent build");

    // BuildReport totalErrors and the result enum must agree; malformed success cannot release false success.
    await SendUpdate(editorB.Socket, BuildUpdate(buildIdB, buildTokenB, 2, "Succeeded", "B", message: "contradictory summary",
        outputPath: outB, totalBytes: 250000, totalErrors: 1), "Succeeded with BuildReport errors", expectError: true);
    await SendUpdate(editorB.Socket, BuildUpdate(buildIdB, buildTokenB, 2, "Succeeded", "B", message: "missing output bytes",
        outputPath: outB, totalBytes: 0), "Succeeded without nonzero artifact bytes", expectError: true);
    await SendUpdate(editorB.Socket, BuildUpdate(buildIdB, buildTokenB, 2, "Succeeded", "B", message: "missing output folder",
        outputPath: "", executablePath: "", manifestPath: "", totalBytes: 250000), "Succeeded without an output folder", expectError: true);
    await SendUpdate(editorB.Socket, BuildUpdate(buildIdB, buildTokenB, 2, "Succeeded", "B", message: "missing executable",
        outputPath: outB, executablePath: "", totalBytes: 250000), "Succeeded without an executable path", expectError: true);
    await SendUpdate(editorB.Socket, BuildUpdate(buildIdB, buildTokenB, 2, "Succeeded", "B", message: "missing manifest",
        outputPath: outB, manifestPath: "", totalBytes: 250000), "Succeeded without a manifest path", expectError: true);
    await SendUpdate(editorB.Socket, BuildUpdate(buildIdB, buildTokenB, 2, "Cancelled", "B", message: "fake cancellation", outputPath: outB),
        "unsupported cancellation state", expectError: true);
    await SendUpdate(editorB.Socket, BuildUpdate(buildIdB, buildTokenB, 2, "Failed", "B", [Log(1, "<img src=x onerror=BUILD-ERROR-XSS>", "Error", "Fixture failure stack")],
        message: "<script>BUILD-ERROR-XSS</script>", outputPath: outB, totalErrors: 1, phase: "Build failed"), "failed build with captured error");
    var failureDetail = await client.GetStringAsync("/Builds/" + buildIdB);
    Check(failureDetail.Contains("Build failed", StringComparison.Ordinal) && failureDetail.Contains("Fixture failure stack", StringComparison.Ordinal),
        "failed build detail renders the final error and stack trace");
    Check(failureDetail.Contains("&lt;script&gt;BUILD-ERROR-XSS&lt;/script&gt;", StringComparison.Ordinal) &&
        !failureDetail.Contains("<script>BUILD-ERROR-XSS</script>", StringComparison.Ordinal), "build failure text is HTML encoded");

    // Exercise both aggregate retention limits, not only the per-message/batch limits.
    var charsFormB = await LoadBuildPage(client, editorB.Socket, projectB);
    var charsCommandB = await StartBuild(client, editorB.Socket, projectB, editorB.SessionId, charsFormB.Anti, charsFormB.Revision, charsFormB.RequestKey);
    var charsBuildId = charsCommandB.GetProperty("buildId").GetString()!;
    var charsBuildToken = charsCommandB.GetProperty("buildToken").GetString()!;
    var charsPath = OutputPath("B", charsBuildId);
    await SendUpdate(editorB.Socket, BuildUpdate(charsBuildId, charsBuildToken, 1, "Running", "B", outputPath: charsPath), "aggregate character-cap build starts");
    var nextCharacterLog = 1;
    var characterWireSequence = 2;
    JsonElement characterCapAck = default;
    while (nextCharacterLog <= 262)
    {
        var count = Math.Min(4, 263 - nextCharacterLog);
        var logs = Enumerable.Range(nextCharacterLog, count)
            .Select(sequence => Log(sequence, new string('x', 16000), "Debug", new string('y', 16000))).ToArray();
        await Send(editorB.Socket, BuildUpdate(charsBuildId, charsBuildToken, characterWireSequence++, "Running", "B", logs, outputPath: charsPath));
        characterCapAck = await Receive(editorB.Socket);
        if ((characterCapAck.GetProperty("error").GetString() ?? "").Length != 0)
            throw new InvalidOperationException("Aggregate character limit rejected valid prefix at log " + nextCharacterLog + ".");
        nextCharacterLog += count;
    }
    Check(characterCapAck.GetProperty("logSequence").GetInt32() == 262, "server retains the complete log prefix just below the 8 MiB aggregate character cap");
    await SendUpdate(editorB.Socket, BuildUpdate(charsBuildId, charsBuildToken, characterWireSequence, "Running", "B",
        [Log(263, new string('x', 16000), "Debug", new string('y', 16000))], outputPath: charsPath),
        "log batch exceeding the 8 MiB aggregate character cap", expectError: true);
    var characterCapStatus = await Status(client, charsBuildId, 261);
    Check(characterCapStatus.GetProperty("logs").GetArrayLength() == 1 &&
        characterCapStatus.GetProperty("build").GetProperty("lastLogSequence").GetInt32() == 262,
        "aggregate character-cap rejection leaves the stored log sequence unchanged");
    await SendUpdate(editorB.Socket, BuildUpdate(charsBuildId, charsBuildToken, characterWireSequence + 1, "Failed", "B",
        outputPath: charsPath, totalErrors: 1, phase: "Fixture cap complete"), "aggregate character-cap fixture closes");

    var countFormB = await LoadBuildPage(client, editorB.Socket, projectB);
    var countCommandB = await StartBuild(client, editorB.Socket, projectB, editorB.SessionId, countFormB.Anti, countFormB.Revision, countFormB.RequestKey);
    var countBuildId = countCommandB.GetProperty("buildId").GetString()!;
    var countBuildToken = countCommandB.GetProperty("buildToken").GetString()!;
    var countPath = OutputPath("B", countBuildId);
    await SendUpdate(editorB.Socket, BuildUpdate(countBuildId, countBuildToken, 1, "Running", "B", outputPath: countPath), "aggregate log-count build starts");
    var nextCountLog = 1;
    var countWireSequence = 2;
    JsonElement countCapAck = default;
    while (nextCountLog <= 10000)
    {
        var count = Math.Min(64, 10001 - nextCountLog);
        var logs = Enumerable.Range(nextCountLog, count).Select(sequence => Log(sequence, "x")).ToArray();
        await Send(editorB.Socket, BuildUpdate(countBuildId, countBuildToken, countWireSequence++, "Running", "B", logs, outputPath: countPath));
        countCapAck = await Receive(editorB.Socket);
        if ((countCapAck.GetProperty("error").GetString() ?? "").Length != 0)
            throw new InvalidOperationException("Aggregate log-count limit rejected valid prefix at log " + nextCountLog + ".");
        nextCountLog += count;
    }
    Check(countCapAck.GetProperty("logSequence").GetInt32() == 10000, "server retains exactly 10,000 sequenced build logs");
    await SendUpdate(editorB.Socket, BuildUpdate(countBuildId, countBuildToken, countWireSequence, "Running", "B", [Log(10001, "overflow")], outputPath: countPath),
        "log exceeding the 10,000-entry aggregate cap", expectError: true);
    var countCapStatus = await Status(client, countBuildId, 9999);
    Check(countCapStatus.GetProperty("logs").GetArrayLength() == 1 &&
        countCapStatus.GetProperty("build").GetProperty("lastLogSequence").GetInt32() == 10000,
        "aggregate log-count rejection leaves the stored log sequence unchanged");
    await SendUpdate(editorB.Socket, BuildUpdate(countBuildId, countBuildToken, countWireSequence + 1, "Failed", "B",
        outputPath: countPath, totalErrors: 1, phase: "Fixture cap complete"), "aggregate log-count fixture closes");

    // A build lock excludes tests on the same project while another project's build proceeds.
    using (var duringBuild = await RequestTest(client, fixture.TestId, fixture.Anti))
    {
        Check(duringBuild.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.SeeOther, "active build request returns the normal dashboard redirect");
        var blockedPage = await client.GetStringAsync(duringBuild.Headers.Location?.ToString() ?? "/");
        Check(blockedPage.Contains("build", StringComparison.OrdinalIgnoreCase), "active build rejects same-project test request with a visible reason");
        Check(!await HasMessageSoon(editorA.Socket), "same-project active build produces no test readiness probe or run request");
    }

    // Finish A successfully with an actual artifact identity and complete log capture.
    var successAck = await SendUpdate(editorA.Socket, BuildUpdate(buildIdA, buildTokenA, 7, "Succeeded", "A", outputPath: outA,
        totalBytes: 12_345_678, totalWarnings: 1, totalErrors: 0, duration: 4.75, phase: "Build succeeded", message: "Windows player built"), "successful build result");
    Check(successAck.GetProperty("logSequence").GetInt32() == 201, "successful result retains every sequenced log entry");
    var statusA = await Status(client, buildIdA);
    Check(statusA.GetProperty("build").GetProperty("state").GetString() == "Succeeded" && !statusA.GetProperty("active").GetBoolean(),
        "successful build is terminal and stops status polling");
    Check(statusA.GetProperty("logs").GetArrayLength() == 200 && statusA.GetProperty("hasMore").GetBoolean(),
        "status endpoint pages logs instead of silently truncating the full history");
    var pageTwo = await Status(client, buildIdA, 200);
    Check(pageTwo.GetProperty("logs").GetArrayLength() == 1 && !pageTwo.GetProperty("hasMore").GetBoolean(), "final status page contains the remaining log");
    var detail = await client.GetStringAsync("/Builds/" + buildIdA);
    Check(detail.Contains("Succeeded", StringComparison.Ordinal) && detail.Contains("Player.exe", StringComparison.Ordinal) && detail.Contains("12345678", StringComparison.Ordinal),
        "build history detail exposes terminal result, executable location and byte summary");
    Check(detail.Contains("&lt;script&gt;BUILD-LOG-XSS&lt;/script&gt;", StringComparison.Ordinal) && !detail.Contains("<script>BUILD-LOG-XSS</script>", StringComparison.Ordinal),
        "Editor-supplied log text is HTML encoded in the detail view");
    using (var logFile = await client.GetAsync("/Builds/" + buildIdA + "?handler=LogFile"))
    {
        Check(logFile.IsSuccessStatusCode && logFile.Content.Headers.ContentType?.MediaType == "text/plain", "all captured logs are available as a plain-text download");
        var text = await logFile.Content.ReadAsStringAsync();
        Check(text.Contains("Persisted full-history log 1", StringComparison.Ordinal) && text.Contains("Persisted full-history log 201", StringComparison.Ordinal),
            "download includes both earliest and latest captured logs");
    }
    using (var statusRemote = new HttpRequestMessage(HttpMethod.Get, "/Builds/" + buildIdA + "?handler=Status&after=0"))
    {
        statusRemote.Headers.Host = "attacker.example";
        using var response = await client.SendAsync(statusRemote);
        Check(response.StatusCode == HttpStatusCode.Forbidden, "build status/log API rejects non-loopback Host headers");
    }

    // A new build on A is left Running across an owned-server restart.
    var secondFormA = await LoadBuildPage(client, editorA.Socket, projectA);
    var runningCommandA = await StartBuild(client, editorA.Socket, projectA, editorA.SessionId, secondFormA.Anti, secondFormA.Revision, secondFormA.RequestKey);
    var runningIdA = runningCommandA.GetProperty("buildId").GetString()!;
    var runningTokenA = runningCommandA.GetProperty("buildToken").GetString()!;
    var runningPathA = OutputPath("A", runningIdA);
    await SendUpdate(editorA.Socket, BuildUpdate(runningIdA, runningTokenA, 1, "Running", "A", outputPath: runningPathA,
        phase: "Compiling", message: "Still building"), "build marked Running before server restart");
    StopServer();
    foreach (var staleSocket in sockets.Where(item => item.State == WebSocketState.Open)) { try { staleSocket.Abort(); } catch { } }
    await StartServer();
    Check(server is { HasExited: false }, "only the verifier's own dashboard process restarted against the same disposable database");
    var unknownPage = await client.GetStringAsync("/Builds/" + runningIdA);
    Check(unknownPage.Contains("Unknown", StringComparison.Ordinal) && unknownPage.Contains("may still be building", StringComparison.OrdinalIgnoreCase),
        "restart converts active build to honest Unknown instead of fake cancel or success");
    Check(unknownPage.Contains("does not stop a running build", StringComparison.OrdinalIgnoreCase), "unknown resolution warns that it does not cancel Unity");

    var resumedA = await Pair(client, projectA, "Build fixture A resumed");
    Check(!await HasMessageSoon(resumedA.Socket), "reconnect does not automatically replay the old build request");
    var statusUnknown = await Status(client, runningIdA);
    Check(statusUnknown.GetProperty("build").GetProperty("state").GetString() == "Unknown" && !statusUnknown.GetProperty("active").GetBoolean(),
        "Unknown stops polling even though the build page keeps the project reserved");
    var unknownForm = await LoadBuildPage(client, resumedA.Socket, projectA, requireStartForm: false);
    Check(unknownForm.Html.Contains("An active or unknown build holds this project", StringComparison.Ordinal), "unknown build keeps the project start control locked");
    Check(!unknownForm.Html.Contains("name=\"requestKey\"", StringComparison.Ordinal) && !unknownForm.Html.Contains(">Build Windows player<", StringComparison.Ordinal),
        "unknown build exposes no fresh start form or request key");
    var testAfterRestart = await PublishTest(client, resumedA.Socket);
    using (var deniedTest = await RequestTest(client, testAfterRestart.TestId, testAfterRestart.Anti))
    {
        Check(deniedTest.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.SeeOther, "Unknown build test request returns the normal dashboard redirect");
        var blockedPage = await client.GetStringAsync(deniedTest.Headers.Location?.ToString() ?? "/");
        Check(blockedPage.Contains("build", StringComparison.OrdinalIgnoreCase), "Unknown build blocks same-project test requests after restart with a visible reason");
        Check(!await HasMessageSoon(resumedA.Socket), "unknown reservation prevents post-restart test dispatch");
    }
    await SendUpdate(resumedA.Socket, BuildUpdate(runningIdA, runningTokenA, 2, "Succeeded", "A", outputPath: runningPathA,
        totalBytes: 42_000, duration: 2.5, phase: "Reconciled", message: "Recovered final BuildReport"), "same-project token reconciles after a new Editor session");
    var reconciled = await Status(client, runningIdA);
    Check(reconciled.GetProperty("build").GetProperty("state").GetString() == "Succeeded", "reconciled terminal result replaces Unknown with confirmed success");

    // A separate Unknown can only be released by the explicit antiforgery-protected operator action.
    var resumedB = await Pair(client, projectB, "Build fixture B resumed");
    var formB2 = await LoadBuildPage(client, resumedB.Socket, projectB);
    var commandB2 = await StartBuild(client, resumedB.Socket, projectB, resumedB.SessionId, formB2.Anti, formB2.Revision, formB2.RequestKey);
    var buildIdB2 = commandB2.GetProperty("buildId").GetString()!;
    var buildTokenB2 = commandB2.GetProperty("buildToken").GetString()!;
    var pathB2 = OutputPath("B", buildIdB2);
    await SendUpdate(resumedB.Socket, BuildUpdate(buildIdB2, buildTokenB2, 1, "Running", "B", outputPath: pathB2), "second project build left active");
    StopServer();
    foreach (var oldSocket in sockets.Where(item => item.State == WebSocketState.Open)) { try { oldSocket.Abort(); } catch { } }
    await StartServer();
    var unknownB = await client.GetStringAsync("/Builds/" + buildIdB2);
    Check(unknownB.Contains("Unknown", StringComparison.Ordinal), "second restart preserves another active build as Unknown");
    using (var noAnti = await client.PostAsync("/Builds/" + buildIdB2 + "?handler=ResolveUnknown", new FormUrlEncodedContent([])))
        Check(noAnti.StatusCode == HttpStatusCode.BadRequest, "unknown-build reservation cannot be released without antiforgery");
    using (var resolve = await client.PostAsync("/Builds/" + buildIdB2 + "?handler=ResolveUnknown", new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["__RequestVerificationToken"] = Antiforgery(unknownB)
    })))
        Check(resolve.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.SeeOther, "operator can explicitly acknowledge Unknown after reviewing warning");
    var interrupted = await Status(client, buildIdB2);
    Check(interrupted.GetProperty("build").GetProperty("state").GetString() == "Interrupted" && !interrupted.GetProperty("active").GetBoolean(),
        "explicit reservation release records Interrupted, never Cancelled or Succeeded");
    Check(interrupted.GetProperty("build").GetProperty("message").GetString()!.Contains("No Unity cancellation was sent", StringComparison.Ordinal),
        "interruption record states that Unity was not stopped");

    Check(checks >= 50, "verification completed a substantial assertion set (minimum 50)");
    passed = true;
    Console.WriteLine($"BUILD EXECUTION VERIFICATION COMPLETE: {checks} checks passed.");
}
catch (Exception exception)
{
    Console.Error.WriteLine("BUILD EXECUTION VERIFICATION FAILED: " + exception.Message);
    Environment.ExitCode = 1;
}
finally
{
    foreach (var socket in sockets)
    {
        try { socket.Abort(); socket.Dispose(); }
        catch (Exception exception) { Console.Error.WriteLine("Socket cleanup failed: " + exception.Message); Environment.ExitCode = 1; }
    }
    try { StopServer(); }
    catch (Exception exception) { Console.Error.WriteLine("Owned server cleanup failed: " + exception.Message); Environment.ExitCode = 1; }
    SaveServerLog();
    if (!passed && Environment.ExitCode == 0) Environment.ExitCode = 1;
}

sealed class MessageInbox
{
    private readonly ClientWebSocket socket;
    private readonly Channel<JsonElement> channel = Channel.CreateUnbounded<JsonElement>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });
    private readonly Task pump;

    public MessageInbox(ClientWebSocket socket)
    {
        this.socket = socket;
        pump = PumpAsync();
    }

    private async Task PumpAsync()
    {
        try
        {
            var buffer = new byte[8192];
            while (socket.State == WebSocketState.Open)
            {
                using var stream = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close) { channel.Writer.TryComplete(); return; }
                    if (result.MessageType != WebSocketMessageType.Text || stream.Length + result.Count > 2 * 1024 * 1024)
                        throw new InvalidDataException("Fixture Editor received an unexpected/oversized WebSocket message.");
                    stream.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                using var document = JsonDocument.Parse(stream.ToArray());
                await channel.Writer.WriteAsync(document.RootElement.Clone());
            }
            channel.Writer.TryComplete();
        }
        catch (Exception exception) { channel.Writer.TryComplete(exception); }
    }

    public async Task<JsonElement> ReceiveAsync(int timeoutMilliseconds)
    {
        using var timeout = new CancellationTokenSource(timeoutMilliseconds);
        return await channel.Reader.ReadAsync(timeout.Token);
    }

    public async Task<bool> HasMessageSoonAsync(int milliseconds)
    {
        using var timeout = new CancellationTokenSource(milliseconds);
        try
        {
            if (!await channel.Reader.WaitToReadAsync(timeout.Token)) return false;
            return channel.Reader.TryRead(out _);
        }
        catch (OperationCanceledException) { return false; }
    }
}
