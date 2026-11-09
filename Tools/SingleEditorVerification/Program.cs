using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using PlaytestOps.Web.Bridge;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: Checks <isolated-output-directory-containing-build> <absolute-web-project-path>");
    Environment.ExitCode = 2;
    return;
}

var output = Path.GetFullPath(args[0]);
var appRoot = Path.GetFullPath(args[1]);
var appDll = Path.Combine(output, "build", "PlaytestOps.Web.dll");
var database = Path.Combine(output, "single-editor-" + Guid.NewGuid().ToString("N") + ".db");
var checks = 0;
Process? server = null;
var serverLogs = new List<string>();
var sockets = new List<ClientWebSocket>();
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException(label);
    checks++;
    Console.WriteLine("PASS " + label);
}

static EditorSession Authenticate(BridgeRegistry registry, string token)
{
    var context = new DefaultHttpContext();
    context.Request.Headers.Authorization = "Bearer " + token;
    return registry.Authenticate(context) ?? throw new InvalidOperationException("Fixture credentials were not accepted.");
}

static (PairResponse Credential, EditorSession Session) PairFixture(BridgeRegistry registry, string name)
{
    var code = registry.CreateCode().Code;
    var credential = registry.Pair(new PairRequest(code, Guid.NewGuid().ToString("N"), name, "6000.6.0f1"))
        ?? throw new InvalidOperationException("Fixture pairing failed without an active Editor.");
    return (credential, Authenticate(registry, credential.Token));
}

static void Expire(EditorSession session) => typeof(EditorSession).GetProperty(nameof(EditorSession.ExpiresAt))!
    .SetValue(session, DateTimeOffset.UtcNow.AddSeconds(-1));

void RegistryChecks()
{
    var registry = new BridgeRegistry();
    var (_, first) = PairFixture(registry, "First");
    var (_, second) = PairFixture(registry, "Second");
    var firstSocket = new FixtureSocket();
    var secondSocket = new FixtureSocket();
    Check(registry.Attach(first, firstSocket), "first pre-paired Editor can attach");
    Check(!registry.Attach(second, secondSocket), "second pre-paired Editor cannot attach while another is active");
    Check(registry.List().Count(x => x.Connected) == 1, "registry exposes exactly one active Editor");
    Check(ReferenceEquals(registry.ConnectedEditor(), first) && registry.CanAttach(first) && !registry.CanAttach(second),
        "global Editor selection and upgrade precheck agree with the atomic owner");
    Check(ReferenceEquals(registry.ConnectedProject(first.ProjectId), first) && registry.ConnectedProject(second.ProjectId) is null,
        "connected-project lookup resolves only the active Editor");
    var sameProjectCode = registry.CreateCode().Code;
    Check(registry.Pair(new PairRequest(sameProjectCode, first.ProjectId, "Competing copy", "6000.6.0f1"), out var sameProjectBusy) is null && sameProjectBusy,
        "pairing a second copy of the same project reports a connection conflict");
    Check(!firstSocket.Aborted && registry.IsCurrent(first, firstSocket), "same-project pairing conflict does not evict the active Editor");
    Check(registry.Pair(new PairRequest("invalid", second.ProjectId, "Invalid code", "6000.6.0f1"), out var invalidBusy) is null && !invalidBusy,
        "invalid code remains an authentication failure even while another Editor is active");

    var replacement = new FixtureSocket();
    first.RunProbeId = "old-probe";
    first.RunReady = true;
    Check(registry.Attach(first, replacement), "same-session domain-reload reconnect is allowed");
    Check(firstSocket.Aborted && !replacement.Aborted, "same-session reconnect aborts only its previous socket");
    Check(first.RunProbeId is null && !first.RunReady, "reconnect resets stale run-readiness state");
    registry.Detach(first, firstSocket);
    Check(registry.IsCurrent(first, replacement) && registry.IsConnected(first.Id), "late old-socket detach cannot clear its replacement");
    Check(!registry.Attach(second, secondSocket), "replacement socket continues to own the sole Editor slot");
    registry.Detach(second, secondSocket);
    Check(registry.IsCurrent(first, replacement), "rejected socket cleanup cannot release another Editor's slot");
    registry.Detach(first, replacement);
    Check(!registry.IsConnected(first.Id), "detaching the active Editor releases its connected state");
    var sameProjectRetry = registry.Pair(new PairRequest(sameProjectCode, first.ProjectId, "Same project after disconnect", "6000.6.0f1"), out var retryBusy);
    Check(sameProjectRetry is not null && !retryBusy, "same-project conflict preserves the code for retry after disconnect");
    first = Authenticate(registry, sameProjectRetry!.Token);
    Check(registry.Attach(second, secondSocket), "a different pre-paired Editor can attach after the active socket detaches");
    registry.Revoke(second);
    Check(secondSocket.Aborted && registry.Attach(first, new FixtureSocket()), "explicit session revocation releases the slot");
    Expire(first);
    Check(registry.List().All(x => !x.Connected), "expired active session is pruned from connected Editors");
    var (_, afterExpiry) = PairFixture(registry, "After expiry");
    Check(registry.Attach(afterExpiry, new FixtureSocket()), "another Editor can attach after session expiry");

    for (var round = 0; round < 32; round++)
    {
        var racing = new BridgeRegistry();
        var (_, left) = PairFixture(racing, "Left");
        var (_, right) = PairFixture(racing, "Right");
        var leftSocket = new FixtureSocket();
        var rightSocket = new FixtureSocket();
        using var barrier = new Barrier(2);
        var leftTask = Task.Run(() => { barrier.SignalAndWait(); return racing.Attach(left, leftSocket); });
        var rightTask = Task.Run(() => { barrier.SignalAndWait(); return racing.Attach(right, rightSocket); });
        Task.WaitAll(leftTask, rightTask);
        Check(leftTask.Result ^ rightTask.Result, $"parallel pre-paired attach round {round + 1}: exactly one winner");
        Check(racing.List().Count(x => x.Connected) == 1, $"parallel pre-paired attach round {round + 1}: one connected session");
        var winner = leftTask.Result ? left : right;
        var winningSocket = leftTask.Result ? leftSocket : rightSocket;
        racing.Detach(leftTask.Result ? right : left, leftTask.Result ? rightSocket : leftSocket);
        Check(racing.IsCurrent(winner, winningSocket), $"parallel pre-paired attach round {round + 1}: loser cleanup preserves winner");
    }
}

