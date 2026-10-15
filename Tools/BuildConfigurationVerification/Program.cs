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
    Console.Error.WriteLine("BUILD CONFIGURATION VERIFICATION FAILED: Usage: Checks <temporary-output-directory> <absolute-web-project-path>");
    Environment.ExitCode = 2;
    return;
}

var root = Path.GetFullPath(args[0]);
var appRoot = Path.GetFullPath(args[1]);
var buildDll = Path.Combine(root, "build", "PlaytestOps.Web.dll");
if (!File.Exists(buildDll)) throw new FileNotFoundException("Build the web app into <temporary-output-directory>/build first.", buildDll);
Directory.CreateDirectory(root);
var database = Path.Combine(root, "build-settings-check-" + Guid.NewGuid().ToString("N") + ".db");
var address = "http://127.0.0.1:5295";
var checks = 0;
var serverLogs = new List<string>();
Process? server = null;
ClientWebSocket? editorSocket = null;
ClientWebSocket? foreignSocket = null;
var extraSockets = new List<ClientWebSocket>();
var inboxes = new Dictionary<ClientWebSocket, MessageInbox>();
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    checks++;
    Console.WriteLine("PASS " + label);
}

MessageInbox Inbox(ClientWebSocket socket)
{
    if (!inboxes.TryGetValue(socket, out var inbox)) inboxes.Add(socket, inbox = new MessageInbox(socket));
    return inbox;
}

Task<JsonElement> Receive(ClientWebSocket socket, int timeoutMilliseconds = 10000) => Inbox(socket).ReceiveAsync(timeoutMilliseconds);
Task Write(ClientWebSocket socket, object payload) => socket.SendAsync(
    new ArraySegment<byte>(JsonSerializer.SerializeToUtf8Bytes(payload, json)),
    WebSocketMessageType.Text, true, CancellationToken.None);

Task<bool> HasMessageSoon(ClientWebSocket socket, int milliseconds = 250) => Inbox(socket).HasMessageSoonAsync(milliseconds);

static string AntiForgery(string page)
{
    var token = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
    return WebUtility.HtmlDecode(token);
}

async Task<(string SessionId, ClientWebSocket Socket)> Pair(HttpClient client, string projectId, string name)
{
    var editors = await client.GetStringAsync("/Editors");
    var anti = AntiForgery(editors);
    Check(anti.Length > 20, "pairing page provides an antiforgery token");
    var generated = await client.PostAsync("/Editors?handler=PairingCode", new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["__RequestVerificationToken"] = anti
    }));
    generated.EnsureSuccessStatusCode();
    var code = Regex.Match(await generated.Content.ReadAsStringAsync(), "id=\"pairing-code\">([^<]+)").Groups[1].Value;
    Check(code.Length == 10, "local pairing code is available to the fixture Editor");
    using var paired = await client.PostAsync("/api/editor/pair", new StringContent(JsonSerializer.Serialize(new
    {
        code, projectId, projectName = name, unityVersion = "6000.6.0f1"
    }, json), Encoding.UTF8, "application/json"));
    paired.EnsureSuccessStatusCode();
    using var pairJson = JsonDocument.Parse(await paired.Content.ReadAsStringAsync());
    var token = pairJson.RootElement.GetProperty("token").GetString()!;
    var sessionId = pairJson.RootElement.GetProperty("sessionId").GetString()!;
    var socket = new ClientWebSocket();
    socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
    await socket.ConnectAsync(new Uri("ws://127.0.0.1:5295/api/editor/connect"), CancellationToken.None);
    var ready = await Receive(socket);
    Check(ready.GetProperty("type").GetString() == "session.ready" &&
        ready.GetProperty("sessionId").GetString() == sessionId && ready.GetProperty("projectId").GetString() == projectId,
        "authenticated fixture Editor owns the paired project session");
    return (sessionId, socket);
}

var projectId = Guid.NewGuid().ToString("N");
var revision = new string('a', 64);
var fields = new List<Field>
{
    new("profile.name", "Profile", "Windows Acceptance", "string", "Windows Acceptance", [], false, "Selected profile (read only in this fixture)."),
    new("windows.architecture", "Windows", "Architecture", "enum", "x64", ["x86", "x64", "arm64"], true, "Windows platform setting."),
    new("windows.development", "Windows", "Development Build", "bool", "false", [], true, "Windows platform setting."),
    new("player.backend", "Player", "Scripting Backend", "enum", "Mono", ["Mono", "IL2CPP"], true, "Global PlayerSettings value keyed to Standalone."),
    new("player.apiCompatibility", "Player", "API Compatibility", "enum", "NET_Standard_2_1", ["NET_Standard_2_1", "NET_Unity_4_8"], true, "Global PlayerSettings value keyed to Standalone."),
    new("player.defines", "Player", "Standalone Defines", "json", "{\"items\":[\"BASE\"]}", [], true, "Global target symbols; profile symbols are separate."),
    new("profile.defines", "Profile", "Profile Defines", "json", "{\"items\":[\"PROFILE\"]}", [], true, "Build-profile symbols apply only while this profile is active."),
    new("scenes.override", "Profile", "Override Global Scenes", "bool", "true", [], true, "Selects the effective scene list source."),
    new("scenes.effective", "Scenes", "Effective Scenes", "json", "{\"items\":[\"+Assets/Probe/ActiveProfile.unity\"]}", [], true, "Ordered effective scene paths and enabled state."),
    new("graphics.automatic", "Rendering", "Automatic Graphics API", "bool", "true", [], true, "Automatic mode must remain distinct from an explicit ordered list."),
    new("graphics.apis", "Rendering", "Graphics APIs", "json", "{\"items\":[\"Direct3D11\",\"Direct3D12\"]}", [], true, "Ordered explicit APIs when automatic mode is disabled."),
    new("player.applicationIdentifier", "Player", "Application Identifier", "string", "com.example.playtestops", [], true, "Global target-specific PlayerSettings value."),
    new("test.integer", "Verifier", "Integer fixture", "integer", "7", [], true, "Typed protocol control."),
    new("test.number", "Verifier", "Number fixture", "number", "1.25", [], true, "Typed protocol control."),
    new("test.json", "Verifier", "JSON fixture", "json", "[\"safe\"]", [], true, "Typed protocol control."),
    new("test.asset", "Verifier", "Asset fixture", "asset", "Assets/Probe/Safe.asset", [], true, "Typed protocol control."),
    new("test.readonly", "Verifier", "Read-only fixture", "string", "unchanged", [], false, "This field must not dispatch a mutation.")
};

FixtureSnapshot Snapshot(string currentRevision, IEnumerable<Field>? currentFields = null, string state = "available", string profileName = "Windows Acceptance", string[]? limitations = null) => new(
    1, DateTime.UtcNow.ToString("O"), currentRevision, state, "", "6000.6.0f1", "StandaloneWindows64", profileName,
    "Assets/Settings/Build Profiles/Windows Acceptance.asset", "Windows profile + Standalone PlayerSettings",
    currentFields?.ToArray() ?? fields.ToArray(), limitations ?? []);

object Reply(string requestId, FixtureSnapshot snapshot) => new { type = "build.settings.result", requestId, snapshot };

async Task<JsonElement> NextCommand(ClientWebSocket socket, string expectedType, string expectedProjectId)
{
    var command = await Receive(socket);
    Check(command.GetProperty("type").GetString() == expectedType, "dashboard requests " + expectedType + " over the Editor bridge");
    Check(Guid.TryParseExact(command.GetProperty("requestId").GetString(), "N", out _), "build-settings request has a bounded correlation ID");
    if (command.TryGetProperty("projectId", out var requestedProject))
        Check(requestedProject.GetString() == expectedProjectId, "build-settings request is scoped to the selected Unity project");
    return command;
}