static string AntiForgery(string html) => WebUtility.HtmlDecode(
    Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);

long Scalar(string sql)
{
    using var connection = new SqliteConnection("Data Source=" + database);
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    return Convert.ToInt64(command.ExecuteScalar());
}

async Task<JsonElement> Receive(ClientWebSocket socket)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    using var stream = new MemoryStream();
    var bytes = new byte[8192];
    WebSocketReceiveResult result;
    do
    {
        result = await socket.ReceiveAsync(bytes, deadline.Token);
        if (result.MessageType == WebSocketMessageType.Close) throw new WebSocketException("Socket closed before a JSON message arrived.");
        stream.Write(bytes, 0, result.Count);
    } while (!result.EndOfMessage);
    return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
}

async Task SendCatalog(ClientWebSocket socket, string name)
{
    var payload = new
    {
        type = "catalog.replace", requestId = "single-editor-check",
        tests = new[] { new { uniqueName = name, fullName = name, name, description = "Isolated catalog retention check",
            assembly = "SingleEditorChecks", mode = "EditMode", runState = "Runnable", skipReason = "" } }
    };
    await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload, json), WebSocketMessageType.Text, true, CancellationToken.None);
    Check((await Receive(socket)).GetProperty("type").GetString() == "catalog.accepted", name + " catalog persists successfully");
}

async Task<bool> Eventually(Func<Task<bool>> probe, int milliseconds = 5000)
{
    var clock = Stopwatch.StartNew();
    do
    {
        if (await probe()) return true;
        await Task.Delay(50);
    } while (clock.ElapsedMilliseconds < milliseconds);
    return false;
}