async Task<(string Html, string Anti)> Load(HttpClient client, ClientWebSocket socket, string selectedProjectId, string currentRevision,
    IEnumerable<Field>? currentFields = null, string state = "available", string profileName = "Windows Acceptance", string[]? limitations = null)
{
    var pageTask = client.GetAsync("/BuildSettings?projectId=" + Uri.EscapeDataString(selectedProjectId));
    var command = await NextCommand(socket, "build.settings.get", selectedProjectId);
    await Write(socket, Reply(command.GetProperty("requestId").GetString()!, Snapshot(currentRevision, currentFields, state, profileName, limitations)));
    using var response = await pageTask;
    var html = await response.Content.ReadAsStringAsync();
    Check(response.StatusCode == HttpStatusCode.OK, "settings page returns an inspectable state response");
    Check(response.Headers.CacheControl?.NoStore == true, "settings page responses are marked no-store");
    return (html, AntiForgery(html));
}

async Task<Posted> Post(HttpClient client, ClientWebSocket socket, string selectedProjectId, string sessionId, string anti,
    string currentRevision, string settingId, string value, bool includeToken = true, string? projectOverride = null,
    bool expectCommand = false, FixtureSnapshot? replySnapshot = null)
{
    var form = new Dictionary<string, string>
    {
        ["editorSessionId"] = sessionId,
        ["projectId"] = projectOverride ?? selectedProjectId,
        ["revision"] = currentRevision,
        ["settingId"] = settingId,
        ["value"] = value
    };
    if (includeToken) form["__RequestVerificationToken"] = anti;
    var responseTask = client.PostAsync("/BuildSettings?handler=Set", new FormUrlEncodedContent(form));
    if (expectCommand)
    {
        var command = await NextCommand(socket, "build.settings.set", selectedProjectId);
        Check(command.GetProperty("revision").GetString() == currentRevision && command.GetProperty("settingId").GetString() == settingId && command.GetProperty("value").GetString() == value,
            "setting mutation preserves exact revision, allowlisted id and value");
        await Write(socket, Reply(command.GetProperty("requestId").GetString()!, replySnapshot ?? Snapshot(currentRevision)));
    }
    using var response = await responseTask;
    var content = await response.Content.ReadAsStringAsync();
    return new(response.StatusCode, content);
}

async Task RejectMutation(HttpClient client, ClientWebSocket socket, string sessionId, string anti, string currentRevision,
    string settingId, string value, string label, string? postedSessionId = null, string? projectOverride = null)
{
    var responseTask = client.PostAsync("/BuildSettings?handler=Set", new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["editorSessionId"] = postedSessionId ?? sessionId,
        ["projectId"] = projectOverride ?? projectId,
        ["revision"] = currentRevision,
        ["settingId"] = settingId,
        ["value"] = value,
        ["__RequestVerificationToken"] = anti
    }));
    using var response = await responseTask;
    var body = await response.Content.ReadAsStringAsync();
    Check(await HasMessageSoon(socket) == false, label + " does not dispatch a mutation to Unity");
    Check(body.Contains("invalid", StringComparison.OrdinalIgnoreCase) || body.Contains("read-only", StringComparison.OrdinalIgnoreCase) ||
        body.Contains("stale", StringComparison.OrdinalIgnoreCase) || body.Contains("refresh", StringComparison.OrdinalIgnoreCase) ||
        response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict,
        label + " returns an explicit validation/stale response");
}

void Stop()
{
    try { editorSocket?.Abort(); editorSocket?.Dispose(); } catch { }
    try { foreignSocket?.Abort(); foreignSocket?.Dispose(); } catch { }
    foreach (var socket in extraSockets) { try { socket.Abort(); socket.Dispose(); } catch { } }
    if (server is { HasExited: false }) { server.Kill(true); server.WaitForExit(); }
    server?.Dispose();
    File.WriteAllLines(Path.Combine(root, "build-settings-server.log"), serverLogs);
}

try
{
    // Fail closed when the documented isolated-fixture port is already occupied.
    using (var probe = new TcpListener(IPAddress.Loopback, 5295)) probe.Start();
    var start = new ProcessStartInfo("dotnet")
    {
        WorkingDirectory = appRoot, UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true
    };
    start.ArgumentList.Add(buildDll);
    start.ArgumentList.Add("--urls");
    start.ArgumentList.Add(address);
    start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
    start.Environment["ConnectionStrings__DefaultConnection"] = "Data Source=" + database;
    var localAppData = Path.Combine(root, "local-app-data");
    Directory.CreateDirectory(Path.Combine(localAppData, "ASP.NET", "DataProtection-Keys"));
    start.Environment["LOCALAPPDATA"] = localAppData;
    server = Process.Start(start) ?? throw new Exception("Could not start the isolated dashboard process.");
    server.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (serverLogs) serverLogs.Add(e.Data); };
    server.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (serverLogs) serverLogs.Add(e.Data); };
    server.BeginOutputReadLine();
    server.BeginErrorReadLine();
    using var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false })
    { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(40) };
    using var readiness = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    var ready = false;
    for (var attempt = 0; attempt < 100; attempt++)
    {
        if (server.HasExited) throw new Exception("Isolated dashboard exited during startup; see build-settings-server.log.");
        try { using var response = await readiness.GetAsync(address + "/Editors"); if (response.IsSuccessStatusCode) { ready = true; break; } }
        catch (HttpRequestException) { }
        await Task.Delay(100);
    }
    Check(ready, "isolated dashboard child process starts on 127.0.0.1:5295");

    var (sessionId, socket) = await Pair(client, projectId, "Build settings fixture");
    editorSocket = socket;

    using (var wrongHost = new HttpRequestMessage(HttpMethod.Get, "/BuildSettings?projectId=" + projectId))
    {
        wrongHost.Headers.Host = "attacker.example";
        using var response = await client.SendAsync(wrongHost);
        Check(!response.IsSuccessStatusCode, "dashboard refuses a non-loopback Host header");
        Check(await HasMessageSoon(socket) == false, "rejected Host request cannot query Unity settings");
    }

    var loaded = await Load(client, socket, projectId, revision);
    var anti = loaded.Anti;
    Check(anti.Length > 20, "build-settings page uses normal Razor antiforgery protection");
    Check(loaded.Html.Contains("StandaloneWindows64", StringComparison.Ordinal) && loaded.Html.Contains("Windows Acceptance", StringComparison.Ordinal),
        "settings page identifies the Windows target and exact profile");
    Check(loaded.Html.Contains("Architecture", StringComparison.Ordinal) && loaded.Html.Contains("Scripting Backend", StringComparison.Ordinal) &&
        loaded.Html.Contains("Effective Scenes", StringComparison.Ordinal) && loaded.Html.Contains("Graphics APIs", StringComparison.Ordinal),
        "settings page renders platform, PlayerSettings, scenes, defines and rendering categories");
    Check(loaded.Html.Contains("test.readonly", StringComparison.Ordinal) && loaded.Html.Contains("Read-only", StringComparison.Ordinal),
        "read-only capability is visible without rendering a save action");

    using (var noToken = await client.PostAsync("/BuildSettings?handler=Set", new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["editorSessionId"] = sessionId, ["projectId"] = projectId, ["revision"] = revision,
        ["settingId"] = "windows.development", ["value"] = "true"
    })))
    {
        Check(noToken.StatusCode == HttpStatusCode.BadRequest, "settings edit rejects a missing antiforgery token");
        Check(await HasMessageSoon(socket) == false, "antiforgery rejection sends no Editor mutation");
    }

    await RejectMutation(client, socket, sessionId, anti, new string('f', 64), "windows.development", "true", "stale revision");
    await RejectMutation(client, socket, sessionId, anti, revision, "../ProjectSettings.asset", "true", "path-like setting ID");
    await RejectMutation(client, socket, sessionId, anti, revision, "unknown.setting", "true", "unknown setting ID");
    await RejectMutation(client, socket, sessionId, anti, revision, "test.readonly", "changed", "read-only field");
    await RejectMutation(client, socket, sessionId, anti, revision, "windows.development", "TRUE", "invalid bool value");
    await RejectMutation(client, socket, sessionId, anti, revision, "windows.architecture", "x64;IL2CPP", "invalid enum choice");
    await RejectMutation(client, socket, sessionId, anti, revision, "test.integer", "not-an-integer", "invalid integer value");
    await RejectMutation(client, socket, sessionId, anti, revision, "test.number", "NaN", "non-finite numeric value");
    await RejectMutation(client, socket, sessionId, anti, revision, "test.number", "Infinity", "infinite numeric value");
    await RejectMutation(client, socket, sessionId, anti, revision, "test.json", "not-json", "malformed JSON setting value");
    await RejectMutation(client, socket, sessionId, anti, revision, "test.json", "42", "non-object/non-array JSON setting value");
    await RejectMutation(client, socket, sessionId, anti, revision, "windows.development", "true", "wrong Editor session", Guid.NewGuid().ToString("N"));
    await RejectMutation(client, socket, sessionId, anti, revision, "windows.development", "true", "wrong project", projectOverride: Guid.NewGuid().ToString("N"));
    await RejectMutation(client, socket, sessionId, anti, revision, "test.asset", "../../outside.asset", "project-traversing asset path");
    await RejectMutation(client, socket, sessionId, anti, revision, "test.asset", "Assets/Probe/Safe.asset#not-a-number", "malformed asset local file ID");
    await RejectMutation(client, socket, sessionId, anti, revision, "windows.development", new string('x', 16385), "oversized setting value");
    await RejectMutation(client, socket, sessionId, anti, revision, "windows.development", "true\0false", "NUL-containing setting value");

    var nextRevision = new string('b', 64);
    var changedFields = fields.Select(field => field.Id == "windows.development" ? field with { Value = "true" } : field).ToArray();
    var nextSnapshot = Snapshot(nextRevision, changedFields) with { ErrorCode = "saved" };
    var saved = await Post(client, socket, projectId, sessionId, anti, revision, "windows.development", "true", expectCommand: true, replySnapshot: nextSnapshot);
    Check(saved.StatusCode is HttpStatusCode.OK or HttpStatusCode.Redirect or HttpStatusCode.SeeOther,
        "valid Windows setting edit completes through one correlated Editor request");
    Check(saved.Body.Contains("Setting saved in Unity", StringComparison.Ordinal), "successful mutation reports the Editor's saved result");
    Check(!await HasMessageSoon(socket), "one setting mutation does not trigger an unrelated second command");

    // A second set of the old page revision must fail after the successful write.
    await RejectMutation(client, socket, sessionId, anti, revision, "windows.development", "false", "stale page after successful edit");

    // Every advertised type must round-trip as one exact, bounded setting mutation.
    var typedWrites = new (string Id, string Value)[]
    {
        ("player.backend", "IL2CPP"),
        ("test.integer", "42"),
        ("test.number", "3.5"),
        ("test.json", "{\"items\":[\"safe\",\"fixture\"]}"),
        ("player.applicationIdentifier", "com.example.updated"),
        ("test.asset", "Assets/Probe/Other.asset"),
        ("scenes.effective", "{\"items\":[\"+Assets/Probe/ActiveProfile.unity\",\"-Assets/Probe/Second.unity\"]}"),
        ("player.defines", "{\"items\":[\"BASE\",\"BUILD_CHECK\"]}"),
        ("graphics.automatic", "false"),
        ("graphics.apis", "{\"items\":[\"Direct3D12\",\"Direct3D11\"]}")
    };
    var lastRevision = nextRevision;
    var activeFields = changedFields;
    foreach (var (id, value) in typedWrites)
    {
        var priorRevision = lastRevision;
        activeFields = activeFields.Select(field => field.Id == id ? field with { Value = value } : field).ToArray();
        lastRevision = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(priorRevision + id + value))).ToLowerInvariant();
        var updated = Snapshot(lastRevision, activeFields);
        var response = await Post(client, socket, projectId, sessionId, anti, priorRevision, id, value,
            expectCommand: true, replySnapshot: updated);
        Check(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Redirect or HttpStatusCode.SeeOther,
            "typed setting round-trip succeeds for " + id);
    }

    var empty = await Load(client, socket, projectId, lastRevision, Array.Empty<Field>(), "available", "Windows Empty");
    Check(!empty.Html.Contains("Architecture", StringComparison.Ordinal) && !empty.Html.Contains("Scripting Backend", StringComparison.Ordinal) &&
        !empty.Html.Contains("Graphics APIs", StringComparison.Ordinal),
        "an empty settings snapshot exposes no editable controls");
    Check(empty.Html.Contains("Build settings unavailable", StringComparison.Ordinal) ||
        !empty.Html.Contains("data-build-state=\"available\"", StringComparison.Ordinal),
        "an empty available snapshot fails closed instead of claiming a complete settings view");

    var busy = await Load(client, socket, projectId, new string('c', 64), fields, "busy", "Windows Busy");
    Check(busy.Html.Contains("data-build-state=\"busy\"", StringComparison.Ordinal) && !busy.Html.Contains("setting-windows.architecture", StringComparison.Ordinal),
        "busy Editor state is reported without editable controls");
    var failed = await Load(client, socket, projectId, new string('d', 64), fields, "failed", "Windows Failed", ["Fixture capability unavailable."]);
    Check(failed.Html.Contains("data-build-state=\"failed\"", StringComparison.Ordinal) && !failed.Html.Contains("setting-windows.architecture", StringComparison.Ordinal),
        "failed Editor state is reported without editable controls");
    Check(failed.Html.Contains("Fixture capability unavailable.", StringComparison.Ordinal), "capability limitations remain visible in failed state");

    var attackField = new Field("test.attack", "<script>SETTINGS-XSS</script>", "<img src=x onerror=SETTINGS-XSS>", "string",
        "\"><svg onload=SETTINGS-XSS>", [], true, "<script>SETTINGS-XSS</script>");
    var attackPage = await Load(client, socket, projectId, new string('c', 64), [attackField], "available", "<script>SETTINGS-XSS</script>");
    Check(!attackPage.Html.Contains("<script>SETTINGS-XSS</script>", StringComparison.Ordinal) &&
        !attackPage.Html.Contains("<img src=x onerror=SETTINGS-XSS>", StringComparison.Ordinal) &&
        !attackPage.Html.Contains("<svg onload=SETTINGS-XSS>", StringComparison.Ordinal),
        "Unity-supplied profile, field labels, notes and values are HTML-encoded");

    // Replies with bad correlation IDs or from a different authenticated Editor must not complete a request.
    var secondProjectId = Guid.NewGuid().ToString("N");
    var (_, otherSocket) = await Pair(client, secondProjectId, "Foreign build settings fixture");
    foreignSocket = otherSocket;
    var pending = client.GetAsync("/BuildSettings?projectId=" + projectId);
    var pendingCommand = await NextCommand(socket, "build.settings.get", projectId);
    var requestId = pendingCommand.GetProperty("requestId").GetString()!;
    using (var concurrentResponse = await client.GetAsync("/BuildSettings?projectId=" + projectId))
    {
        var concurrentBody = await concurrentResponse.Content.ReadAsStringAsync();
        Check(concurrentResponse.StatusCode == HttpStatusCode.OK && concurrentBody.Contains("already active", StringComparison.OrdinalIgnoreCase),
            "a second same-project settings request reports busy while the first is pending");
        Check(await HasMessageSoon(socket) == false, "same-project request serialization prevents a second Unity dispatch");
    }
    await Write(otherSocket, Reply(requestId, Snapshot(new string('d', 64))));
    await Write(socket, Reply(Guid.NewGuid().ToString("N"), Snapshot(new string('d', 64))));
    await Task.Delay(200);
    Check(!pending.IsCompleted, "foreign-session and wrong-correlation replies cannot satisfy a settings request");
    await Write(socket, Reply(requestId, Snapshot(new string('d', 64))));
    using (var response = await pending) Check(response.StatusCode == HttpStatusCode.OK, "only the owning Editor's matching reply completes the settings request");

    // Four project reads may be active globally; the fifth must fail closed without a bridge dispatch.
    var activeProjects = new List<(string ProjectId, ClientWebSocket Socket)> { (projectId, socket), (secondProjectId, otherSocket) };
    for (var index = 0; index < 2; index++)
    {
        var id = Guid.NewGuid().ToString("N");
        var (_, additionalSocket) = await Pair(client, id, "Concurrent build settings fixture " + index);
        extraSockets.Add(additionalSocket);
        activeProjects.Add((id, additionalSocket));
    }
    var activeReads = new List<(ClientWebSocket Socket, Task<HttpResponseMessage> Response, string RequestId)>();
    foreach (var activeProject in activeProjects)
    {
        var response = client.GetAsync("/BuildSettings?projectId=" + activeProject.ProjectId);
        var command = await NextCommand(activeProject.Socket, "build.settings.get", activeProject.ProjectId);
        activeReads.Add((activeProject.Socket, response, command.GetProperty("requestId").GetString()!));
    }
    var fifthProjectId = Guid.NewGuid().ToString("N");
    var (_, fifthSocket) = await Pair(client, fifthProjectId, "Over-limit build settings fixture");
    extraSockets.Add(fifthSocket);
    using (var overLimitResponse = await client.GetAsync("/BuildSettings?projectId=" + fifthProjectId))
    {
        var overLimitBody = await overLimitResponse.Content.ReadAsStringAsync();
        Check(overLimitResponse.StatusCode == HttpStatusCode.OK && overLimitBody.Contains("already active", StringComparison.OrdinalIgnoreCase),
            "global settings broker refuses a fifth concurrent project request");
        Check(await HasMessageSoon(fifthSocket) == false, "global concurrency limit prevents a fifth Unity dispatch");
    }
    foreach (var activeRead in activeReads)
        await Write(activeRead.Socket, Reply(activeRead.RequestId, Snapshot(new string('e', 64))));
    foreach (var activeRead in activeReads)
    {
        using var response = await activeRead.Response;
        Check(response.StatusCode == HttpStatusCode.OK, "one of four permitted concurrent project snapshots completes");
    }

    var badSnapshots = new (string Name, FixtureSnapshot Snapshot)[]
    {
        ("unsupported protocol", Snapshot(new string('e', 64)) with { ProtocolVersion = 2 }),
        ("bad revision", Snapshot("not-a-revision")),
        ("duplicate field ID", Snapshot(new string('e', 64), [fields[0], fields[0]])),
        ("unknown field kind", Snapshot(new string('e', 64), [fields[0] with { Kind = "arbitrary" }])),
        ("control character in label", Snapshot(new string('e', 64), [fields[0] with { Label = "bad\nlabel" }])),
        ("more than 512 fields", Snapshot(new string('e', 64), Enumerable.Range(0, 513).Select(index =>
            new Field("limit." + index, "Limits", "Bounded field " + index, "string", "value", [], true, "Field count boundary." )).ToArray()))
    };
    foreach (var (name, badSnapshot) in badSnapshots)
    {
        var failedPage = client.GetAsync("/BuildSettings?projectId=" + projectId);
        var command = await NextCommand(socket, "build.settings.get", projectId);
        await Write(socket, Reply(command.GetProperty("requestId").GetString()!, badSnapshot));
        using var failedResponse = await failedPage;
        var body = await failedResponse.Content.ReadAsStringAsync();
        Check(failedResponse.StatusCode == HttpStatusCode.OK && body.Contains("Build settings unavailable", StringComparison.Ordinal) &&
            !body.Contains("Windows Acceptance", StringComparison.Ordinal),
            "invalid " + name + " snapshot is rejected without rendering partial settings");
    }
    await RejectMutation(client, socket, sessionId, attackPage.Anti, lastRevision, "windows.development", "false", "failed refresh invalidates prior schema");

    // Check the transport envelope limit independently of the field-count limit.
    var oversizedFields = Enumerable.Range(0, 100).Select(index => new Field("limit." + index, "Limit", "Bounded field " + index,
        "string", new string('v', 12000), [], true, "Envelope boundary fixture.")).ToArray();
    var tooLarge = Snapshot(new string('f', 64), oversizedFields);
    var tooLargeTask = client.GetAsync("/BuildSettings?projectId=" + projectId);
    var tooLargeCommand = await NextCommand(socket, "build.settings.get", projectId);
    await Write(socket, Reply(tooLargeCommand.GetProperty("requestId").GetString()!, tooLarge));
    using (var tooLargeResponse = await tooLargeTask)
    {
        var body = await tooLargeResponse.Content.ReadAsStringAsync();
        Check(tooLargeResponse.StatusCode == HttpStatusCode.OK && !body.Contains("Bounded field 0", StringComparison.Ordinal),
            "over-1MiB Unity settings replies fail closed without partial editing");
    }

    Check(checks >= 40, "suite executed its minimum security, protocol and rendering acceptance coverage");
    Console.WriteLine($"BUILD CONFIGURATION VERIFICATION COMPLETE: {checks} checks passed.");
}
catch (Exception exception)
{
    Console.Error.WriteLine("BUILD CONFIGURATION VERIFICATION FAILED: " + exception.Message);
    Console.Error.WriteLine("Owned dashboard logs: " + Path.Combine(root, "build-settings-server.log"));
    Environment.ExitCode = 1;
}
finally
{
    try { Stop(); }
    catch (Exception exception)
    {
        Console.Error.WriteLine("BUILD CONFIGURATION VERIFICATION CLEANUP FAILED: " + exception.Message);
        Environment.ExitCode = 1;
    }
}