try
{
    RegistryChecks();
    if (!File.Exists(appDll)) throw new FileNotFoundException("Build the web app into <isolated-output-directory>/build first.", appDll);
    Directory.CreateDirectory(output);
    int port;
    using (var reservation = new TcpListener(IPAddress.Loopback, 0))
    {
        reservation.Start();
        port = ((IPEndPoint)reservation.LocalEndpoint).Port;
    }
    var address = "http://127.0.0.1:" + port;
    var socketAddress = new Uri("ws://127.0.0.1:" + port + "/api/editor/connect");
    var start = new ProcessStartInfo("dotnet")
    {
        WorkingDirectory = appRoot, UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true
    };
    start.ArgumentList.Add(appDll);
    start.ArgumentList.Add("--urls");
    start.ArgumentList.Add(address);
    start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
    start.Environment["ConnectionStrings__DefaultConnection"] = "Data Source=" + database;
    server = Process.Start(start) ?? throw new InvalidOperationException("Isolated server did not start.");
    server.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (serverLogs) serverLogs.Add(e.Data); };
    server.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (serverLogs) serverLogs.Add(e.Data); };
    server.BeginOutputReadLine();
    server.BeginErrorReadLine();
    using var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() })
    {
        BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(8)
    };
    Check(await Eventually(async () =>
    {
        if (server.HasExited) throw new InvalidOperationException("Isolated server exited during startup.");
        try { return (await client.GetAsync("/Editors")).IsSuccessStatusCode; }
        catch (HttpRequestException) { return false; }
    }, 15000), "isolated dashboard starts on a private dynamic loopback port and disposable database");

    async Task<string> Code()
    {
        var anti = AntiForgery(await client.GetStringAsync("/Editors"));
        using var generated = await client.PostAsync("/Editors?handler=PairingCode", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["__RequestVerificationToken"] = anti }));
        generated.EnsureSuccessStatusCode();
        var code = Regex.Match(await generated.Content.ReadAsStringAsync(), "id=\"pairing-code\">([^<]+)").Groups[1].Value;
        Check(code.Length == 10, "isolated local form generates a valid short-lived pairing code");
        return code;
    }
    async Task<PairResponse> Pair(string code, string id, string name)
    {
        using var response = await client.PostAsJsonAsync("/api/editor/pair", new PairRequest(code, id, name, "6000.6.0f1"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PairResponse>())!;
    }
    async Task<ClientWebSocket> Connect(PairResponse credential)
    {
        var socket = new ClientWebSocket();
        sockets.Add(socket);
        socket.Options.SetRequestHeader("Authorization", "Bearer " + credential.Token);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(socketAddress, timeout.Token);
        var ready = await Receive(socket);
        Check(ready.GetProperty("type").GetString() == "session.ready" && ready.GetProperty("sessionId").GetString() == credential.SessionId,
            "only the authenticated accepted Editor receives its own ready handshake");
        return socket;
    }
    async Task Revoke(PairResponse credential)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/editor/session");
        request.Headers.Authorization = new("Bearer", credential.Token);
        using var response = await client.SendAsync(request);
        Check(response.StatusCode == HttpStatusCode.NoContent, "explicit disconnect revokes the selected Editor session");
    }
    async Task<HttpResponseMessage> OperatorDisconnect(string id, bool withAnti = true, string? host = null)
    {
        var fields = new Dictionary<string, string> { ["sessionId"] = id };
        if (withAnti) fields["__RequestVerificationToken"] = AntiForgery(await client.GetStringAsync("/Editors"));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Editors?handler=Disconnect")
        {
            Content = new FormUrlEncodedContent(fields)
        };
        if (host is not null) request.Headers.Host = host;
        return await client.SendAsync(request);
    }

    var firstId = Guid.NewGuid().ToString("N");
    var secondId = Guid.NewGuid().ToString("N");
    var first = await Pair(await Code(), firstId, "Single-editor first project");
    var second = await Pair(await Code(), secondId, "Single-editor second project");
    var firstSocket = await Connect(first);
    await SendCatalog(firstSocket, "First retained fixture test");
    using (var missingAnti = await OperatorDisconnect(first.SessionId, withAnti: false))
        Check(missingAnti.StatusCode == HttpStatusCode.BadRequest, "dashboard disconnect form requires antiforgery validation");
    using (var foreignHost = await OperatorDisconnect(first.SessionId, host: "example.com"))
        Check(foreignHost.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest,
            "dashboard disconnect form rejects a non-local Host header");
    using (var staleSession = await OperatorDisconnect(Guid.NewGuid().ToString("N")))
        Check(staleSession.StatusCode == HttpStatusCode.Conflict, "stale dashboard disconnect cannot revoke a different Editor");
    var firstTestId = Scalar("SELECT Id FROM Playtests WHERE UniqueName='First retained fixture test'");
    Scalar("UPDATE Playtests SET Status=3 WHERE Id=" + firstTestId + "; SELECT COUNT(*) FROM Playtests;");
    var pendingCode = await Code();
    using (var blockedPair = await client.PostAsJsonAsync("/api/editor/pair",
        new PairRequest(pendingCode, Guid.NewGuid().ToString("N"), "Blocked project", "6000.6.0f1")))
    {
        Check(blockedPair.StatusCode == HttpStatusCode.Conflict, "pairing another Editor while one is active returns HTTP 409");
        Check((await blockedPair.Content.ReadAsStringAsync()).Contains("Editor", StringComparison.OrdinalIgnoreCase),
            "busy pairing response explains the Editor connection conflict");
    }
    using (var blockedSocket = new ClientWebSocket())
    {
        blockedSocket.Options.SetRequestHeader("Authorization", "Bearer " + second.Token);
        var rejected = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await blockedSocket.ConnectAsync(socketAddress, timeout.Token);
            try { rejected = (await Receive(blockedSocket)).GetProperty("type").GetString() != "session.ready"; }
            catch (WebSocketException) { rejected = true; }
        }
        catch (WebSocketException) { rejected = true; }
        Check(rejected, "previously paired second Editor cannot receive a ready handshake while first is active");
    }
    foreach (var route in new[] { "/Scripts", "/VersionControl", "/BuildSettings", "/Builds" })
    {
        var page = await client.GetStringAsync(route + "?projectId=" + secondId);
        Check(page.Contains("id=\"dashboard-editor-context\"") && page.Contains("Single-editor first project"),
            route + " retains the same active Editor context for a stale foreign-project URL");
        Check(!page.Contains("Single-editor second project"), route + " does not offer the disconnected foreign Editor as a selectable project");
    }
    var replacementSocket = await Connect(first);
    await Task.Delay(150); // Give the aborted old handler a chance to execute its detach finally block.
    await SendCatalog(replacementSocket, "First retained fixture test");
    Check(Scalar("SELECT Id FROM Playtests WHERE UniqueName='First retained fixture test'") == firstTestId &&
        Scalar("SELECT Status FROM Playtests WHERE Id=" + firstTestId) == 3,
        "same-session socket replacement and old-handler cleanup preserve saved test identity and outcome");
    var firstDashboard = await client.GetStringAsync("/");
    Check(firstDashboard.Contains("First retained fixture test") && !firstDashboard.Contains("Second retained fixture test"),
        "dashboard tests resolve to the sole active Editor");
    using (var disconnected = await OperatorDisconnect(first.SessionId))
        Check(disconnected.IsSuccessStatusCode, "dashboard's protected Disconnect Editor action releases the active slot");
    Check(await Eventually(async () => !(await client.GetStringAsync("/")).Contains("First retained fixture test")),
        "disconnected Editor tests disappear from the active dashboard");
    var third = await Pair(pendingCode, Guid.NewGuid().ToString("N"), "Retried project");
    Check(!string.IsNullOrWhiteSpace(third.Token), "HTTP 409 does not consume the pairing code; it works after disconnect");
    await Revoke(third);
    var secondSocket = await Connect(second);
    await SendCatalog(secondSocket, "Second retained fixture test");
    var secondDashboard = await client.GetStringAsync("/");
    Check(secondDashboard.Contains("Second retained fixture test") && !secondDashboard.Contains("First retained fixture test"),
        "switching Editors shows only the new active project's tests");
    Check(Scalar("SELECT COUNT(*) FROM Playtests") == 2 &&
        Scalar("SELECT Status FROM Playtests WHERE Id=" + firstTestId) == 3,
        "switching Editors preserves both projects' saved catalog data and prior outcomes");
    await Revoke(second);
    Check(Scalar("SELECT COUNT(*) FROM UnityProjects") == 2 && Scalar("SELECT COUNT(*) FROM Playtests") == 2,
        "disconnecting all Editors does not delete any saved project or test records");
    Check(!(await client.GetStringAsync("/")).Contains("Second retained fixture test"),
        "dashboard has no active tests after the final disconnect");
    foreach (var route in new[] { "/Scripts", "/VersionControl", "/BuildSettings", "/Builds" })
    {
        var page = await client.GetStringAsync(route);
        Check(page.Contains("id=\"dashboard-editor-context\"") && page.Contains("Not connected"),
            route + " opens without an Editor query when no Editor is connected");
    }
    Console.WriteLine($"SINGLE EDITOR VERIFICATION PASSED: {checks} checks; disposable database {database}");
}
catch (Exception ex)
{
    Console.Error.WriteLine("SINGLE EDITOR VERIFICATION FAILED: " + ex.Message);
    lock (serverLogs) foreach (var line in serverLogs.TakeLast(35)) Console.Error.WriteLine(line);
    Environment.ExitCode = 1;
}
finally
{
    foreach (var socket in sockets) { socket.Abort(); socket.Dispose(); }
    if (server is { HasExited: false }) { server.Kill(true); server.WaitForExit(); }
    server?.Dispose();
}

sealed class FixtureSocket : WebSocket
{
    private WebSocketState state = WebSocketState.Open;
    public bool Aborted { get; private set; }
    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => state;
    public override string? SubProtocol => null;
    public override void Abort() { Aborted = true; state = WebSocketState.Aborted; }
    public override void Dispose() { state = WebSocketState.Closed; }
    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    { state = WebSocketState.Closed; return Task.CompletedTask; }
    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    { state = WebSocketState.CloseSent; return Task.CompletedTask; }
    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Registry fixture sockets do not receive payloads.");
    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