sealed record Posted(HttpStatusCode StatusCode, string Body);
sealed record Field(string Id, string Category, string Label, string Kind, string Value, string[] Choices, bool Editable, string Note);

sealed record FixtureSnapshot(int ProtocolVersion, string CapturedAtUtc, string Revision, string State, string ErrorCode, string UnityVersion,
    string ActiveTarget, string ProfileName, string ProfilePath, string Scope, Field[] Fields, string[] Limitations);

sealed class MessageInbox
{
    private readonly ClientWebSocket socket;
    private readonly Channel<JsonElement> messages = Channel.CreateUnbounded<JsonElement>(new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });

    public MessageInbox(ClientWebSocket socket)
    {
        this.socket = socket;
        _ = ReceiveLoopAsync();
    }

    public async Task<JsonElement> ReceiveAsync(int timeoutMilliseconds)
    {
        using var timeout = new CancellationTokenSource(timeoutMilliseconds);
        return await messages.Reader.ReadAsync(timeout.Token);
    }

    public async Task<bool> HasMessageSoonAsync(int milliseconds)
    {
        using var timeout = new CancellationTokenSource(milliseconds);
        try { await messages.Reader.ReadAsync(timeout.Token); return true; }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { return false; }
        catch (ChannelClosedException) { return true; }
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            var buffer = new byte[8192];
            while (true)
            {
                using var stream = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close) throw new Exception("Fixture Editor socket closed unexpectedly.");
                    if (result.MessageType != WebSocketMessageType.Text || stream.Length + result.Count > 2 * 1024 * 1024)
                        throw new Exception("Fixture received a non-text or oversized command.");
                    stream.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                await messages.Writer.WriteAsync(JsonDocument.Parse(stream.ToArray()).RootElement.Clone());
            }
        }
        catch (Exception exception) { messages.Writer.TryComplete(exception); }
    }
}
